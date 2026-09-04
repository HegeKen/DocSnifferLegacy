//! 全文索引与搜索：基于 Tantivy，集成 jieba-rs 中文分词。
//! 检索延迟目标 <20ms；索引内存上限默认 64MB（弱机安全值）。

use crate::extract::extract_text;
use crate::scanner::FileMeta;
use std::path::Path;
use std::sync::Arc;
use tantivy::collector::TopDocs;
use tantivy::query::QueryParser;
use tantivy::schema::{Field, IndexRecordOption, Schema, TextFieldIndexing, TextOptions, Value};
use tantivy::tokenizer::{BoxTokenStream, TextAnalyzer, Token, TokenStream, Tokenizer};
use tantivy::{Index, IndexWriter};

/// 搜索结果命中项（路径 | 文件名 | 匹配片段 | 分数 | 大小 | 修改时间）。
#[derive(Debug, Clone)]
pub struct SearchHit {
    pub path: String,
    pub filename: String,
    pub snippet: String,
    pub matched: String,
    pub score: f64,
    pub size: u64,
    pub modified_ms: i64,
}

/// 搜索模式（决定默认检索字段）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SearchMode {
    Filename,
    Content,
    Both,
}

/// 索引结构：持有 schema 字段句柄。
pub struct Indexer {
    index: Index,
    fields: Fields,
    shard: String,
}

struct Fields {
    path: Field,
    filename: Field,
    content: Field,
    snippet: Field,
    size: Field,
    modified: Field,
    ext: Field,
}

/// 索引存储的目录名（便携模式下位于 `Data/index`）。
pub const INDEX_DIR_NAME: &str = "index";

impl Indexer {
    /// 打开（或创建）指定数据目录下的索引分片。
    pub fn open(data_dir: &Path, shard: &str) -> Result<Self, String> {
        let index_path = data_dir.join(INDEX_DIR_NAME).join(shard);
        std::fs::create_dir_all(&index_path).map_err(|e| e.to_string())?;

        let schema = build_schema();
        let index = match Index::open_in_dir(&index_path) {
            Ok(idx) => idx,
            Err(_) => Index::create_in_dir(&index_path, schema).map_err(|e| e.to_string())?,
        };
        register_tokenizers(&index);
        let fields = resolve_fields(&index.schema());
        Ok(Self { index, fields, shard: shard.to_string() })
    }

    /// 创建索引写入器（内存预算默认 64MB，远超则强制落盘）。
    pub fn writer(&self) -> Result<IndexWriter, String> {
        self.index.writer(64 * 1024 * 1024).map_err(|e| e.to_string())
    }

    /// 将一个文件写入索引（提取内容、构造 snippet）。
    pub fn add_file(&self, writer: &mut IndexWriter, meta: &FileMeta) -> Result<(), String> {
        let content = extract_text(&meta.path);
        self.add_with_content(writer, meta, &content)
    }

    /// 使用已提取的内容写入索引（避免重复提取）。
    pub fn add_with_content(
        &self,
        writer: &mut IndexWriter,
        meta: &FileMeta,
        content: &str,
    ) -> Result<(), String> {
        let mut doc = tantivy::TantivyDocument::default();
        let path_str = meta.path.to_string_lossy().to_string();
        let snippet = truncate(content, 1024);

        doc.add_text(self.fields.path, path_str);
        doc.add_text(self.fields.filename, meta.filename.clone());
        doc.add_text(self.fields.content, content.to_string());
        doc.add_text(self.fields.snippet, snippet);
        doc.add_u64(self.fields.size, meta.size);
        doc.add_i64(self.fields.modified, meta.modified_ms);
        doc.add_text(self.fields.ext, meta.ext.clone());
        writer.add_document(doc).map_err(|e| e.to_string())?;
        Ok(())
    }

    /// 访问索引 schema（供外部构造 doc 或校验字段）。
    pub fn schema(&self) -> Schema {
        self.index.schema()
    }

    /// 获取指定名字的字段句柄。
    pub fn field(&self, name: &str) -> Result<Field, String> {
        self.index
            .schema()
            .get_field(name)
            .map_err(|_| format!("字段缺失: {name}"))
    }

    /// 当前索引分片名。
    pub fn shard(&self) -> &str {
        &self.shard
    }

    /// 搜索。`query_str` 支持 tantivy 语法（`+`/`-` AND/OR、双引号短语）。
    pub fn search(
        &self,
        query: &str,
        mode: SearchMode,
        limit: usize,
    ) -> Result<Vec<SearchHit>, String> {
        let query = query.trim();
        if query.is_empty() {
            return Ok(Vec::new());
        }
        let reader = self.index.reader().map_err(|e| e.to_string())?;
        let searcher = reader.searcher();

        let default_fields: Vec<Field> = match mode {
            SearchMode::Filename => vec![self.fields.filename],
            SearchMode::Content => vec![self.fields.content],
            SearchMode::Both => vec![self.fields.content, self.fields.filename],
        };
        let qp = QueryParser::for_index(&self.index, default_fields);
        let q = qp.parse_query(query).map_err(|e| e.to_string())?;

        let top = searcher
            .search(&q, &TopDocs::with_limit(limit))
            .map_err(|e| e.to_string())?;

        let mut hits = Vec::with_capacity(top.len());
        for (score, addr) in top {
            let doc: tantivy::TantivyDocument =
                searcher.doc(addr).map_err(|e| e.to_string())?;
            let path = doc
                .get_first(self.fields.path)
                .and_then(|v| v.as_str())
                .unwrap_or("")
                .to_string();
            let filename = doc
                .get_first(self.fields.filename)
                .and_then(|v| v.as_str())
                .unwrap_or("")
                .to_string();
            let snippet = doc
                .get_first(self.fields.snippet)
                .and_then(|v| v.as_str())
                .unwrap_or("")
                .to_string();
            let matched = make_context(query, &snippet, 50);
            let size = doc
                .get_first(self.fields.size)
                .and_then(|v| v.as_u64())
                .unwrap_or(0);
            let modified_ms = doc
                .get_first(self.fields.modified)
                .and_then(|v| v.as_i64())
                .unwrap_or(0);
            hits.push(SearchHit {
                path,
                filename,
                snippet,
                matched,
                score: score as f64,
                size,
                modified_ms,
            });
        }
        Ok(hits)
    }

    /// 索引中的文档总数。
    pub fn count(&self) -> Result<u64, String> {
        self.index
            .reader()
            .map_err(|e| e.to_string())
            .map(|r| r.searcher().num_docs())
    }
}

/// 截断字符串（按字符边界，避免 panic）。
fn truncate(s: &str, max_chars: usize) -> String {
    s.chars().take(max_chars).collect()
}

/// 生成匹配上下文：定位首个命中词并展示前后窗口。
fn make_context(query: &str, snippet: &str, window: usize) -> String {
    // 先把查询拆成候选词，取 snippet 中第一个命中的词。
    for term in query.split_whitespace() {
        let term = term.trim_matches(|c| matches!(c, '"' | '+' | '-'));
        if term.is_empty() {
            continue;
        }
        if let Some(idx) = find_case_insensitive(snippet, term) {
            let start = idx.saturating_sub(window);
            let end = (idx + term.len().max(window)).min(snippet.len());
            let mut ctx = snippet[start..end].to_string();
            if start > 0 {
                ctx = format!("…{ctx}");
            }
            if end < snippet.len() {
                ctx.push('…');
            }
            return ctx;
        }
    }
    truncate(snippet, window * 2)
}

fn find_case_insensitive(haystack: &str, needle: &str) -> Option<usize> {
    let h = haystack.to_lowercase();
    let n = needle.to_lowercase();
    h.find(&n)
}

/// 构建 Tantivy schema。
fn build_schema() -> Schema {
    let mut builder = Schema::builder();

    // path / ext：仅存储，不索引（用于展示与精确定位）。
    builder.add_text_field("path", TextOptions::default().set_stored());
    builder.add_text_field("ext", TextOptions::default().set_stored());

    // snippet：存储前 1024 字，供结果展示；不索引。
    builder.add_text_field("snippet", TextOptions::default().set_stored());

    // filename / content：jieba 分词 + 位置记录（支持短语查询）+ 存储。
    let content_opts = TextOptions::default()
        .set_indexing_options(
            TextFieldIndexing::default()
                .set_tokenizer("jieba")
                .set_index_option(IndexRecordOption::WithFreqsAndPositions),
        )
        .set_stored();
    builder.add_text_field("filename", content_opts.clone());
    builder.add_text_field("content", content_opts);

    builder.add_u64_field("size", tantivy::schema::STORED);
    builder.add_i64_field("modified", tantivy::schema::STORED);

    builder.build()
}

/// 将 jieba 分词器注册到索引，供写入与解析查询时使用。
/// 必须在创建 QueryParser / IndexWriter 之前调用。
fn register_tokenizers(index: &Index) {
    index
        .tokenizers()
        .register("jieba", TextAnalyzer::builder(JiebaTokenizer::new()).build());
}

/// 从已有 schema 提取字段句柄。
fn resolve_fields(schema: &Schema) -> Fields {
    Fields {
        path: schema.get_field("path").expect("path 字段缺失"),
        filename: schema.get_field("filename").expect("filename 字段缺失"),
        content: schema.get_field("content").expect("content 字段缺失"),
        snippet: schema.get_field("snippet").expect("snippet 字段缺失"),
        size: schema.get_field("size").expect("size 字段缺失"),
        modified: schema.get_field("modified").expect("modified 字段缺失"),
        ext: schema.get_field("ext").expect("ext 字段缺失"),
    }
}

/// 基于 jieba 的中文分词器实现 Tantivy `Tokenizer`。
/// 说明：Tantivy 需要 tokenizer 在创建索引时即注册；`open()` 中创建索引前先注册。
#[derive(Clone)]
pub struct JiebaTokenizer {
    jieba: Arc<jieba_rs::Jieba>,
}

impl JiebaTokenizer {
    pub fn new() -> Self {
        Self {
            jieba: Arc::new(jieba_rs::Jieba::new()),
        }
    }
}

impl Default for JiebaTokenizer {
    fn default() -> Self {
        Self::new()
    }
}

impl Tokenizer for JiebaTokenizer {
    type TokenStream<'a> = BoxTokenStream<'a>;
    fn token_stream<'a>(&'a mut self, text: &'a str) -> BoxTokenStream<'a> {
        let words = self.jieba.cut(text, true);
        BoxTokenStream::new(JiebaTokenStream::new(text, words))
    }
}

/// Tantivy TokenStream：把 jieba 词语流映射为 token，并维护字节偏移。
struct JiebaTokenStream<'a> {
    words: std::vec::IntoIter<&'a str>,
    text: &'a str,
    cursor: usize,
    token: Token,
    position: usize,
}

impl<'a> JiebaTokenStream<'a> {
    fn new(text: &'a str, words: Vec<&'a str>) -> Self {
        Self {
            words: words.into_iter(),
            text,
            cursor: 0,
            token: Token {
                position: 0,
                text: String::new(),
                offset_from: 0,
                offset_to: 0,
                position_length: 1,
            },
            position: 0,
        }
    }
}

impl<'a> TokenStream for JiebaTokenStream<'a> {
    fn advance(&mut self) -> bool {
        while let Some(word) = self.words.next() {
            let rest = &self.text[self.cursor..];
            // 从当前游标向后定位该词，确保重复字符场景下偏移正确。
            let Some(rel) = rest.find(word) else {
                continue;
            };
            let start = self.cursor + rel;
            self.cursor = start + word.len();
            self.token.position = self.position;
            self.token.position_length = 1;
            self.token.offset_from = start;
            self.token.offset_to = self.cursor;
            self.token.text = word.to_lowercase();
            self.position += 1;
            return true;
        }
        false
    }

    fn token(&self) -> &Token {
        &self.token
    }

    fn token_mut(&mut self) -> &mut Token {
        &mut self.token
    }
}

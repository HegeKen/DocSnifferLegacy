using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Lucene.Net.Analysis;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers;
using Lucene.Net.Search;
using Lucene.Net.Store;
using DocSnifferLegacy.Core.Extract;
using DocSnifferLegacy.Core.IO;
using DocSnifferLegacy.Core.Routing;

namespace DocSnifferLegacy.Core.Index
{
    public sealed class FileStamp
    {
        public long Length;
        public long LastWriteUtcTicks;
        public string Batch;
    }

    public sealed class IndexProgress
    {
        public int Processed;
        public int Total;
        public string CurrentFile;
    }

    public sealed class IndexRunStats
    {
        public int Added;
        public int Updated;
        public int Skipped;
        public int Failed;
        public int Unsupported;
        public int Removed;
        public long TotalDocs;
        public bool Completed;
        public List<string> Errors = new List<string>();
    }

    public sealed class SearchResultItem
    {
        public string Path;
        public string FileName;
        public string FileType;
        public long Size;
        public DateTime Modified;
        public float Score;
    }

    /// <summary>
    /// Lucene.NET 3.0.3 索引服务：全量/增量建索引、删除消失文件、多字段检索。
    /// 字段：path(存)/filename(存+析)/content(析不存)/size(存)/modified(存)/filetype(存)。
    /// </summary>
    public sealed class LuceneIndexService : IDisposable
    {
        private readonly string indexPath;
        private FSDirectory directory;
        private IndexReader reader;

        public LuceneIndexService(string indexPath)
        {
            this.indexPath = indexPath;
        }

        public string IndexPath { get { return indexPath; } }

        public long TotalDocs
        {
            get
            {
                IndexReader r = EnsureReader();
                return r == null ? 0 : r.NumDocs();
            }
        }

        private FSDirectory Dir
        {
            get
            {
                if (directory == null)
                {
                    if (!System.IO.Directory.Exists(indexPath)) System.IO.Directory.CreateDirectory(indexPath);
                    // SimpleFSLockFactory：纯文件锁，兼容网络共享盘与非 Windows 运行时
                    directory = FSDirectory.Open(new DirectoryInfo(indexPath), new SimpleFSLockFactory());
                }
                return directory;
            }
        }

        private IndexReader EnsureReader()
        {
            if (reader != null) return reader;
            if (!System.IO.Directory.Exists(indexPath) || !IndexReader.IndexExists(Dir)) return null;
            reader = IndexReader.Open(Dir, true);
            return reader;
        }

        private void ReopenReader()
        {
            if (reader != null)
            {
                IndexReader reopened = reader.Reopen();
                if (!ReferenceEquals(reopened, reader))
                {
                    reader.Dispose();
                    reader = reopened;
                }
            }
            else
            {
                EnsureReader();
            }
        }

        /// <summary>把现有索引中的 path → (大小, 修改时间) 读出来，用于增量比对。</summary>
        private void LoadStamps(IndexReader snapshot, Dictionary<string, FileStamp> target)
        {
            TermEnum terms = snapshot.Terms(new Term("path", ""));
            try
            {
                while (terms.Term != null && terms.Term.Field == "path")
                {
                    TermDocs docs = snapshot.TermDocs(terms.Term);
                    while (docs.Next())
                    {
                        Document d = snapshot.Document(docs.Doc);
                        string p = d.Get("path");
                        if (p == null) continue;
                        var stamp = new FileStamp();
                        long size;
                        if (long.TryParse(d.Get("size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size))
                            stamp.Length = size;
                        long ticks;
                        if (long.TryParse(d.Get("modified"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks))
                            stamp.LastWriteUtcTicks = ticks;
                        stamp.Batch = d.Get("batch") ?? string.Empty;
                        target[p] = stamp;
                    }
                    docs.Dispose();
                    terms.Next();
                }
            }
            finally
            {
                terms.Dispose();
            }
        }

        private IndexWriter OpenWriter()
        {
            bool exists = IndexReader.IndexExists(Dir);
            Analyzer analyzer = new CjkBigramAnalyzer();
            try
            {
                return new IndexWriter(Dir, analyzer, !exists, IndexWriter.MaxFieldLength.UNLIMITED);
            }
            catch (LockObtainFailedException)
            {
                // 上次异常退出遗留的写锁：强制解锁后重试
                IndexWriter.Unlock(Dir);
                return new IndexWriter(Dir, analyzer, !exists, IndexWriter.MaxFieldLength.UNLIMITED);
            }
            catch (CorruptIndexException)
            {
                // 索引损坏：丢弃重建
                return new IndexWriter(Dir, analyzer, true, IndexWriter.MaxFieldLength.UNLIMITED);
            }
        }

        /// <summary>
        /// 建立索引。fullRebuild=true 时清空重建；否则按 (大小, 修改时间, 所属批次) 增量并删除已消失文件。
        /// 每个文档标记 batchId（一次导入对应一个批次，同一路径以最后一次扫描所属批次为准）；
        /// includeContent=false 时仅索引文件名。shouldContinue 返回 false 中断（已处理部分照常提交）。
        /// </summary>
        public IndexRunStats IndexFiles(
            IList<FileRecord> files,
            bool fullRebuild,
            string batchId,
            bool includeContent,
            FormatRouter router,
            Action<IndexProgress> progress,
            Func<bool> shouldContinue,
            int commitEvery,
            int maxChars,
            long maxFileBytes)
        {
            var stats = new IndexRunStats();
            var existing = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
            if (!fullRebuild)
            {
                IndexReader snapshot = EnsureReader();
                if (snapshot != null) LoadStamps(snapshot, existing);
            }

            IndexWriter writer = OpenWriter();
            try
            {
                if (fullRebuild) writer.DeleteAll();

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int sinceCommit = 0;
                int total = files.Count;
                for (int i = 0; i < total; i++)
                {
                    if (shouldContinue != null && !shouldContinue()) break;
                    FileRecord f = files[i];
                    seen.Add(f.FullPath);

                    bool changed;
                    FileStamp stamp;
                    if (fullRebuild || !existing.TryGetValue(f.FullPath, out stamp))
                    {
                        changed = true;
                    }
                    else
                    {
                        changed = stamp.Length != f.Length
                            || stamp.LastWriteUtcTicks != f.LastWriteTimeUtc.Ticks
                            || stamp.Batch != (batchId ?? string.Empty);
                    }

                    if (!changed)
                    {
                        stats.Skipped++;
                        Report(progress, i + 1, total, f.FullPath);
                        continue;
                    }

                    IndexOne(writer, router, f, stats, existing, fullRebuild, batchId, includeContent, maxChars, maxFileBytes);
                    Report(progress, i + 1, total, f.FullPath);
                    if (++sinceCommit >= commitEvery)
                    {
                        writer.Commit();
                        sinceCommit = 0;
                    }
                }

                if (!fullRebuild)
                {
                    var gone = new List<string>();
                    foreach (KeyValuePair<string, FileStamp> kv in existing)
                    {
                        if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
                    }
                    foreach (string p in gone)
                    {
                        writer.DeleteDocuments(new Term("path", p));
                    }
                    stats.Removed = gone.Count;
                }

                writer.Commit();
            }
            finally
            {
                writer.Dispose();
            }

            ReopenReader();
            stats.TotalDocs = TotalDocs;
            stats.Completed = true;
            return stats;
        }

        private void IndexOne(
            IndexWriter writer,
            FormatRouter router,
            FileRecord f,
            IndexRunStats stats,
            Dictionary<string, FileStamp> existing,
            bool fullRebuild,
            string batchId,
            bool includeContent,
            int maxChars,
            long maxFileBytes)
        {
            string text = string.Empty;
            string label = "其他";
            ITextExtractor extractor = router.Route(f.Extension);

            if (!includeContent)
            {
                // 仅索引文件名
            }
            else if (extractor != null)
            {
                label = extractor.GetFileTypeLabel(f.Extension);
                if (f.Length > maxFileBytes)
                {
                    // 超大文件仅索引文件名（风险表策略）
                    stats.Failed++;
                    stats.Errors.Add(f.FullPath + "：文件超过单文件上限，仅索引文件名");
                }
                else
                {
                    try
                    {
                        using (FileStream fs = new FileStream(f.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            string error;
                            string extracted;
                            if (extractor.TryExtract(fs, maxChars, out extracted, out error))
                            {
                                text = extracted ?? string.Empty;
                            }
                            else
                            {
                                stats.Failed++;
                                stats.Errors.Add(f.FullPath + "：" + error);
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        stats.Failed++;
                        stats.Errors.Add(f.FullPath + "：" + ex.Message);
                        return;
                    }
                }
            }
            else
            {
                stats.Unsupported++;
            }

            var doc = new Document();
            doc.Add(new Field("path", f.FullPath, Field.Store.YES, Field.Index.NOT_ANALYZED));
            doc.Add(new Field("filename", f.Name, Field.Store.YES, Field.Index.ANALYZED));
            doc.Add(new Field("filetype", label, Field.Store.YES, Field.Index.NOT_ANALYZED));
            doc.Add(new Field("batch", batchId ?? string.Empty, Field.Store.YES, Field.Index.NOT_ANALYZED));
            doc.Add(new Field("size", f.Length.ToString(CultureInfo.InvariantCulture), Field.Store.YES, Field.Index.NOT_ANALYZED));
            doc.Add(new Field("modified", f.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), Field.Store.YES, Field.Index.NOT_ANALYZED));
            doc.Add(new Field("content", text, Field.Store.NO, Field.Index.ANALYZED));

            if (fullRebuild)
            {
                writer.AddDocument(doc);
                stats.Added++;
            }
            else
            {
                writer.UpdateDocument(new Term("path", f.FullPath), doc);
                if (existing.ContainsKey(f.FullPath)) stats.Updated++;
                else stats.Added++;
            }
        }

        private static void Report(Action<IndexProgress> progress, int processed, int total, string currentFile)
        {
            if (progress == null) return;
            var p = new IndexProgress { Processed = processed, Total = total, CurrentFile = currentFile };
            progress(p);
        }

        /// <summary>
        /// 多字段（filename + content）检索，默认 OR 评分排序；batchId 非空时限定批次范围。
        /// 查询语法错误时自动转义重试。
        /// </summary>
        public IList<SearchResultItem> Search(string queryText, int maxResults, string batchId)
        {
            var empty = new SearchResultItem[0];
            if (string.IsNullOrEmpty(queryText) || queryText.Trim().Length == 0) return empty;
            IndexReader r = EnsureReader();
            if (r == null) return empty;

            Analyzer analyzer = new CjkBigramAnalyzer();
            var parser = new MultiFieldQueryParser(
                Lucene.Net.Util.Version.LUCENE_30, new[] { "filename", "content" }, analyzer);
            // 默认 OR：按评分排序，命中词越多的文档越靠前；AND 会要求所有字段同时命中

            Query query;
            try
            {
                query = parser.Parse(queryText);
            }
            catch (ParseException)
            {
                try { query = parser.Parse(QueryParser.Escape(queryText.Trim())); }
                catch (Exception) { return empty; }
            }

            Filter filter = string.IsNullOrEmpty(batchId)
                ? null
                : new QueryWrapperFilter(new TermQuery(new Term("batch", batchId)));

            var searcher = new IndexSearcher(r);
            try
            {
                TopDocs hits = searcher.Search(query, filter, maxResults);
                var results = new List<SearchResultItem>(hits.ScoreDocs.Length);
                foreach (ScoreDoc sd in hits.ScoreDocs)
                {
                    Document d = searcher.Doc(sd.Doc);
                    var item = new SearchResultItem();
                    item.Path = d.Get("path") ?? string.Empty;
                    item.FileName = d.Get("filename") ?? string.Empty;
                    item.FileType = d.Get("filetype") ?? string.Empty;
                    long size;
                    long.TryParse(d.Get("size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                    item.Size = size;
                    long ticks;
                    long.TryParse(d.Get("modified"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks);
                    item.Modified = ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime() : DateTime.MinValue;
                    item.Score = sd.Score;
                    results.Add(item);
                }
                return results;
            }
            finally
            {
                searcher.Dispose();
            }
        }

        /// <summary>删除指定批次的全部文档并提交。</summary>
        public void DeleteBatch(string batchId)
        {
            if (string.IsNullOrEmpty(batchId)) return;
            IndexWriter writer = OpenWriter();
            try
            {
                writer.DeleteDocuments(new Term("batch", batchId));
                writer.Commit();
            }
            finally
            {
                writer.Dispose();
            }
            ReopenReader();
        }

        /// <summary>清空全部索引并提交（用于"一键清除全部索引"）。</summary>
        public void ClearAll()
        {
            IndexWriter writer = OpenWriter();
            try
            {
                writer.DeleteAll();
                writer.Commit();
            }
            finally
            {
                writer.Dispose();
            }
            ReopenReader();
        }

        /// <summary>某批次当前的文档数（用于索引管理列表）。走检索以排除标记删除的文档。</summary>
        public long DocCountForBatch(string batchId)
        {
            if (string.IsNullOrEmpty(batchId)) return 0;
            IndexReader r = EnsureReader();
            if (r == null) return 0;
            var searcher = new IndexSearcher(r);
            try
            {
                return searcher.Search(new TermQuery(new Term("batch", batchId)), null, 1).TotalHits;
            }
            finally
            {
                searcher.Dispose();
            }
        }

        public void Dispose()
        {
            if (reader != null)
            {
                try { reader.Dispose(); } catch (Exception) { }
                reader = null;
            }
            if (directory != null)
            {
                try { directory.Dispose(); } catch (Exception) { }
                directory = null;
            }
        }
    }
}

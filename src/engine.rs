//! 任务编排引擎：将 扫描 → 提取 → 索引 / 敏感扫描 串联为可取消、可汇报进度的流程。
//! 供 GUI 后台线程与 CLI 静默模式共用。

use crate::extract::extract_text;
use crate::indexer::Indexer;
use crate::report::ReportRow;
use crate::rules::SensitiveRule;
use crate::scanner::{scan_files, FileMeta, ScanOptions};
use crate::sensitive::{scan_file, SensitiveHit};
use crossbeam_channel::bounded;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

/// 共享进度状态。
pub struct Progress {
    pub discovered: AtomicU64,
    pub indexed: AtomicU64,
    pub current: Mutex<String>,
    pub cancelled: AtomicBool,
}

impl Progress {
    pub fn new() -> Self {
        Self {
            discovered: AtomicU64::new(0),
            indexed: AtomicU64::new(0),
            current: Mutex::new(String::new()),
            cancelled: AtomicBool::new(false),
        }
    }
    pub fn is_cancelled(&self) -> bool {
        self.cancelled.load(Ordering::Relaxed)
    }
    pub fn set_current(&self, s: String) {
        if let Ok(mut c) = self.current.lock() {
            *c = s;
        }
    }
}

/// 索引扫描：把根系下文件写入 Tantivy 索引。
/// 返回 (发现的文件数, 索引文档数)。
pub fn index_scan(
    roots: &[PathBuf],
    opts: &ScanOptions,
    data_dir: &Path,
    progress: Arc<Progress>,
) -> Result<(usize, u64), String> {
    // 1) 收集元数据
    let mut metas: Vec<FileMeta> = Vec::new();
    scan_files(roots, opts, |m| {
        progress.discovered.fetch_add(1, Ordering::Relaxed);
        progress.set_current(m.path.to_string_lossy().to_string());
        if !progress.is_cancelled() {
            metas.push(m);
        }
    });

    if progress.is_cancelled() {
        return Ok((0, 0));
    }

    // 索引线程：消费提取后的内容，写入 Tantivy 并周期性 commit。
    let indexer = Indexer::open(data_dir, "local")?;
    let mut writer = indexer.writer()?;
    let (tx, rx) = bounded::<(usize, FileMeta, String)>(64);
    let consumer = {
        let progress = Arc::clone(&progress);
        std::thread::spawn(move || -> Result<usize, String> {
            let mut written = 0usize;
            while let Ok((idx, meta, content)) = rx.recv() {
                let _ = idx;
                if progress.is_cancelled() {
                    continue;
                }
                indexer.add_with_content(&mut writer, &meta, &content)?;
                written += 1;
                progress.indexed.store(written as u64, Ordering::Relaxed);
                // 每 500 条 commit 一次，控制内存并支持部分结果
                if written % 500 == 0 {
                    writer.commit().map_err(|e| e.to_string())?;
                }
            }
            writer.commit().map_err(|e| e.to_string())?;
            Ok(written)
        })
    };

    // 2) 并行提取内容并送入索引线程
    let cancelled = Arc::clone(&progress);
    let num_cores = std::thread::available_parallelism()
        .map(|n| n.get())
        .unwrap_or(1);
    let pool = rayon::ThreadPoolBuilder::new()
        .num_threads(num_cores.max(1))
        .build()
        .map_err(|e| e.to_string())?;

    pool.install(|| {
        use rayon::prelude::*;
        metas
            .par_iter()
            .take_any_while(|_| !cancelled.is_cancelled())
            .for_each_with(&tx, |tx, meta| {
                let content = extract_text(&meta.path);
                let _ = tx.send((0usize, meta.clone(), content));
            });
    });

    // 关闭发送端，等待索引线程完成
    drop(tx);
    let written = consumer.join().map_err(|_| "索引线程异常退出".to_string())??;

    Ok((metas.len(), written as u64))
}

/// 敏感扫描：并行扫描文件名 + 内容，返回命中项。仅返回命中，不建索引。
pub fn sensitive_scan(
    roots: &[PathBuf],
    opts: &ScanOptions,
    rules: &[SensitiveRule],
    progress: Arc<Progress>,
    filenames_only: bool,
) -> Result<Vec<SensitiveHit>, String> {
    let mut metas: Vec<FileMeta> = Vec::new();
    scan_files(roots, opts, |m| {
        progress.discovered.fetch_add(1, Ordering::Relaxed);
        progress.set_current(m.path.to_string_lossy().to_string());
        if !progress.is_cancelled() {
            metas.push(m);
        }
    });

    let cancelled = Arc::clone(&progress);
    let hits: Vec<SensitiveHit> = {
        let num_cores = std::thread::available_parallelism()
            .map(|n| n.get())
            .unwrap_or(1);
        let pool = rayon::ThreadPoolBuilder::new()
            .num_threads(num_cores.max(1))
            .build()
            .map_err(|e| e.to_string())?;
        pool.install(|| {
            use rayon::prelude::*;
            metas
                .par_iter()
                .take_any_while(|_| !cancelled.is_cancelled())
                .filter_map(|m| {
                    let content = if filenames_only || !crate::extract::is_textual_ext(&m.ext) {
                        String::new()
                    } else {
                        extract_text(&m.path)
                    };
                    let file_hits = scan_file(
                        &m.path.to_string_lossy(),
                        &m.filename,
                        &content,
                        rules,
                    );
                    cancelled.indexed.fetch_add(1, Ordering::Relaxed);
                    if file_hits.is_empty() {
                        None
                    } else {
                        Some((m, file_hits))
                    }
                })
                .flat_map_iter(|(_, hits)| {
                    hits.into_iter().map(move |h| SensitiveHit {
                        path: h.path,
                        filename: h.filename,
                        rule_id: h.rule_id,
                        rule_name: h.rule_name,
                        matched: h.matched,
                        count: h.count,
                        in_filename: h.in_filename,
                    })
                })
                .collect()
        })
    };

    let _ = &metas;
    Ok(hits)
}

/// 把命中项转换为报告行。
pub fn hits_to_report(hits: &[SensitiveHit]) -> Vec<ReportRow> {
    hits.iter()
        .map(|h| ReportRow {
            path: h.path.clone(),
            filename: h.filename.clone(),
            kind: "sensitive".to_string(),
            detail: h.rule_name.clone(),
            matched: h.matched.clone(),
            count: h.count as u64,
            size: 0,
            modified_ms: 0,
            score: 0.0,
        })
        .collect()
}

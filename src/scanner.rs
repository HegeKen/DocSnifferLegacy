//! 文件扫描：基于 `walkdir` 递归遍历，跳过系统/敏感目录，读取元数据。
//! 弱机（单核）上由调用方决定线程数；此处负责“发现文件”并产出元数据流。

use crate::config::SKIP_EXTS;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};
use walkdir::WalkDir;

/// 单个文件的元数据。
#[derive(Debug, Clone)]
pub struct FileMeta {
    pub path: PathBuf,
    pub filename: String,
    pub ext: String,
    pub size: u64,
    pub modified_ms: i64,
}

/// 扫描选项。
#[derive(Debug, Clone)]
pub struct ScanOptions {
    /// 额外跳过的目录名（默认含系统目录，见 config::DEFAULT_SKIP_DIRS）。
    pub skip_dirs: Vec<String>,
    /// 是否跳过已知无意义扩展名（exe/dll/bin 等）。
    pub skip_ext: bool,
}

impl Default for ScanOptions {
    fn default() -> Self {
        Self {
            skip_dirs: crate::config::DEFAULT_SKIP_DIRS
                .iter()
                .map(|s| s.to_string())
                .collect(),
            skip_ext: true,
        }
    }
}

/// 递归扫描 `roots` 下的文件，对每个文件回调 `on_file`，返回发现的文件总数。
/// 通过 `filter_entry` 在遍历阶段剪枝，避免进入系统目录触发权限弹窗。
pub fn scan_files<F>(roots: &[PathBuf], opts: &ScanOptions, mut on_file: F) -> usize
where
    F: FnMut(FileMeta),
{
    let skip_set: Vec<String> = opts
        .skip_dirs
        .iter()
        .map(|s| s.to_ascii_lowercase())
        .collect();

    let mut count = 0usize;
    for root in roots {
        if !root.is_dir() {
            continue;
        }
        let walker = WalkDir::new(root)
            .follow_links(false)
            .into_iter()
            .filter_entry(|e| {
                // 允许根目录进入；非根目录命中 skip 集合则剪枝。
                if e.path() == root {
                    return true;
                }
                if e.file_type().is_dir() {
                    if let Some(name) = e.file_name().to_str() {
                        return !skip_set.contains(&name.to_ascii_lowercase());
                    }
                }
                true
            });

        for entry in walker.flatten() {
            let meta = match entry.metadata() {
                Ok(m) => m,
                Err(_) => continue,
            };
            if !meta.is_file() {
                continue;
            }
            let path = entry.path();
            let ext = path
                .extension()
                .and_then(|e| e.to_str())
                .unwrap_or("")
                .to_ascii_lowercase();
            if opts.skip_ext && is_skip_ext(&ext) {
                continue;
            }
            let filename = path
                .file_name()
                .and_then(|f| f.to_str())
                .unwrap_or("")
                .to_string();

            let fe = FileMeta {
                path: path.to_path_buf(),
                filename,
                ext,
                size: meta.len(),
                modified_ms: time_to_millis(meta.modified().ok()),
            };
            on_file(fe);
            count += 1;
        }
    }
    count
}

fn is_skip_ext(ext: &str) -> bool {
    let e = ext.trim_start_matches('.');
    SKIP_EXTS.contains(&e)
}

/// 将 SystemTime 转为毫秒时间戳（旧系统缺 GetTickCount64，使用 EPOCH 偏移即可）。
fn time_to_millis(t: Option<SystemTime>) -> i64 {
    t.and_then(|t| t.duration_since(UNIX_EPOCH).ok())
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

/// 便捷工具：判断路径是否已是绝对路径或存在。
pub fn normalize_root(p: &str) -> PathBuf {
    Path::new(p).to_path_buf()
}

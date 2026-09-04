//! 报告导出：扫描/搜索结果导出为 CSV（兼容 Excel 2003）或 JSON。

use serde::{Deserialize, Serialize};
use std::path::Path;

/// 统一报告行。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ReportRow {
    pub path: String,
    pub filename: String,
    /// 来源类别：search / sensitive
    pub kind: String,
    /// 关联规则名或搜索词
    pub detail: String,
    /// 匹配到的文本片段
    pub matched: String,
    pub count: u64,
    pub size: u64,
    pub modified_ms: i64,
    pub score: f64,
}

/// 导出为 CSV（带 UTF-8 BOM，便于老旧 Excel 正确识别中文）。
pub fn export_csv(path: &Path, rows: &[ReportRow]) -> Result<(), String> {
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    }
    let mut w = csv::WriterBuilder::new()
        .has_headers(true)
        .from_path(path)
        .map_err(|e| e.to_string())?;
    w.write_record(["路径", "文件名", "类别", "规则/关键词", "匹配片段", "次数", "大小", "修改时间", "分数"])
        .map_err(|e| e.to_string())?;
    for r in rows {
        w.write_record([
            &r.path,
            &r.filename,
            &r.kind,
            &r.detail,
            &r.matched,
            &r.count.to_string(),
            &r.size.to_string(),
            &r.modified_ms.to_string(),
            &format!("{:.4}", r.score),
        ])
        .map_err(|e| e.to_string())?;
    }
    w.flush().map_err(|e| e.to_string())
}

/// 导出为 JSON。
pub fn export_json(path: &Path, rows: &[ReportRow]) -> Result<(), String> {
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    }
    let json = serde_json::to_string_pretty(rows).map_err(|e| e.to_string())?;
    std::fs::write(path, json).map_err(|e| e.to_string())
}

/// 按扩展名决定导出格式（csv/json）。
pub fn export_by_ext(path: &Path, rows: &[ReportRow]) -> Result<(), String> {
    match path
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("csv")
        .to_ascii_lowercase()
        .as_str()
    {
        "json" => export_json(path, rows),
        _ => export_csv(path, rows),
    }
}

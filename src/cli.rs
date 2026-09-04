//! 命令行静默模式：无界面运行，适合批量终端自查（README §6.3）。
//! 示例：
//!   docsniffer_legacy.exe --scan D:\ --export report.csv --silent
//!   docsniffer_legacy.exe --scan C:\ --rules custom_rules.json --export result.json

use crate::config;
use crate::engine::{sensitive_scan, Progress};
use crate::report::{export_by_ext, ReportRow};
use crate::rules;
use crate::scanner::ScanOptions;
use crate::sys;
use std::path::{Path, PathBuf};

/// 解析后的命令行参数。
#[derive(Debug, Default)]
pub struct CliArgs {
    pub scan: Option<String>,
    pub rules: Option<String>,
    pub export: Option<String>,
    pub silent: bool,
    pub help: bool,
    pub version: bool,
}

pub fn parse_args(args: &[String]) -> CliArgs {
    let mut a = CliArgs::default();
    let mut i = 0usize;
    while i < args.len() {
        let arg = &args[i];
        match arg.as_str() {
            "--scan" | "-s" => {
                i += 1;
                if let Some(v) = args.get(i) {
                    a.scan = Some(v.clone());
                }
            }
            "--rules" | "-r" => {
                i += 1;
                if let Some(v) = args.get(i) {
                    a.rules = Some(v.clone());
                }
            }
            "--export" | "-e" => {
                i += 1;
                if let Some(v) = args.get(i) {
                    a.export = Some(v.clone());
                }
            }
            "--silent" => a.silent = true,
            "--help" | "-h" => a.help = true,
            "--version" | "-V" => a.version = true,
            _ => {}
        }
        i += 1;
    }
    a
}

pub fn usage() -> String {
    format!(
        "{} - 轻量级敏感文件嗅探工具 (Legacy Edition)\n\
         \n\
         用法:\n\
         \x20  默认（无参数）    启动图形界面\n\
         \x20  --scan <路径>     静默扫描指定路径（敏感自查）\n\
         \x20  --rules <文件>    指定自定义规则库 (rules.json)（可选，默认内嵌规则）\n\
         \x20  --export <文件>   导出报告（.csv 或 .json）（可选）\n\
         \x20  --silent          无界面运行\n\
         \x20  --help            显示帮助\n\
         \x20  --version         显示版本\n\
         \n\
         示例:\n\
         \x20  docsniffer_legacy.exe --scan D:\\ --export report.csv --silent\n\
         \x20  docsniffer_legacy.exe --scan C:\\ --rules rules.json --export result.json\n",
        config::APP_NAME
    )
}

/// 执行 CLI 静默扫描，返回进程退出码。
pub fn run(args: &CliArgs) -> i32 {
    let exe_dir = config::exe_dir();
    let data_dir = config::data_dir(&exe_dir);
    if let Err(e) = std::fs::create_dir_all(&data_dir) {
        eprintln!("无法创建数据目录 {}: {e}", data_dir.display());
        return 2;
    }

    // 静默扫描时把进程优先级调低，避免影响前台业务。
    let _ = sys::set_below_normal_priority();

    let root = match &args.scan {
        Some(p) => normalize_root(p),
        None => {
            eprintln!("错误：--scan 需要指定扫描路径。\n\n{}", usage());
            return 2;
        }
    };

    let loaded_rules = match &args.rules {
        Some(f) => load_rules_file(Path::new(f)),
        None => rules::load_rules(&data_dir),
    };

    println!("开始敏感扫描: {}", root.display());
    let progress = std::sync::Arc::new(Progress::new());
    let opts = ScanOptions::default();
    let roots = vec![root];

    let hits = match sensitive_scan(&roots, &opts, &loaded_rules, progress, false) {
        Ok(h) => h,
        Err(e) => {
            eprintln!("扫描失败: {e}");
            return 1;
        }
    };

    let mut printed = 0usize;
    for h in &hits {
        if printed >= 200 {
            break;
        }
        println!(
            "[{}] {} | 规则: {} | 命中: {}",
            if h.in_filename { "文件名" } else { "内容" },
            h.path,
            h.rule_name,
            h.matched
        );
        printed += 1;
    }
    println!();
    println!("扫描完成：共 {} 个命中。", hits.len());

    // 导出报告
    if let Some(export) = &args.export {
        let rows: Vec<ReportRow> = hits
            .iter()
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
            .collect();
        match export_by_ext(Path::new(export), &rows) {
            Ok(_) => println!("报告已导出: {export}"),
            Err(e) => {
                eprintln!("导出报告失败: {e}");
                return 1;
            }
        }
    }

    0
}

fn normalize_root(p: &str) -> PathBuf {
    let pb = PathBuf::from(p);
    if pb.is_absolute() {
        pb
    } else {
        std::env::current_dir()
            .map(|cwd| cwd.join(&pb))
            .unwrap_or(pb)
    }
}

fn load_rules_file(path: &Path) -> Vec<rules::SensitiveRule> {
    match std::fs::read_to_string(path) {
        Ok(s) => match serde_json::from_str::<Vec<rules::SensitiveRule>>(&s) {
            Ok(r) if !r.is_empty() => r,
            _ => {
                eprintln!("警告：规则文件解析失败或为空，改用默认规则库。");
                rules::default_rules()
            }
        },
        Err(e) => {
            eprintln!("警告：无法读取规则文件 {}: {e}，改用默认规则库。", path.display());
            rules::default_rules()
        }
    }
}

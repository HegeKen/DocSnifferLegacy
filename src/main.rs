//! DocSniffer Legacy Edition 二进制入口。
//!
//! 入口分发逻辑：
//! - 不带参数          → 启动 egui 图形界面
//! - `--help` / `-h`   → 打印帮助后退出
//! - `--version` / `-V`→ 打印版本后退出
//! - `--scan <路径>`   → 进入命令行静默扫描（可用 `--rules` / `--export` / `--silent`）
//!
//! 引擎与界面统一复用 `docsniffer_legacy` crate 的各模块，保证 GUI 与 CLI 行为一致。

use docsniffer_legacy::app::DocSnifferApp;
use docsniffer_legacy::cli;
use docsniffer_legacy::config;
use docsniffer_legacy::settings::Settings;
use eframe::egui;

/// 默认窗口初始尺寸。
const DEFAULT_WIN_W: f32 = 1100.0;
const DEFAULT_WIN_H: f32 = 720.0;

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let parsed = cli::parse_args(&args);

    if parsed.help {
        print!("{}", cli::usage());
        return;
    }
    if parsed.version {
        println!("{} v{}", config::APP_NAME, env!("CARGO_PKG_VERSION"));
        return;
    }

    // 命令行静默模式：扫描、导出、规则库任意之一出现即走 CLI。
    if parsed.scan.is_some() || parsed.export.is_some() || parsed.rules.is_some() {
        let rc = cli::run(&parsed);
        std::process::exit(rc);
    }

    // 默认启动图形界面。
    run_gui();
}

/// 以 egui 图形界面方式启动。
fn run_gui() {
    let exe_dir = config::exe_dir();
    let data_dir = config::data_dir(&exe_dir);
    // 确保数据目录存在（第一次运行会在 %APPDATA% 或便携 Data 下创建）。
    let _ = std::fs::create_dir_all(&data_dir);
    let settings = Settings::load(&data_dir);
    // 以低优先级运行，避免影响前台业务系统。
    let _ = docsniffer_legacy::sys::set_below_normal_priority();

    let native_options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_title(config::APP_NAME)
            .with_inner_size([DEFAULT_WIN_W, DEFAULT_WIN_H])
            .with_min_inner_size([760.0, 480.0]),
        ..Default::default()
    };

    let result = eframe::run_native(
        config::APP_NAME,
        native_options,
        Box::new(move |cc| Ok(Box::new(DocSnifferApp::new(cc, data_dir, settings)))),
    );
    if let Err(e) = result {
        eprintln!("程序启动失败: {e}");
        std::process::exit(1);
    }
}

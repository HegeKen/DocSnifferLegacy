//! egui 图形界面：三栏式布局（目录树 | 搜索栏 | 结果列表）。
//! 支持后台线程扫描/索引/敏感扫描，通过共享进度对象汇报，结果经 mpsc 回传。

use crate::config;
use crate::engine::{index_scan, sensitive_scan, Progress};
use crate::indexer::{Indexer, SearchHit, SearchMode};
use crate::report::{export_by_ext, ReportRow};
use crate::rules::{self, SensitiveRule};
use crate::scanner::ScanOptions;
use crate::settings::{Settings, ThemeChoice};
use crate::sensitive::SensitiveHit;
use eframe::egui;
use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::atomic::Ordering;
use std::sync::mpsc::{self, Receiver};
use std::sync::Arc;
use std::thread::JoinHandle;

/// 后台任务完成事件。
enum JobResult {
    Indexed(Result<(usize, u64), String>),
    Sensitive(Result<Vec<SensitiveHit>, String>),
}

pub struct DocSnifferApp {
    data_dir: PathBuf,
    settings: Settings,
    scan_opts: ScanOptions,
    roots: Vec<PathBuf>,
    rules: Vec<SensitiveRule>,

    // 目录树缓存
    tree_cache: HashMap<PathBuf, Vec<PathBuf>>,

    // 搜索状态
    query: String,
    mode: SearchMode,
    results: Vec<SearchHit>,
    hits: Vec<SensitiveHit>,
    search_stats: String,

    // 后台任务
    busy: bool,
    progress: Option<Arc<Progress>>,
    receiver: Option<Receiver<JobResult>>,
    handle: Option<JoinHandle<()>>,

    // 界面状态
    request_focus_search: bool,
    show_rules: bool,
    export_path: String,
    status: String,
}

/// 尝试加载系统中文字体并注册为 egui 后备字体，避免中文显示为方框（tofu）。
/// 依次尝试常见中文系统字体，读取首个存在的字体文件；全部失败则保持默认（仅拉丁）。
fn setup_cjk_fonts(ctx: &egui::Context) {
    const CANDIDATES: &[&str] = &[
        "C:\\Windows\\Fonts\\msyh.ttc",   // 微软雅黑（Vista+）
        "C:\\Windows\\Fonts\\simsun.ttc", // 宋体（XP+）
        "C:\\Windows\\Fonts\\simhei.ttf", // 黑体
        "C:\\Windows\\Fonts\\simfang.ttf",// 仿宋
        "C:\\Windows\\Fonts\\simkai.ttf", // 楷体
    ];
    let mut bytes: Option<Vec<u8>> = None;
    for p in CANDIDATES {
        if let Ok(b) = std::fs::read(p) {
            bytes = Some(b);
            break;
        }
    }
    let Some(bytes) = bytes else { return };

    let mut fonts = egui::FontDefinitions::default();
    // 作为后备字体追加到各字体族末尾：拉丁沿用默认，中文由 CJK 字体补全。
    fonts
        .font_data
        .insert("cjk".to_owned(), egui::FontData::from_owned(bytes));
    for family in [egui::FontFamily::Proportional, egui::FontFamily::Monospace] {
        if let Some(list) = fonts.families.get_mut(&family) {
            list.push("cjk".to_owned());
        }
    }
    ctx.set_fonts(fonts);
}

impl DocSnifferApp {
    pub fn new(cc: &eframe::CreationContext<'_>, data_dir: PathBuf, settings: Settings) -> Self {
        cc.egui_ctx.set_visuals(theme_visuals(settings.theme));
        setup_cjk_fonts(&cc.egui_ctx);
        let rules = rules::load_rules(&data_dir);
        Self {
            data_dir,
            settings,
            scan_opts: ScanOptions::default(),
            roots: default_tree_roots(),
            rules,
            tree_cache: HashMap::new(),
            query: String::new(),
            mode: SearchMode::Both,
            results: Vec::new(),
            hits: Vec::new(),
            search_stats: String::new(),
            busy: false,
            progress: None,
            receiver: None,
            handle: None,
            request_focus_search: false,
            show_rules: false,
            export_path: String::new(),
            status: "就绪".to_string(),
        }
    }

    /// 读取目录子项（带缓存）。
    fn list_children(&mut self, dir: &PathBuf) -> Vec<PathBuf> {
        if let Some(v) = self.tree_cache.get(dir) {
            return v.clone();
        }
        let mut out = Vec::new();
        if let Ok(rd) = std::fs::read_dir(dir) {
            for e in rd.flatten() {
                let p = e.path();
                // 跳过系统目录，避免旧系统权限弹窗
                if let Some(name) = p.file_name().and_then(|n| n.to_str()) {
                    let lower = name.to_ascii_lowercase();
                    if config::DEFAULT_SKIP_DIRS.iter().any(|s| s.eq_ignore_ascii_case(&lower)) && p.is_dir() {
                        continue;
                    }
                }
                out.push(p);
            }
            out.sort_by(|a, b| a.file_name().cmp(&b.file_name()));
        }
        self.tree_cache.insert(dir.clone(), out.clone());
        out
    }

    fn start_index(&mut self) {
        if self.busy || self.roots.is_empty() {
            return;
        }
        self.results.clear();
        self.hits.clear();
        self.search_stats.clear();
        self.status = "正在索引…".to_string();

        let progress = Arc::new(Progress::new());
        let (tx, rx) = mpsc::channel();
        let data_dir = self.data_dir.clone();
        let roots = self.roots.clone();
        let opts = self.scan_opts.clone();
        let p = Arc::clone(&progress);
        let handle = std::thread::spawn(move || {
            let res = index_scan(&roots, &opts, &data_dir, p);
            let _ = tx.send(JobResult::Indexed(res));
        });
        self.progress = Some(progress);
        self.receiver = Some(rx);
        self.handle = Some(handle);
        self.busy = true;
    }

    fn start_sensitive(&mut self, filenames_only: bool) {
        if self.busy || self.roots.is_empty() {
            return;
        }
        self.results.clear();
        self.hits.clear();
        self.search_stats.clear();
        self.status = if filenames_only {
            "正在按文件名敏感扫描…".to_string()
        } else {
            "正在敏感扫描…".to_string()
        };

        let progress = Arc::new(Progress::new());
        let (tx, rx) = mpsc::channel();
        let roots = self.roots.clone();
        let opts = self.scan_opts.clone();
        let rules = self.rules.clone();
        let p = Arc::clone(&progress);
        let handle = std::thread::spawn(move || {
            let res = sensitive_scan(&roots, &opts, &rules, p, filenames_only);
            let _ = tx.send(JobResult::Sensitive(res));
        });
        self.progress = Some(progress);
        self.receiver = Some(rx);
        self.handle = Some(handle);
        self.busy = true;
    }

    fn cancel(&mut self) {
        if let Some(p) = &self.progress {
            p.cancelled.store(true, Ordering::Relaxed);
            self.status = "已请求取消".to_string();
        }
    }

    /// 每帧轮询后台任务结果。
    fn poll_job(&mut self) {
        let jobs_done = if let Some(rx) = &self.receiver {
            rx.try_recv().is_ok()
        } else {
            false
        };
        if jobs_done {
            if let Some(rx) = self.receiver.take() {
                match rx.try_recv() {
                    Ok(JobResult::Indexed(res)) => match res {
                        Ok((discovered, indexed)) => {
                            self.status = format!("索引完成：发现 {discovered} 个文件，写入 {indexed} 条。");
                        }
                        Err(e) => self.status = format!("索引失败：{e}"),
                    },
                    Ok(JobResult::Sensitive(res)) => match res {
                        Ok(hits) => {
                            let count = hits.len();
                            self.hits = hits;
                            self.search_stats = format!("敏感命中 {count} 项");
                            self.status = if count > 0 {
                                format!("敏感扫描完成，共命中 {count} 项。")
                            } else {
                                "敏感扫描完成，未发现命中。".to_string()
                            };
                        }
                        Err(e) => self.status = format!("敏感扫描失败：{e}"),
                    },
                    Err(_) => {}
                }
            }
            if let Some(h) = self.handle.take() {
                let _ = h.join();
            }
            self.busy = false;
            self.progress = None;
        }
    }

    fn do_search(&mut self) {
        let data_dir = self.data_dir.clone();
        let shard = "local".to_string();
        let mode = self.mode;
        let query = self.query.clone();
        let limit = 500;
        match Indexer::open(&data_dir, &shard).and_then(|indexer| {
            indexer.search(&query, mode, limit)
        }) {
            Ok(hits) => {
                self.results = hits;
                self.search_stats = format!("找到 {} 条结果", self.results.len());
                self.status = format!("搜索完成：{} 条结果", self.results.len());
            }
            Err(e) => self.status = format!("搜索失败：{e}"),
        }
    }

    fn export_report(&mut self) {
        let path = PathBuf::from(if self.export_path.is_empty() {
            "report.csv".to_string()
        } else {
            self.export_path.clone()
        });
        let rows: Vec<ReportRow> = if !self.hits.is_empty() {
            self.hits
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
                .collect()
        } else {
            self.results
                .iter()
                .map(|r| ReportRow {
                    path: r.path.clone(),
                    filename: r.filename.clone(),
                    kind: "search".to_string(),
                    detail: self.query.clone(),
                    matched: r.matched.clone(),
                    count: 1,
                    size: r.size,
                    modified_ms: r.modified_ms,
                    score: r.score,
                })
                .collect()
        };
        match export_by_ext(&path, &rows) {
            Ok(_) => self.status = format!("报告已导出：{}", path.display()),
            Err(e) => self.status = format!("导出失败：{e}"),
        }
    }
}

impl eframe::App for DocSnifferApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        self.poll_job();

        // 键盘快捷键
        ctx.input(|input| {
            if input.modifiers.command && input.key_pressed(egui::Key::F) {
                self.request_focus_search = true;
            }
            if input.modifiers.command && input.key_pressed(egui::Key::E) {
                self.export_report();
            }
            if input.key_pressed(egui::Key::Escape) {
                self.cancel();
            }
        });

        self.toolbar(ctx);
        self.tree_panel(ctx);
        self.central_panel(ctx);
    }
}

impl DocSnifferApp {
    fn toolbar(&mut self, ctx: &egui::Context) {
        egui::TopBottomPanel::top("toolbar").show(ctx, |ui| {
            ui.add_space(4.0);
            ui.horizontal(|ui| {
                ui.label("[🔍]");
                let resp = ui.add(
                    egui::TextEdit::singleline(&mut self.query)
                        .hint_text("输入搜索词（支持 +/空格 AND/OR、双引号短语）")
                        .desired_width(280.0),
                );
                if self.request_focus_search {
                    resp.request_focus();
                    self.request_focus_search = false;
                }

                egui::ComboBox::from_id_source("mode")
                    .selected_text(match self.mode {
                        SearchMode::Filename => "文件名",
                        SearchMode::Content => "内容",
                        SearchMode::Both => "文件名+内容",
                    })
                    .show_ui(ui, |ui| {
                        ui.selectable_value(&mut self.mode, SearchMode::Filename, "文件名");
                        ui.selectable_value(&mut self.mode, SearchMode::Content, "内容");
                        ui.selectable_value(&mut self.mode, SearchMode::Both, "文件名+内容");
                    });

                if ui.button("搜索").clicked() {
                    self.do_search();
                }

                ui.separator();

                if ui.button("[📂] 选择路径").clicked() {
                    if let Some(folder) = rfd::FileDialog::new().pick_folder() {
                        if !self.roots.contains(&folder) {
                            self.roots.push(folder);
                        }
                    }
                }

                ui.separator();

                if ui
                    .add_enabled(!self.busy && !self.roots.is_empty(), egui::Button::new("[▶] 索引扫描"))
                    .clicked()
                {
                    self.start_index();
                }
                if ui
                    .add_enabled(!self.busy && !self.roots.is_empty(), egui::Button::new("[★] 敏感扫描"))
                    .clicked()
                {
                    self.start_sensitive(false);
                }
                if ui
                    .add_enabled(!self.busy && !self.roots.is_empty(), egui::Button::new("[☰] 文件名敏感"))
                    .clicked()
                {
                    self.start_sensitive(true);
                }
                if ui
                    .add_enabled(self.busy, egui::Button::new("[✕] 取消"))
                    .clicked()
                {
                    self.cancel();
                }

                ui.separator();

                if ui.button("[▣] 规则管理").clicked() {
                    self.show_rules = !self.show_rules;
                }
            });

            // 进度/状态栏
            ui.horizontal(|ui| {
                if self.busy {
                    let (disc, idx) = if let Some(p) = &self.progress {
                        (
                            p.discovered.load(Ordering::Relaxed),
                            p.indexed.load(Ordering::Relaxed),
                        )
                    } else {
                        (0, 0)
                    };
                    ui.label(format!("发现 {disc} | 处理 {idx}"));
                    ui.add_space(8.0);
                    ui.label("当前: ");
                    if let Some(p) = &self.progress {
                        if let Ok(c) = p.current.lock() {
                            ui.add(egui::Label::new(egui::RichText::new(c.as_str()).small()).truncate());
                        }
                    }
                }
            });
            ui.add_space(4.0);
        });
    }

    fn tree_panel(&mut self, ctx: &egui::Context) {
        egui::SidePanel::left("tree")
            .resizable(true)
            .default_width(240.0)
            .show(ctx, |ui| {
                ui.strong("扫描根目录");
                ui.horizontal(|ui| {
                    if ui.button("+ 添加").clicked() {
                        if let Some(folder) = rfd::FileDialog::new().pick_folder() {
                            if !self.roots.contains(&folder) {
                                self.roots.push(folder);
                                self.tree_cache.clear();
                            }
                        }
                    }
                    if ui.button("清空").clicked() {
                        self.roots.clear();
                        self.tree_cache.clear();
                    }
                });
                ui.separator();

                // 已选根目录列表
                let mut to_remove: Option<usize> = None;
                egui::ScrollArea::vertical().id_source("roots").show(ui, |ui| {
                    for (i, root) in self.roots.iter().enumerate() {
                        ui.horizontal(|ui| {
                            let name = root.to_string_lossy().to_string();
                            if ui.button("✕").clicked() {
                                to_remove = Some(i);
                            }
                            ui.monospace(&name);
                        });
                    }
                });
                if let Some(i) = to_remove {
                    self.roots.remove(i);
                }

                ui.separator();
                ui.strong("目录浏览");
                egui::ScrollArea::vertical().id_source("tree").auto_shrink([false; 2]).show(ui, |ui| {
                    let tree_roots = default_tree_roots();
                    for r in tree_roots {
                        self.tree_node(ui, &r);
                    }
                });

                ui.separator();
                ui.small(format!("数据目录: {}", self.data_dir.display()));
            });
    }

    fn tree_node(&mut self, ui: &mut egui::Ui, path: &PathBuf) {
        let label = path
            .file_name()
            .map(|f| f.to_string_lossy().to_string())
            .unwrap_or_else(|| path.to_string_lossy().to_string());
        let children = self.list_children(path);
        if children.is_empty() {
            ui.label(label);
        } else {
            let default_open = self.roots.iter().any(|r| r == path);
            egui::CollapsingHeader::new(label)
                .default_open(default_open)
                .show(ui, |ui| {
                    for c in children {
                        self.tree_node(ui, &c);
                    }
                });
        }
    }

    fn central_panel(&mut self, ctx: &egui::Context) {
        egui::CentralPanel::default().show(ctx, |ui| {
            // 顶部：结果显示模式提示
            ui.horizontal(|ui| {
                ui.strong("搜索结果");
                if !self.search_stats.is_empty() {
                    ui.small(&self.search_stats);
                }
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    ui.horizontal(|ui| {
                        ui.label("导出");
                        ui.add(egui::TextEdit::singleline(&mut self.export_path).desired_width(120.0));
                        if ui.add(egui::Button::new("导出报告")).clicked() {
                            self.export_report();
                        }
                    });
                });
            });
            ui.separator();

            // 结果表格（虚拟滚动）
            if !self.hits.is_empty() {
                self.sensitive_table(ui);
            } else {
                self.search_table(ui);
            }

            ui.separator();
            ui.small(&self.status);
        });

        if self.show_rules {
            self.rules_window(ctx);
        }

        // 应用设置字体
        self.apply_style(ctx);
    }

    fn search_table(&mut self, ui: &mut egui::Ui) {
        use egui_extras::{Column, TableBuilder};
        let n = self.results.len();
        let headers = ["路径", "文件名", "匹配片段", "分数"];
        TableBuilder::new(ui)
            .striped(true)
            .resizable(true)
            .cell_layout(egui::Layout::left_to_right(egui::Align::Center))
            .column(Column::initial(300.0).at_least(120.0).clip(true))
            .column(Column::initial(160.0).at_least(80.0).clip(true))
            .column(Column::remainder().clip(true))
            .column(Column::initial(60.0).at_least(50.0))
            .header(22.0, |mut header| {
                for h in headers {
                    header.col(|ui| {
                        ui.strong(h);
                    });
                }
            })
            .body(|body| {
                body.rows(22.0, n, |mut row| {
                    let i = row.index();
                    if let Some(r) = self.results.get(i) {
                        row.col(|ui| {
                            ui.label(&r.path).on_hover_text(&r.path);
                        });
                        row.col(|ui| {
                            ui.label(&r.filename);
                        });
                        row.col(|ui| {
                            ui.label(&r.matched);
                        });
                        row.col(|ui| {
                            ui.label(format!("{:.2}", r.score));
                        });
                    }
                });
            });
    }

    fn sensitive_table(&mut self, ui: &mut egui::Ui) {
        use egui_extras::{Column, TableBuilder};
        let n = self.hits.len();
        TableBuilder::new(ui)
            .striped(true)
            .resizable(true)
            .cell_layout(egui::Layout::left_to_right(egui::Align::Center))
            .column(Column::initial(300.0).at_least(120.0).clip(true))
            .column(Column::initial(120.0).at_least(70.0).clip(true))
            .column(Column::initial(120.0).at_least(60.0))
            .column(Column::initial(200.0).at_least(80.0).clip(true))
            .column(Column::initial(60.0).at_least(40.0))
            .header(22.0, |mut header| {
                for h in ["路径", "文件名", "规则", "命中文本", "次数"] {
                    header.col(|ui| {
                        ui.strong(h);
                    });
                }
            })
            .body(|body| {
                body.rows(22.0, n, |mut row| {
                    let i = row.index();
                    if let Some(h) = self.hits.get(i) {
                        row.col(|ui| {
                            ui.label(&h.path).on_hover_text(&h.path);
                        });
                        row.col(|ui| {
                            ui.label(&h.filename);
                        });
                        row.col(|ui| {
                            let tag = if h.in_filename { "文件名" } else { "内容" };
                            ui.label(format!("{} / {}", h.rule_name, tag));
                        });
                        row.col(|ui| {
                            ui.label(&h.matched);
                        });
                        row.col(|ui| {
                            ui.label(h.count.to_string());
                        });
                    }
                });
            });
    }

    fn rules_window(&mut self, ctx: &egui::Context) {
        let mut open = self.show_rules;
        egui::Window::new("规则管理")
            .open(&mut open)
            .default_size([520.0, 420.0])
            .show(ctx, |ui| {
                ui.horizontal(|ui| {
                    if ui.button("添加规则").clicked() {
                        self.rules.push(SensitiveRule {
                            id: format!("r{}", self.rules.len() + 1),
                            name: "新规则".to_string(),
                            kind: "keyword".to_string(),
                            pattern: String::new(),
                            scan_content: true,
                            enabled: true,
                        });
                    }
                    if ui.button("保存").clicked() {
                        match rules::save_rules(&self.data_dir, &self.rules) {
                            Ok(_) => self.status = "规则已保存".to_string(),
                            Err(e) => self.status = format!("保存失败：{e}"),
                        }
                    }
                    if ui.button("恢复默认").clicked() {
                        self.rules = rules::default_rules();
                    }
                });
                ui.separator();
                egui::ScrollArea::vertical().show(ui, |ui| {
                    let mut remove: Option<usize> = None;
                    for (i, rule) in self.rules.iter_mut().enumerate() {
                        ui.horizontal(|ui| {
                            if ui.button("✕").clicked() {
                                remove = Some(i);
                            }
                            ui.checkbox(&mut rule.enabled, "");
                            ui.add(
                                egui::TextEdit::singleline(&mut rule.name).desired_width(120.0),
                            );
                            egui::ComboBox::from_id_source(("kind", i))
                                .selected_text(if rule.kind == "regex" { "正则" } else { "关键词" })
                                .show_ui(ui, |ui| {
                                    ui.selectable_value(&mut rule.kind, "regex".to_string(), "正则");
                                    ui.selectable_value(&mut rule.kind, "keyword".to_string(), "关键词");
                                });
                            ui.add(
                                egui::TextEdit::singleline(&mut rule.pattern).hint_text("正则或关键词").desired_width(220.0),
                            );
                            ui.checkbox(&mut rule.scan_content, "内容");
                        });
                    }
                    if let Some(i) = remove {
                        self.rules.remove(i);
                    }
                });
            });
        self.show_rules = open;
    }

    fn apply_style(&mut self, ctx: &egui::Context) {
        ctx.style_mut(|style| {
            style.text_styles.get_mut(&egui::TextStyle::Body).map(|f| *f =
                egui::FontId::proportional(self.settings.font_size));
        });
        let _ = &self.settings.theme;
    }
}

fn theme_visuals(theme: ThemeChoice) -> egui::Visuals {
    let mut v = egui::Visuals::dark();
    match theme {
        ThemeChoice::Classic => {}
        ThemeChoice::Green => {
            v.selection.bg_fill = egui::Color32::from_rgb(38, 120, 60);
            v.hyperlink_color = egui::Color32::from_rgb(100, 200, 120);
        }
        ThemeChoice::Blue => {
            v.selection.bg_fill = egui::Color32::from_rgb(40, 80, 150);
            v.hyperlink_color = egui::Color32::from_rgb(120, 170, 240);
        }
    }
    v
}

/// 默认目录树根（Windows: 盘符；其他: / 与用户主目录）。
fn default_tree_roots() -> Vec<PathBuf> {
    let mut out = Vec::new();
    #[cfg(windows)]
    {
        for c in b'A'..=b'Z' {
            let letter = (c as char).to_string();
            let path = format!("{letter}:\\");
            if PathBuf::from(&path).exists() {
                out.push(PathBuf::from(path));
            }
        }
    }
    #[cfg(not(windows))]
    {
        out.push(PathBuf::from("/"));
        if let Some(home) = dirs::home_dir() {
            out.push(home);
        }
    }
    if out.is_empty() {
        out.push(PathBuf::from("/"));
    }
    out
}

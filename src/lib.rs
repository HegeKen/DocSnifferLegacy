//! DocSniffer Legacy Edition —— 面向 Windows 2000/XP/Vista/7 的轻量级敏感文件嗅探工具。
//!
//! 本 crate 将引擎与界面分层：
//! - 引擎层：`config` / `settings` / `rules` / `extract` / `scanner` / `indexer` / `sensitive` / `report` / `engine`
//! - 界面/入口：`cli`（静默模式）、`app`（egui 界面）、`main`（入口分发）

pub mod app;
pub mod cli;
pub mod config;
pub mod engine;
pub mod extract;
pub mod indexer;
pub mod report;
pub mod rules;
pub mod scanner;
pub mod sensitive;
pub mod settings;
pub mod sys;

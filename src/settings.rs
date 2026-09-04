//! 界面配置：主题/字体大小/历史路径等，持久化到 `settings.json`。

use serde::{Deserialize, Serialize};
use std::path::Path;

/// 可选主题（Win2K 经典灰度 / 墨绿 / 深蓝）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ThemeChoice {
    Classic,
    Green,
    Blue,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct Settings {
    pub theme: ThemeChoice,
    pub font_size: f32,
    /// 是否在启动时自动扫描上次路径
    pub auto_scan: bool,
    /// 历史扫描路径（用于目录树快速定位）
    pub history_paths: Vec<String>,
    /// 结果展示的上下文长度
    pub context_len: usize,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            theme: ThemeChoice::Classic,
            font_size: 13.0,
            auto_scan: false,
            history_paths: Vec::new(),
            context_len: 50,
        }
    }
}

impl Settings {
    /// 读取设置；文件不存在或解析失败时返回默认值。
    pub fn load(data_dir: &Path) -> Self {
        let path = data_dir.join("settings.json");
        match std::fs::read_to_string(&path) {
            Ok(s) => serde_json::from_str(&s).unwrap_or_default(),
            Err(_) => Self::default(),
        }
    }

    pub fn save(&self, data_dir: &Path) -> Result<(), String> {
        if let Err(e) = std::fs::create_dir_all(data_dir) {
            return Err(format!("创建数据目录失败: {e}"));
        }
        let path = data_dir.join("settings.json");
        let json = serde_json::to_string_pretty(self).map_err(|e| e.to_string())?;
        std::fs::write(&path, json).map_err(|e| e.to_string())
    }
}

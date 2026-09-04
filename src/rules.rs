//! 敏感信息规则库：身份证、手机号、密级关键词等。
//! 默认规则内嵌于程序（`rules.json` 缺失时自动生成），用户可在界面增删。

use regex::Regex;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct SensitiveRule {
    pub id: String,
    pub name: String,
    /// 匹配类型：regex（正则）或 keyword（包含关键词）
    pub kind: String,
    /// 正则模式 或 关键词
    pub pattern: String,
    /// 是否同时对文件名 + 内容匹配
    pub scan_content: bool,
    pub enabled: bool,
}

impl Default for SensitiveRule {
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            kind: "keyword".to_string(),
            pattern: String::new(),
            scan_content: true,
            enabled: true,
        }
    }
}

impl SensitiveRule {
    /// 编译为正则对象。keyword 类型会做转义处理，regex 类型直接使用。
    pub fn compile(&self) -> Option<Regex> {
        if !self.enabled || self.pattern.is_empty() {
            return None;
        }
        let re = match self.kind.as_str() {
            "regex" => self.pattern.clone(),
            _ => regex::escape(self.pattern.trim()),
        };
        Regex::new(&re).ok()
    }
}

/// 内置默认规则库。
pub fn default_rules() -> Vec<SensitiveRule> {
    vec![
        SensitiveRule {
            id: "id_card".into(),
            name: "身份证号".into(),
            kind: "regex".into(),
            pattern: r"\b\d{17}[\dXx]\b".into(),
            scan_content: true,
            enabled: true,
        },
        SensitiveRule {
            id: "mobile".into(),
            name: "手机号".into(),
            kind: "regex".into(),
            pattern: r"\b1[3-9]\d{9}\b".into(),
            scan_content: true,
            enabled: true,
        },
        SensitiveRule {
            id: "level_secret".into(),
            name: "绝密/机密/内部".into(),
            kind: "regex".into(),
            pattern: r"(绝密|机密|秘密|内部资料|仅限内部|confidential)".into(),
            scan_content: true,
            enabled: true,
        },
        SensitiveRule {
            id: "password".into(),
            name: "密码/口令".into(),
            kind: "keyword".into(),
            pattern: "密码".into(),
            scan_content: true,
            enabled: false,
        },
    ]
}

pub fn load_rules(data_dir: &std::path::Path) -> Vec<SensitiveRule> {
    let path = data_dir.join("rules.json");
    if let Ok(s) = std::fs::read_to_string(&path) {
        if let Ok(rules) = serde_json::from_str::<Vec<SensitiveRule>>(&s) {
            return rules;
        }
    }
    // 缺失或解析失败 → 写入默认规则并返回
    let rules = default_rules();
    let _ = save_rules(data_dir, &rules);
    rules
}

pub fn save_rules(data_dir: &std::path::Path, rules: &[SensitiveRule]) -> Result<(), String> {
    if let Err(e) = std::fs::create_dir_all(data_dir) {
        return Err(format!("创建数据目录失败: {e}"));
    }
    let path = data_dir.join("rules.json");
    let json = serde_json::to_string_pretty(rules).map_err(|e| e.to_string())?;
    std::fs::write(&path, json).map_err(|e| e.to_string())
}

/// 匹配结果命中项。
#[derive(Debug, Clone)]
pub struct RuleHit {
    pub rule: SensitiveRule,
    /// 匹配到的具体文本（去重、截断）
    pub matched: String,
    pub count: usize,
}

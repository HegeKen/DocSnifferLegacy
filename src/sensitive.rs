//! 敏感信息扫描：对文件名 + 文件内容应用规则库，输出命中项。
//! 支持身份证、手机号、密级关键词等（见 rules.rs）。

use crate::rules::{RuleHit, SensitiveRule};
use regex::Regex;

/// 扫描结果命中项。
#[derive(Debug, Clone)]
pub struct SensitiveHit {
    pub path: String,
    pub filename: String,
    pub rule_id: String,
    pub rule_name: String,
    pub matched: String,
    pub count: usize,
    /// 命中来源：true=文件名，false=文件内容
    pub in_filename: bool,
}

/// 用单个规则匹配文本，返回首个要点 + 计数（去重、截断）。
pub fn match_text(rule: &SensitiveRule, text: &str) -> Option<RuleHit> {
    let re = rule.compile()?;
    let mut first: Option<String> = None;
    let mut count = 0usize;
    for m in re.find_iter(text) {
        if first.is_none() {
            let v = m.as_str().chars().take(40).collect::<String>();
            first = Some(v);
        }
        count += 1;
        if count >= 50 {
            break;
        }
    }
    let first = first?;
    Some(RuleHit {
        rule: rule.clone(),
        matched: first,
        count,
    })
}

/// 扫描单个文件内容（已提取文本）。
pub fn scan_content(content: &str, rules: &[SensitiveRule]) -> Vec<RuleHit> {
    __scan(content, rules)
}

/// 扫描单个文件名（不读文件体）。
pub fn scan_filename(filename: &str, rules: &[SensitiveRule]) -> Vec<RuleHit> {
    __scan(filename, rules)
}

fn __scan(text: &str, rules: &[SensitiveRule]) -> Vec<RuleHit> {
    let mut hits = Vec::new();
    for rule in rules {
        if !rule.scan_content {
            continue;
        }
        if let Some(hit) = match_text(rule, text) {
            hits.push(hit);
        }
    }
    hits
}

/// 组合扫描：文件名 + 内容（若 provided_content 非空）。
pub fn scan_file(
    path: &str,
    filename: &str,
    content: &str,
    rules: &[SensitiveRule],
) -> Vec<SensitiveHit> {
    let mut out = Vec::new();
    for hit in scan_filename(filename, rules) {
        out.push(SensitiveHit {
            path: path.to_string(),
            filename: filename.to_string(),
            rule_id: hit.rule.id.clone(),
            rule_name: hit.rule.name.clone(),
            matched: hit.matched,
            count: hit.count,
            in_filename: true,
        });
    }
    for hit in scan_content(content, rules) {
        out.push(SensitiveHit {
            path: path.to_string(),
            filename: filename.to_string(),
            rule_id: hit.rule.id.clone(),
            rule_name: hit.rule.name.clone(),
            matched: hit.matched,
            count: hit.count,
            in_filename: false,
        });
    }
    out
}

/// 供外部校验规则是否合法（避免保存损坏的正则）。
pub fn validate_rule(rule: &SensitiveRule) -> Result<(), String> {
    let re = &rule.pattern;
    if rule.kind == "regex" {
        Regex::new(re).map_err(|e| format!("正则无效: {e}"))?;
    }
    Ok(())
}

//! 端到端验证：索引写入 + 全文搜索（含 jieba 中文分词）。
//! 运行：cargo test --test index_search

use docsniffer_legacy::engine::{index_scan, Progress};
use docsniffer_legacy::indexer::{Indexer, SearchMode};
use docsniffer_legacy::scanner::ScanOptions;
use std::path::PathBuf;
use std::sync::Arc;

fn setup(sandbox: &PathBuf, files: &[(&str, &str)]) {
    std::fs::create_dir_all(sandbox).unwrap();
    for (name, content) in files {
        std::fs::write(sandbox.join(name), content).unwrap();
    }
}

#[test]
fn index_and_search_works() {
    let data_dir = std::env::temp_dir().join("docsniffer_idx_test");
    let scan_root = data_dir.join("scan");
    let _ = std::fs::remove_dir_all(&data_dir);
    setup(&scan_root, &[
        ("a.txt", "今天天气很好，我们讨论了项目计划。"),
        ("b.txt", "这是一份关于绝密清单的内部资料。"),
        ("c.txt", "normal english text about weather and plan."),
    ]);

    // 1) 索引扫描
    let progress = Arc::new(Progress::new());
    let opts = ScanOptions::default();
    let (found, written) =
        index_scan(&[scan_root.clone()], &opts, &data_dir, progress).unwrap();
    assert_eq!(found, 3, "应发现 3 个文件");
    assert!(written >= 3, "应至少写入 3 条索引，实际 {written}");

    // 2) 中文内容搜索
    let indexer = Indexer::open(&data_dir, "local").unwrap();
    let hits = indexer
        .search("绝密", SearchMode::Content, 10)
        .expect("搜索失败");
    assert!(!hits.is_empty(), "搜索'绝密'应命中内容");
    assert!(hits.iter().any(|h| h.filename == "b.txt"));

    // 3) 关键字 '计划' 命中 a.txt
    let hits2 = indexer.search("计划", SearchMode::Both, 10).unwrap();
    assert!(hits2.iter().any(|h| h.filename == "a.txt"), "'计划'应命中 a.txt");

    // 4) 英文文件名搜索
    let hits3 = indexer.search("c.txt", SearchMode::Filename, 10).unwrap();
    assert!(hits3.iter().any(|h| h.filename == "c.txt"));
}

//! 全局配置：应用元信息、便携模式判定、数据目录定位。

use std::path::{Path, PathBuf};

/// 应用显示名称
pub const APP_NAME: &str = "DocSniffer Legacy Edition";
/// 便携模式触发标记文件名
pub const PORTABLE_FLAG: &str = "PORTABLE.flag";
/// 默认敏感规则库文件名
pub const RULES_FILE: &str = "rules.json";
/// 界面配置文件名
pub const SETTINGS_FILE: &str = "settings.json";

/// 默认跳过的系统/敏感目录（触发旧系统权限弹窗或无关紧要）。
pub const DEFAULT_SKIP_DIRS: &[&str] = &[
    "Windows",
    "System Volume Information",
    "$Recycle.Bin",
    "Program Files",
    "Program Files (x86)",
    "WINNT",
    "Recovery",
    "Document and Settings",
    "$Windows.~BT",
    "$Windows.~WS",
];

/// 需要跳过（扫描/索引时忽略）的文件扩展名。
pub const SKIP_EXTS: &[&str] = &[
    "exe", "dll", "sys", "bin", "obj", "lib", "pdb", "so", "dylib", "ocx", "drv",
    "iso", "img", "vhd", "vmdk", "ova", "cab", "msi", "wim", "esd", "swp", "tmp",
    "lnk", "ini", "lock", "cache",
];

/// 便携模式判定：exe 同目录下存在 `PORTABLE.flag` 空文件。
pub fn is_portable(exe_dir: &Path) -> bool {
    exe_dir.join(PORTABLE_FLAG).exists()
}

/// 计算当前进程目录（Windows: exe 所在目录）。
pub fn exe_dir() -> PathBuf {
    #[cfg(windows)]
    {
        if let Ok(p) = std::env::current_exe() {
            if let Some(dir) = p.parent() {
                return dir.to_path_buf();
            }
        }
    }
    // 非 Windows / 兜底：使用当前工作目录，便于本地开发与测试。
    std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."))
}

/// Data 根目录：
/// - 便携模式 → `<exe_dir>/Data`
/// - 非便携模式 → 系统应用数据目录（Windows: %APPDATA%，macOS/Linux: 标准数据目录）
pub fn data_dir(exe_dir: &Path) -> PathBuf {
    if is_portable(exe_dir) {
        return exe_dir.join("Data");
    }
    if let Some(base) = dirs::data_dir() {
        return base.join("DocSnifferLegacy");
    }
    exe_dir.join("Data")
}

/// 判定某个扩展名是否需要跳过。
pub fn is_skipped_ext(ext: &str) -> bool {
    let e = ext.trim_start_matches('.').to_ascii_lowercase();
    SKIP_EXTS.contains(&e.as_str())
}

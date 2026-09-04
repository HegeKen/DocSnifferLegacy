//! 为最终 GUI 二进制锁定 Windows 子系统版本，确保可在旧系统上运行。
//!
//! 使用 `cargo:rustc-link-arg-bins` 只作用于本 crate 的二进制目标，避免像以前那样
//! 在 `.cargo/config.toml` 中用全局 `rustflags` 注入 `-SUBSYSTEM:WINDOWS`——那会把它
//! 一并加给所有依赖的 build script（它们是 console 二进制、用 `main` 入口），导致
//! MSVC 链接器因找不到 `WinMain` 而报 `LNK2019`。

use std::env;

fn main() {
    // 仅在 Windows 目标上生效；Linux/macOS 构建不受影响。
    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }

    let ver = match env::var("CARGO_CFG_TARGET_ARCH").as_deref() {
        Ok("x86_64") => "5.02", // Win7-64 / Vista-64
        Ok("x86") => "5.01",    // Windows XP SP2+ / Vista / 7-32
        _ => return,
    };

    println!("cargo:rustc-link-arg-bins=-SUBSYSTEM:WINDOWS,{ver}");
}

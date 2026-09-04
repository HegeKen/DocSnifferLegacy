//! 系统级兼容辅助（Windows 宿主相关；非 Windows 平台为空实现）。

/// 将进程优先级设为 `BELOW_NORMAL`，避免影响前台业务系统运行（README §7）。
/// 返回 0 表示成功；非 Windows 平台不执行任何操作。
pub fn set_below_normal_priority() -> i32 {
    #[cfg(windows)]
    {
        const BELOW_NORMAL_PRIORITY_CLASS: u32 = 0x0000_4000;
        #[allow(non_snake_case)]
        unsafe extern "system" {
            fn GetCurrentProcess() -> *mut core::ffi::c_void;
            fn SetPriorityClass(hProcess: *mut core::ffi::c_void, dwPriorityClass: u32) -> i32;
        }
        unsafe {
            let h = GetCurrentProcess();
            SetPriorityClass(h, BELOW_NORMAL_PRIORITY_CLASS)
        }
    }
    #[cfg(not(windows))]
    {
        0
    }
}

/// 获取毫秒级单调时钟（用于耗时统计）。
/// 旧系统缺 `GetTickCount64`，回退到 `GetTickCount`（约 49 天回绕，本工具单次扫描通常远小于此）。
pub fn tick_ms() -> u64 {
    #[cfg(windows)]
    {
        #[allow(non_snake_case)]
        unsafe extern "system" {
            fn GetTickCount() -> u32;
        }
        unsafe { GetTickCount() as u64 }
    }
    #[cfg(not(windows))]
    {
        use std::time::Instant;
        // macOS/Linux：用进程启动以来经过的时间近似
        static START: once_cell::sync::Lazy<Instant> =
            once_cell::sync::Lazy::new(Instant::now);
        START.elapsed().as_millis() as u64
    }
}

# DocSniffer Legacy Edition

**面向老旧 Windows 系统的轻量级本地文件检索与信息检测工具**

Rust · egui · Tantivy · 单文件 `.exe` · 完全离线


> **适用系统**：Windows 2000 / XP / Vista / Windows 7（含 32 位及 64 位）
> **发布形态**：单个独立 `.exe` 可执行文件（无需 .NET Framework、无需 VC++ Redist、无需 WebView2）
> **核心定位**：为仍在使用老旧 Windows 终端的用户提供轻量级的本地文件搜索、内容检索与按需信息检测能力，不依赖网络，开箱即用。

---

## 一、版本定位

本版本 **Legacy Edition** 面向 Windows 7 以下的旧系统，采用 **Rust 原生 Win32 API + egui 即时渲染** 方案，完全脱离浏览器内核依赖，在低配硬件上保持低内存、低 CPU 开销。

| 对比项 | 现代版本 | Legacy Edition |
| :--- | :--- | :--- |
| **支持系统** | Windows 10/11、macOS、Linux | Windows 2000 / XP / Vista / 7 |
| **架构方案** | Tauri + 系统 WebView | Rust + Win32 API + egui（无浏览器内核） |
| **GUI 渲染** | 现代 CSS/HTML 界面 | 原生 GDI / Direct2D 即时绘制 |
| **富文档预览** | 支持高亮片段预览 | 仅提供纯文本摘要（降低内存开销） |
| **安装包体积** | 较大 | 单个 `.exe`，体积小 |

---

## 二、技术选型（实际实现）

| 组件 | 选型方案 | 说明 |
| :--- | :--- | :--- |
| **编程语言** | **Rust 1.70+**（`i686-pc-windows-msvc` / `gnu` 工具链） | 通过静态链接消除运行时依赖 |
| **GUI 框架** | **egui + eframe 0.28**（原生 Win32 后端） | 纯 Rust 即时模式 GUI，不依赖 WebView / IE / COM |
| **全文检索引擎** | **Tantivy 0.22** | 纯 Rust 实现，仅依赖标准文件 I/O |
| **中文分词** | **jieba-rs 0.7**（自定义 Tantivy Tokenizer） | 支持中文内容搜索 |
| **文件扫描** | `walkdir` + `rayon` + `crossbeam-channel` | 多线程并发扫描，支持进度与取消 |
| **内容提取** | 自研轻量解析器 | 纯文本（自动编码检测）、Office OOXML、WPS 专有 OLE(.wps/.dps/.et)、PDF 文本 |
| **文本编码** | `encoding_rs` + `chardetng` | 兼容 GBK / GB18030 / Big5 等历史中文编码 |
| **数据存储** | Tantivy 索引 + `rules.json` + `settings.json` | 嵌入式、无外部服务 |

---

## 三、核心功能

- ✅ **全盘/目录递归扫描**：支持多线程并发，实时进度反馈，可随时取消
- ✅ **文件名/内容全文搜索**：基于 Tantivy 倒排索引，支持中文分词、AND/OR、双引号短语查询
- ✅ **信息检测规则扫描**：内置身份证号、手机号、关键词规则；支持仅按文件名扫描；支持自定义规则库
- ✅ **便携模式（Portable Mode）**：exe 同目录存在 `PORTABLE.flag` 时，所有数据读写于 `./Data`，即插即用、不留痕
- ✅ **离线规则库**：默认 `rules.json` 内嵌于程序，可在界面增删、保存、恢复默认
- ✅ **报告导出**：扫描/搜索结果导出为 CSV（带 UTF-8 BOM，兼容 Excel 2003）或 JSON
- ✅ **命令行静默模式**：无界面运行，适合批量/无人值守场景

---

## 四、界面布局（egui 三栏式）

采用 **左树形目录 + 右上搜索栏 + 右下结果列表** 的三栏式布局，全部由原生 Win32 消息循环驱动，无任何 Web 组件。

> 中文字体：egui 默认字体仅含拉丁字符，程序启动时会自动从系统加载一款中文字体（按 微软雅黑 `msyh.ttc` → 宋体 `simsun.ttc` → 黑体 `simhei.ttf` 顺序查找）注册为后备字体，因此界面中文可正常显示；若系统缺少上述字体则退化为仅拉丁显示。

```
┌──────────────────────────────────────────────────────────────┐
│  [🔍 搜索框]  [模式: 内容|文件名|文件名+内容]                │  ← 工具栏
│  [搜索] [📂 选择路径] [▶ 索引扫描] [★ 敏感扫描]            │
│  [☰ 文件名敏感] [▣ 规则管理] [✕ 取消]                       │
│  状态栏: 发现 N | 处理 M | 当前: <文件路径>                  │
├──────────┬───────────────────────────────────────────────────┤
│ 扫描根   │  搜索结果表（路径|文件名|匹配片段|分数）          │
│ 目录列表 │  敏感结果表（路径|文件名|规则|命中文本|次数）     │
│ [+ 添加] │  导出: [路径] [导出报告]                          │
│ [清空]   │  状态: 就绪                                       │
├──────────┴───────────────────────────────────────────────────┤
│  规则管理窗口: [添加规则] [保存] [恢复默认] [✕]             │
└──────────────────────────────────────────────────────────────┘
```

**交互特性**

- 键盘快捷键：`Ctrl+F` 聚焦搜索、`Ctrl+E` 导出报告、`Esc` 取消当前任务
- 大结果集采用虚拟滚动（仅渲染可视区域），低配机流畅滚动
- 支持主题切换（经典灰度 / 墨绿 / 深蓝）与字号调节

---

## 五、数据存储与目录结构

### 5.1 便携模式目录

```
DocSnifferLegacy/
├── docsniffer_legacy.exe       # 主程序
├── PORTABLE.flag               # 空文件，触发便携模式
└── Data/                       # 便携模式下自动创建
    ├── index/
    │   └── local/              # Tantivy 索引（单分片）
    ├── rules.json              # 用户自定义规则
    └── settings.json           # 界面配置（主题/字号/历史路径）
```

### 5.2 非便携模式

当不存在 `PORTABLE.flag` 时，数据写入系统应用数据目录（Windows 为 `%APPDATA%/DocSnifferLegacy`，macOS/Linux 为标准用户数据目录）。

---

## 六、构建与发布

### 6.1 静态链接配置（`.cargo/config.toml`）

针对旧系统，编译时必须锁定 **静态 CRT 链接** 与兼容的 API 版本：

> 注意：GUI 子系统须在 `src/main.rs` 声明 `#![windows_subsystem = "windows"]`，否则 MSVC 链接器会因入口点不匹配而报 `LNK2019: 无法解析的外部符号 WinMain`。`config.toml` 中的 `-SUBSYSTEM:WINDOWS` 仅负责锁定兼容的 Windows 版本号。

> **重要（Win7/XP 兼容）**：MSVC（VS2017+ / Windows SDK 10）编译的产物会引用 `api-ms-win-*.dll`（API-Set 转发库）。`+crt-static` 只能免掉 VC++ 运行库，**免不掉**这套 API-Set；而它们（如 `api-ms-win-core-libraryloader-l1-2-0.dll`）由 Universal CRT 补丁 **KB2999226** 引入，Win7/XP 默认不存在。因此**追求 XP/Win7 免依赖运行，应优先选择 GNU（MinGW-w64）工具链**——它直接链接 `kernel32/user32/gdi32`，不引入 API-Set，无此问题。

```toml
[target.i686-pc-windows-msvc]
rustflags = ["-C", "target-feature=+crt-static", "-C", "link-arg=-SUBSYSTEM:WINDOWS,5.01"]

[target.x86_64-pc-windows-msvc]
rustflags = ["-C", "target-feature=+crt-static", "-C", "link-arg=-SUBSYSTEM:WINDOWS,5.02"]

# 若使用 GNU 工具链（更适合免 VC++ 环境）
[target.i686-pc-windows-gnu]
rustflags = ["-C", "target-feature=+crt-static"]
linker = "i686-w64-mingw32-gcc"

[target.x86_64-pc-windows-gnu]
rustflags = ["-C", "target-feature=+crt-static"]
linker = "x86_64-w64-mingw32-gcc"
```

### 6.2 构建命令

```bash
# ---- 推荐：GNU（MinGW-w64）工具链，免 UCRT/API-Set，最适合 XP/Win7 免依赖运行 ----
# 需要先：rustup target add x86_64-pc-windows-gnu 并安装 mingw-w64
cargo build --release --target x86_64-pc-windows-gnu   # 64 位（Win7 64 位）

# 32 位（XP/Vista 及 32 位 Win7）
rustup target add i686-pc-windows-gnu
cargo build --release --target i686-pc-windows-gnu

# ---- 备选：MSVC 工具链（需目标机器安装 KB2999226 / KB3118401 才能在 Win7/XP 运行）----
cargo build --release --target i686-pc-windows-msvc    # 32 位
cargo build --release --target x86_64-pc-windows-msvc  # 64 位
```

产物（`docsniffer_legacy.exe`）直接位于 target 目录，**无需** 安装 Visual C++ Redistributable。若用 GNU 工具链构建，同时在纯净 XP/Win7 上可直接双击运行；若用 MSVC 构建，Win7/XP 需先安装 UCRT 补丁（KB2999226 或 KB3118401）才能运行，否则会提示缺失 `api-ms-win-*.dll`。如需进一步压缩，可选用 **UPX** 二次压缩（可选，不影响运行）。

---

## 七、命令行静默模式

为满足批量终端自查与无人值守场景，支持无界面运行：

```cmd
# 扫描 D 盘，生成报告到 report.csv 后自动退出
docsniffer_legacy.exe --scan D:\ --export report.csv --silent

# 指定自定义规则库扫描并导出 JSON
docsniffer_legacy.exe --scan C:\ --rules custom_rules.json --export result.json

# 查看版本 / 帮助
docsniffer_legacy.exe --version
docsniffer_legacy.exe --help
```

| 参数 | 说明 |
| :--- | :--- |
| `--scan <路径>` | 静默扫描指定路径（信息检测） |
| `--rules <文件>` | 指定自定义规则库（`.json`），缺省用内嵌默认规则 |
| `--export <文件>` | 导出报告（`.csv` 或 `.json`），可选 |
| `--silent` | 无界面运行 |
| `--help` / `--version` | 打印帮助 / 版本后退出 |

命令行模式下进程会以低优先级运行，避免影响前台业务系统。

---

## 八、内容提取支持

为降低内存与 CPU 开销，仅提取纯文本摘要，不解析样式、图片与表格结构：

| 类型 | 提取方式 |
| :--- | :--- |
| **纯文本/代码文件** | 字节流 → 编码检测（chardetng）→ 解码；自动回退 GB18030 |
| **Office OOXML** | `docx` / `xlsx` / `pptx` / `docm` / `xlsm` / `pptm`（zip 内 XML 文本标签抽取） |
| **WPS 专有 OLE 二进制** | `wps` / `dps` / `et`（OLE2/CFB 复合文档：按内部流解析 .wps 文本、.dps 演示文本、.et 共享字符串表） |
| **PDF** | 解析文本操作符（`Tj` / `TJ`），自动解压 FlateDecode 流 |
| **其他** | 一律按纯文本处理（含未知类型） |

---

## 九、性能与兼容性

| 优化策略 | 具体措施 |
| :--- | :--- |
| **I/O 优先级** | 调用 Win32 `SetPriorityClass` 将进程设为 `BELOW_NORMAL`，避免影响前台业务 |
| **跳过系统目录** | 默认跳过 `Windows`、`System Volume Information`、`$Recycle.Bin` 等，避免触发权限弹窗 |
| **内存限制** | 索引器内存上限默认 64MB，超出则强制落盘（基于 Tantivy 内存预算） |
| **弱机适配** | 根据可用核数动态调整线程池；线程切换开销较大的单核环境自动退化为较少线程 |
| **时间时钟** | 旧系统缺 `GetTickCount64` 时回退 `GetTickCount`（约 49 天回绕，单次扫描远小于此） |

**兼容性测试清单**

| 测试项目 | 预期结果 |
| :--- | :--- |
| **Windows XP SP3 (32位)** | 双击 exe 直接运行，检索与信息检测正常，中文无乱码 |
| **Windows Vista (32/64位)** | 正常运行（如需则请求提权） |
| **Windows 7 (32/64位)** | 正常运行，界面清晰 |
| **无网络/受控网络环境** | 完全不依赖网络，启动不访问 DNS/ICMP |

---

## 十、已知限制

| 问题 | 解决方法 |
| :--- | :--- |
| **旧版 MS Office .doc/.ppt/.xls（OLE 格式）** | 不解析 MS Office 97-2003 二进制格式（Word/PPT/Excel 原生 OLE）；WPS 专有 `.wps`/`.dps`/`.et` 已支持，其余建议转换为 .docx/.xlsx/.pptx 后扫描 |
| **富文本预览** | 仅展示纯文本摘要与前 50 字上下文，不支持 HTML 高亮 |
| **网络映射盘（如 Z:\）** | 可扫描，但速度受限于网络带宽，建议拷贝至本地再扫描 |

---

## 快速使用指南

1. 将 `docsniffer_legacy.exe` 拷贝至 U 盘或本地文件夹，建议放在磁盘根目录。
2. **（可选）** 新建一个空的 `PORTABLE.flag` 文件，让程序将所有缓存写在 U 盘上，不在被检电脑留下任何痕迹。
3. 双击 exe 打开程序，单击 **「选择路径」**，勾选需要扫描的目录（建议逐个扫描以节约内存）。
4. 在搜索框输入关键词并选择搜索模式（内容 / 文件名），点击 **「搜索」** 检索；或点击 **「索引扫描」** 建立索引后，用 **「敏感扫描」** / **「文件名敏感」** 进行信息检测。
5. 扫描完毕后，点击 **「导出报告」**，将结果保存为 CSV 或 JSON 文件查看。

---

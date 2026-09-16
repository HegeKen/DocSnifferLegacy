# DocSnifferLegacy

DocSnifferLegacy 是一款面向 **Windows XP SP3 ~ Windows 11** 的 **本地文件搜索与内容检索** 桌面应用，基于 .NET Framework 4.0 WinForms 构建。它扫描本地目录、为目标文件建立全文索引（含 Office / PDF / OFD / 压缩包内容提取），并提供基于自定义规则的敏感信息匹配检测。**完全离线**，所有数据仅保存在本地。

它是 [DocSniffer](https://github.com/HegeKen/DocSniffer)（Tauri 2 + Rust + Tantivy 的现代版）的**向下兼容版本**：项目名称、页面布局、批次模型与规则检测语义均与 DocSniffer 对齐，补上 WebView2 / WebKitGTK 覆盖不到的 XP SP3 / Win7 RTM 一档，供低配旧机使用。

## 系统要求

| 平台 | 要求 |
|------|------|
| Windows | Windows XP SP3（32 位）/ Vista / 7 / 8 / 10 / 11，x86 或 x64 |
| 运行时 | .NET Framework 4.0（XP / Win7 需离线安装包，x86 约 48MB；Win7 未内置需手动安装） |
| 网络与注册表 | 无任何网络依赖、无注册表写入 |

> 现代版 DocSniffer 自 v0.3.0 起最低支持 Windows 10（依赖 WebView2），Windows 7 及更早系统的支持由本仓库承接。两者可在一台机器上并存，数据目录相互独立。

## 功能特性

- **目录扫描**：递归遍历指定目录（跳过重解析点防循环、排除目录可配置），实时反馈扫描进度（已处理数 / 总数 / 当前路径）；被跳过或索引失败的条目汇总为告警列表在界面展示，不再静默丢弃。支持"仅索引文件名"模式与增量比对。
- **全文索引**：对文件路径、文件名、文件内容建立 Lucene.NET 3.0.3 索引，按相关性评分排序。中文使用自研 CJK 二元分词器，无需引入外部分词库。
- **内容提取**：多种纯文本 / 代码文件（自动识别编码，含 GBK、GB18030 等历史中文编码）、OOXML（DOCX / XLSX / PPTX 及 PK 结构的 WPS 文件）、OLE2 旧格式（.doc / .xls / .ppt 与旧版 .wps / .et / .dps）、OFD 电子公文、PDF 与 ZIP 压缩包穿透。
- **快速搜索**：文件名 / 内容统一检索，支持布尔语法与字段限定，结果含文件名 / 类型 / 大小 / 修改时间 / 得分 / 命中摘要；单击结果在预览面板查看命中内容并**高亮关键词**。
- **敏感信息检测**：内置 5 条默认规则（正则 / 关键词），支持在界面上增删改并持久化，可作用于文件名和 / 或文件内容，输出带风险等级的命中报告。
- **批次化索引管理**：每次扫描生成一个索引批次，可查看各批次实时文档数、重新扫描更新（变更文件重索引、新增文件入库、已删除文件出库）、删除单个批次或一键清空全部索引；搜索可限定到指定批次。
- **大文件与格式防护**：单文件索引上限 50MB（超大文件降级为"仅按文件名索引"）、单文档内容上限 400 万字符、压缩包穿透设条目数 / 解压量 / 嵌套层数限额（防 zip 炸弹）、损坏文件只记录告警不中断整体扫描。

## 技术栈

| 层级 | 选型 |
|------|------|
| 界面 | WinForms（.NET Framework 4.0，C# 5，x86） |
| 全文检索引擎 | Lucene.NET 3.0.3（评分排序、SimpleFSLockFactory 文件锁） |
| 中文分词 | 自研 CjkBigramAnalyzer（二元切分，索引与查询两侧一致） |
| OLE2 容器 / .xls 解析 | NPOI 2.4.1（最后一个提供 net40 目标的版本；doc / ppt 内容解析为自研实现） |
| ZIP / XML | 自研 ZipReader（store/deflate）+ System.Xml（OOXML / OFD / 压缩包穿透零第三方依赖） |
| PDF | Chromium PDFium（P/Invoke 原生库，缺失时优雅降级） |
| 编码检测 | 自研（BOM → UTF-8 → UTF-16 启发式 → GB18030 → Big5） |
| 存储与持久化 | XML（settings / batches / rules）+ Lucene 索引目录 |
| 发布 | ILRepack 合并单文件 exe（含 NPOI，构建期跨平台运行） |

## 界面布局（与 DocSniffer 一致）

- **头部**：品牌 "DocSnifferLegacy · 文档嗅探器（遗留系统版）" + 状态徽章（"已索引 N 个文件" + 索引目录路径）。
- **搜索页**：批次范围下拉（"全部索引" / 各批次及文档数）+ 关键词输入 + 搜索按钮；结果列表含文件名 / 类型 / 大小 / 修改时间 / 得分 / **摘要** / 路径，双击打开文件，右键打开所在文件夹，单击结果时下方预览面板显示命中摘要并高亮关键词（摘要需重读文件，列表默认为前 50 条预生成，其余在选中时后台生成）。
- **扫描页**：目录 + 批次名称（可选，自动生成）+ "索引文件内容（否则仅索引文件名）"开关 + 开始扫描；进度条（N / M · 当前路径）、完成提示与"被跳过或索引失败"告警列表。
- **敏感检测页**：规则表格（名称 / 类型 regex|keyword / 风险 low~critical / 作用范围 文件名·内容 / 正则或关键词 / 删除），可增删改并持久化；下方"执行检测"对指定路径（文件或目录）输出命中报告（风险等级着色）。
- **索引管理页**：批次表格（批次名称 / 导入目录 / 实时文档数 / 创建时间）+ 更新 / 删除 / 一键清除全部索引。

配色（浅灰底 #F5F6F8、白面板、蓝色主按钮 #2563EB、风险等级色阶）跟随 Web 版 `styles.css`；
字体 Win7+ 使用微软雅黑，XP 自动回退宋体；标签页自绘为平面样式（激活项蓝字 + 蓝色下划线）。

## 格式支持明细

| 格式 | 提取方式 | 说明 |
|---|---|---|
| DOCX / XLSX / PPTX / DOCM / XLSM / PPTM | 自研 ZIP + XmlReader | 含 PK 结构的 .wps / .et / .dps |
| .doc（Word 97~2003 / doc 兼容的旧版 .wps） | NPOI POIFS 容器 + 自研 FIB/分片表解析 | 支持 UTF-16 与 8 位压缩分片、域结果提取、页眉页脚脚注故事；加密文档明确报错 |
| .xls（BIFF8） | NPOI HSSF + DataFormatter | BIFF5（Excel 5/95）自动降级为启发式挖掘 |
| .ppt | NPOI POIFS + 自研记录树扫描 | 提取 TextCharsAtom（UTF-16）与 TextBytesAtom（ANSI/GBK）文本 |
| 旧版 OLE2 结构 .wps / .et / .dps | 启发式流挖掘 | 无规范结构时的兜底：解码全部流并保留可读片段，尽力而为 |
| .ofd（GB/T 33190） | 自研 ZIP + XmlReader | 提取 Doc_x/Pages/Page_y 下 TextCode 文本，按页序输出 |
| .pdf | Pdfium（P/Invoke 原生库） | 需部署 32 位 pdfium.dll（见部署章节），缺失时优雅降级为仅文件名索引并提示 |
| .zip | 自研 ZIP 读取器 | 穿透提取内部文件（条目名可检索）；嵌套最多 3 层；单条目 32MB / 累计 128MB / 512 条目限额防 zip 炸弹 |
| 纯文本 / 代码（40+ 扩展名） | 编码检测 + 解码 | — |

格式路由为链式：按扩展名注册的优先级逐个尝试（如 .wps 先按 OOXML 解析，失败再按 OLE2 解析）。
OLE2/PDF 的结构识别不依赖扩展名（按魔数与容器内流名自动分派），扩展名错误的文件也能被正确提取。

## 系统架构

界面通过服务容器驱动同一套业务核心（与 DocSniffer 的分层一一对应）：

```
┌────────────────────────── 界面层 (WinForms) ──────────────────────────┐
│  搜索 (SearchPage)          扫描 (ScanPage)                            │
│  敏感检测 (RulesPage)       索引管理 (BatchesPage)                      │
│     MainForm 外壳：品牌头部 + 状态徽章 + 平面标签页                      │
└────────────────────────────────┬─────────────────────────────────────┘
                                 │  AppServices（服务容器 + 全局任务忙闸）
┌────────────────────────────────▼─────────────────────────────────────┐
│                       核心层 (DocSnifferLegacy.Core)                  │
│  IO/FileScanner      目录遍历，重解析点跳过，排除目录                  │
│  IO/ZipReader        自研 ZIP（store/deflate），OOXML/OFD/穿透共用    │
│  Routing/FormatRouter 扩展名 → 提取器路由（链式尝试，顺序即优先级）     │
│  Extract/*           内容提取：Ooxml（OOXML 与 PK 结构 WPS）           │
│                      Ole2（NPOI 容器 + doc 分片表/ppt 记录树/WPS 挖掘） │
│                      Ofd（TextCode 页面文本）· Pdfium（P/Invoke）      │
│                      ZipArchive（压缩包穿透）· TextFile（编码检测）     │
│  Text/EncodingDetector  BOM → UTF-8 → UTF-16 启发式 → GB18030 → Big5  │
│  Search/SnippetBuilder  查询高亮词提取 + 命中摘要生成                  │
│  Sensitive/*         规则匹配检测（文件名 / 内容）                     │
│  Index/*             CjkBigramAnalyzer + LuceneIndexService（批次字段）│
└──────────────────────────────────────────────────────────────────────┘
```

## 索引设计

索引字段（Lucene Schema）：

| 字段 | 索引 | 存储 | 说明 |
|------|------|------|------|
| `path` | NOT_ANALYZED | 是 | 文件完整路径（唯一键，增量比对与删除依据） |
| `filename` | ANALYZED | 是 | 文件名 |
| `content` | ANALYZED | 否 | 文件内容（仅索引不存储，保持索引体积） |
| `filetype` | NOT_ANALYZED | 是 | 类型展示名（Word / Excel / PPT / 文本 / PDF / OFD / 压缩包 / 其他） |
| `batch` | NOT_ANALYZED | 是 | 所属导入批次 ID（按批次统计 / 范围过滤 / 删除） |
| `size` | NOT_ANALYZED | 是 | 文件字节数 |
| `modified` | NOT_ANALYZED | 是 | 修改时间（UTC Ticks） |

> `content` 刻意不存储以保持索引体积；结果中的摘要（snippet）通过重新读取文件文本生成
> （与 DocSniffer 的 make_snippet 行为一致）。中文按二元切分：相邻两字一个词，孤立单字仅
> 在整段仅一字时成词，保证查询与索引两侧切分一致。索引锁使用 `SimpleFSLockFactory`，
> 兼容网络共享盘与非 Windows 运行时。

## 查询语法

| 语法 | 示例 | 说明 |
|------|------|------|
| 多词 | `机密 报告` | 默认 OR，命中词越多的文档越靠前 |
| `AND` / `OR` / `NOT` | `机密 AND 财务` | 布尔组合（AND 要求同时命中） |
| `+` / `-` | `+机密 -草稿` | 必含 / 排除 |
| 引号 | `"项目计划书"` | 短语精确匹配 |
| 字段限定 | `filename:合同`、`content:机密`、`filetype:PDF` | `filename` / `content` 分词查询；`filetype` / `batch` 等不分词字段需精确值 |

> 查询语法错误时自动转义整串重试，保证用户输入不会导致搜索失败。

## 数据目录

应用数据默认存放在 `%APPDATA%\DocSnifferLegacy\`：

```
%APPDATA%\DocSnifferLegacy\
├── settings.xml    # 设置（上次扫描目录 / 索引目录 / 全量重建标记）
├── batches.xml     # 批次元数据
├── rules.xml       # 敏感检测规则（与 DocSniffer 的 JSON 各自独立）
└── index\          # Lucene 索引（可在设置中改为其他目录）
```

## 批次模型（对齐 DocSniffer）

- 每次在"扫描"页导入目录都会生成一个批次（可命名，默认"批次 yyyy-MM-dd HH:mm"）；
  同一路径以**最后一次扫描所属批次为准**（重新导入会把已有文档归入新批次）。
- 搜索页可把范围限定到某个批次；"索引管理"页可对批次重新扫描更新（增量）、删除批次
  （连同其文档）或一键清除全部索引。
- 仅文件名模式（扫描页取消勾选"索引文件内容"）下，提取失败的文件也会按文件名入库。

## 敏感检测（对齐 DocSniffer 规则页）

- 规则 = 名称 + 类型（`regex` 按原样匹配 / `keyword` 不区分大小写包含）+ 风险等级
  （low / medium / high / critical）+ 作用范围（文件名、内容）+ 模式串；非法正则自动跳过。
- 首次运行内置 5 条默认规则：身份证号、手机号码、IP 地址、机密字样、内部资料。
- 每条规则每个文件记首个命中；关键词命中的内容会带前后约 40 字上下文。
- 内容检测走与索引一致的提取管道，覆盖全部已支持格式（含压缩包穿透与 OLE2 旧格式）。
- 规则持久化于 `%APPDATA%\DocSnifferLegacy\rules.xml`（与 DocSniffer 的 JSON 各自独立）。

## 构建与运行

代码面向 .NET Framework 4.0（`net40`），构建不依赖 Windows：

```bash
# macOS / Linux / Windows 通用（需要任意 .NET SDK 6+）
dotnet build DocSnifferLegacy.sln -c Release
```

Windows 上也可用 Visual Studio 2017+ 打开 `DocSnifferLegacy.sln` 构建（VS2010/2012 无法打开
SDK 风格工程；若必须用旧版 IDE 可转成传统工程文件，目标框架不变）。源码统一使用 C# 5 语法。

### 部署（绿色单目录）

将以下文件复制到同一目录即可运行，无需安装程序：

```
DocSnifferLegacy.exe
DocSnifferLegacy.Core.dll
Lucene.Net.dll
ICSharpCode.SharpZipLib.dll
NPOI.dll
pdfium.dll        （可选：仅在需要 PDF 内容提取时放置，32 位/x86）
```

`dotnet build` 后各文件位于 `src/DocSnifferLegacy.App/bin/Release/net40/`。

**pdfium.dll 说明**：PDF 提取通过 P/Invoke 调用 Chromium PDFium 原生库（纯 Windows，无法
随 .NET 程序集合并）。请使用 **32 位（x86）** 构建与本程序位数一致；XP 上需选用仍支持 XP 的
旧版 pdfium（Chrome 49 世代，约 2016 年的构建）。缺失该文件不影响其余全部功能——.pdf 文件
会按文件名入库并在扫描告警中提示部署方法。

### 单文件版

Release 构建会额外将 `DocSnifferLegacy.Core.dll`、`Lucene.Net.dll`、`ICSharpCode.SharpZipLib.dll`、
`NPOI.dll` 用 ILRepack 合并进单一 exe，输出到
`src/DocSnifferLegacy.App/bin/Release/net40/publish/DocSnifferLegacy.exe`（约 2.9 MB）。
合并仅发生在构建期（ILRepack 2.0.48 起可在 macOS/Linux 的 .NET SDK 构建下运行，经 NuGet
包引用随仓库自动还原，无需全局安装），产物仍是面向 .NET Framework 4.0 的普通程序集，
XP SP3 / Win7 运行要求不变；x86 GUI 子系统、应用图标与版本资源均保留。
PDF 提取依赖的 `pdfium.dll` 为原生库，不参与合并，需按上节单独放置。

说明：

- `bin/Release/net40/` 下的多文件产物与单文件产物并存，前者仍供 Net8 验证壳按路径引用。
- 单文件目录内不再生成 `.exe.config`（其内容仅是默认 `supportedRuntime` 声明，可省）。
- 合并目标带增量检查：输入未变化时重复构建会跳过合并；Debug 构建不做合并。

### 应用图标

应用图标来自仓库根目录的 `DocSnifferLegacy.png`（2048×2048）：构建时通过
`src/DocSnifferLegacy.App/DocSnifferLegacy.ico`（16/24/32/48/64/128/256 全 BMP 条目，
兼容不支持 PNG 图标条目的 Windows XP）嵌入 exe（csproj `ApplicationIcon`），
窗体与任务栏图标运行时从 exe 内嵌资源读取。若替换源图，用 Pillow 重新生成：

```bash
python3 -c "from PIL import Image; Image.open('DocSnifferLegacy.png').convert('RGBA')\
.save('src/DocSnifferLegacy.App/DocSnifferLegacy.ico', format='ICO', \
sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)], bitmap_format='bmp')"
```

## 目录结构

```
DocSnifferLegacy/
├── DocSnifferLegacy.sln
├── Directory.Build.props           # C# 5 / net40 引用程序集 / 确定性构建
├── src/
│   ├── DocSnifferLegacy.App/       # WinForms 界面（x86，GUI 子系统）
│   │   ├── Program.cs              # 入口（异常兜底 + 内嵌图标读取）
│   │   ├── MainForm.cs             # 外壳：品牌头部 + 状态徽章 + 平面标签页
│   │   ├── AppServices.cs          # 服务容器 + 全局任务忙闸
│   │   ├── AppSettings.cs          # settings.xml 持久化
│   │   ├── BatchStore.cs           # 批次元数据 XML
│   │   ├── RuleStore.cs            # 敏感规则 XML
│   │   ├── Theme.cs                # 配色与控件工厂（对齐 Web 版 styles.css）
│   │   ├── AppLog.cs
│   │   └── Pages/
│   │       ├── SearchPage.cs       # 搜索：结果列表 + 摘要列 + 高亮预览
│   │       ├── ScanPage.cs         # 扫描：进度 + 告警列表
│   │       ├── RulesPage.cs        # 敏感检测：规则增删改 + 执行检测
│   │       └── BatchesPage.cs      # 索引管理：批次更新 / 删除 / 清空
│   └── DocSnifferLegacy.Core/      # 业务核心（无 UI 依赖）
│       ├── IO/
│       │   ├── FileScanner.cs      # 递归扫描（重解析点跳过 / 排除目录）
│       │   └── ZipReader.cs        # 自研 ZIP 读取器（store/deflate/GBK 文件名）
│       ├── Routing/
│       │   └── FormatRouter.cs     # 链式提取路由
│       ├── Extract/
│       │   ├── ITextExtractor.cs   # 提取器统一接口
│       │   ├── OoxmlExtractor.cs   # OOXML 三族 + PK 结构 WPS
│       │   ├── Ole2Extractor.cs    # NPOI 容器 + doc/ppt/WPS 挖掘
│       │   ├── OfdExtractor.cs     # OFD（TextCode 页面文本）
│       │   ├── PdfiumExtractor.cs  # PDF（P/Invoke，优雅降级）
│       │   ├── ZipArchiveExtractor.cs # 压缩包穿透（嵌套递归 + 限额）
│       │   └── TextFileExtractor.cs   # 纯文本 / 代码（40+ 扩展名）
│       ├── Text/
│       │   └── EncodingDetector.cs # 编码检测
│       ├── Search/
│       │   └── SnippetBuilder.cs   # 高亮词提取 + 命中摘要
│       ├── Sensitive/
│       │   └── SensitiveScanner.cs # 规则引擎
│       └── Index/
│           ├── CjkBigramAnalyzer.cs    # 中文二元分词
│           └── LuceneIndexService.cs   # 索引 / 检索 / 批次 / 增量
├── tests/
│   ├── DocSnifferLegacy.Tests/         # net40 测试工程（自研断言框架）
│   └── DocSnifferLegacy.Tests.Net8/    # 非 Windows 开发机验证壳（.NET 8 运行时）
├── DocSnifferLegacy.png                # 应用图标源图
└── README.md
```

## 测试

```bash
dotnet build DocSnifferLegacy.sln -c Debug
mono tests/DocSnifferLegacy.Tests/bin/Debug/net40/DocSnifferLegacy.Tests.exe   # macOS/Linux
# Windows 上直接运行同名 exe
```

覆盖：编码检测 9 组、ZIP 读取器（store/deflate/GBK 文件名/损坏流）、OOXML 三族提取
（富文本/拼音注音/页码域/tab 换行）、OLE2 提取（HSSF 工作表/doc 分片表 UTF-16 与压缩
分片/加密文档报错/ppt 文本原子/旧版 WPS 挖掘/路由链降级）、OFD 提取（TextCode/页序/
结构校验）、压缩包穿透（条目名行/内部 OOXML/嵌套递归/层数上限/系统条目跳过/损坏报错）、
PDF 优雅降级（非 PDF 拒绝/原生库缺失提示）、搜索摘要（高亮词提取/窗口省略号/空白折叠/
重读文件）、扫描+索引+搜索全链路（含 OLE2/OFD/压缩包新格式与类型标签）、增量
（修改/删除/新增/无变化/损坏文件容错/文件名检索）、批次（计数/范围过滤/重新导入归属/
删除/一键清除）、敏感检测（正则/关键词/作用范围/非法正则跳过/仅文件名模式）
共 108 项断言，当前全部通过。

**非 Windows 开发机的运行时验证**（新版 macOS 已不再提供 mono 运行时）：

```bash
dotnet run --project tests/DocSnifferLegacy.Tests.Net8 -c Release
```

该壳用 .NET 8 运行时加载 net40 编译产物执行同一套测试源码，仅用于开发机验证；
发布物以 `net40` 产物为准，最终需在真实 XP SP3 / Win7 上回归验证。

## 已知限制

- **Big5 繁体**：与 GB18030 字节重叠度高，会被按 GB18030 解出"兼容乱码"（仍可部分检索）。
- **单字中文检索**：二元切分下，被其他汉字包围的单字不单独成词，建议两字以上关键词。
- **旧版 OLE2 .wps / .et / .dps（无规范结构时）**：启发式流挖掘为尽力而为，二进制
  废字节中可能混入少量误读片段；doc 兼容结构与 .xls/.ppt 为精确解析，不受此限。
- **PDF**：依赖随目录部署的 32 位 pdfium.dll（原生库，见部署章节）；加密 PDF 只能按文件名检索。
- **压缩包穿透**：仅 .zip（自研读取器不支持 rar/7z/zip64）；超限额条目只保留名字行。
- FAT32 修改时间精度 2 秒，同秒内同大小改写可能漏检增量（NTFS 无此问题）。
- 桌面 WinForms 版没有 DocSniffer 的文件监控自动重索引（按路线图补充）。

## 相关项目

| 项目 | 定位 | 运行环境 | 技术栈 |
|------|------|----------|--------|
| **[DocSniffer](https://github.com/HegeKen/DocSniffer)** | 现代版主线，功能与性能持续演进 | Windows 10（1803+）/ macOS 10.15+ / Linux | Tauri 2 + Rust + Tantivy |
| **DocSnifferLegacy**（本仓库） | 向下兼容的遗留系统版，承接 Windows 7 / XP 等旧机 | Windows XP SP3 / Vista / 7 / 8 / 10 / 11（需 .NET Framework 4.0） | .NET Framework 4.0 WinForms + Lucene.NET |

两者刻意保持一致的使用语义：**批次模型**（一次扫描一个批次，支持按批次搜索 / 更新 / 删除 / 清空）、
**规则检测**（名称 + `regex`/`keyword` + 风险等级 + 作用范围）与**界面布局**
（搜索 / 扫描 / 敏感检测 / 索引管理 四页）。数据目录相互独立（DocSniffer 使用用户数据目录下的
`DocSniffer/`，本仓库使用 `%APPDATA%\DocSnifferLegacy\`），可在一台机器上并存。

## 许可

仅供学习与内部本地使用，请遵守所在地区法律法规及第三方依赖（Lucene.NET、SharpZipLib、
NPOI 均为 Apache License 2.0）的开源许可协议。

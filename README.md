# DocSnifferLegacy

DocSnifferLegacy 是 [DocSniffer](../../../Desktop/Codes/DocSniffer)（Tauri 2 + Rust + Tantivy 的现代版文档嗅探器）的**遗留系统配套版**：面向 Windows XP SP3 ~ Windows 7 的 .NET Framework 4.0 WinForms 实现，项目名称、页面布局、批次模型与规则检测均与 DocSniffer 对齐。**完全离线**，所有数据仅保存在本地。

> DocSniffer 桌面版依赖 WebView2（Windows 10+），服务器模式依赖浏览器（Windows 7 SP1+）；
> DocSnifferLegacy 补上更老的 XP SP3 / Win7 RTM 一档，供低配旧机使用。

## 界面布局（与 DocSniffer 一致）

- **头部**：品牌 "DocSnifferLegacy · 文档嗅探器（遗留系统版）" + 状态徽章（"已索引 N 个文件" + 索引目录路径）。
- **搜索页**：批次范围下拉（"全部索引" / 各批次及文档数）+ 关键词输入 + 搜索按钮；结果列表含文件名 / 类型 / 大小 / 修改时间 / 得分 / 路径，双击打开文件，右键打开所在文件夹。
- **扫描页**：目录 + 批次名称（可选，自动生成）+ "索引文件内容（否则仅索引文件名）"开关 + 开始扫描；进度条（N / M · 当前路径）、完成提示与"被跳过或索引失败"告警列表。
- **敏感检测页**：规则表格（名称 / 类型 regex|keyword / 风险 low~critical / 作用范围 文件名·内容 / 正则或关键词 / 删除），可增删改并持久化；下方"执行检测"对指定路径（文件或目录）输出命中报告（风险等级着色）。
- **索引管理页**：批次表格（批次名称 / 导入目录 / 实时文档数 / 创建时间）+ 更新 / 删除 / 一键清除全部索引。

配色（浅灰底 #F5F6F8、白面板、蓝色主按钮 #2563EB、风险等级色阶）跟随 Web 版 `styles.css`；
字体 Win7+ 使用微软雅黑，XP 自动回退宋体；标签页自绘为平面样式（激活项蓝字 + 蓝色下划线）。

## 当前状态

| 模块 | 状态 |
|---|---|
| 文件扫描器（递归 / 扩展名过滤 / 跳过重解析点 / 增量比对） | ✅ 完成 |
| 编码检测（BOM / UTF-8 / UTF-16 / GB18030 / GBK / Big5） | ✅ 完成 |
| 纯文本 / 代码文件提取（40+ 扩展名） | ✅ 完成 |
| OOXML 提取：DOCX / XLSX / PPTX + PK 结构的 .wps / .et / .dps | ✅ 完成（自研 ZIP+XML，零依赖） |
| Lucene.NET 3.0.3 索引 / 检索 + 自研 CJK 二元分词 | ✅ 完成 |
| 批次模型（导入即批次 / 范围过滤 / 更新 / 删除 / 一键清除） | ✅ 完成 |
| 敏感规则检测（正则 / 关键词，文件名+内容，内置默认规则） | ✅ 完成 |
| WinForms 四标签页界面 | ✅ 完成 |
| OLE2 格式（旧版 .wps / .et / .dps、.doc / .xls / .ppt） | ⏳ 规划（接入 NPOI） |
| PDF（Pdfium） / OFD（自研 XML） / 压缩包穿透 / 结果高亮 | ⏳ 按路线图 |

## 运行环境要求

- Windows XP SP3（32 位）/ Vista / 7 / 8 / 10 / 11，x86 或 x64
- .NET Framework 4.0（XP / Win7 需离线安装包，x86 约 48MB；Win7 未内置 4.0 需手动安装）
- 无任何网络依赖、无注册表写入；设置 / 批次 / 规则存于 `%APPDATA%\DocSnifferLegacy\`，
  Lucene 索引默认存于其 `index\` 子目录

## 构建

代码面向 .NET Framework 4.0（`net40`），构建不依赖 Windows：

```bash
# macOS / Linux / Windows 通用（需要任意 .NET SDK 6+）
dotnet build DocSnifferLegacy.sln -c Release
```

Windows 上也可用 Visual Studio 2017+ 打开 `DocSnifferLegacy.sln` 构建（VS2010/2012 无法打开
SDK 风格工程；若必须用旧版 IDE 可转成传统工程文件，目标框架不变）。源码统一使用 C# 5 语法。

## 部署（绿色单目录）

将以下文件复制到同一目录即可运行，无需安装程序：

```
DocSnifferLegacy.exe
DocSnifferLegacy.Core.dll
Lucene.Net.dll
ICSharpCode.SharpZipLib.dll
```

`dotnet build` 后各文件位于 `src/DocSnifferLegacy.App/bin/Release/net40/`。

应用图标来自仓库根目录的 `DocSnifferLegacy.png`（2048×2048）：构建时通过
`src/DocSnifferLegacy.App/DocSnifferLegacy.ico`（16/24/32/48/64/128/256 全 BMP 条目，
兼容不支持 PNG 图标条目的 Windows XP）嵌入 exe（csproj `ApplicationIcon`），
窗体与任务栏图标运行时从 exe 内嵌资源读取。若替换源图，用 Pillow 重新生成：

```bash
python3 -c "from PIL import Image; Image.open('DocSnifferLegacy.png').convert('RGBA')\
.save('src/DocSnifferLegacy.App/DocSnifferLegacy.ico', format='ICO', \
sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)], bitmap_format='bmp')"
```

### 单文件版

Release 构建会额外将 `DocSnifferLegacy.Core.dll`、`Lucene.Net.dll`、`ICSharpCode.SharpZipLib.dll`
用 ILRepack 合并进单一 exe，输出到
`src/DocSnifferLegacy.App/bin/Release/net40/publish/DocSnifferLegacy.exe`（约 1.3 MB）。
合并仅发生在构建期（ILRepack 2.0.48 起可在 macOS/Linux 的 .NET SDK 构建下运行，经 NuGet
包引用随仓库自动还原，无需全局安装），产物仍是面向 .NET Framework 4.0 的普通程序集，
XP SP3 / Win7 运行要求不变；x86 GUI 子系统、应用图标与版本资源均保留。

说明：

- `bin/Release/net40/` 下的多文件产物与单文件产物并存，前者仍供 Net8 验证壳按路径引用。
- 单文件目录内不再生成 `.exe.config`（其内容仅是默认 `supportedRuntime` 声明，可省）。
- 合并目标带增量检查：输入未变化时重复构建会跳过合并；Debug 构建不做合并。


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
- 规则持久化于 `%APPDATA%\DocSnifferLegacy\rules.xml`（与 DocSniffer 的 JSON 各自独立）。

## 架构

```
DocSnifferLegacy.App        WinForms（头部/标签页外壳 + 四页面，主题对齐 Web 版）
├── MainForm                外壳：品牌头部 + 状态徽章 + 平面标签页
├── Pages/Search|Scan|Rules|Batches
├── AppServices             服务容器 + 全局任务忙闸
└── BatchStore / RuleStore  批次与规则 XML 持久化
DocSnifferLegacy.Core
├── IO/FileScanner          递归扫描，重解析点跳过，排除目录
├── IO/ZipReader            自研 ZIP（store/deflate），OOXML 与压缩包穿透共用
├── Routing/FormatRouter    扩展名 → 提取器路由（顺序即优先级）
├── Extract/*               ITextExtractor 管道：OoxmlExtractor / TextFileExtractor
├── Text/EncodingDetector  BOM → UTF-8 严格 → UTF-16 启发式评分 → GB18030 → Big5 → 回退
├── Sensitive/*             规则引擎（正则/关键词，文件名+内容）
└── Index/*                 CjkBigramAnalyzer（二元切分）+ LuceneIndexService（批次字段）
```

索引字段：`path`(存)、`filename`(存+析)、`content`(析不存)、`batch`(存)、`size`、`modified`、`filetype`。
中文按二元切分（相邻两字一个词，孤立单字仅在整段仅一字时成词，保证查询与索引两侧切分一致）；
多关键词默认 OR、按评分排序；查询语法错误自动转义重试。索引锁使用 `SimpleFSLockFactory`。

## 已知限制

- **Big5 繁体**：与 GB18030 字节重叠度高，会被按 GB18030 解出"兼容乱码"（仍可部分检索）。
- **单字中文检索**：二元切分下，被其他汉字包围的单字不单独成词，建议两字以上关键词。
- **OLE2 旧版 WPS / .doc 系**：暂不支持内容提取（可按文件名检索）；接入 NPOI 后解决。
- **PDF / OFD**：暂不支持。
- FAT32 修改时间精度 2 秒，同秒内同大小改写可能漏检增量（NTFS 无此问题）。
- 桌面 WinForms 版没有 DocSniffer 的文件监控自动重索引与 snippet 摘要（按路线图补充）。

## 测试

```bash
dotnet build DocSnifferLegacy.sln -c Debug
mono tests/DocSnifferLegacy.Tests/bin/Debug/net40/DocSnifferLegacy.Tests.exe   # macOS/Linux
# Windows 上直接运行同名 exe
```

覆盖：编码检测 9 组、ZIP 读取器（store/deflate/GBK 文件名/损坏流）、OOXML 三族提取
（富文本/拼音注音/页码域/tab 换行）、扫描+索引+搜索全链路、增量（修改/删除/新增/无变化/
损坏文件容错/文件名检索）、批次（计数/范围过滤/重新导入归属/删除/一键清除）、
敏感检测（正则/关键词/作用范围/非法正则跳过/仅文件名模式）共 64 项断言，当前全部通过。

**非 Windows 开发机的运行时验证**（mono 不可用时，新版 macOS 已无 mono bottle）：

```bash
dotnet run --project tests/DocSnifferLegacy.Tests.Net8 -c Release
```

该壳用 .NET 8 运行时加载 net40 编译产物执行同一套测试源码，仅用于开发机验证；
发布物以 `net40` 产物为准，最终需在真实 XP SP3 / Win7 上回归验证。

## 许可

仅供学习与内部本地使用，请遵守所在地区法律法规及第三方依赖（Lucene.NET、SharpZipLib）的开源许可协议。

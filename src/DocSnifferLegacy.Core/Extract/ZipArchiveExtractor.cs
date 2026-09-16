using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DocSnifferLegacy.Core.IO;
using DocSnifferLegacy.Core.Routing;

namespace DocSnifferLegacy.Core.Extract
{
    /// <summary>
    /// 压缩包穿透：把 ZIP 内部文件拼接为可索引文本（自研 ZipReader）。
    /// 每个条目输出一行 "── 路径 ──" 标头（条目名可检索），可提取的扩展名按
    /// FormatRouter 链式提取内容（嵌套压缩包递归穿透，层数受限）。
    /// 单条目 / 累计解压量 / 条目数均设上限，防 zip 炸弹。
    /// 说明：索引/检索由全局任务闸保证同一时刻单线程使用，深度用 ThreadStatic 兜底。
    /// </summary>
    public sealed class ZipArchiveExtractor : ITextExtractor
    {
        private static readonly string[] Extensions = new[] { ".zip" };

        public const int MaxDepth = 3;                 // 嵌套压缩包层数上限
        public const int MaxEntries = 512;             // 每个压缩包最多处理条目数
        public const long MaxEntryBytes = 32L * 1024 * 1024;   // 单条目解压上限
        public const long MaxTotalBytes = 128L * 1024 * 1024;  // 单压缩包累计解压上限

        private static readonly string[] SkipSuffixes = new[]
        {
            "/.DS_Store", ".DS_Store", "Thumbs.db", "/__MACOSX"
        };

        private readonly ISet<string> extensions;
        private ISet<string> routableExtensions;

        /// <summary>组装后由外壳注入的路由器（用于按扩展名提取内部文件与递归嵌套包）。</summary>
        public FormatRouter InnerRouter { get; set; }

        [ThreadStatic]
        private static int depth;

        public ZipArchiveExtractor()
        {
            extensions = new HashSet<string>(Extensions);
        }

        public ISet<string> SupportedExtensions { get { return extensions; } }

        public string GetFileTypeLabel(string extension) { return "压缩包"; }

        public bool TryExtract(Stream stream, int maxChars, out string text, out string error)
        {
            text = null;
            error = null;
            try
            {
                List<ZipEntryInfo> entries = ZipReader.ReadEntries(stream);
                if (entries.Count == 0)
                {
                    text = string.Empty;
                    return true;
                }
                if (depth >= MaxDepth)
                {
                    text = string.Empty;
                    return true; // 超出嵌套层数：只保留包内文件名行
                }

                if (InnerRouter != null && routableExtensions == null)
                {
                    routableExtensions = new HashSet<string>(InnerRouter.AllExtensions);
                }

                var sb = new StringBuilder();
                int processed = 0;
                long totalBytes = 0;
                depth++;
                try
                {
                    foreach (ZipEntryInfo e in entries)
                    {
                        if (processed >= MaxEntries || sb.Length >= maxChars) break;
                        if (e.IsDirectory || ShouldSkip(e.Name)) continue;

                        processed++;
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append("── ").Append(e.Name).Append(" ──\n");

                        if (e.UncompressedSize <= 0) continue;
                        if (e.UncompressedSize > MaxEntryBytes)
                        {
                            sb.Append("（条目过大，跳过内容）\n");
                            continue;
                        }
                        totalBytes += e.UncompressedSize;
                        if (totalBytes > MaxTotalBytes)
                        {
                            sb.Append("（累计解压量超出上限，停止穿透）\n");
                            break;
                        }

                        string ext = InnerExtension(e.Name);
                        if (InnerRouter == null || !routableExtensions.Contains(ext)) continue;

                        string inner = ExtractEntry(stream, e, ext, maxChars - sb.Length);
                        if (!string.IsNullOrEmpty(inner)) sb.Append(inner).Append('\n');
                    }
                }
                finally
                {
                    depth--;
                }
                text = sb.ToString();
                if (text.Length > maxChars) text = text.Substring(0, maxChars);
                return true;
            }
            catch (Exception ex)
            {
                text = null;
                error = ex.Message;
                return false;
            }
        }

        private string ExtractEntry(Stream zip, ZipEntryInfo e, string ext, int remainChars)
        {
            if (remainChars <= 0) return null;
            // 读入内存再交路由器：内部条目流不可查找（deflate），而 OOXML/OLE2 提取器需要可查找流
            var ms = new MemoryStream((int)Math.Min(e.UncompressedSize, int.MaxValue));
            using (Stream s = ZipReader.OpenEntry(zip, e))
            {
                var buf = new byte[81920];
                long total = 0;
                while (true)
                {
                    int n = s.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    ms.Write(buf, 0, n);
                    total += n;
                    if (total > MaxEntryBytes) break; // 实际数据超出声明时兜底
                }
            }
            byte[] data = ms.ToArray();

            string text = null;
            string error = null;
            if (ext == ".zip")
            {
                // 嵌套压缩包：走自身 TryExtract（深度限制在方法内处理）
                using (var inner = new MemoryStream(data))
                {
                    if (TryExtract(inner, remainChars, out text, out error)) return text;
                    return null;
                }
            }
            using (var inner = new MemoryStream(data))
            {
                if (InnerRouter.TryExtractText(ext, inner, remainChars, out text, out error)) return text;
                return null; // 提取失败仅保留条目名行
            }
        }

        private static string InnerExtension(string name)
        {
            int dot = name.LastIndexOf('.');
            if (dot < 0) return string.Empty;
            return name.Substring(dot).ToLowerInvariant();
        }

        private static bool ShouldSkip(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (name.StartsWith("__MACOSX/", StringComparison.Ordinal)) return true;
            if (name.EndsWith("/__MACOSX", StringComparison.Ordinal)) return true;
            foreach (string suffix in SkipSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}

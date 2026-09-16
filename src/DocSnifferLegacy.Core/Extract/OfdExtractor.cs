using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using DocSnifferLegacy.Core.IO;

namespace DocSnifferLegacy.Core.Extract
{
    /// <summary>
    /// OFD（GB/T 33190 电子公文格式）文本提取：自研 ZIP + XmlReader，无外部依赖。
    /// OFD 是 ZIP 容器，页面文本位于 Doc_x/Pages/Page_y[/Content].xml 的 &lt;TextCode&gt; 元素，
    /// 按文档号与页码自然排序后逐页提取（批注页文件也在 Pages 目录下，一并提取）。
    /// </summary>
    public sealed class OfdExtractor : ITextExtractor
    {
        private static readonly string[] Extensions = new[] { ".ofd" };

        private readonly ISet<string> extensions;

        public OfdExtractor()
        {
            extensions = new HashSet<string>(Extensions);
        }

        public ISet<string> SupportedExtensions { get { return extensions; } }

        public string GetFileTypeLabel(string extension) { return "OFD"; }

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

                // 结构校验：OFD.xml 或 Doc_ 开头的文档目录
                bool hasOfdRoot = false;
                bool hasDocDir = false;
                var pageEntries = new List<ZipEntryInfo>();
                foreach (ZipEntryInfo e in entries)
                {
                    if (e.IsDirectory) continue;
                    if (e.Name == "OFD.xml") hasOfdRoot = true;
                    if (e.Name.StartsWith("Doc_", StringComparison.Ordinal) || e.Name.StartsWith("Doc_/", StringComparison.Ordinal))
                    {
                        hasDocDir = true;
                    }
                    if (IsPageXml(e.Name)) pageEntries.Add(e);
                }
                if (!hasOfdRoot && !hasDocDir)
                {
                    error = "可识别的 ZIP 容器，但未找到 OFD 文档结构";
                    return false;
                }

                // 无规范页面路径时，回退扫描 Doc_ 目录下的全部 xml
                if (pageEntries.Count == 0)
                {
                    foreach (ZipEntryInfo e in entries)
                    {
                        if (e.IsDirectory) continue;
                        if (e.Name.StartsWith("Doc_", StringComparison.Ordinal) &&
                            e.Name.EndsWith(".xml", StringComparison.Ordinal) &&
                            !e.Name.Contains("_rels"))
                        {
                            pageEntries.Add(e);
                        }
                    }
                }
                pageEntries.Sort(CompareEntryNames);

                var sb = new StringBuilder();
                foreach (ZipEntryInfo e in pageEntries)
                {
                    if (sb.Length >= maxChars) break;
                    using (Stream s = ZipReader.OpenEntry(stream, e))
                    using (XmlReader r = CreateXmlReader(s))
                    {
                        ExtractPage(r, sb, maxChars);
                    }
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

        /// <summary>页面内容：Doc_x/Pages/ 下的 xml（Content.xml、Annot_xx.xml 等）。</summary>
        private static bool IsPageXml(string name)
        {
            if (name == null || !name.EndsWith(".xml", StringComparison.Ordinal)) return false;
            if (name.Contains("_rels")) return false;
            int pages = name.IndexOf("/Pages/", StringComparison.Ordinal);
            if (pages < 0) return false;
            return name.StartsWith("Doc_", StringComparison.Ordinal);
        }

        private static int CompareEntryNames(ZipEntryInfo a, ZipEntryInfo b)
        {
            int da = PathNumber(a.Name, "Doc_"), db = PathNumber(b.Name, "Doc_");
            if (da != db) return da.CompareTo(db);
            int pa = PathNumber(a.Name, "Page_"), pb = PathNumber(b.Name, "Page_");
            if (pa != pb) return pa.CompareTo(pb);
            return string.CompareOrdinal(a.Name, b.Name);
        }

        /// <summary>取 name 前缀后的数字（Doc_0 / Page_12）；无数字返回 int.MaxValue 保持稳定。</summary>
        private static int PathNumber(string name, string prefix)
        {
            int start = name.LastIndexOf(prefix, StringComparison.Ordinal);
            if (start < 0) return int.MaxValue;
            start += prefix.Length;
            int end = start;
            while (end < name.Length && name[end] >= '0' && name[end] <= '9') end++;
            int v;
            if (end > start && int.TryParse(name.Substring(start, end - start), out v)) return v;
            return int.MaxValue;
        }

        /// <summary>提取页面 XML：TextObject 内的 TextCode 文本，TextObject 之间换行。</summary>
        private static void ExtractPage(XmlReader r, StringBuilder sb, int maxChars)
        {
            bool inTextObject = false;
            while (true)
            {
                if (sb.Length >= maxChars) return;
                if (r.NodeType == XmlNodeType.Element)
                {
                    string local = r.LocalName;
                    if (local == "TextObject") inTextObject = true;
                    else if (local == "TextCode" && inTextObject)
                    {
                        AppendElementText(r, sb);
                        continue;
                    }
                }
                else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "TextObject")
                {
                    inTextObject = false;
                    if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                }
                if (!r.Read()) return;
            }
        }

        private static void AppendElementText(XmlReader r, StringBuilder sb)
        {
            try { sb.Append(r.ReadElementContentAsString()); }
            catch (XmlException) { try { r.Skip(); } catch (Exception) { } }
        }

        private static XmlReader CreateXmlReader(Stream s)
        {
            var settings = new XmlReaderSettings();
            settings.DtdProcessing = DtdProcessing.Prohibit;
            settings.XmlResolver = null;
            settings.IgnoreComments = true;
            settings.IgnoreProcessingInstructions = true;
            settings.IgnoreWhitespace = true;
            settings.CloseInput = false;
            return XmlReader.Create(s, settings);
        }
    }
}

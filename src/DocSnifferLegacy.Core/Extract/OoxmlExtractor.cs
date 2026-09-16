using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using DocSnifferLegacy.Core.IO;

namespace DocSnifferLegacy.Core.Extract
{
    /// <summary>
    /// OOXML 文本提取（自研 ZIP + XmlReader，无外部依赖）。
    /// 覆盖 DOCX/XLSX/PPTX/DOCM/XLSM/PPTM，以及内容为 OOXML 结构的 WPS 文件（.wps/.et/.dps）。
    /// 内部按条目路径识别文档族：word/* → Word，xl/* → Excel，ppt/slides → PPT。
    /// </summary>
    public sealed class OoxmlExtractor : ITextExtractor
    {
        private static readonly string[] Extensions = new[]
        {
            ".docx", ".docm", ".xlsx", ".xlsm", ".pptx", ".pptm", ".wps", ".et", ".dps"
        };

        private readonly ISet<string> extensions;

        public OoxmlExtractor()
        {
            extensions = new HashSet<string>(Extensions);
        }

        public ISet<string> SupportedExtensions { get { return extensions; } }

        public string GetFileTypeLabel(string extension)
        {
            switch ((extension ?? string.Empty).ToLowerInvariant())
            {
                case ".docx": case ".docm": case ".wps": return "Word";
                case ".xlsx": case ".xlsm": case ".et": return "Excel";
                case ".pptx": case ".pptm": case ".dps": return "PPT";
                default: return "OOXML";
            }
        }

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

                if (HasWordParts(entries)) text = ExtractWord(stream, entries, maxChars);
                else if (HasSheetParts(entries)) text = ExtractWorkbook(stream, entries, maxChars);
                else if (HasSlideParts(entries)) text = ExtractSlides(stream, entries, maxChars);
                else
                {
                    error = "可识别的 ZIP 容器，但未找到已知的 OOXML 文档结构";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool HasWordParts(List<ZipEntryInfo> entries)
        {
            foreach (ZipEntryInfo e in entries)
            {
                if (e.IsDirectory) continue;
                if (e.Name == "word/document.xml") return true;
            }
            return false;
        }

        private static bool HasSheetParts(List<ZipEntryInfo> entries)
        {
            foreach (ZipEntryInfo e in entries)
            {
                if (e.IsDirectory) continue;
                if (e.Name == "xl/sharedStrings.xml" || e.Name.StartsWith("xl/worksheets/", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static bool HasSlideParts(List<ZipEntryInfo> entries)
        {
            foreach (ZipEntryInfo e in entries)
            {
                if (e.IsDirectory) continue;
                if (e.Name.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && e.Name.EndsWith(".xml", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // ---------- Word ----------

        private static string ExtractWord(Stream stream, List<ZipEntryInfo> entries, int maxChars)
        {
            var parts = new List<string>();
            parts.Add("word/document.xml");
            parts.Add("word/footnotes.xml");
            parts.Add("word/endnotes.xml");
            foreach (ZipEntryInfo e in entries)
            {
                if (e.IsDirectory) continue;
                string n = e.Name;
                if (n.StartsWith("word/header", StringComparison.Ordinal) ||
                    n.StartsWith("word/footer", StringComparison.Ordinal))
                {
                    if (n.EndsWith(".xml", StringComparison.Ordinal)) parts.Add(n);
                }
            }

            var sb = new StringBuilder();
            foreach (string part in parts)
            {
                ZipEntryInfo entry = FindEntry(entries, part);
                if (entry == null) continue;
                using (Stream s = ZipReader.OpenEntry(stream, entry))
                using (XmlReader r = CreateXmlReader(s))
                {
                    ExtractWordPart(r, sb, maxChars);
                }
                if (sb.Length >= maxChars) break;
            }
            return Finish(sb, maxChars);
        }

        private static void ExtractWordPart(XmlReader r, StringBuilder sb, int maxChars)
        {
            // 模式：先处理当前节点，循环底部再推进。ReadElementContentAsString 会使读取器
            // 落在下一个节点上，此时必须 continue 重新检查，否则紧随其后的 <w:tab/> 等会被跳过。
            while (true)
            {
                if (sb.Length >= maxChars) return;
                if (r.NodeType == XmlNodeType.Element)
                {
                    string local = r.LocalName;
                    if (local == "t")
                    {
                        AppendElementText(r, sb);
                        continue;
                    }
                    if (local == "p")
                    {
                        if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                    }
                    else if (local == "tab")
                    {
                        sb.Append('\t');
                    }
                    else if (local == "br" || local == "cr")
                    {
                        sb.Append('\n');
                    }
                }
                if (!r.Read()) return;
            }
        }

        // ---------- Excel ----------

        private static string ExtractWorkbook(Stream stream, List<ZipEntryInfo> entries, int maxChars)
        {
            var shared = new List<string>();
            ZipEntryInfo ss = FindEntry(entries, "xl/sharedStrings.xml");
            if (ss != null)
            {
                using (Stream s = ZipReader.OpenEntry(stream, ss))
                using (XmlReader r = CreateXmlReader(s))
                {
                    ReadSharedStrings(r, shared);
                }
            }

            var sb = new StringBuilder();
            foreach (ZipEntryInfo e in entries)
            {
                if (e.IsDirectory) continue;
                if (!e.Name.StartsWith("xl/worksheets/", StringComparison.Ordinal)) continue;
                if (!e.Name.EndsWith(".xml", StringComparison.Ordinal)) continue;
                if (e.Name.Contains("_rels")) continue;

                using (Stream s = ZipReader.OpenEntry(stream, e))
                using (XmlReader r = CreateXmlReader(s))
                {
                    ExtractSheet(r, shared, sb, maxChars);
                }
                sb.Append('\n');
                if (sb.Length >= maxChars) break;
            }
            return Finish(sb, maxChars);
        }

        private static void ReadSharedStrings(XmlReader r, List<string> shared)
        {
            while (true)
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "si")
                {
                    var item = new StringBuilder();
                    ReadSiContent(r, item);
                    shared.Add(item.ToString());
                    continue; // ReadSiContent 已推进到 </si> 之后
                }
                if (!r.Read()) return;
            }
        }

        private static void ReadSiContent(XmlReader r, StringBuilder item)
        {
            if (r.IsEmptyElement) { r.Read(); return; }
            int siDepth = r.Depth;
            while (true)
            {
                if (r.NodeType == XmlNodeType.Element)
                {
                    if (r.LocalName == "rPh") { r.Skip(); continue; } // 跳过拼音注音
                    if (r.LocalName == "t")
                    {
                        AppendElementText(r, item);
                        continue;
                    }
                }
                else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "si" && r.Depth == siDepth)
                {
                    r.Read(); // 消费 </si>，落在下一节点
                    return;
                }
                if (!r.Read()) return;
            }
        }

        private static void ExtractSheet(XmlReader r, List<string> shared, StringBuilder sb, int maxChars)
        {
            string cellType = null;
            var cellValue = new StringBuilder();
            bool inCell = false;
            while (true)
            {
                if (sb.Length >= maxChars) return;
                if (r.NodeType == XmlNodeType.Element)
                {
                    string local = r.LocalName;
                    if (local == "c")
                    {
                        inCell = true;
                        cellType = r.GetAttribute("t");
                        cellValue.Length = 0;
                    }
                    else if (local == "v" && inCell)
                    {
                        AppendElementText(r, cellValue);
                        continue;
                    }
                    else if (local == "is" && inCell)
                    {
                        AppendInlineString(r, cellValue);
                        continue; // 已推进到 </is> 之后
                    }
                }
                else if (r.NodeType == XmlNodeType.EndElement)
                {
                    string local = r.LocalName;
                    if (local == "c" && inCell)
                    {
                        string resolved = ResolveCell(cellType, cellValue.ToString(), shared);
                        if (resolved.Length > 0)
                        {
                            if (sb.Length > 0) sb.Append('\t');
                            sb.Append(resolved);
                        }
                        inCell = false;
                    }
                    else if (local == "row")
                    {
                        sb.Append('\n');
                    }
                }
                if (!r.Read()) return;
            }
        }

        private static void AppendInlineString(XmlReader r, StringBuilder cellValue)
        {
            if (r.IsEmptyElement) { r.Read(); return; }
            int depth = r.Depth;
            while (true)
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "t")
                {
                    AppendElementText(r, cellValue);
                    continue;
                }
                if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "is" && r.Depth == depth)
                {
                    r.Read(); // 消费 </is>，落在下一节点
                    return;
                }
                if (!r.Read()) return;
            }
        }

        private static string ResolveCell(string type, string raw, List<string> shared)
        {
            if (type == "s")
            {
                int idx;
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx) &&
                    idx >= 0 && idx < shared.Count)
                {
                    return shared[idx];
                }
                return string.Empty;
            }
            return raw; // str/n/b/e/inlineStr 均取原始值
        }

        // ---------- PPT ----------

        private static string ExtractSlides(Stream stream, List<ZipEntryInfo> entries, int maxChars)
        {
            var slides = new List<ZipEntryInfo>();
            foreach (ZipEntryInfo e in entries)
            {
                if (e.IsDirectory) continue;
                if ((e.Name.StartsWith("ppt/slides/slide", StringComparison.Ordinal) ||
                     e.Name.StartsWith("ppt/notesSlides/notesSlide", StringComparison.Ordinal)) &&
                    e.Name.EndsWith(".xml", StringComparison.Ordinal))
                {
                    slides.Add(e);
                }
            }
            slides.Sort(delegate(ZipEntryInfo a, ZipEntryInfo b)
            {
                int na = SlideNumber(a.Name), nb = SlideNumber(b.Name);
                if (na != nb) return na.CompareTo(nb);
                return string.CompareOrdinal(a.Name, b.Name);
            });

            var sb = new StringBuilder();
            foreach (ZipEntryInfo e in slides)
            {
                using (Stream s = ZipReader.OpenEntry(stream, e))
                using (XmlReader r = CreateXmlReader(s))
                {
                    ExtractPptPart(r, sb, maxChars);
                }
                sb.Append('\n');
                if (sb.Length >= maxChars) break;
            }
            return Finish(sb, maxChars);
        }

        private static void ExtractPptPart(XmlReader r, StringBuilder sb, int maxChars)
        {
            int fldDepth = -1;
            while (true)
            {
                if (sb.Length >= maxChars) return;
                if (r.NodeType == XmlNodeType.Element)
                {
                    string local = r.LocalName;
                    if (local == "fld") { fldDepth = r.Depth; } // 跳过页码等域文本
                    else if (local == "p" && fldDepth < 0)
                    {
                        if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                    }
                    else if (local == "t" && fldDepth < 0)
                    {
                        AppendElementText(r, sb);
                        continue;
                    }
                }
                else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "fld" && r.Depth == fldDepth)
                {
                    fldDepth = -1;
                }
                if (!r.Read()) return;
            }
        }

        private static int SlideNumber(string name)
        {
            int start = name.LastIndexOf("Slide", StringComparison.Ordinal);
            if (start < 0) start = name.LastIndexOf("slide", StringComparison.Ordinal);
            if (start < 0) return int.MaxValue;
            start += 5;
            int end = start;
            while (end < name.Length && name[end] >= '0' && name[end] <= '9') end++;
            int v;
            if (int.TryParse(name.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return int.MaxValue;
        }

        // ---------- 公共 ----------

        private static ZipEntryInfo FindEntry(List<ZipEntryInfo> entries, string name)
        {
            foreach (ZipEntryInfo e in entries)
            {
                if (!e.IsDirectory && e.Name == name) return e;
            }
            return null;
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

        private static string Finish(StringBuilder sb, int maxChars)
        {
            string t = sb.ToString();
            if (t.Length > maxChars) t = t.Substring(0, maxChars);
            return t;
        }
    }
}

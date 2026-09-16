using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DocSnifferLegacy.Core.Text;
using NPOI.POIFS.FileSystem;

namespace DocSnifferLegacy.Core.Extract
{
    /// <summary>
    /// OLE2 复合文档文本提取（容器层接入 NPOI POIFS，内容层自研解析）。
    /// 覆盖 .doc（Word 97~95 分片表解析）、.xls（NPOI HSSF）、.ppt（记录树扫描 TextAtom），
    /// 以及旧版 WPS 的 .wps/.et/.dps：doc 兼容结构直接走 .doc 解析，其余按流内容启发式挖掘。
    /// 结构识别不依赖扩展名，按根存储内的流名自动分派。
    /// </summary>
    public sealed class Ole2Extractor : ITextExtractor
    {
        private static readonly string[] Extensions = new[]
        {
            ".doc", ".xls", ".ppt", ".wps", ".et", ".dps"
        };

        private static readonly byte[] Ole2Magic = new byte[]
        {
            0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1
        };

        /// <summary>整个复合文档读入内存的上限（索引侧另有单文件上限把关）。</summary>
        public const long MaxContainerBytes = 64L * 1024 * 1024;
        /// <summary>启发式挖掘单个流的上限。</summary>
        private const int MaxMineStreamBytes = 8 * 1024 * 1024;
        /// <summary>挖掘片段的最短可读长度。</summary>
        private const int MinMineRunChars = 3;

        private const ushort WordMagic = 0xA5EC;
        private const uint FcCompressedBit = 0x40000000u;
        private const uint FcMask = 0x3FFFFFFFu;

        private const ushort PptTextCharsAtom = 0x0FA0;   // UTF-16LE 文本
        private const ushort PptTextBytesAtom = 0x0FA8;   // ANSI 单字节文本

        private readonly ISet<string> extensions;
        private static readonly Encoding Cp1252 = SafeEncoding(1252);

        public Ole2Extractor()
        {
            extensions = new HashSet<string>(Extensions);
        }

        public ISet<string> SupportedExtensions { get { return extensions; } }

        public string GetFileTypeLabel(string extension)
        {
            switch ((extension ?? string.Empty).ToLowerInvariant())
            {
                case ".doc": case ".wps": return "Word";
                case ".xls": case ".et": return "Excel";
                case ".ppt": case ".dps": return "PPT";
                default: return "OLE2";
            }
        }

        public bool TryExtract(Stream stream, int maxChars, out string text, out string error)
        {
            text = null;
            error = null;
            try
            {
                byte[] head = new byte[8];
                if (!ReadFully(stream, head, 8) || !MagicEquals(head))
                {
                    error = "非 OLE2 复合文档（缺少魔数 D0 CF 11 E0）";
                    return false;
                }
                stream.Position = 0;

                byte[] data = ReadAll(stream, MaxContainerBytes);
                NPOIFSFileSystem fs = new NPOIFSFileSystem(new MemoryStream(data));
                try
                {
                    text = ExtractRoot(fs.Root, data, maxChars);
                    return true;
                }
                finally
                {
                    try { fs.Close(); } catch (Exception) { }
                }
            }
            catch (Exception ex)
            {
                text = null;
                error = ex.Message;
                return false;
            }
        }

        private static bool MagicEquals(byte[] head)
        {
            for (int i = 0; i < 8; i++)
            {
                if (head[i] != Ole2Magic[i]) return false;
            }
            return true;
        }

        /// <summary>按根存储的流名分派：Workbook → Excel，WordDocument → Word，PowerPoint → PPT，否则启发式挖掘。</summary>
        private static string ExtractRoot(DirectoryEntry root, byte[] containerBytes, int maxChars)
        {
            IList<string> names = NameList(root);

            if (Contains(names, "Workbook") || Contains(names, "Book"))
            {
                string xls = TryExtractXls(containerBytes, maxChars);
                if (xls != null) return xls;
            }

            if (Contains(names, "WordDocument"))
            {
                string doc = TryExtractDoc(root, maxChars);
                if (!string.IsNullOrEmpty(doc)) return doc;
            }

            if (Contains(names, "PowerPoint Document"))
            {
                string ppt = TryExtractPpt(root, maxChars);
                if (!string.IsNullOrEmpty(ppt)) return ppt;
            }

            return MineRoot(root, maxChars);
        }

        private static IList<string> NameList(DirectoryEntry dir)
        {
            var names = new List<string>();
            IEnumerator<NPOI.POIFS.FileSystem.Entry> it = dir.Entries;
            while (it.MoveNext()) names.Add(it.Current.Name);
            return names;
        }

        private static bool Contains(IList<string> names, string name)
        {
            foreach (string n in names)
            {
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static Stream OpenDocument(DirectoryEntry dir, string name)
        {
            IEnumerator<NPOI.POIFS.FileSystem.Entry> it = dir.Entries;
            while (it.MoveNext())
            {
                if (!it.Current.IsDocumentEntry) continue;
                if (!string.Equals(it.Current.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                return new NDocumentInputStream((DocumentEntry)it.Current);
            }
            return null;
        }

        private static byte[] ReadDocument(DirectoryEntry dir, string name, int maxBytes)
        {
            Stream s = OpenDocument(dir, name);
            if (s == null) return null;
            using (s)
            {
                return ReadAll(s, maxBytes);
            }
        }

        // ---------- Excel（NPOI HSSF） ----------

        private static string TryExtractXls(byte[] containerBytes, int maxChars)
        {
            try
            {
                // HSSFWorkbook 自行解析容器：直接吃整个文档字节。
                // 结构分派阶段已确认存在 Workbook/Book 流，解析失败则退回挖掘。
                if (containerBytes == null) return null;
                NPOI.HSSF.UserModel.HSSFWorkbook workbook = null;
                try
                {
                    workbook = new NPOI.HSSF.UserModel.HSSFWorkbook(new MemoryStream(containerBytes));
                }
                catch (Exception)
                {
                    return null; // BIFF5（Excel 5/95）等旧结构 → 交挖掘兜底
                }

                var formatter = new NPOI.HSSF.UserModel.HSSFDataFormatter();
                var sb = new StringBuilder();
                for (int si = 0; si < workbook.NumberOfSheets && sb.Length < maxChars; si++)
                {
                    NPOI.SS.UserModel.ISheet sheet = workbook.GetSheetAt(si);
                    if (sheet == null) continue;
                    for (int r = sheet.FirstRowNum; r <= sheet.LastRowNum && sb.Length < maxChars; r++)
                    {
                        NPOI.SS.UserModel.IRow row = sheet.GetRow(r);
                        if (row == null) continue;
                        bool rowHasText = false;
                        for (int c = row.FirstCellNum; c >= 0 && c < row.LastCellNum; c++)
                        {
                            NPOI.SS.UserModel.ICell cell = row.GetCell(c);
                            if (cell == null) continue;
                            string v = FormatCell(cell, formatter);
                            if (v == null || v.Length == 0) continue;
                            if (rowHasText) sb.Append('\t');
                            sb.Append(v);
                            rowHasText = true;
                            if (sb.Length >= maxChars) break;
                        }
                        if (rowHasText) sb.Append('\n');
                    }
                }
                try { workbook.Close(); } catch (Exception) { }
                return Finish(sb, maxChars);
            }
            catch (Exception)
            {
                return null; // 交由上层挖掘兜底
            }
        }

        private static string FormatCell(NPOI.SS.UserModel.ICell cell, NPOI.HSSF.UserModel.HSSFDataFormatter formatter)
        {
            try
            {
                string v = formatter.FormatCellValue(cell);
                if (v != null && v.Length > 0) return v;
            }
            catch (Exception) { }
            try
            {
                if (cell.CellType == NPOI.SS.UserModel.CellType.String) return cell.StringCellValue;
            }
            catch (Exception) { }
            return null;
        }

        // ---------- Word（FIB + 分片表自研解析） ----------

        private static string TryExtractDoc(DirectoryEntry root, int maxChars)
        {
            try
            {
                byte[] wd = ReadDocument(root, "WordDocument", int.MaxValue);
                if (wd == null || wd.Length < 0x1AA) return null;

                ushort wIdent = (ushort)Le16(wd, 0x00);
                if (wIdent != WordMagic) return null; // 非Word/WPS文字结构 → 挖掘
                ushort flags = (ushort)Le16(wd, 0x0A);
                if ((flags & 0x0100) != 0) throw new InvalidDataException("文档已加密，无法提取文本");

                string table = (flags & 0x0200) != 0 ? "1Table" : "0Table"; // fWhichTblStm
                byte[] tbl = ReadDocument(root, table, int.MaxValue);
                if (tbl == null) tbl = ReadDocument(root, table == "1Table" ? "0Table" : "1Table", int.MaxValue);

                ushort nFib = (ushort)Le16(wd, 0x02);
                if (nFib >= 0x00C1 && tbl != null) // Word 97+：分片表
                {
                    uint fcClx = Le32u(wd, 0x01A2);
                    uint lcbClx = Le32u(wd, 0x01A6);
                    if (lcbClx > 0 && fcClx + lcbClx <= (uint)tbl.Length)
                    {
                        string s = TextFromPieceTable(wd, tbl, fcClx, lcbClx, maxChars);
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }

                // Word 6/95（或无分片表）：fcMin..fcMac 连续文本
                uint fcMin = Le32u(wd, 0x18);
                uint fcMac = Le32u(wd, 0x1C);
                if (fcMac > fcMin && fcMac <= (uint)wd.Length)
                {
                    int bytes = (int)(fcMac - fcMin);
                    if ((flags & 0x4000) != 0) // fExtChar → UTF-16LE
                    {
                        return Finish(new StringBuilder(CleanDocText(Encoding.Unicode.GetString(wd, (int)fcMin, bytes & ~1))), maxChars);
                    }
                    var slice = new byte[bytes];
                    Array.Copy(wd, (int)fcMin, slice, 0, bytes);
                    TextDecodeResult dr = EncodingDetector.Decode(slice, bytes);
                    return Finish(new StringBuilder(CleanDocText(dr.Text ?? string.Empty)), maxChars);
                }
                return null;
            }
            catch (InvalidDataException)
            {
                throw; // 加密等明确错误向上抛
            }
            catch (Exception)
            {
                return null; // 结构异常 → 挖掘兜底
            }
        }

        /// <summary>解析 CLX（Pcdt/PlcPcd），按 CP 顺序拼接各分片文本（含页眉页脚脚注等故事）。</summary>
        private static string TextFromPieceTable(byte[] wd, byte[] tbl, uint fcClx, uint lcbClx, int maxChars)
        {
            int pos = (int)fcClx;
            int end = pos + (int)lcbClx;
            while (pos < end) // 跳过 Prc（clxt=1），定位 Pcdt（clxt=2）
            {
                byte clxt = tbl[pos];
                if (clxt == 1)
                {
                    if (pos + 3 > end) return null;
                    int cb = Le16(tbl, pos + 1);
                    pos += 3 + cb;
                }
                else if (clxt == 2)
                {
                    pos++;
                    break;
                }
                else
                {
                    return null;
                }
            }
            if (pos + 4 > end) return null;
            int lcb = (int)Le32u(tbl, pos);
            pos += 4;
            int n = (lcb - 4) / 12;
            if (n <= 0 || pos + lcb > end) return null;

            var sb = new StringBuilder();
            for (int i = 0; i < n && sb.Length < maxChars; i++)
            {
                uint cpStart = Le32u(tbl, pos + i * 4);
                uint cpEnd = Le32u(tbl, pos + (i + 1) * 4);
                if (cpEnd <= cpStart) continue;
                int pcdOff = pos + (n + 1) * 4 + i * 8;
                uint fc = Le32u(tbl, pcdOff + 2);
                int count = (int)Math.Min(cpEnd - cpStart, (uint)(maxChars - sb.Length));

                if ((fc & FcCompressedBit) != 0)
                {
                    int off = (int)((fc & FcMask) >> 1); // 8 位压缩文本：字节偏移 = fc/2
                    if (off < 0 || off + count > wd.Length) continue;
                    sb.Append(CleanDocText(Cp1252.GetString(wd, off, count)));
                }
                else
                {
                    int off = (int)(fc & FcMask);
                    int bytes = count * 2;
                    if (off < 0 || off + bytes > wd.Length) continue;
                    sb.Append(CleanDocText(Encoding.Unicode.GetString(wd, off, bytes)));
                }
            }
            return Finish(sb, maxChars);
        }

        /// <summary>Word 文本清理：控制字符转换、域指令丢弃（\x13..&lt;instr&gt;\x14..&lt;result&gt;\x15）。</summary>
        private static string CleanDocText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            var sb = new StringBuilder(raw.Length);
            var fieldStack = new List<bool>(); // 每层域是否处于"保留结果"状态
            bool keep = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                switch (c)
                {
                    case '\x13': // 域开始
                        fieldStack.Add(keep);
                        keep = false;
                        continue;
                    case '\x14': // 域分隔：进入结果区
                        keep = fieldStack.Count > 0;
                        continue;
                    case '\x15': // 域结束
                        if (fieldStack.Count > 0) keep = fieldStack[fieldStack.Count - 1];
                        if (fieldStack.Count > 0) fieldStack.RemoveAt(fieldStack.Count - 1);
                        continue;
                    case '\r':
                    case '\x0b': // 软回车
                    case '\x0c': // 分页符
                        sb.Append('\n');
                        continue;
                    case '\x07': // 单元格/行结束
                        sb.Append('\t');
                        continue;
                    case '\x1e': // 不换行连字符
                        sb.Append('-');
                        continue;
                    default:
                        if (c < ' ' && c != '\t' && c != '\n') continue; // 其余控制字符丢弃
                        if (c == '\x1f' || c == '\x01' || c == '\x05' || c == '\x08') continue;
                        if (keep || fieldStack.Count == 0) sb.Append(c);
                        continue;
                }
            }
            return sb.ToString();
        }

        // ---------- PowerPoint（记录树扫描） ----------

        private static string TryExtractPpt(DirectoryEntry root, int maxChars)
        {
            try
            {
                var sb = new StringBuilder();
                IEnumerator<NPOI.POIFS.FileSystem.Entry> it = root.Entries;
                while (it.MoveNext() && sb.Length < maxChars)
                {
                    if (!it.Current.IsDocumentEntry) continue;
                    if (!string.Equals(it.Current.Name, "PowerPoint Document", StringComparison.OrdinalIgnoreCase)) continue;
                    byte[] data = ReadAll(new NDocumentInputStream((DocumentEntry)it.Current), MaxMineStreamBytes);
                    WalkPptRecords(data, 0, data.Length, sb, maxChars, 0);
                }
                return Finish(sb, maxChars);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WalkPptRecords(byte[] data, int start, int end, StringBuilder sb, int maxChars, int depth)
        {
            int pos = start;
            while (pos + 8 <= end && sb.Length < maxChars)
            {
                int recVer = Le16(data, pos) & 0xF;
                ushort recType = (ushort)Le16(data, pos + 2);
                int recLen = (int)Math.Min(Le32u(data, pos + 4), (uint)(end - pos - 8));
                int body = pos + 8;
                int bodyEnd = body + recLen;
                if (recVer == 0xF && depth < 48)
                {
                    WalkPptRecords(data, body, bodyEnd, sb, maxChars, depth + 1); // 容器记录：递归
                }
                else if (recType == PptTextCharsAtom) // UTF-16LE
                {
                    if (recLen >= 2) sb.Append(CleanPptText(Encoding.Unicode.GetString(data, body, recLen & ~1)));
                }
                else if (recType == PptTextBytesAtom) // ANSI 单字节
                {
                    if (recLen > 0)
                    {
                        var slice = new byte[recLen];
                        Array.Copy(data, body, slice, 0, recLen);
                        TextDecodeResult dr = EncodingDetector.Decode(slice, recLen);
                        sb.Append(CleanPptText(dr.Text ?? string.Empty));
                    }
                }
                pos = bodyEnd > pos ? bodyEnd : pos + 8; // 损坏长度容错
            }
        }

        private static string CleanPptText(string raw)
        {
            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '\r' || c == '\x0b') sb.Append('\n');
                else if (c == '\x1e') sb.Append('-');
                else if (c >= ' ' || c == '\t') { if (c != '\x1f') sb.Append(c); }
            }
            return sb.ToString();
        }

        // ---------- 旧版 WPS / ET / DPS 启发式挖掘 ----------

        private static string MineRoot(DirectoryEntry root, int maxChars)
        {
            try
            {
                var sb = new StringBuilder();
                MineDirectory(root, sb, maxChars, 0);
                return Finish(sb, maxChars);
            }
            catch (Exception)
            {
                return string.Empty; // 挖掘尽力而为，失败不致命
            }
        }

        private static void MineDirectory(DirectoryEntry dir, StringBuilder sb, int maxChars, int depth)
        {
            IEnumerator<NPOI.POIFS.FileSystem.Entry> it = dir.Entries;
            while (it.MoveNext() && sb.Length < maxChars)
            {
                NPOI.POIFS.FileSystem.Entry e = it.Current;
                if (e.IsDirectoryEntry)
                {
                    if (depth < 8) MineDirectory((DirectoryEntry)e, sb, maxChars, depth + 1);
                }
                else if (e.IsDocumentEntry)
                {
                    DocumentEntry de = (DocumentEntry)e;
                    if (de.Size < 32 || de.Size > MaxMineStreamBytes) continue;
                    try
                    {
                        string mined = MineStream(ReadAll(new NDocumentInputStream(de), MaxMineStreamBytes));
                        if (mined.Length > 0)
                        {
                            if (sb.Length > 0) sb.Append('\n');
                            sb.Append(mined);
                        }
                    }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>解码单个流并提取可读片段：过滤控制字符与替换符，保留长度达标且含实义字符的连续片段。</summary>
        private static string MineStream(byte[] data)
        {
            if (data == null || data.Length < 4) return string.Empty;
            TextDecodeResult dr = EncodingDetector.Decode(data, data.Length);
            string decoded = dr.Text;
            if (string.IsNullOrEmpty(decoded)) return string.Empty;

            var sb = new StringBuilder();
            var run = new StringBuilder();
            for (int i = 0; i < decoded.Length; i++)
            {
                char c = decoded[i];
                if (char.IsControl(c) || c == '\uFFFD')
                {
                    FlushRun(sb, run);
                }
                else if (char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsWhiteSpace(c) ||
                         (c >= 0x2E80 && c <= 0x9FFF) ||   // CJK 部首/符号/汉字
                         (c >= 0xFF00 && c <= 0xFFEF))     // 全角字符
                {
                    run.Append(c);
                }
                else
                {
                    FlushRun(sb, run);
                }
            }
            FlushRun(sb, run);
            return sb.ToString();
        }

        private static void FlushRun(StringBuilder sb, StringBuilder run)
        {
            if (run.Length == 0) return;
            string s = run.ToString().Trim();
            run.Length = 0;
            if (s.Length < MinMineRunChars) return;
            bool hasContent = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsLetterOrDigit(c) || (c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xFF00 && c <= 0xFFEF))
                {
                    hasContent = true;
                    break;
                }
            }
            if (!hasContent) return;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(s);
        }

        // ---------- 公共工具 ----------

        private static byte[] ReadAll(Stream s, long maxBytes)
        {
            var ms = new MemoryStream();
            var buf = new byte[81920];
            long total = 0;
            while (true)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                if (total + n > maxBytes) throw new IOException("流超出读取上限");
                ms.Write(buf, 0, n);
                total += n;
            }
            return ms.ToArray();
        }

        private static bool ReadFully(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        private static int Le16(byte[] b, int off) { return b[off] | (b[off + 1] << 8); }

        private static uint Le32u(byte[] b, int off)
        {
            return (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
        }

        private static Encoding SafeEncoding(int codePage)
        {
            try { return Encoding.GetEncoding(codePage); }
            catch (Exception) { return new Latin1Encoding(); }
        }

        private sealed class Latin1Encoding : Encoding
        {
            public override int GetByteCount(char[] chars, int index, int count) { return count; }
            public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
            {
                for (int i = 0; i < charCount; i++) bytes[byteIndex + i] = (byte)chars[charIndex + i];
                return charCount;
            }
            public override int GetCharCount(byte[] bytes, int index, int count) { return count; }
            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
            {
                for (int i = 0; i < byteCount; i++) chars[charIndex + i] = (char)bytes[byteIndex + i];
                return byteCount;
            }
            public override int GetMaxByteCount(int charCount) { return charCount; }
            public override int GetMaxCharCount(int byteCount) { return byteCount; }
        }

        private static string Finish(StringBuilder sb, int maxChars)
        {
            string t = sb.ToString();
            if (t.Length > maxChars) t = t.Substring(0, maxChars);
            return t;
        }
    }
}

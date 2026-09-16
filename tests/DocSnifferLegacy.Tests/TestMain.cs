using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DocSnifferLegacy.Core.Extract;
using DocSnifferLegacy.Core.Index;
using DocSnifferLegacy.Core.IO;
using DocSnifferLegacy.Core.Routing;
using DocSnifferLegacy.Core.Search;
using DocSnifferLegacy.Core.Sensitive;
using DocSnifferLegacy.Core.Text;
using NPOI.HSSF.UserModel;
using NPOI.POIFS.FileSystem;
using NPOI.SS.UserModel;

namespace DocSnifferLegacy.Tests
{
    internal static class TestMain
    {
        private static int failures;

        private static void Check(bool condition, string name)
        {
            Console.WriteLine((condition ? "[PASS] " : "[FAIL] ") + name);
            if (!condition) failures++;
        }

        private static int Main()
        {
#if NET8_0_OR_GREATER
            // net8 验证壳：GBK/Big5/GB18030/CP1252 属于 Windows 代码页，需注册 CodePages 提供程序
            Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
#endif
            TestEncoding();
            TestZipReader();
            TestOoxmlExtractor();
            TestOle2Extractor();
            TestOfdExtractor();
            TestZipPenetration();
            TestPdfiumExtractor();
            TestSnippetBuilder();
            TestPipelineAndIndex();
            TestSensitive();
            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "===== ALL TESTS PASSED =====" : "===== " + failures + " TEST(S) FAILED =====");
            return failures == 0 ? 0 : 1;
        }

        // ---------- 编码检测 ----------

        private static void TestEncoding()
        {
            Console.WriteLine("--- EncodingDetector ---");
            Encoding gbk = Encoding.GetEncoding(936);
            Encoding big5 = null;
            try { big5 = Encoding.GetEncoding(950); } catch (Exception) { }

            TextDecodeResult r1 = EncodingDetector.Decode(gbk.GetBytes("内部文档检索测试"));
            Check(r1.EncodingLabel == "GB18030" && r1.Text == "内部文档检索测试",
                "GBK 无 BOM → GB18030 且解码还原（实际 " + r1.EncodingLabel + " / " + r1.Text + "）");

            byte[] utf8Bom = new UTF8Encoding(true).GetBytes("测试ABC内容");
            TextDecodeResult r2 = EncodingDetector.Decode(utf8Bom, utf8Bom.Length);
            Check(r2.EncodingLabel == "UTF-8" && r2.Text == "测试ABC内容", "UTF-8 BOM 识别与还原");

            byte[] utf8NoBom = new UTF8Encoding(false).GetBytes("无 BOM 的 UTF-8 中文");
            TextDecodeResult r3 = EncodingDetector.Decode(utf8NoBom, utf8NoBom.Length);
            Check(r3.EncodingLabel == "UTF-8" && r3.Text == "无 BOM 的 UTF-8 中文", "UTF-8 无 BOM 识别");

            byte[] utf16Bom = Encoding.Unicode.GetPreamble();
            byte[] utf16Body = Encoding.Unicode.GetBytes("系统日志ABC");
            byte[] utf16 = new byte[utf16Bom.Length + utf16Body.Length];
            Array.Copy(utf16Bom, utf16, utf16Bom.Length);
            Array.Copy(utf16Body, 0, utf16, utf16Bom.Length, utf16Body.Length);
            TextDecodeResult r4 = EncodingDetector.Decode(utf16, utf16.Length);
            Check(r4.EncodingLabel == "UTF-16LE" && r4.Text == "系统日志ABC", "UTF-16LE BOM 识别与还原");

            byte[] utf16NoBom = Encoding.Unicode.GetBytes("中文内容没有字节序标记");
            TextDecodeResult r5 = EncodingDetector.Decode(utf16NoBom, utf16NoBom.Length);
            Check(r5.EncodingLabel == "UTF-16LE" && r5.Text == "中文内容没有字节序标记", "UTF-16LE 无 BOM 启发式");

            byte[] utf16BeNoBom = Encoding.BigEndianUnicode.GetBytes("大端序中文内容测试");
            TextDecodeResult r6 = EncodingDetector.Decode(utf16BeNoBom, utf16BeNoBom.Length);
            Check(r6.EncodingLabel == "UTF-16BE", "UTF-16BE 无 BOM 启发式（标签）");

            byte[] ascii = new ASCIIEncoding().GetBytes("plain ascii text 123");
            TextDecodeResult r7 = EncodingDetector.Decode(ascii, ascii.Length);
            Check(r7.EncodingLabel == "ASCII" && r7.Text == "plain ascii text 123",
                "纯 ASCII（实际 " + r7.EncodingLabel + "）");

            if (big5 != null)
            {
                byte[] b5 = big5.GetBytes("測試文件內容");
                TextDecodeResult r8 = EncodingDetector.Decode(b5, b5.Length);
                // Big5 与 GB18030 高度重叠：只要可解码即视为通过（已知局限）
                Check(!string.IsNullOrEmpty(r8.Text), "Big5 可解码（标签=" + r8.EncodingLabel + "，已知局限）");
            }

            byte[] g18030 = Encoding.GetEncoding("GB18030").GetBytes("生僻字镕堃旻四字节𠀀");
            TextDecodeResult r9 = EncodingDetector.Decode(g18030, g18030.Length);
            Check(r9.EncodingLabel == "GB18030" && r9.Text == "生僻字镕堃旻四字节𠀀", "GB18030 四字节序列识别与还原");
        }

        // ---------- ZIP 读取器 ----------

        private static void TestZipReader()
        {
            Console.WriteLine("--- ZipReader ---");
            var writer = new TestZipWriter();
            writer.Add("[Content_Types].xml", "<Types/>", false);
            writer.Add("word/document.xml", "<w:document><w:body><w:p/></w:body></w:document>", true);
            writer.Add("xl/worksheets/sheet1.xml", "<worksheet><sheetData/></worksheet>", true);
            writer.Add("gbk名.txt", "GBKNAMES", true);
            byte[] zip = writer.Build();

            List<ZipEntryInfo> entries;
            using (MemoryStream ms = new MemoryStream(zip))
            {
                entries = ZipReader.ReadEntries(ms);
                Check(entries.Count == 4, "中央目录条目数 = 4（实际 " + entries.Count + "）");
                Check(entries[0].Name == "[Content_Types].xml", "条目按名称排序（首条）");

                ZipEntryInfo storeEntry = FindEntry(entries, "[Content_Types].xml");
                ZipEntryInfo deflateEntry = FindEntry(entries, "word/document.xml");
                ZipEntryInfo gbkEntry = FindEntry(entries, "gbk名.txt");
                ZipEntryInfo sheetEntry = FindEntry(entries, "xl/worksheets/sheet1.xml");
                Check(storeEntry != null && ReadAll(ZipReader.OpenEntry(ms, storeEntry)) == "<Types/>", "store 条目读取");
                Check(deflateEntry != null && ReadAll(ZipReader.OpenEntry(ms, deflateEntry)).Contains("<w:document>"), "deflate 条目读取");
                Check(gbkEntry != null && ReadAll(ZipReader.OpenEntry(ms, gbkEntry)) == "GBKNAMES", "GBK 文件名条目解码与读取");
                Check(sheetEntry != null && ReadAll(ZipReader.OpenEntry(ms, sheetEntry)).Contains("<sheetData/>"), "第二个 deflate 条目读取");
            }

            try
            {
                using (MemoryStream bad = new MemoryStream(new byte[] { 1, 2, 3, 4 }))
                {
                    ZipReader.ReadEntries(bad);
                }
                Check(false, "损坏流应抛异常");
            }
            catch (Exception)
            {
                Check(true, "损坏流应抛异常");
            }
        }

        private static ZipEntryInfo FindEntry(List<ZipEntryInfo> entries, string name)
        {
            foreach (ZipEntryInfo e in entries)
            {
                if (e.Name == name) return e;
            }
            return null;
        }

        private static string ReadAll(Stream s)
        {
            using (s)
            {
                var ms = new MemoryStream();
                byte[] buf = new byte[8192];
                while (true)
                {
                    int n = s.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    ms.Write(buf, 0, n);
                }
                return new UTF8Encoding(false).GetString(ms.ToArray());
            }
        }

        // ---------- OOXML 提取 ----------

        private static string BuildDocxXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                   "<w:body>" +
                   "<w:p><w:r><w:t>项目计划书</w:t></w:r></w:p>" +
                   "<w:p><w:r><w:t>会议纪要：关于全文检索</w:t><w:br/><w:t>工具的立项说明</w:t></w:r></w:p>" +
                   "<w:p><w:r><w:t xml:space=\"preserve\">Tab测试</w:t><w:tab/><w:t>结束</w:t></w:r></w:p>" +
                   "</w:body></w:document>";
        }

        private static string BuildSharedStrings()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                   "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                   "<si><t>部门预算表</t></si>" +
                   "<si><r><t>富文本</t></r><r><t>拼接</t></r></si>" +
                   "<si><t>拼音</t><rPh sb=\"0\" eb=\"2\"><t>pin yin</t></rPh></si>" +
                   "</sst>";
        }

        private static string BuildSheetXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                   "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                   "<sheetData>" +
                   "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row>" +
                   "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>2</v></c><c r=\"B2\"><v>12345</v></c><c r=\"C2\" t=\"inlineStr\"><is><t>内联串</t></is></c></row>" +
                   "</sheetData></worksheet>";
        }

        private static string BuildSlideXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                   "<p:sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
                   "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">" +
                   "<p:cSld><p:spTree>" +
                   "<p:sp><p:txBody><a:p><a:r><a:t>年度总结汇报</a:t></a:r></a:p>" +
                   "<a:p><a:r><a:t>第二页要点内容</a:t></a:r></a:p></p:txBody></p:sp>" +
                   "<p:sp><p:txBody><a:p><a:fld id=\"{1}\" type=\"slidenum\"><a:t>7</a:t></a:fld></a:p></p:txBody></p:sp>" +
                   "</p:spTree></p:cSld></p:sld>";
        }

        private static void TestOoxmlExtractor()
        {
            Console.WriteLine("--- OoxmlExtractor ---");
            var extractor = new OoxmlExtractor();

            // DOCX
            var docx = new TestZipWriter();
            docx.Add("[Content_Types].xml", "<Types/>", false);
            docx.Add("_rels/.rels", "<Relationships/>", false);
            docx.Add("word/document.xml", BuildDocxXml(), true);
            string wordText = Extract(extractor, docx.Build());
            Check(wordText.Contains("项目计划书") && wordText.Contains("会议纪要：关于全文检索"), "DOCX 提取 w:t 文本");
            Check(wordText.Contains("工具的立项说明"), "DOCX 换行后文本仍在");
            Check(wordText.Contains("Tab测试\t结束"), "DOCX tab 转换（实际：" + Show(wordText) + "）");

            // XLSX
            var xlsx = new TestZipWriter();
            xlsx.Add("[Content_Types].xml", "<Types/>", false);
            xlsx.Add("xl/sharedStrings.xml", BuildSharedStrings(), true);
            xlsx.Add("xl/worksheets/sheet1.xml", BuildSheetXml(), true);
            string sheetText = Extract(extractor, xlsx.Build());
            Check(sheetText.Contains("部门预算表"), "XLSX 共享字符串解析");
            Check(sheetText.Contains("富文本拼接"), "XLSX 富文本 si 多 run 拼接");
            Check(sheetText.Contains("拼音"), "XLSX 拼音注音跳过后主文本保留");
            Check(sheetText.Contains("12345") && sheetText.Contains("内联串"), "XLSX 数值与内联字符串");

            // PPTX
            var pptx = new TestZipWriter();
            pptx.Add("[Content_Types].xml", "<Types/>", false);
            pptx.Add("ppt/slides/slide1.xml", BuildSlideXml(), true);
            pptx.Add("ppt/slides/slide10.xml", BuildSlideXml(), true);
            pptx.Add("ppt/slides/slide2.xml", BuildSlideXml(), true);
            string slideText = Extract(extractor, pptx.Build());
            Check(slideText.Contains("年度总结汇报"), "PPTX 提取 a:t 文本");
            Check(!slideText.Contains(">7<") && CountOccurrences(slideText, "年度总结汇报") == 3, "PPTX 页码域文本被跳过且 3 张页全部处理");

            // 结构不符
            var junk = new TestZipWriter();
            junk.Add("readme.txt", "hello", false);
            string err;
            string t;
            using (MemoryStream ms = new MemoryStream(junk.Build()))
            {
                bool ok = extractor.TryExtract(ms, 1024 * 1024, out t, out err);
                Check(!ok && !string.IsNullOrEmpty(err), "非 OOXML 的 ZIP 应报结构不符");
            }
        }

        private static string Show(string s)
        {
            return s.Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0, idx = 0;
            while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }

        private static string Extract(OoxmlExtractor extractor, byte[] zipBytes)
        {
            using (MemoryStream ms = new MemoryStream(zipBytes))
            {
                string text, error;
                if (!extractor.TryExtract(ms, 1024 * 1024, out text, out error))
                {
                    throw new IOException("OOXML 提取失败: " + error);
                }
                return text;
            }
        }

        // ---------- OLE2 提取（NPOI 容器 + 自研 doc/ppt 解析） ----------

        private static byte[] BuildOle2(params KeyValuePair<string, byte[]>[] streams)
        {
            var poifs = new NPOIFSFileSystem();
            try
            {
                foreach (KeyValuePair<string, byte[]> kv in streams)
                {
                    poifs.Root.CreateDocument(kv.Key, new MemoryStream(kv.Value));
                }
                var ms = new MemoryStream();
                poifs.WriteFileSystem(ms);
                return ms.ToArray();
            }
            finally
            {
                poifs.Close();
            }
        }

        private static byte[] BuildXlsFixture()
        {
            var wb = new HSSFWorkbook();
            ISheet sheet = wb.CreateSheet("预算");
            IRow row = sheet.CreateRow(0);
            row.CreateCell(0).SetCellValue("部门预算表");
            row.CreateCell(1).SetCellValue(12345.5);
            IRow row2 = sheet.CreateRow(1);
            row2.CreateCell(0).SetCellValue("季度支出明细");
            var ms = new MemoryStream();
            wb.Write(ms);
            return ms.ToArray();
        }

        /// <summary>
        /// 构造 Word 97 结构 .doc：WordDocument 流（FIB + 文本区）+ 1Table 流（CLX 分片表）。
        /// 两个分片：UTF-16LE 中文分片 + 8 位压缩 ASCII 分片，覆盖两种 fc 形态。
        /// </summary>
        private static byte[] BuildDocFixture(out string expected)
        {
            const int textOffset = 0x400;
            string text1 = "合同编号：HT-2024-001。\r甲方：北京示例科技有限公司。";
            string text2 = "REPORT-2024 ";
            byte[] text1Bytes = Encoding.Unicode.GetBytes(text1);
            byte[] text2Bytes = Encoding.ASCII.GetBytes(text2);
            int text2Offset = textOffset + text1Bytes.Length;

            var wd = new byte[text2Offset + text2Bytes.Length];
            wd[0] = 0xEC; wd[1] = 0xA5;              // wIdent = 0xA5EC
            wd[2] = 0xC1; wd[3] = 0x00;              // nFib = 0x00C1（Word 97）
            wd[0x0A] = 0x00; wd[0x0B] = 0x02;        // fWhichTblStm → 1Table
            Array.Copy(text1Bytes, 0, wd, textOffset, text1Bytes.Length);
            Array.Copy(text2Bytes, 0, wd, text2Offset, text2Bytes.Length);

            uint fcCompressed = ((uint)text2Offset << 1) | 0x40000000u;
            var tbl = new byte[33];                   // Pcdt: 1 + 4 + PlcPcd(28)
            tbl[0] = 0x02;
            WriteLe32(tbl, 1, 28);                    // lcb(PlcPcd)
            WriteLe32(tbl, 5, 0);                     // cp0
            WriteLe32(tbl, 9, (uint)text1.Length);    // cp1
            WriteLe32(tbl, 13, (uint)(text1.Length + text2.Length)); // cp2
            WriteLe32(tbl, 17, 0);                    // pcd1 保留字 + prm
            WriteLe32(tbl, 19, (uint)textOffset);     // pcd1.fc（UTF-16）
            WriteLe32(tbl, 23, 0);
            WriteLe32(tbl, 25, 0);                    // pcd2 保留字 + prm
            WriteLe32(tbl, 27, fcCompressed);         // pcd2.fc（压缩）
            WriteLe32(wd, 0x1A2, 0);                  // fcClx = 0
            WriteLe32(wd, 0x1A6, (uint)tbl.Length);   // lcbClx

            expected = text1.Replace('\r', '\n') + text2;
            return BuildOle2(
                new KeyValuePair<string, byte[]>("WordDocument", wd),
                new KeyValuePair<string, byte[]>("1Table", tbl));
        }

        /// <summary>构造 fEncrypted 置位的 .doc（应报错而非静默）。</summary>
        private static byte[] BuildEncryptedDocFixture()
        {
            var wd = new byte[0x400];
            wd[0] = 0xEC; wd[1] = 0xA5;
            wd[2] = 0xC1; wd[3] = 0x00;
            wd[0x0A] = 0x00; wd[0x0B] = 0x03;        // fWhichTblStm | fEncrypted
            return BuildOle2(new KeyValuePair<string, byte[]>("WordDocument", wd));
        }

        /// <summary>构造 .ppt：容器记录内含 TextCharsAtom（UTF-16）与 TextBytesAtom（GBK）。</summary>
        private static byte[] BuildPptFixture(out string expectedChars, out string expectedBytes)
        {
            expectedChars = "项目评审会汇报材料";
            expectedBytes = "年度预算说明";
            byte[] charsBody = Encoding.Unicode.GetBytes(expectedChars);
            byte[] bytesBody = Encoding.GetEncoding(936).GetBytes(expectedBytes);

            var payload = new byte[8 + charsBody.Length + 8 + bytesBody.Length];
            int pos = 0;
            // TextCharsAtom：recVer=0, recType=0x0FA0
            payload[pos] = 0; payload[pos + 1] = 0;
            payload[pos + 2] = 0xA0; payload[pos + 3] = 0x0F;
            WriteLe32(payload, pos + 4, (uint)charsBody.Length);
            Array.Copy(charsBody, 0, payload, pos + 8, charsBody.Length);
            pos += 8 + charsBody.Length;
            // TextBytesAtom：recVer=0, recType=0x0FA8
            payload[pos] = 0; payload[pos + 1] = 0;
            payload[pos + 2] = 0xA8; payload[pos + 3] = 0x0F;
            WriteLe32(payload, pos + 4, (uint)bytesBody.Length);
            Array.Copy(bytesBody, 0, payload, pos + 8, bytesBody.Length);
            pos += 8 + bytesBody.Length;

            // 外层容器记录：recVer=0xF, recType=0x03E8
            var stream = new byte[8 + payload.Length];
            stream[0] = 0x0F; stream[1] = 0x00;
            stream[2] = 0xE8; stream[3] = 0x03;
            WriteLe32(stream, 4, (uint)payload.Length);
            Array.Copy(payload, 0, stream, 8, payload.Length);

            return BuildOle2(new KeyValuePair<string, byte[]>("PowerPoint Document", stream));
        }

        /// <summary>构造旧版 WPS 风格 OLE2：无 WordDocument/Workbook 结构，文本混在二进制废字节中。</summary>
        private static byte[] BuildWpsMiningFixture()
        {
            var body = new List<byte>();
            body.AddRange(new byte[] { 0x01, 0x02, 0x03, 0x07, 0x08, 0x1F, 0x7F, 0x00,
                                       0x02, 0x00, 0x03, 0x00, 0x04, 0x00, 0x05, 0x00 });
            body.AddRange(Encoding.GetEncoding(936).GetBytes("旧版WPS文档内容示例第一段落"));
            body.AddRange(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
                                       0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F });
            body.AddRange(Encoding.GetEncoding(936).GetBytes("预算数字12345结尾"));
            body.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
            return BuildOle2(
                new KeyValuePair<string, byte[]>("WPS", body.ToArray()),
                new KeyValuePair<string, byte[]>("Padding", new byte[64]));
        }

        private static void WriteLe32(byte[] buf, int off, uint v)
        {
            buf[off] = (byte)(v & 0xFF);
            buf[off + 1] = (byte)((v >> 8) & 0xFF);
            buf[off + 2] = (byte)((v >> 16) & 0xFF);
            buf[off + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static string ExtractOle2(Ole2Extractor extractor, byte[] bytes, out string error)
        {
            string text;
            using (MemoryStream ms = new MemoryStream(bytes))
            {
                if (!extractor.TryExtract(ms, 1024 * 1024, out text, out error))
                {
                    throw new IOException("OLE2 提取失败: " + error);
                }
            }
            return text;
        }

        private static void TestOle2Extractor()
        {
            Console.WriteLine("--- Ole2Extractor ---");
            var extractor = new Ole2Extractor();

            Check(extractor.GetFileTypeLabel(".doc") == "Word" && extractor.GetFileTypeLabel(".wps") == "Word" &&
                  extractor.GetFileTypeLabel(".xls") == "Excel" && extractor.GetFileTypeLabel(".et") == "Excel" &&
                  extractor.GetFileTypeLabel(".ppt") == "PPT" && extractor.GetFileTypeLabel(".dps") == "PPT",
                "OLE2 类型标签映射");

            // .xls（NPOI HSSF）
            string xlsError;
            string xlsText = ExtractOle2(extractor, BuildXlsFixture(), out xlsError);
            Check(xlsText.Contains("部门预算表") && xlsText.Contains("季度支出明细") && xlsText.Contains("12345.5"),
                "XLS 提取 HSSF 单元格文本（实际：" + Show(xlsText) + "）");

            // .doc（分片表：UTF-16 分片 + 压缩分片）
            string expectedDoc;
            byte[] docBytes = BuildDocFixture(out expectedDoc);
            string docError;
            string docText = ExtractOle2(extractor, docBytes, out docError);
            Check(docText.Replace("\n", "") == expectedDoc.Replace("\n", "") &&
                  docText.Contains("合同编号：HT-2024-001。\n甲方：北京示例科技有限公司。"),
                "DOC 分片表提取（UTF-16 分片 + \\r 转换，实际：" + Show(docText) + "）");
            Check(docText.Contains("REPORT-2024"), "DOC 8 位压缩分片解码");

            // 加密 .doc 明确报错
            string encText, encError;
            using (MemoryStream ms = new MemoryStream(BuildEncryptedDocFixture()))
            {
                bool ok = extractor.TryExtract(ms, 1024 * 1024, out encText, out encError);
                Check(!ok && encError != null && encError.Contains("加密"), "加密 DOC 明确报错（实际：" + encError + "）");
            }

            // .ppt（TextCharsAtom + TextBytesAtom）
            string expectedChars, expectedBytes;
            byte[] pptBytes = BuildPptFixture(out expectedChars, out expectedBytes);
            string pptError;
            string pptText = ExtractOle2(extractor, pptBytes, out pptError);
            Check(pptText.Contains(expectedChars) && pptText.Contains(expectedBytes),
                "PPT 记录树扫描文本原子（实际：" + Show(pptText) + "）");

            // .wps 旧版 OLE2（启发式挖掘）
            string wpsError;
            string wpsText = ExtractOle2(extractor, BuildWpsMiningFixture(), out wpsError);
            Check(wpsText.Contains("旧版WPS文档内容示例") && wpsText.Contains("第一段落"),
                "WPS 挖掘提取 UTF-16/GBK 混排文本（实际：" + Show(wpsText) + "）");
            Check(wpsText.Contains("预算数字12345"), "WPS 挖掘第二个片段");

            // 路由链：.wps 先 OOXML 失败后 OLE2 成功；.doc 直接 OLE2
            var router = new FormatRouter(new ITextExtractor[] { new OoxmlExtractor(), extractor });
            string chainText, chainError;
            using (MemoryStream ms = new MemoryStream(BuildWpsMiningFixture()))
            {
                bool ok = router.TryExtractText(".wps", ms, 1024 * 1024, out chainText, out chainError);
                Check(ok && chainText.Contains("旧版WPS文档内容示例"), "路由链：OLE2 结构 .wps 降级到 OLE2 提取器");
            }
            using (MemoryStream ms = new MemoryStream(docBytes))
            {
                bool ok = router.TryExtractText(".doc", ms, 1024 * 1024, out chainText, out chainError);
                Check(ok && chainText.Contains("合同编号"), "路由链：.doc 提取");
            }
        }

        // ---------- OFD 提取 ----------

        private static string BuildOfdPageXml(string[] texts)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.Append("<ofd:Page xmlns:ofd=\"http://www.ofdspec.org/2016\"><ofd:Content>");
            foreach (string t in texts)
            {
                sb.Append("<ofd:TextObject Boundary=\"10 10 100 20\"><ofd:TextCode>").Append(t).Append("</ofd:TextCode></ofd:TextObject>");
            }
            sb.Append("</ofd:Content></ofd:Page>");
            return sb.ToString();
        }

        private static byte[] BuildOfdFixture()
        {
            var ofd = new TestZipWriter();
            ofd.Add("OFD.xml", "<ofd:OFDDocRoot xmlns:ofd=\"http://www.ofdspec.org/2016\"><ofd:DocRoot DocBase=\"Doc_0\"/></ofd:OFDDocRoot>", false);
            ofd.Add("Doc_0/Document.xml", "<ofd:Document xmlns:ofd=\"http://www.ofdspec.org/2016\"><ofd:Page ID=\"1\" BaseLoc=\"Pages/Page_0/Content.xml\"/></ofd:Document>", false);
            ofd.Add("Doc_0/Pages/Page_0/Content.xml", BuildOfdPageXml(new[] { "北京示例科技有限公司", "电子公文内容页" }), true);
            ofd.Add("Doc_0/Pages/Page_1/Content.xml", BuildOfdPageXml(new[] { "第二页签批内容" }), true);
            return ofd.Build();
        }

        private static void TestOfdExtractor()
        {
            Console.WriteLine("--- OfdExtractor ---");
            var extractor = new OfdExtractor();
            string text, error;
            using (MemoryStream ms = new MemoryStream(BuildOfdFixture()))
            {
                bool ok = extractor.TryExtract(ms, 1024 * 1024, out text, out error);
                Check(ok && text != null, "OFD 提取成功（错误：" + error + "）");
            }
            Check(text.Contains("北京示例科技有限公司") && text.Contains("电子公文内容页"),
                "OFD TextCode 文本提取（实际：" + Show(text) + "）");
            int p0 = text.IndexOf("电子公文内容页", StringComparison.Ordinal);
            int p1 = text.IndexOf("第二页签批内容", StringComparison.Ordinal);
            Check(p0 >= 0 && p1 > p0, "OFD 按页序提取");

            // 非 OFD 的 ZIP 报结构不符
            var junk = new TestZipWriter();
            junk.Add("readme.txt", "hello", false);
            using (MemoryStream ms = new MemoryStream(junk.Build()))
            {
                bool ok = extractor.TryExtract(ms, 1024 * 1024, out text, out error);
                Check(!ok && error.Contains("OFD"), "非 OFD 的 ZIP 报结构不符（实际：" + error + "）");
            }
        }

        // ---------- 压缩包穿透 ----------

        private static byte[] BuildSimpleDocx(string text)
        {
            var docx = new TestZipWriter();
            docx.Add("[Content_Types].xml", "<Types/>", false);
            docx.Add("word/document.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>" +
                text + "</w:t></w:r></w:p></w:body></w:document>", true);
            return docx.Build();
        }

        private static FormatRouter BuildFullRouter(out ZipArchiveExtractor zipExtractor)
        {
            zipExtractor = new ZipArchiveExtractor();
            var router = new FormatRouter(new ITextExtractor[]
            {
                new OoxmlExtractor(), new Ole2Extractor(), new OfdExtractor(),
                new PdfiumExtractor(), zipExtractor, new TextFileExtractor()
            });
            zipExtractor.InnerRouter = router;
            return router;
        }

        private static void TestZipPenetration()
        {
            Console.WriteLine("--- ZipArchiveExtractor ---");
            ZipArchiveExtractor zipExtractor;
            FormatRouter router = BuildFullRouter(out zipExtractor);

            // 外层压缩包：文本 + OOXML + 嵌套压缩包 + 未知类型 + 系统垃圾条目
            var nested = new TestZipWriter();
            nested.Add("n.txt", "嵌套压缩包穿透成功", true);
            var outer = new TestZipWriter();
            outer.Add("说明.txt", "压缩包内部文本检索测试", true);
            outer.Add("docs/inner.docx", BuildSimpleDocx("压缩包里的项目计划书"), true);
            outer.Add("nested.zip", nested.Build(), false);
            outer.Add("img.bin", new byte[] { 1, 2, 3, 4, 5 }, false);
            outer.Add("__MACOSX/._说明.txt", "junk", false);
            byte[] zipBytes = outer.Build();

            string text, error;
            using (MemoryStream ms = new MemoryStream(zipBytes))
            {
                bool ok = zipExtractor.TryExtract(ms, 1024 * 1024, out text, out error);
                Check(ok && text != null, "压缩包穿透提取成功（错误：" + error + "）");
            }
            Check(text.Contains("说明.txt ──") && text.Contains("压缩包内部文本检索测试"), "穿透：条目名行与文本内容");
            Check(text.Contains("docs/inner.docx ──") && text.Contains("压缩包里的项目计划书"), "穿透：内部 OOXML 内容提取");
            Check(text.Contains("nested.zip ──") && text.Contains("嵌套压缩包穿透成功"), "穿透：嵌套压缩包递归");
            Check(text.Contains("img.bin ──"), "穿透：不可提取条目仅保留名字行");
            Check(!text.Contains("__MACOSX"), "穿透：跳过 __MACOSX 系统条目");

            // 嵌套层数限制：3 层内可穿透，第 4 层仅保留名字行
            var l4 = new TestZipWriter();
            l4.Add("l4.txt", "第四层内容不可穿透", true);
            var l3 = new TestZipWriter();
            l3.Add("l3.txt", "第三层内容可穿透", true);
            l3.Add("level4.zip", l4.Build(), false);
            var l2 = new TestZipWriter();
            l2.Add("level3.zip", l3.Build(), false);
            var l1 = new TestZipWriter();
            l1.Add("level2.zip", l2.Build(), false);

            using (MemoryStream ms = new MemoryStream(l1.Build()))
            {
                string t2, err2;
                zipExtractor.TryExtract(ms, 1024 * 1024, out t2, out err2);
                Check(t2.Contains("第三层内容可穿透"), "穿透：第 3 层内容可见");
                Check(t2.Contains("level4.zip ──") && !t2.Contains("第四层内容不可穿透"),
                    "穿透：超过层数上限仅保留名字行");
            }

            // 损坏 ZIP 报错
            using (MemoryStream ms = new MemoryStream(new byte[] { 1, 2, 3, 4 }))
            {
                string t3, err3;
                bool ok = zipExtractor.TryExtract(ms, 1024 * 1024, out t3, out err3);
                Check(!ok && !string.IsNullOrEmpty(err3), "损坏 ZIP 报错");
            }

            // 全链路：router.TryExtractText(".zip") 穿透后内层 docx 命中
            string chainText, chainError;
            using (MemoryStream ms = new MemoryStream(zipBytes))
            {
                bool ok = router.TryExtractText(".zip", ms, 1024 * 1024, out chainText, out chainError);
                Check(ok && chainText.Contains("压缩包里的项目计划书"), "路由链：.zip 穿透 + 内部 OOXML 链式提取");
            }
        }

        // ---------- PDF（pdfium，优雅降级） ----------

        private static void TestPdfiumExtractor()
        {
            Console.WriteLine("--- PdfiumExtractor ---");
            var extractor = new PdfiumExtractor();
            Check(extractor.GetFileTypeLabel(".pdf") == "PDF", "PDF 类型标签");

            // 非 PDF 头直接拒绝（不依赖原生库）
            string text, error;
            using (MemoryStream ms = new MemoryStream(Encoding.ASCII.GetBytes("PK\x03\x04 not a pdf")))
            {
                bool ok = extractor.TryExtract(ms, 1024 * 1024, out text, out error);
                Check(!ok && error.Contains("非 PDF"), "非 PDF 文件被拒（实际：" + error + "）");
            }

            // 合法 PDF 头：无 pdfium.dll 时优雅降级（错误提示部署方式）；有 pdfium 时解析失败也返回明确错误
            var pdf = new MemoryStream();
            byte[] head = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\nendobj\n%%EOF");
            pdf.Write(head, 0, head.Length);
            pdf.Position = 0;
            bool pdfOk = extractor.TryExtract(pdf, 1024 * 1024, out text, out error);
            Check(!pdfOk && !string.IsNullOrEmpty(error) &&
                      (error.Contains("pdfium.dll") || error.Contains("PDF 解析失败")),
                "PDF 无原生库时优雅降级并给出部署提示（实际：" + error + "）");
        }

        // ---------- 摘要与高亮 ----------

        private static void TestSnippetBuilder()
        {
            Console.WriteLine("--- SnippetBuilder ---");
            List<string> terms = SnippetBuilder.ExtractTerms("敏感信息 ABC-12 采购");
            Check(terms.Count == 4 && terms[0] == "敏感信息" && terms[1] == "abc" && terms[2] == "12" && terms[3] == "采购",
                "高亮词提取（连续片段 + 小写化，实际：" + string.Join("|", terms.ToArray()) + "）");

            string longText = new string('前', 200) + "关键词出现位置在中间这一段" + new string('后', 200);
            string snip = SnippetBuilder.Build(longText, new List<string> { "关键词" });
            Check(snip.Contains("关键词") && snip.StartsWith("…") && snip.EndsWith("…") && snip.Length <= 200,
                "摘要窗口与省略号（长度 " + snip.Length + "）");

            string snip2 = SnippetBuilder.Build("短文本带关键词", new List<string> { "关键词" });
            Check(snip2 == "短文本带关键词", "短文本摘要不加省略号");

            Check(SnippetBuilder.Build("没有命中内容", new List<string> { "关键词" }) == string.Empty,
                "无命中返回空摘要");
            Check(SnippetBuilder.Build("A\n\t B  测试", new List<string> { "a" }).StartsWith("A B"),
                "空白折叠（大小写不敏感命中、原文截取）");

            // TryMake：重读文件生成摘要
            string root = Path.Combine(Path.GetTempPath(), "DocSnifferSnippetTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(root);
                string file = Path.Combine(root, "note.txt");
                File.WriteAllBytes(file, new UTF8Encoding(false).GetBytes("会议纪要：关于文档检索工具的立项说明。"));

                ZipArchiveExtractor zipExtractor;
                FormatRouter router = BuildFullRouter(out zipExtractor);
                string made = SnippetBuilder.TryMake(router, file, "检索工具");
                Check(made.Contains("文档检索工具") && made.Contains("立项"), "TryMake 重读文件生成摘要（实际：" + made + "）");

                Check(SnippetBuilder.TryMake(router, Path.Combine(root, "missing.txt"), "检索") == string.Empty,
                    "文件不存在返回空摘要");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        // ---------- 扫描 + 路由 + 索引 + 增量 ----------

        private static void TestPipelineAndIndex()
        {
            Console.WriteLine("--- Scanner / Router / LuceneIndexService ---");
            string root = Path.Combine(Path.GetTempPath(), "DocSnifferLegacyTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string indexDir = Path.Combine(Path.GetTempPath(), "DocSnifferLegacyIdx_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(Path.Combine(root, "sub"));

                File.WriteAllBytes(Path.Combine(root, "a.txt"), new UTF8Encoding(false).GetBytes("全文检索引擎测试文档第一部分"));
                File.WriteAllBytes(Path.Combine(root, "b.txt"), Encoding.GetEncoding(936).GetBytes("内部机密文件：季度财务报表"));
                byte[] cBody = Encoding.Unicode.GetPreamble();
                byte[] cText = Encoding.Unicode.GetBytes("系统运行日志错误代码E003");
                byte[] cAll = new byte[cBody.Length + cText.Length];
                Array.Copy(cBody, cAll, cBody.Length);
                Array.Copy(cText, 0, cAll, cBody.Length, cText.Length);
                File.WriteAllBytes(Path.Combine(root, "sub", "c.log"), cAll);

                var docx = new TestZipWriter();
                docx.Add("[Content_Types].xml", "<Types/>", false);
                docx.Add("word/document.xml", BuildDocxXml(), true);
                File.WriteAllBytes(Path.Combine(root, "doc1.docx"), docx.Build());
                File.WriteAllBytes(Path.Combine(root, "old.wps"), docx.Build()); // PK 结构的 .wps

                var xlsx = new TestZipWriter();
                xlsx.Add("xl/sharedStrings.xml", BuildSharedStrings(), true);
                xlsx.Add("xl/worksheets/sheet1.xml", BuildSheetXml(), true);
                File.WriteAllBytes(Path.Combine(root, "sheet1.xlsx"), xlsx.Build());

                var pptx = new TestZipWriter();
                pptx.Add("ppt/slides/slide1.xml", BuildSlideXml(), true);
                File.WriteAllBytes(Path.Combine(root, "deck1.pptx"), pptx.Build());

                // OLE2 族
                string expectedDocText;
                File.WriteAllBytes(Path.Combine(root, "合同.doc"), BuildDocFixture(out expectedDocText));
                string pptChars, pptBytes;
                File.WriteAllBytes(Path.Combine(root, "演示.ppt"), BuildPptFixture(out pptChars, out pptBytes));
                File.WriteAllBytes(Path.Combine(root, "表.xls"), BuildXlsFixture());
                File.WriteAllBytes(Path.Combine(root, "老文档.wps"), BuildWpsMiningFixture()); // OLE2 结构 .wps

                // OFD
                File.WriteAllBytes(Path.Combine(root, "单据.ofd"), BuildOfdFixture());

                // 压缩包穿透（内含文本 + OOXML）
                var innerZip = new TestZipWriter();
                innerZip.Add("说明.txt", "压缩包内员工花名册内容", true);
                innerZip.Add("report.docx", BuildSimpleDocx("压缩包里的工作总结报告"), true);
                File.WriteAllBytes(Path.Combine(root, "inner.zip"), innerZip.Build());

                ZipArchiveExtractor unusedZip;
                var router = BuildFullRouter(out unusedZip);
                using (var service = new LuceneIndexService(indexDir))
                {
                    List<FileRecord> files = FileScanner.Scan(root, router.AllExtensions, null, null);
                    Check(files.Count == 13, "扫描到 13 个文件（实际 " + files.Count + "）");

                    IndexRunStats stats = service.IndexFiles(files, true, "b1", true, router, null, null, 200, 1024 * 1024, 50 * 1024 * 1024);
                    Check(stats.Added == 13 && stats.Failed == 0,
                        "全量索引：新增13/失败0（实际 新增" + stats.Added + "/失败" + stats.Failed + "）");
                    Check(stats.TotalDocs == 13, "文档总数 = 13（实际 " + stats.TotalDocs + "）");

                    Check(HasHit(service.Search("检索", 50, null), "a.txt"), "搜'检索'命中 a.txt");
                    Check(HasHit(service.Search("财务报表", 50, null), "b.txt"), "搜'财务报表'命中 GBK 文件 b.txt");
                    Check(HasHit(service.Search("错误代码", 50, null), "c.log"), "搜'错误代码'命中 UTF-16LE 日志 c.log");
                    Check(HasHit(service.Search("项目计划", 50, null), "doc1.docx") && HasHit(service.Search("项目计划", 50, null), "old.wps"),
                        "搜'项目计划'同时命中 docx 与 PK 结构 wps");
                    Check(HasHit(service.Search("预算表", 50, null), "sheet1.xlsx"), "搜'预算表'命中 xlsx 共享字符串");
                    Check(HasHit(service.Search("年度总结", 50, null), "deck1.pptx"), "搜'年度总结'命中 pptx");
                    Check(service.Search("不存在的词组", 50, null).Count == 0, "不存在的词组无结果");

                    // 新格式：OLE2 / OFD / 压缩包穿透
                    Check(HasHit(service.Search("合同编号", 50, null), "合同.doc"), "搜'合同编号'命中 OLE2 .doc（分片表解析）");
                    Check(HasHit(service.Search("评审会", 50, null), "演示.ppt"), "搜'评审会'命中 OLE2 .ppt");
                    Check(HasHit(service.Search("支出明细", 50, null), "表.xls"), "搜'支出明细'命中 OLE2 .xls");
                    Check(HasHit(service.Search("签批内容", 50, null), "单据.ofd"), "搜'签批内容'命中 OFD");
                    Check(HasHit(service.Search("文档内容", 50, null), "老文档.wps"), "搜'文档内容'命中 OLE2 旧版 .wps（启发式挖掘）");
                    Check(HasHit(service.Search("花名册", 50, null), "inner.zip"), "搜'花名册'命中压缩包穿透文本");
                    Check(HasHit(service.Search("工作总结", 50, null), "inner.zip"), "搜'工作总结'命中压缩包内 OOXML");

                    // 类型标签
                    IList<SearchResultItem> labelHits = service.Search("合同编号", 50, null);
                    Check(labelHits.Count == 1 && labelHits[0].FileType == "Word", "OLE2 .doc 类型标签 = Word");
                    labelHits = service.Search("签批内容", 50, null);
                    Check(labelHits.Count == 1 && labelHits[0].FileType == "OFD", "OFD 类型标签 = OFD");
                    labelHits = service.Search("花名册", 50, null);
                    Check(labelHits.Count == 1 && labelHits[0].FileType == "压缩包", "压缩包类型标签 = 压缩包");

                    // 大小/修改时间/类型字段（用 a.txt 独有的词，避免多文件命中的排序干扰）
                    IList<SearchResultItem> hits = service.Search("引擎测试", 50, null);
                    Check(hits.Count == 1 && hits[0].FileName == "a.txt" && hits[0].Size > 0 &&
                          hits[0].Modified != DateTime.MinValue && hits[0].FileType == "文本",
                        "结果带大小/修改时间/类型字段（命中 " + hits.Count + " 条）");

                    // ---- 增量：修改 ----
                    File.WriteAllBytes(Path.Combine(root, "b.txt"), Encoding.GetEncoding(936).GetBytes("公开信息：年会通知安排"));
                    files = FileScanner.Scan(root, router.AllExtensions, null, null);
                    stats = service.IndexFiles(files, false, "b1", true, router, null, null, 200, 1024 * 1024, 50 * 1024 * 1024);
                    Check(stats.Updated == 1 && stats.Skipped == 12 && stats.Added == 0,
                        "增量索引：更新1/跳过12（实际 更新" + stats.Updated + "/跳过" + stats.Skipped + "/新增" + stats.Added + "）");
                    Check(HasHit(service.Search("年会通知", 50, null), "b.txt"), "修改后新内容可搜");
                    Check(service.Search("财务报表", 50, null).Count == 0, "修改后旧内容不再命中");

                    // ---- 增量：删除 ----
                    File.Delete(Path.Combine(root, "doc1.docx"));
                    files = FileScanner.Scan(root, router.AllExtensions, null, null);
                    stats = service.IndexFiles(files, false, "b1", true, router, null, null, 200, 1024 * 1024, 50 * 1024 * 1024);
                    Check(stats.Removed == 1, "增量索引：清理已删除文件 1（实际 " + stats.Removed + "）");
                    IList<SearchResultItem> plan = service.Search("项目计划", 50, null);
                    Check(plan.Count == 1 && plan[0].FileName == "old.wps", "删除后仅剩 old.wps 命中");

                    // ---- 增量：新增 ----
                    File.WriteAllBytes(Path.Combine(root, "sub", "d.md"), new UTF8Encoding(false).GetBytes("增量新增内容测试标记"));
                    files = FileScanner.Scan(root, router.AllExtensions, null, null);
                    stats = service.IndexFiles(files, false, "b1", true, router, null, null, 200, 1024 * 1024, 50 * 1024 * 1024);
                    Check(stats.Added == 1, "增量索引：新增 1（实际 " + stats.Added + "）");
                    Check(HasHit(service.Search("增量新增", 50, null), "d.md"), "新增文件可搜");

                    // ---- 无变化 ----
                    files = FileScanner.Scan(root, router.AllExtensions, null, null);
                    stats = service.IndexFiles(files, false, "b1", true, router, null, null, 200, 1024 * 1024, 50 * 1024 * 1024);
                    Check(stats.Skipped == files.Count && stats.Added == 0 && stats.Updated == 0,
                        "无变化时全部跳过（跳过 " + stats.Skipped + "/" + files.Count + "）");

                    // ---- 损坏文件容错 ----
                    File.WriteAllBytes(Path.Combine(root, "bad.docx"), new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 });
                    files = FileScanner.Scan(root, router.AllExtensions, null, null);
                    stats = service.IndexFiles(files, false, "b1", true, router, null, null, 200, 1024 * 1024, 50 * 1024 * 1024);
                    Check(stats.Failed == 1 && stats.Errors.Count >= 1 && stats.TotalDocs == 13,
                        "损坏 docx 提取失败被记录且不入索引（失败 " + stats.Failed + "，文档 " + stats.TotalDocs + "）");
                    Check(service.Search("bad", 50, null).Count == 0, "损坏文件完全不入索引（文件名也不命中）");

                    // ---- 文件名检索 ----
                    Check(HasHit(service.Search("sheet1", 50, null), "sheet1.xlsx"), "按文件名检索");

                    // ---- 批次：范围过滤 ----
                    Check(service.DocCountForBatch("b1") == 13, "批次 b1 文档数 = 13（实际 " + service.DocCountForBatch("b1") + "）");
                    Check(HasHit(service.Search("检索", 50, "b1"), "a.txt"), "限定批次检索命中");
                    Check(service.Search("检索", 50, "b_none").Count == 0, "不存在批次范围为空");

                    // ---- 批次：重新导入归属新批次（同一路径以最后一次扫描为准）----
                    files = FileScanner.Scan(root, router.AllExtensions, null, null);
                    stats = service.IndexFiles(files, false, "b2", true, router, null, null, 200, 1024 * 1024, 50 * 1024 * 1024);
                    Check(service.DocCountForBatch("b2") == 13 && service.DocCountForBatch("b1") == 0,
                        "重新导入归属新批次（b2=" + service.DocCountForBatch("b2") + ", b1=" + service.DocCountForBatch("b1") + "）");

                    // ---- 批次：删除 ----
                    service.DeleteBatch("b2");
                    Check(service.TotalDocs == 0, "删除批次后文档清空（实际 " + service.TotalDocs + "）");

                    // ---- 仅索引文件名 ----
                    files = FileScanner.Scan(root, router.AllExtensions, null, null);
                    stats = service.IndexFiles(files, true, "b3", false, router, null, null, 200, 1024 * 1024, 50 * 1024 * 1024);
                    Check(stats.Added == 14, "仅文件名模式全量入库（含此前提取失败的文件，实际 " + stats.Added + "）");
                    Check(service.Search("检索", 50, null).Count == 0, "仅文件名模式下内容不命中");
                    Check(HasHit(service.Search("sheet1", 50, null), "sheet1.xlsx"), "仅文件名模式下文件名可搜");

                    // ---- 一键清除 ----
                    service.ClearAll();
                    Check(service.TotalDocs == 0, "一键清除后文档为空（实际 " + service.TotalDocs + "）");
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
                try { Directory.Delete(indexDir, true); } catch (Exception) { }
            }
        }

        private static bool HasHit(IList<SearchResultItem> results, string fileName)
        {
            foreach (SearchResultItem item in results)
            {
                if (string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ---------- 敏感规则检测 ----------

        private static void TestSensitive()
        {
            Console.WriteLine("--- SensitiveScanner ---");
            string root = Path.Combine(Path.GetTempPath(), "DocSnifferSensTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllBytes(Path.Combine(root, "staff.txt"),
                    new UTF8Encoding(false).GetBytes("联系人张三，证件号 11010119900307851X，电话 13812345678。"));
                File.WriteAllBytes(Path.Combine(root, "机密-会议纪要.txt"),
                    new UTF8Encoding(false).GetBytes("本周例会要点。"));
                File.WriteAllBytes(Path.Combine(root, "readme.txt"),
                    new UTF8Encoding(false).GetBytes("普通说明文件。"));

                var rules = new List<SensitiveRule>
                {
                    new SensitiveRule { Id="r1", Name="身份证号", Pattern="\\d{17}[\\dXx]", Type="regex",
                        RiskLevel="high", MatchFilename=false, MatchContent=true },
                    new SensitiveRule { Id="r2", Name="机密字样", Pattern="机密", Type="keyword",
                        RiskLevel="high", MatchFilename=true, MatchContent=false },
                    new SensitiveRule { Id="r3", Name="联系人", Pattern="张三", Type="keyword",
                        RiskLevel="medium", MatchFilename=false, MatchContent=true },
                    new SensitiveRule { Id="r4", Name="坏正则", Pattern="[unclosed", Type="regex",
                        RiskLevel="low", MatchFilename=false, MatchContent=true },
                    new SensitiveRule { Id="r5", Name="无命中", Pattern="不存在的词", Type="keyword",
                        RiskLevel="low", MatchFilename=true, MatchContent=true }
                };

                var router = new FormatRouter(new ITextExtractor[] { new OoxmlExtractor(), new TextFileExtractor() });
                List<SensitiveHit> hits = SensitiveScanner.Scan(root, true, rules, router, null, 1024 * 1024, 50 * 1024 * 1024);

                Check(HasSensitiveHit(hits, "身份证号", "content", "staff.txt"), "正则规则命中内容（身份证号）");
                Check(HasSensitiveHit(hits, "机密字样", "filename", "机密-会议纪要.txt"), "关键词规则命中文件名（机密）");
                Check(HasSensitiveHit(hits, "联系人", "content", "staff.txt"), "关键词规则命中内容（张三）");
                Check(hits.TrueForAll(delegate(SensitiveHit h) { return h.RuleName != "坏正则"; }), "非法正则被跳过");
                Check(hits.TrueForAll(delegate(SensitiveHit h) { return h.RuleName != "无命中"; }), "无命中规则不产生结果");
                Check(hits.Count == 3, "命中总数 = 3（实际 " + hits.Count + "）");

                // includeContent=false 时不匹配内容
                List<SensitiveHit> nameOnly = SensitiveScanner.Scan(root, false, rules, router, null, 1024 * 1024, 50 * 1024 * 1024);
                Check(nameOnly.Count == 1 && nameOnly[0].MatchType == "filename", "仅文件名模式只出文件名命中（实际 " + nameOnly.Count + "）");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        private static bool HasSensitiveHit(List<SensitiveHit> hits, string rule, string matchType, string fileName)
        {
            foreach (SensitiveHit h in hits)
            {
                if (h.RuleName == rule && h.MatchType == matchType &&
                    h.Path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}

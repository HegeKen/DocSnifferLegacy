using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DocSnifferLegacy.Core.Extract;
using DocSnifferLegacy.Core.Index;
using DocSnifferLegacy.Core.IO;
using DocSnifferLegacy.Core.Routing;
using DocSnifferLegacy.Core.Sensitive;
using DocSnifferLegacy.Core.Text;

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
            // net8 验证壳：GBK/Big5/GB18030 属于 Windows 代码页，需注册 CodePages 提供程序
            Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
#endif
            TestEncoding();
            TestZipReader();
            TestOoxmlExtractor();
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

                var router = new FormatRouter(new ITextExtractor[] { new OoxmlExtractor(), new TextFileExtractor() });
                using (var service = new LuceneIndexService(indexDir))
                {
                    List<FileRecord> files = FileScanner.Scan(root, router.AllExtensions, null, null);
                    Check(files.Count == 7, "扫描到 7 个文件（实际 " + files.Count + "）");

                    IndexRunStats stats = service.IndexFiles(files, true, "b1", true, router, null, null, 200, 1024 * 1024, 50 * 1024 * 1024);
                    Check(stats.Added == 7 && stats.Failed == 0,
                        "全量索引：新增7/失败0（实际 新增" + stats.Added + "/失败" + stats.Failed + "）");
                    Check(stats.TotalDocs == 7, "文档总数 = 7（实际 " + stats.TotalDocs + "）");

                    Check(HasHit(service.Search("检索", 50, null), "a.txt"), "搜'检索'命中 a.txt");
                    Check(HasHit(service.Search("财务报表", 50, null), "b.txt"), "搜'财务报表'命中 GBK 文件 b.txt");
                    Check(HasHit(service.Search("错误代码", 50, null), "c.log"), "搜'错误代码'命中 UTF-16LE 日志 c.log");
                    Check(HasHit(service.Search("项目计划", 50, null), "doc1.docx") && HasHit(service.Search("项目计划", 50, null), "old.wps"),
                        "搜'项目计划'同时命中 docx 与 PK 结构 wps");
                    Check(HasHit(service.Search("预算表", 50, null), "sheet1.xlsx"), "搜'预算表'命中 xlsx 共享字符串");
                    Check(HasHit(service.Search("年度总结", 50, null), "deck1.pptx"), "搜'年度总结'命中 pptx");
                    Check(service.Search("不存在的词组", 50, null).Count == 0, "不存在的词组无结果");

                    // 大小/修改时间/类型字段（用 a.txt 独有的词，避免多文件命中的排序干扰）
                    IList<SearchResultItem> hits = service.Search("引擎测试", 50, null);
                    Check(hits.Count == 1 && hits[0].FileName == "a.txt" && hits[0].Size > 0 &&
                          hits[0].Modified != DateTime.MinValue && hits[0].FileType == "文本",
                        "结果带大小/修改时间/类型字段（命中 " + hits.Count + " 条）");

                    // ---- 增量：修改 ----
                    File.WriteAllBytes(Path.Combine(root, "b.txt"), Encoding.GetEncoding(936).GetBytes("公开信息：年会通知安排"));
                    files = FileScanner.Scan(root, router.AllExtensions, null, null);
                    stats = service.IndexFiles(files, false, "b1", true, router, null, null, 200, 1024 * 1024, 50 * 1024 * 1024);
                    Check(stats.Updated == 1 && stats.Skipped == 6 && stats.Added == 0,
                        "增量索引：更新1/跳过6（实际 更新" + stats.Updated + "/跳过" + stats.Skipped + "/新增" + stats.Added + "）");
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
                    Check(stats.Failed == 1 && stats.Errors.Count >= 1 && stats.TotalDocs == 7,
                        "损坏 docx 提取失败被记录且不入索引（失败 " + stats.Failed + "，文档 " + stats.TotalDocs + "）");
                    Check(service.Search("bad", 50, null).Count == 0, "损坏文件完全不入索引（文件名也不命中）");

                    // ---- 文件名检索 ----
                    Check(HasHit(service.Search("sheet1", 50, null), "sheet1.xlsx"), "按文件名检索");

                    // ---- 批次：范围过滤 ----
                    Check(service.DocCountForBatch("b1") == 7, "批次 b1 文档数 = 7（实际 " + service.DocCountForBatch("b1") + "）");
                    Check(HasHit(service.Search("检索", 50, "b1"), "a.txt"), "限定批次检索命中");
                    Check(service.Search("检索", 50, "b_none").Count == 0, "不存在批次范围为空");

                    // ---- 批次：重新导入归属新批次（同一路径以最后一次扫描为准）----
                    files = FileScanner.Scan(root, router.AllExtensions, null, null);
                    stats = service.IndexFiles(files, false, "b2", true, router, null, null, 200, 1024 * 1024, 50 * 1024 * 1024);
                    Check(service.DocCountForBatch("b2") == 7 && service.DocCountForBatch("b1") == 0,
                        "重新导入归属新批次（b2=" + service.DocCountForBatch("b2") + ", b1=" + service.DocCountForBatch("b1") + "）");

                    // ---- 批次：删除 ----
                    service.DeleteBatch("b2");
                    Check(service.TotalDocs == 0, "删除批次后文档清空（实际 " + service.TotalDocs + "）");

                    // ---- 仅索引文件名 ----
                    files = FileScanner.Scan(root, router.AllExtensions, null, null);
                    stats = service.IndexFiles(files, true, "b3", false, router, null, null, 200, 1024 * 1024, 50 * 1024 * 1024);
                    Check(stats.Added == 8, "仅文件名模式全量入库（含此前提取失败的文件，实际 " + stats.Added + "）");
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

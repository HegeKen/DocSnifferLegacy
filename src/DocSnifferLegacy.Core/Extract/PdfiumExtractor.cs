using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DocSnifferLegacy.Core.Extract
{
    /// <summary>
    /// PDF 文本提取：P/Invoke 调用 pdfium.dll（Chromium PDFium 原生库）。
    /// pdfium.dll 为 Windows 原生库，需与本程序（x86）同目录部署；缺失或位数不符时
    /// 优雅降级 —— 返回 false 并给出部署提示，不影响其他格式的索引与检索。
    /// XP 上请使用支持 XP 的旧版 pdfium 构建（Chrome 49 世代，~2016）。
    /// </summary>
    public sealed class PdfiumExtractor : ITextExtractor
    {
        private static readonly string[] Extensions = new[] { ".pdf" };

        /// <summary>整个 PDF 读入内存的上限（索引侧另有单文件上限把关）。</summary>
        public const long MaxContainerBytes = 64L * 1024 * 1024;

        private const uint FPDF_ERR_PASSWORD = 4;

        private readonly ISet<string> extensions;
        private static readonly object initLock = new object();
        private static bool? nativeAvailable; // null=未尝试，true=可用，false=不可用（缓存避免逐文件反复加载）

        public PdfiumExtractor()
        {
            extensions = new HashSet<string>(Extensions);
        }

        public ISet<string> SupportedExtensions { get { return extensions; } }

        public string GetFileTypeLabel(string extension) { return "PDF"; }

        // ---------- pdfium 导入（稳定 ABI） ----------

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_InitLibrary();

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadMemDocument(byte[] data, int size, string password);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDF_GetPageCount(IntPtr document);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadPage(IntPtr document, int page_index);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_ClosePage(IntPtr page);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_CloseDocument(IntPtr document);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFText_LoadPage(IntPtr page);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFText_ClosePage(IntPtr text_page);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFText_CountChars(IntPtr text_page);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFText_GetText(IntPtr text_page, int start_index, int count, [Out] ushort[] result);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FPDF_GetLastError();

        public bool TryExtract(Stream stream, int maxChars, out string text, out string error)
        {
            text = null;
            error = null;
            try
            {
                byte[] head = new byte[5];
                if (!ReadFully(stream, head, 5) ||
                    head[0] != (byte)'%' || head[1] != (byte)'P' || head[2] != (byte)'D' || head[3] != (byte)'F')
                {
                    error = "非 PDF 文件（缺少 %PDF 头）";
                    return false;
                }
                stream.Position = 0;

                string initError;
                if (!EnsureInitialized(out initError))
                {
                    error = initError;
                    return false;
                }

                byte[] data = ReadAll(stream, MaxContainerBytes);
                IntPtr doc = FPDF_LoadMemDocument(data, data.Length, null);
                if (doc == IntPtr.Zero)
                {
                    uint err = FPDF_GetLastError();
                    error = err == FPDF_ERR_PASSWORD
                        ? "PDF 已加密，无法提取文本"
                        : "PDF 解析失败（pdfium 错误码 " + err + "）";
                    return false;
                }

                var sb = new StringBuilder();
                try
                {
                    int pageCount = FPDF_GetPageCount(doc);
                    for (int i = 0; i < pageCount && sb.Length < maxChars; i++)
                    {
                        IntPtr page = FPDF_LoadPage(doc, i);
                        if (page == IntPtr.Zero) continue;
                        try
                        {
                            IntPtr tp = FPDFText_LoadPage(page);
                            if (tp == IntPtr.Zero) continue;
                            try
                            {
                                int count = FPDFText_CountChars(tp);
                                int remain = maxChars - sb.Length;
                                if (count > 0 && remain > 0)
                                {
                                    if (count > remain) count = remain;
                                    var buf = new ushort[count];
                                    int written = FPDFText_GetText(tp, 0, count, buf);
                                    if (written > 0)
                                    {
                                        var chars = new char[written];
                                        for (int c = 0; c < written; c++) chars[c] = (char)buf[c];
                                        sb.Append(new string(chars));
                                        if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                                    }
                                }
                            }
                            finally { FPDFText_ClosePage(tp); }
                        }
                        finally { FPDF_ClosePage(page); }
                    }
                }
                finally { FPDF_CloseDocument(doc); }

                text = Normalize(sb.ToString());
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

        /// <summary>统一换行：pdfium 输出 \r\n。</summary>
        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static bool EnsureInitialized(out string error)
        {
            lock (initLock)
            {
                if (nativeAvailable == null)
                {
                    try
                    {
                        FPDF_InitLibrary();
                        nativeAvailable = true;
                    }
                    catch (Exception)
                    {
                        // DllNotFoundException / EntryPointNotFoundException / BadImageFormatException
                        nativeAvailable = false;
                    }
                }
                if (nativeAvailable != true)
                {
                    error = "未找到可用的 pdfium.dll：PDF 提取依赖 Windows 原生库，"
                        + "请将 32 位（x86）pdfium.dll 与程序放在同一目录后重试"
                        + "（XP 请使用支持 XP 的旧版构建）";
                    return false;
                }
                error = null;
                return true;
            }
        }

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
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DocSnifferLegacy.Core.Routing;

namespace DocSnifferLegacy.Core.Search
{
    /// <summary>
    /// 搜索结果摘要与高亮：对齐 DocSniffer Web 版 make_snippet 的行为 ——
    /// 从原始查询词提取高亮词（连续字母数字/CJK 片段），重读文件提取文本（索引不存正文），
    /// 折叠空白后取首个命中词前后各 80 字生成摘要。
    /// </summary>
    public static class SnippetBuilder
    {
        /// <summary>摘要窗口（命中词前 + 后合计字符数）。</summary>
        public const int Window = 160;

        /// <summary>重读文件提取文本时的上限。</summary>
        public const int SnippetMaxChars = 8192;

        /// <summary>重读文件的大小上限（超大文件不做摘要）。</summary>
        public const long SnippetMaxFileBytes = 64L * 1024 * 1024;

        /// <summary>
        /// 从原始查询提取高亮词：连续的字母/数字/CJK 片段各成一词，转小写。
        /// 例："敏感信息 ABC-12 采购" → ["敏感信息", "abc", "12", "采购"]。
        /// </summary>
        public static List<string> ExtractTerms(string query)
        {
            var terms = new List<string>();
            if (string.IsNullOrEmpty(query)) return terms;
            var cur = new StringBuilder();
            foreach (char c in query.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c >= 0x2E80)
                {
                    cur.Append(c);
                }
                else if (cur.Length > 0)
                {
                    terms.Add(cur.ToString());
                    cur.Length = 0;
                }
            }
            if (cur.Length > 0) terms.Add(cur.ToString());
            return terms;
        }

        /// <summary>
        /// 在文本中找首个命中词生成摘要；折叠空白，窗口外补省略号。
        /// 无命中或无词返回空串。
        /// </summary>
        public static string Build(string text, IList<string> terms)
        {
            if (string.IsNullOrEmpty(text) || terms == null || terms.Count == 0) return string.Empty;

            // 折叠空白为单空格，保持摘要紧凑
            var sb = new StringBuilder(text.Length);
            bool lastWs = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWs) sb.Append(' ');
                    lastWs = true;
                }
                else
                {
                    sb.Append(c);
                    lastWs = false;
                }
            }
            string flat = sb.ToString();
            string flatLower = flat.ToLowerInvariant(); // 命中查找不区分大小写，摘要仍按原文截取

            int start = -1;
            int len = 0;
            foreach (string term in terms)
            {
                if (term.Length == 0) continue;
                int idx = flatLower.IndexOf(term, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    start = idx;
                    len = term.Length;
                    break;
                }
            }
            if (start < 0) return string.Empty;

            int half = Window / 2;
            int from = Math.Max(0, start - half);
            int to = Math.Min(flat.Length, start + len + half);
            string snip = flat.Substring(from, to - from);
            if (from > 0) snip = "…" + snip;
            if (to < flat.Length) snip = snip + "…";
            return snip;
        }

        /// <summary>
        /// 重读文件生成摘要（链式路由提取，覆盖全部已支持格式）。
        /// 文件不存在/超大/提取失败/无命中一律返回空串。
        /// </summary>
        public static string TryMake(FormatRouter router, string path, string query)
        {
            try
            {
                if (router == null || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(query)) return string.Empty;
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists || fi.Length > SnippetMaxFileBytes) return string.Empty;

                List<string> terms = ExtractTerms(query);
                if (terms.Count == 0) return string.Empty;

                string ext = (fi.Extension ?? string.Empty).ToLowerInvariant();
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    string text, error;
                    if (!router.TryExtractText(ext, fs, SnippetMaxChars, out text, out error)) return string.Empty;
                    return Build(text, terms);
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}

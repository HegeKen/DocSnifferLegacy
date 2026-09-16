using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DocSnifferLegacy.Core.Extract;
using DocSnifferLegacy.Core.IO;
using DocSnifferLegacy.Core.Routing;

namespace DocSnifferLegacy.Core.Sensitive
{
    /// <summary>敏感检测规则：正则或关键词，作用于文件名和/或文件内容。</summary>
    public sealed class SensitiveRule
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Pattern = string.Empty;
        public string Type = "regex";        // regex | keyword
        public string RiskLevel = "medium";  // low | medium | high | critical
        public bool MatchFilename;
        public bool MatchContent = true;
    }

    public sealed class SensitiveHit
    {
        public string RuleName;
        public string RiskLevel;
        public string MatchType;   // filename | content
        public string Path;
        public string Matched;
    }

    /// <summary>
    /// 规则检测：按文件名和/或提取后的内容匹配规则，每条规则每个文件只记首个命中。
    /// regex 按原样匹配；keyword 用不区分大小写的包含判断。
    /// </summary>
    public static class SensitiveScanner
    {
        public const int MaxHits = 1000;
        private const int KeywordContext = 40;

        public static List<SensitiveHit> Scan(
            string path,
            bool includeContent,
            IList<SensitiveRule> rules,
            FormatRouter router,
            Func<bool> shouldContinue,
            int maxChars,
            long maxFileBytes)
        {
            var hits = new List<SensitiveHit>();
            var prepared = Prepare(rules);
            if (prepared.Count == 0) return hits;

            if (File.Exists(path))
            {
                ScanFile(path, includeContent, prepared, router, hits, maxChars, maxFileBytes);
                return hits;
            }

            List<FileRecord> files = FileScanner.Scan(path, null, null, shouldContinue);
            foreach (FileRecord f in files)
            {
                if (shouldContinue != null && !shouldContinue()) break;
                if (hits.Count >= MaxHits) break;
                ScanFile(f.FullPath, includeContent, prepared, router, hits, maxChars, maxFileBytes);
            }
            return hits;
        }

        private static void ScanFile(
            string fullPath,
            bool includeContent,
            List<PreparedRule> rules,
            FormatRouter router,
            List<SensitiveHit> hits,
            int maxChars,
            long maxFileBytes)
        {
            string fileName = Path.GetFileName(fullPath);
            foreach (PreparedRule rule in rules)
            {
                if (hits.Count >= MaxHits) return;
                if (rule.MatchFilename && rule.Match(fileName))
                {
                    hits.Add(new SensitiveHit
                    {
                        RuleName = rule.Rule.Name,
                        RiskLevel = rule.Rule.RiskLevel,
                        MatchType = "filename",
                        Path = fullPath,
                        Matched = rule.Explain(fileName)
                    });
                }
            }

            if (!includeContent) return;
            if (router == null) return;
            string ext = (Path.GetExtension(fullPath) ?? string.Empty).ToLowerInvariant();
            if (router.RouteAll(ext).Count == 0) return;
            if (new FileInfo(fullPath).Length > maxFileBytes) return;

            string text;
            string error;
            try
            {
                using (FileStream fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (!router.TryExtractText(ext, fs, maxChars, out text, out error)) return;
                }
            }
            catch (Exception)
            {
                return;
            }
            if (string.IsNullOrEmpty(text)) return;

            foreach (PreparedRule rule in rules)
            {
                if (hits.Count >= MaxHits) return;
                if (!rule.MatchContent) continue;
                string matched = rule.FirstMatch(text);
                if (matched != null)
                {
                    hits.Add(new SensitiveHit
                    {
                        RuleName = rule.Rule.Name,
                        RiskLevel = rule.Rule.RiskLevel,
                        MatchType = "content",
                        Path = fullPath,
                        Matched = matched
                    });
                }
            }
        }

        private static List<PreparedRule> Prepare(IList<SensitiveRule> rules)
        {
            var list = new List<PreparedRule>();
            if (rules == null) return list;
            foreach (SensitiveRule r in rules)
            {
                if (r == null || string.IsNullOrEmpty(r.Pattern)) continue;
                bool isRegex = string.Equals(r.Type, "regex", StringComparison.OrdinalIgnoreCase);
                Regex regex = null;
                if (isRegex)
                {
                    try { regex = new Regex(r.Pattern, RegexOptions.None); }
                    catch (ArgumentException) { continue; } // 非法正则跳过，不中断整体检测
                }
                list.Add(new PreparedRule(r, regex));
            }
            return list;
        }

        private sealed class PreparedRule
        {
            public readonly SensitiveRule Rule;
            private readonly Regex regex;

            public PreparedRule(SensitiveRule rule, Regex regex)
            {
                Rule = rule;
                this.regex = regex;
            }

            public bool MatchFilename { get { return Rule.MatchFilename; } }
            public bool MatchContent { get { return Rule.MatchContent; } }

            public bool Match(string text)
            {
                if (regex != null) return regex.IsMatch(text);
                return text.IndexOf(Rule.Pattern, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            public string Explain(string text)
            {
                if (regex != null)
                {
                    Match m = regex.Match(text);
                    return m.Success ? m.Value : Rule.Pattern;
                }
                return Rule.Pattern;
            }

            /// <summary>内容首个命中；关键词命中带前后上下文。</summary>
            public string FirstMatch(string text)
            {
                if (regex != null)
                {
                    Match m = regex.Match(text);
                    if (!m.Success) return null;
                    return TrimContext(text, m.Index, m.Length);
                }
                int idx = text.IndexOf(Rule.Pattern, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return null;
                return TrimContext(text, idx, Rule.Pattern.Length);
            }

            private static string TrimContext(string text, int index, int length)
            {
                int start = Math.Max(0, index - KeywordContext);
                int end = Math.Min(text.Length, index + length + KeywordContext);
                var sb = new StringBuilder();
                if (start > 0) sb.Append('…');
                sb.Append(text.Substring(start, end - start).Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' '));
                if (end < text.Length) sb.Append('…');
                return sb.ToString();
            }
        }
    }
}

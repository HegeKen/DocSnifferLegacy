using System;
using System.Collections.Generic;
using System.IO;
using DocSnifferLegacy.Core.Extract;

namespace DocSnifferLegacy.Core.Routing
{
    /// <summary>
    /// 格式路由器：按扩展名把文件分发给第一个声明的提取器。
    /// 提取器顺序即优先级：OOXML 提取器自带 PK 魔数校验，失败后由文本提取器兜底（仅限文本扩展名）。
    /// </summary>
    public sealed class FormatRouter
    {
        private readonly List<ITextExtractor> extractors;
        private readonly ISet<string> allExtensions;

        public FormatRouter(IEnumerable<ITextExtractor> extractorList)
        {
            extractors = new List<ITextExtractor>(extractorList);
            allExtensions = new HashSet<string>();
            foreach (ITextExtractor e in extractors)
            {
                foreach (string ext in e.SupportedExtensions) allExtensions.Add(ext);
            }
        }

        public ISet<string> AllExtensions { get { return allExtensions; } }

        public ITextExtractor Route(string extension)
        {
            string ext = (extension ?? string.Empty).ToLowerInvariant();
            foreach (ITextExtractor e in extractors)
            {
                if (e.SupportedExtensions.Contains(ext)) return e;
            }
            return null;
        }

        /// <summary>声明该扩展名的全部提取器（按注册顺序，即优先级）。</summary>
        public IList<ITextExtractor> RouteAll(string extension)
        {
            string ext = (extension ?? string.Empty).ToLowerInvariant();
            var matched = new List<ITextExtractor>(2);
            foreach (ITextExtractor e in extractors)
            {
                if (e.SupportedExtensions.Contains(ext)) matched.Add(e);
            }
            return matched;
        }

        /// <summary>
        /// 链式提取：按优先级逐个尝试声明该扩展名的提取器，第一个成功者生效。
        /// 用于双解析路径的扩展名，如 .wps 先按 OOXML（PK 结构）解析、失败降级 OLE2（旧版 WPS）。
        /// 每次尝试前把流复位到起点，要求传入的流可查找。
        /// </summary>
        public bool TryExtractText(string extension, Stream stream, int maxChars, out string text, out string error)
        {
            text = null;
            error = null;
            IList<ITextExtractor> chain = RouteAll(extension);
            if (chain.Count == 0)
            {
                error = "不支持的格式";
                return false;
            }
            string lastError = null;
            for (int i = 0; i < chain.Count; i++)
            {
                if (stream.CanSeek)
                {
                    try { stream.Position = 0; }
                    catch (Exception) { }
                }
                if (chain[i].TryExtract(stream, maxChars, out text, out error)) return true;
                lastError = error;
            }
            text = null;
            error = lastError;
            return false;
        }

        public string GetFileTypeLabel(string extension)
        {
            ITextExtractor e = Route(extension);
            return e != null ? e.GetFileTypeLabel(extension) : "其他";
        }
    }
}

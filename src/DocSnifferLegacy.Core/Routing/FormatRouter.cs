using System;
using System.Collections.Generic;
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

        public string GetFileTypeLabel(string extension)
        {
            ITextExtractor e = Route(extension);
            return e != null ? e.GetFileTypeLabel(extension) : "其他";
        }
    }
}

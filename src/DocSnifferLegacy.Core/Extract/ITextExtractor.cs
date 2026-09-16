using System.Collections.Generic;
using System.IO;

namespace DocSnifferLegacy.Core.Extract
{
    /// <summary>
    /// 文本提取器统一接口（文本提取管道）。实现方须自行做魔数/结构校验，
    /// 结构不符时返回 false 并在 error 中说明，由路由继续降级或标记不支持。
    /// </summary>
    public interface ITextExtractor
    {
        /// <summary>可处理的扩展名集合（小写、带点）。</summary>
        ISet<string> SupportedExtensions { get; }

        /// <summary>文件类型展示名（Word / Excel / PPT / 文本）。</summary>
        string GetFileTypeLabel(string extension);

        /// <summary>尝试提取文本。maxChars 为输出文本长度上限。</summary>
        bool TryExtract(Stream stream, int maxChars, out string text, out string error);
    }
}

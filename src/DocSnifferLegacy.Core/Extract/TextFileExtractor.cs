using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DocSnifferLegacy.Core.IO;
using DocSnifferLegacy.Core.Text;

namespace DocSnifferLegacy.Core.Extract
{
    /// <summary>纯文本/代码文件提取：整读（限量）后交给编码检测器解码。</summary>
    public sealed class TextFileExtractor : ITextExtractor
    {
        public const long MaxBytes = 20L * 1024 * 1024;

        private static readonly string[] Extensions = new[]
        {
            ".txt", ".log", ".ini", ".cfg", ".conf", ".csv", ".tsv",
            ".xml", ".json", ".html", ".htm", ".css", ".js",
            ".py", ".java", ".c", ".cpp", ".cc", ".h", ".hpp", ".cs", ".sql",
            ".bat", ".cmd", ".ps1", ".vbs", ".md", ".markdown", ".yml", ".yaml",
            ".properties", ".php", ".rb", ".go", ".rs", ".tex", ".srt", ".lrc", ".reg"
        };

        private readonly ISet<string> extensions;

        public TextFileExtractor()
        {
            extensions = new HashSet<string>(Extensions);
        }

        public ISet<string> SupportedExtensions { get { return extensions; } }

        public string GetFileTypeLabel(string extension) { return "文本"; }

        public bool TryExtract(Stream stream, int maxChars, out string text, out string error)
        {
            text = null;
            error = null;
            try
            {
                var ms = new MemoryStream();
                var buf = new byte[81920];
                long total = 0;
                while (true)
                {
                    int n = stream.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    if (total + n > MaxBytes)
                    {
                        ms.Write(buf, 0, (int)(MaxBytes - total));
                        break;
                    }
                    ms.Write(buf, 0, n);
                    total += n;
                }

                byte[] data = ms.ToArray();
                TextDecodeResult dr = EncodingDetector.Decode(data, data.Length);
                string t = dr.Text ?? string.Empty;
                t = t.Replace("\0", string.Empty);
                if (t.Length > maxChars) t = t.Substring(0, maxChars);
                text = t;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}

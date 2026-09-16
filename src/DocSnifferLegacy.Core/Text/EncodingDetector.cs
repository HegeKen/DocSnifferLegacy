using System;
using System.Text;

namespace DocSnifferLegacy.Core.Text
{
    /// <summary>解码结果：文本与检测到的编码标签（用于日志与界面展示）。</summary>
    public sealed class TextDecodeResult
    {
        public string Text;
        public string EncodingLabel;
    }

    /// <summary>
    /// 纯文本编码检测与解码。
    /// 判定顺序：BOM → UTF-8 严格校验 → 无 BOM UTF-16 双向评分 → GB18030 严格校验 → Big5 严格校验 → 按 GB18030 回退。
    /// 已知局限：Big5 与 GBK/GB18030 字节重叠度高，多数 Big5 文件会被判为 GB18030 解出兼容乱码；
    /// 改善需引入统计学检测（UDE/chardet 移植），列入后续阶段。
    /// </summary>
    public static class EncodingDetector
    {
        private const int SampleSize = 256 * 1024;

        public static TextDecodeResult Decode(byte[] data)
        {
            return data == null ? Decode(null, 0) : Decode(data, data.Length);
        }

        public static TextDecodeResult Decode(byte[] data, int length)
        {
            if (data == null || length <= 0)
                return new TextDecodeResult { Text = string.Empty, EncodingLabel = "ASCII" };

            int bomSkip;
            string label = PickLabel(data, length, out bomSkip);
            Encoding enc = Resolve(label);
            string text = enc.GetString(data, bomSkip, length - bomSkip);
            return new TextDecodeResult { Text = text, EncodingLabel = label };
        }

        private static string PickLabel(byte[] d, int n, out int bomSkip)
        {
            bomSkip = 0;
            if (n >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) { bomSkip = 3; return "UTF-8"; }
            if (n >= 4 && d[0] == 0xFF && d[1] == 0xFE && d[2] == 0x00 && d[3] == 0x00) { bomSkip = 4; return "UTF-32LE"; }
            if (n >= 4 && d[0] == 0x00 && d[1] == 0x00 && d[2] == 0xFE && d[3] == 0xFF) { bomSkip = 4; return "UTF-32BE"; }
            if (n >= 2 && d[0] == 0xFF && d[1] == 0xFE) { bomSkip = 2; return "UTF-16LE"; }
            if (n >= 2 && d[0] == 0xFE && d[1] == 0xFF) { bomSkip = 2; return "UTF-16BE"; }

            int sampleLen = Math.Min(n, SampleSize);
            if (IsValidUtf8(d, sampleLen))
            {
                // UTF-8 字节合法性成立：含非 ASCII 字节即为 UTF-8；
                // 全 ASCII 字节时只可能再是"无 BOM 的 ASCII 区 UTF-16"，用 0x00 字节的奇偶分布判定，
                // 不能用双字节评分（ASCII 两两配对会大量落入 CJK 基本区，评分必然失真）。
                bool hasNonAscii = false;
                int zeroOdd = 0, zeroEven = 0;
                for (int i = 0; i < sampleLen; i++)
                {
                    if (d[i] >= 0x80) { hasNonAscii = true; break; }
                    if (d[i] == 0x00)
                    {
                        if ((i & 1) != 0) zeroOdd++;
                        else zeroEven++;
                    }
                }
                if (hasNonAscii) return "UTF-8";
                int half = sampleLen / 2;
                if (zeroOdd >= half * 3 / 10 && zeroEven <= half / 20) return "UTF-16LE";
                if (zeroEven >= half * 3 / 10 && zeroOdd <= half / 20) return "UTF-16BE";
                return "ASCII";
            }

            // 非 UTF-8 合法：CJK 为主的 UTF-16（必含大量 0x80-0x9F 高位字节）在此用评分区分端序
            string u16NoBom = DetectUtf16NoBom(d, sampleLen);
            if (u16NoBom != null) return u16NoBom;
            if (IsValidGb18030(d, sampleLen)) return "GB18030";
            if (IsValidBig5(d, sampleLen)) return "Big5";
            return "GB18030";
        }

        /// <summary>
        /// 无 BOM UTF-16 判定：分别按 LE/BE 成对解码采样区，统计"常见字符占比"
        /// （ASCII 可打印 + CJK 基本区 + 全角符号 + 常用标点）。真正的 UTF-16 文本占比显著高，
        /// 而 GBK 等双字节编码被误按 UTF-16 切分后多落入生僻区块，占比低。
        /// 注意：常见字符集刻意不含谚文区，否则 GBK 文本会被误判。
        /// </summary>
        private static string DetectUtf16NoBom(byte[] d, int n)
        {
            double leCommon, leControl, beCommon, beControl;
            ScoreUtf16(d, n, false, out leCommon, out leControl);
            ScoreUtf16(d, n, true, out beCommon, out beControl);

            if (leCommon > 0.60 && leCommon - beCommon > 0.20 && leControl < 0.10) return "UTF-16LE";
            if (beCommon > 0.60 && beCommon - leCommon > 0.20 && beControl < 0.10) return "UTF-16BE";
            return null;
        }

        private static void ScoreUtf16(byte[] d, int n, bool bigEndian, out double commonRatio, out double controlRatio)
        {
            int pairs = n / 2;
            if (pairs == 0) { commonRatio = 0; controlRatio = 0; return; }
            int common = 0, control = 0;
            for (int i = 0; i + 1 < n; i += 2)
            {
                int c = bigEndian ? (d[i] << 8) | d[i + 1] : (d[i + 1] << 8) | d[i];
                if (c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c <= 0x7E)) common++;
                else if (c >= 0x4E00 && c <= 0x9FA5) common++;
                else if (c >= 0x3000 && c <= 0x303F) common++;
                else if (c >= 0xFF00 && c <= 0xFFEF) common++;
                else if (c == 0x2018 || c == 0x2019 || c == 0x201C || c == 0x201D ||
                         c == 0x2013 || c == 0x2014 || c == 0x2026 || c == 0x00B7) common++;
                else if (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F)) control++;
            }
            commonRatio = (double)common / pairs;
            controlRatio = (double)control / pairs;
        }

        private static bool IsValidUtf8(byte[] d, int n)
        {
            int i = 0;
            while (i < n)
            {
                int x = d[i];
                if (x < 0x80) { i++; continue; }
                int extra;
                if ((x & 0xE0) == 0xC0) { extra = 1; if (x < 0xC2) return false; }
                else if ((x & 0xF0) == 0xE0) extra = 2;
                else if ((x & 0xF8) == 0xF0) extra = 3;
                else return false;
                if (i + extra >= n) return true; // 采样截断处的残缺序列不作否决
                for (int k = 1; k <= extra; k++)
                    if ((d[i + k] & 0xC0) != 0x80) return false;
                if (extra == 2 && x == 0xE0 && d[i + 1] < 0xA0) return false;
                if (extra == 3 && x == 0xF0 && d[i + 1] < 0x90) return false;
                if (x == 0xF4 && d[i + 1] > 0x8F) return false;
                i += extra + 1;
            }
            return true;
        }

        private static bool IsValidGb18030(byte[] d, int n)
        {
            int i = 0;
            while (i < n)
            {
                int b = d[i];
                if (b < 0x80) { i++; continue; }
                if (b == 0x80 || b == 0xFF) return false;
                if (i + 1 >= n) return true;
                int b2 = d[i + 1];
                if (b2 >= 0x30 && b2 <= 0x39)
                {
                    if (i + 3 >= n) return true;
                    if (d[i + 2] < 0x81 || d[i + 2] > 0xFE || d[i + 3] < 0x30 || d[i + 3] > 0x39) return false;
                    i += 4;
                }
                else
                {
                    if (b2 < 0x40 || b2 == 0x7F || b2 > 0xFE) return false;
                    i += 2;
                }
            }
            return true;
        }

        private static bool IsValidBig5(byte[] d, int n)
        {
            int i = 0;
            while (i < n)
            {
                int b = d[i];
                if (b < 0x80) { i++; continue; }
                if (b < 0xA1 || b > 0xF9) return false;
                if (i + 1 >= n) return true;
                int t = d[i + 1];
                if (!((t >= 0x40 && t <= 0x7E) || (t >= 0xA1 && t <= 0xFE))) return false;
                i += 2;
            }
            return true;
        }

        private static Encoding Resolve(string label)
        {
            switch (label)
            {
                case "UTF-8": return new UTF8Encoding(false);
                case "UTF-16LE": return new UnicodeEncoding(false, false);
                case "UTF-16BE": return new UnicodeEncoding(true, false);
                case "UTF-32LE": return new UTF32Encoding(false, false);
                case "UTF-32BE": return new UTF32Encoding(true, false);
                case "ASCII": return new ASCIIEncoding();
                case "Big5": return SafeGetEncoding("big5", 950);
                default: return SafeGetEncoding("GB18030", 936);
            }
        }

        private static Encoding SafeGetEncoding(string name, int fallbackCodePage)
        {
            try { return Encoding.GetEncoding(name); }
            catch (Exception) { }
            try { return Encoding.GetEncoding(fallbackCodePage); }
            catch (Exception) { }
            return Encoding.Default;
        }
    }
}

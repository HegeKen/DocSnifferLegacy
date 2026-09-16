using System;
using System.IO;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Tokenattributes;

namespace DocSnifferLegacy.Core.Index
{
    /// <summary>
    /// 中日韩二元切分分析器：相邻 CJK 字符组成重叠 bigram，连续 CJK 串末尾补一个单字；
    /// 拉丁字母/数字串整体成词并转小写；标点与空白为分隔符。
    /// 自研而非依赖 contrib 的 CJKAnalyzer，行为可控且不增加程序集依赖。
    /// </summary>
    public sealed class CjkBigramAnalyzer : Analyzer
    {
        public override TokenStream TokenStream(string fieldName, TextReader reader)
        {
            return new CjkBigramTokenizer(reader);
        }
    }

    public sealed class CjkBigramTokenizer : Tokenizer
    {
        private readonly ITermAttribute termAtt;
        private readonly char[] buffer = new char[4096];
        private readonly char[] termBuf = new char[2];
        private int bufferPos;
        private int bufferLen;
        private int prevCjk = -1;     // 尚未与后续字符合成 bigram 的 CJK 字符
        private bool prevEmitted;     // prevCjk 是否已作为某个 bigram 的组成部分输出
        private int pendingChar = -1; // 冲刷孤立单字时回退的非 CJK 字符

        public CjkBigramTokenizer(TextReader input)
            : base(input)
        {
            termAtt = AddAttribute<ITermAttribute>();
        }

        /// <summary>
        /// 切分规则保证查询侧与索引侧一致：连续 CJK 串输出重叠 bigram，
        /// 仅当整个 run 只有一个字符时输出该单字（两侧判定方式相同），
        /// 因此"检索"在查询侧是单个词项，而不是"检索"+"索"的短语。
        /// </summary>
        public override bool IncrementToken()
        {
            ClearAttributes();
            while (true)
            {
                int c = NextChar();
                if (c < 0)
                {
                    if (prevCjk >= 0 && !prevEmitted)
                    {
                        EmitSingle(prevCjk);
                        prevCjk = -1;
                        return true;
                    }
                    return false;
                }
                if (IsCjk(c))
                {
                    if (prevCjk >= 0)
                    {
                        EmitBigram(prevCjk, c);
                        prevCjk = c;
                        prevEmitted = true;
                        return true;
                    }
                    prevCjk = c;
                    prevEmitted = false;
                    continue;
                }
                if (prevCjk >= 0)
                {
                    if (!prevEmitted)
                    {
                        pendingChar = c;
                        EmitSingle(prevCjk);
                        prevCjk = -1;
                        return true;
                    }
                    prevCjk = -1;
                }
                if (IsWordChar(c))
                {
                    EmitWord(c);
                    return true;
                }
                // 标点/空白：跳过
            }
        }

        private int NextChar()
        {
            if (pendingChar >= 0)
            {
                int c = pendingChar;
                pendingChar = -1;
                return c;
            }
            if (bufferPos >= bufferLen)
            {
                bufferLen = input.Read(buffer, 0, buffer.Length);
                bufferPos = 0;
                if (bufferLen <= 0) return -1;
            }
            return buffer[bufferPos++];
        }

        private void EmitSingle(int c)
        {
            termBuf[0] = (char)c;
            termAtt.SetTermBuffer(termBuf, 0, 1);
        }

        private void EmitBigram(int a, int b)
        {
            termBuf[0] = (char)a;
            termBuf[1] = (char)b;
            termAtt.SetTermBuffer(termBuf, 0, 2);
        }

        private void EmitWord(int first)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append((char)first);
            while (true)
            {
                int c = NextChar();
                if (c < 0) break;
                if (IsCjk(c)) { prevCjk = c; prevEmitted = false; break; }
                if (!IsWordChar(c)) break;
                sb.Append((char)c);
            }
            termAtt.SetTermBuffer(sb.ToString().ToLowerInvariant());
        }

        private static bool IsCjk(int c)
        {
            if (c < 0 || c > 0xFFFF) return false;
            char ch = (char)c;
            if (char.IsSurrogate(ch)) return true; // 增补平面（CJK 扩展 B 等）
            return (ch >= 0x3040 && ch <= 0x30FF)   // 平假名/片假名
                || (ch >= 0x3400 && ch <= 0x9FFF)   // CJK 统一表意（含扩展 A）
                || (ch >= 0xAC00 && ch <= 0xD7A3)   // 谚文音节
                || (ch >= 0xF900 && ch <= 0xFAFF);  // 兼容表意
        }

        private static bool IsWordChar(int c)
        {
            if (c < 0 || c > 0xFFFF) return false;
            return char.IsLetterOrDigit((char)c);
        }
    }
}

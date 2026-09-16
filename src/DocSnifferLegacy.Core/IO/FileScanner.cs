using System;
using System.Collections.Generic;
using System.IO;

namespace DocSnifferLegacy.Core.IO
{
    public sealed class FileRecord
    {
        public string FullPath;
        public string Name;
        public string Extension;
        public long Length;
        public DateTime LastWriteTimeUtc;
    }

    /// <summary>
    /// 递归目录扫描：跳过重解析点（符号链接/联接，防循环）、不可访问目录与排除目录，
    /// 按扩展名白名单过滤。目录访问失败一律静默跳过，不中断整体扫描。
    /// </summary>
    public static class FileScanner
    {
        public static List<FileRecord> Scan(string root, ISet<string> extensions, ISet<string> excludeDirs, Func<bool> shouldContinue)
        {
            var results = new List<FileRecord>();
            var stack = new Stack<string>();
            stack.Push(Path.GetFullPath(root));
            while (stack.Count > 0)
            {
                if (shouldContinue != null && !shouldContinue()) throw new OperationCanceledException();
                string dir = stack.Pop();

                string[] subDirs = SafeGet(delegate { return Directory.GetDirectories(dir); });
                if (subDirs != null)
                {
                    foreach (string sub in subDirs)
                    {
                        if (IsExcluded(sub, excludeDirs)) continue;
                        if (IsReparsePoint(sub)) continue;
                        stack.Push(sub);
                    }
                }

                string[] fileNames = SafeGet(delegate { return Directory.GetFiles(dir); });
                if (fileNames == null) continue;
                foreach (string f in fileNames)
                {
                    string ext = Path.GetExtension(f) ?? string.Empty;
                    ext = ext.ToLowerInvariant();
                    if (extensions != null && !extensions.Contains(ext)) continue; // null = 全部文件（敏感检测用）
                    try
                    {
                        var fi = new FileInfo(f);
                        results.Add(new FileRecord
                        {
                            FullPath = fi.FullName,
                            Name = fi.Name,
                            Extension = ext,
                            Length = fi.Length,
                            LastWriteTimeUtc = fi.LastWriteTimeUtc
                        });
                    }
                    catch (Exception) { }
                }
            }
            results.Sort(delegate(FileRecord a, FileRecord b) { return string.CompareOrdinal(a.FullPath, b.FullPath); });
            return results;
        }

        private static string[] SafeGet(Func<string[]> action)
        {
            try { return action(); }
            catch (Exception) { return null; }
        }

        private static bool IsExcluded(string dir, ISet<string> excludeDirs)
        {
            if (excludeDirs == null || excludeDirs.Count == 0) return false;
            string full = Path.GetFullPath(dir).TrimEnd('\\', '/');
            foreach (string ex in excludeDirs)
            {
                string exFull = Path.GetFullPath(ex).TrimEnd('\\', '/');
                if (string.Equals(full, exFull, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool IsReparsePoint(string dir)
        {
            try { return (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0; }
            catch (Exception) { return true; }
        }
    }
}

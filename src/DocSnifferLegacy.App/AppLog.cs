using System;
using System.IO;
using System.Text;

namespace DocSnifferLegacy.App
{
    /// <summary>
    /// 轻量文件日志，写入 %APPDATA%\DocSnifferLegacy\logs\app.log（超过 2MB 滚动为 app.log.old）。
    /// 用于问题定位：异常记录完整类型、消息与堆栈。写入失败一律静默，不影响业务流程。
    /// </summary>
    internal static class AppLog
    {
        private static readonly object Gate = new object();
        private const long MaxBytes = 2L * 1024 * 1024;

        public static string LogPath
        {
            get { return Path.Combine(AppSettings.BaseDirectory, "logs", "app.log"); }
        }

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message, ex);
        }

        private static void Write(string level, string message, Exception ex)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                    RollIfNeeded();
                    var sb = new StringBuilder();
                    sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    sb.Append(" [").Append(level).Append("] ").Append(message).AppendLine();
                    for (Exception e = ex; e != null; e = e.InnerException)
                    {
                        sb.Append("    类型: ").Append(e.GetType().FullName).AppendLine();
                        sb.Append("    消息: ").Append(e.Message).AppendLine();
                        if (e.StackTrace != null)
                        {
                            sb.Append("    堆栈: ").Append(e.StackTrace.Replace("\n", "\n    ")).AppendLine();
                        }
                    }
                    File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
                }
            }
            catch (Exception) { }
        }

        private static void RollIfNeeded()
        {
            FileInfo fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > MaxBytes)
            {
                string old = LogPath + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(LogPath, old);
            }
        }
    }
}

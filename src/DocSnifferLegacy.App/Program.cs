using System;
using System.Windows.Forms;

namespace DocSnifferLegacy.App
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            AppLog.Info("应用启动，版本 " + Application.ProductVersion
                + "，CLR " + Environment.Version
                + "，OS " + Environment.OSVersion.VersionString);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}

using System;
using System.IO;
using System.Xml.Serialization;

namespace DocSnifferLegacy.App
{
    /// <summary>持久化到 %APPDATA%\DocSnifferLegacy\settings.xml 的简单设置。</summary>
    public sealed class AppSettings
    {
        public string SourceDirectory = string.Empty;
        public string IndexDirectory = string.Empty;
        public bool FullRebuild;

        public static string BaseDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DocSnifferLegacy");
            }
        }

        public static string DefaultIndexPath
        {
            get { return Path.Combine(BaseDirectory, "index"); }
        }

        private static string SettingsPath
        {
            get { return Path.Combine(BaseDirectory, "settings.xml"); }
        }

        public static AppSettings Load()
        {
            try
            {
                string path = SettingsPath;
                if (File.Exists(path))
                {
                    var serializer = new XmlSerializer(typeof(AppSettings));
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                    {
                        var loaded = (AppSettings)serializer.Deserialize(fs);
                        if (loaded != null) return loaded;
                    }
                }
            }
            catch (Exception) { }
            var fresh = new AppSettings();
            fresh.IndexDirectory = DefaultIndexPath;
            return fresh;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(BaseDirectory);
                var serializer = new XmlSerializer(typeof(AppSettings));
                using (FileStream fs = new FileStream(SettingsPath, FileMode.Create, FileAccess.Write))
                {
                    serializer.Serialize(fs, this);
                }
            }
            catch (Exception) { }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using DocSnifferLegacy.Core.Sensitive;

namespace DocSnifferLegacy.App
{
    /// <summary>敏感规则持久化：%APPDATA%\DocSnifferLegacy\rules.xml；首次运行内置默认规则。</summary>
    public sealed class RuleStore
    {
        private readonly string path;
        public readonly object SyncRoot = new object();
        public List<SensitiveRule> Items = new List<SensitiveRule>();

        private RuleStore(string path)
        {
            this.path = path;
        }

        public static RuleStore Load(string path)
        {
            var store = new RuleStore(path);
            try
            {
                if (File.Exists(path))
                {
                    var serializer = new XmlSerializer(typeof(RuleStore));
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                    {
                        var loaded = (RuleStore)serializer.Deserialize(fs);
                        if (loaded != null && loaded.Items != null) store.Items = loaded.Items;
                    }
                }
            }
            catch (Exception) { }
            if (store.Items.Count == 0) store.Items = Defaults();
            return store;
        }

        public void Save()
        {
            lock (SyncRoot)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var serializer = new XmlSerializer(typeof(RuleStore));
                    using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                    {
                        serializer.Serialize(fs, this);
                    }
                }
                catch (Exception) { }
            }
        }

        public static List<SensitiveRule> Defaults()
        {
            var list = new List<SensitiveRule>();
            list.Add(new SensitiveRule
            {
                Id = "rule_idcard",
                Name = "身份证号",
                Pattern = "\\d{17}[\\dXx]",
                Type = "regex",
                RiskLevel = "high",
                MatchFilename = false,
                MatchContent = true
            });
            list.Add(new SensitiveRule
            {
                Id = "rule_mobile",
                Name = "手机号码",
                Pattern = "1[3-9]\\d{9}",
                Type = "regex",
                RiskLevel = "medium",
                MatchFilename = false,
                MatchContent = true
            });
            list.Add(new SensitiveRule
            {
                Id = "rule_ipv4",
                Name = "IP 地址",
                Pattern = "\\b(?:\\d{1,3}\\.){3}\\d{1,3}\\b",
                Type = "regex",
                RiskLevel = "low",
                MatchFilename = false,
                MatchContent = true
            });
            list.Add(new SensitiveRule
            {
                Id = "rule_confidential",
                Name = "机密字样",
                Pattern = "机密",
                Type = "keyword",
                RiskLevel = "high",
                MatchFilename = true,
                MatchContent = true
            });
            list.Add(new SensitiveRule
            {
                Id = "rule_internal",
                Name = "内部资料",
                Pattern = "内部资料",
                Type = "keyword",
                RiskLevel = "medium",
                MatchFilename = true,
                MatchContent = true
            });
            return list;
        }
    }
}

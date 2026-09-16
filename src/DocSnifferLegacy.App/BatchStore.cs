using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace DocSnifferLegacy.App
{
    /// <summary>一次目录导入对应一个批次（对齐 DocSniffer 的批次模型）。</summary>
    public sealed class BatchInfo
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string RootPath = string.Empty;
        public DateTime CreatedAt;
        public bool IncludeContent = true;
    }

    /// <summary>批次登记表：持久化到 %APPDATA%\DocSnifferLegacy\batches.xml。</summary>
    public sealed class BatchStore
    {
        private readonly string path;
        public readonly object SyncRoot = new object();
        public List<BatchInfo> Items = new List<BatchInfo>();

        private BatchStore(string path)
        {
            this.path = path;
        }

        public static BatchStore Load(string path)
        {
            var store = new BatchStore(path);
            try
            {
                if (File.Exists(path))
                {
                    var serializer = new XmlSerializer(typeof(BatchStore));
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                    {
                        var loaded = (BatchStore)serializer.Deserialize(fs);
                        if (loaded != null && loaded.Items != null) store.Items = loaded.Items;
                    }
                }
            }
            catch (Exception) { }
            return store;
        }

        public void Save()
        {
            lock (SyncRoot)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var serializer = new XmlSerializer(typeof(BatchStore));
                    using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                    {
                        serializer.Serialize(fs, this);
                    }
                }
                catch (Exception) { }
            }
        }

        public BatchInfo Find(string id)
        {
            lock (SyncRoot)
            {
                foreach (BatchInfo b in Items)
                {
                    if (b.Id == id) return b;
                }
                return null;
            }
        }

        public void Add(BatchInfo batch)
        {
            lock (SyncRoot)
            {
                Items.Add(batch);
                Save();
            }
        }

        public void Remove(string id)
        {
            lock (SyncRoot)
            {
                for (int i = Items.Count - 1; i >= 0; i--)
                {
                    if (Items[i].Id == id) Items.RemoveAt(i);
                }
                Save();
            }
        }

        public void Clear()
        {
            lock (SyncRoot)
            {
                Items.Clear();
                Save();
            }
        }
    }
}

using System;
using DocSnifferLegacy.Core.Extract;
using DocSnifferLegacy.Core.Index;
using DocSnifferLegacy.Core.Routing;

namespace DocSnifferLegacy.App
{
    /// <summary>
    /// 应用服务容器：索引服务、格式路由、批次与规则存储，以及全局任务忙闸
    /// （同一时刻只允许一个写索引/检测任务，对应各页面的按钮联动禁用）。
    /// </summary>
    internal sealed class AppServices : IDisposable
    {
        public readonly AppSettings Settings;
        public readonly FormatRouter Router;
        public readonly LuceneIndexService Index;
        public readonly BatchStore Batches;
        public readonly RuleStore Rules;

        private readonly object workLock = new object();
        private bool busy;

        public event Action BusyChanged;

        public AppServices()
        {
            Settings = AppSettings.Load();
            ZipArchiveExtractor zipExtractor = new ZipArchiveExtractor();
            // 路由顺序即优先级：OOXML 在前（PK 结构的 .wps 优先按 OOXML 解析），OLE2 兜底旧版 WPS/DOC 系，
            // ZipArchiveExtractor 穿透压缩包（需要引用路由器自身，构建完成后注入）。
            Router = new FormatRouter(new ITextExtractor[]
            {
                new OoxmlExtractor(),
                new Ole2Extractor(),
                new OfdExtractor(),
                new PdfiumExtractor(),
                zipExtractor,
                new TextFileExtractor()
            });
            zipExtractor.InnerRouter = Router;
            Index = new LuceneIndexService(
                string.IsNullOrEmpty(Settings.IndexDirectory) ? AppSettings.DefaultIndexPath : Settings.IndexDirectory);
            Batches = BatchStore.Load(System.IO.Path.Combine(AppSettings.BaseDirectory, "batches.xml"));
            Rules = RuleStore.Load(System.IO.Path.Combine(AppSettings.BaseDirectory, "rules.xml"));
        }

        public bool IsBusy { get { lock (workLock) { return busy; } } }

        public long TotalDocs
        {
            get { try { return Index.TotalDocs; } catch (Exception) { return 0; } }
        }

        /// <summary>尝试占用任务闸；false 表示已有任务在进行。</summary>
        public bool TryBeginWork()
        {
            lock (workLock)
            {
                if (busy) return false;
                busy = true;
            }
            Action h = BusyChanged;
            if (h != null) h();
            return true;
        }

        public void EndWork()
        {
            lock (workLock)
            {
                busy = false;
            }
            Action h = BusyChanged;
            if (h != null) h();
        }

        public void Dispose()
        {
            Index.Dispose();
        }
    }
}

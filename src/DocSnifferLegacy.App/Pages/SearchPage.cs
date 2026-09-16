using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using DocSnifferLegacy.Core.Index;

namespace DocSnifferLegacy.App.Pages
{
    /// <summary>搜索页：批次范围选择 + 关键词输入 + 结果列表（对齐 Web 版搜索页布局）。</summary>
    internal sealed class SearchPage : Panel
    {
        private readonly AppServices services;

        private ComboBox batchSelect;
        private TextBox txtQuery;
        private Button btnSearch;
        private Label metaLabel;
        private ListView lvResults;
        private Thread worker;

        public SearchPage(AppServices services)
        {
            this.services = services;
            Dock = DockStyle.Fill;
            BackColor = Theme.Panel;
            Padding = new Padding(16);
            BuildUi();
        }

        private void BuildUi()
        {
            batchSelect = new ComboBox();
            batchSelect.DropDownStyle = ComboBoxStyle.DropDownList;
            batchSelect.Width = 210;
            batchSelect.Height = 28;
            batchSelect.FlatStyle = FlatStyle.Flat;
            batchSelect.Items.Add(new BatchOption(null, "全部索引"));
            batchSelect.SelectedIndex = 0;

            txtQuery = Theme.Input();

            btnSearch = Theme.PrimaryButton("搜索");
            btnSearch.Width = 92;
            btnSearch.Click += delegate { StartSearch(); };

            var searchRow = new Panel();
            searchRow.Dock = DockStyle.Top;
            searchRow.Height = 36;
            searchRow.BackColor = Theme.Panel;
            searchRow.Controls.Add(txtQuery);
            searchRow.Controls.Add(batchSelect);
            searchRow.Controls.Add(btnSearch);
            searchRow.Resize += delegate
            {
                int w = searchRow.ClientSize.Width;
                btnSearch.Location = new Point(w - 92, 2);
                txtQuery.SetBounds(220, 2, w - 220 - 92 - 12, 28);
                batchSelect.SetBounds(0, 3, 210, 28);
            };

            metaLabel = Theme.Hint("输入关键词开始搜索。");
            metaLabel.Dock = DockStyle.Top;
            metaLabel.Height = 22;
            metaLabel.Padding = new Padding(0, 6, 0, 0);

            lvResults = new ListView();
            lvResults.View = View.Details;
            lvResults.FullRowSelect = true;
            lvResults.HideSelection = false;
            lvResults.Dock = DockStyle.Fill;
            lvResults.BorderStyle = BorderStyle.FixedSingle;
            lvResults.Columns.Add("文件名", 230);
            lvResults.Columns.Add("类型", 64);
            lvResults.Columns.Add("大小", 92, HorizontalAlignment.Right);
            lvResults.Columns.Add("修改时间", 130);
            lvResults.Columns.Add("得分", 60, HorizontalAlignment.Right);
            lvResults.Columns.Add("路径", 420);
            lvResults.DoubleClick += delegate { OpenSelected(false); };
            try
            {
                typeof(ListView).InvokeMember("DoubleBuffered",
                    System.Reflection.BindingFlags.SetProperty | System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic, null, lvResults, new object[] { true });
            }
            catch (Exception) { }

            var menu = new ContextMenuStrip();
            var openItem = new ToolStripMenuItem("打开文件");
            openItem.Click += delegate { OpenSelected(false); };
            var openFolderItem = new ToolStripMenuItem("打开所在文件夹");
            openFolderItem.Click += delegate { OpenSelected(true); };
            menu.Items.Add(openItem);
            menu.Items.Add(openFolderItem);
            lvResults.ContextMenuStrip = menu;

            Controls.Add(lvResults);
            Controls.Add(metaLabel);
            Controls.Add(searchRow);
        }

        /// <summary>刷新批次下拉框（扫描/删除批次后由主窗体调用）。</summary>
        public void ReloadBatches()
        {
            object selected = batchSelect.SelectedItem;
            string selectedId = selected is BatchOption ? ((BatchOption)selected).Id : null;
            batchSelect.BeginUpdate();
            try
            {
                batchSelect.Items.Clear();
                batchSelect.Items.Add(new BatchOption(null, "全部索引"));
                lock (services.Batches.SyncRoot)
                {
                    foreach (BatchInfo b in services.Batches.Items)
                    {
                        long n = services.Index.DocCountForBatch(b.Id);
                        batchSelect.Items.Add(new BatchOption(b.Id, b.Name + "（" + n + " 个文档）"));
                    }
                }
                int restore = 0;
                for (int i = 0; i < batchSelect.Items.Count; i++)
                {
                    if (((BatchOption)batchSelect.Items[i]).Id == selectedId) { restore = i; break; }
                }
                batchSelect.SelectedIndex = restore;
            }
            finally
            {
                batchSelect.EndUpdate();
            }
        }

        private string SelectedBatchId
        {
            get
            {
                BatchOption opt = batchSelect.SelectedItem as BatchOption;
                return opt == null ? null : opt.Id;
            }
        }

        private void StartSearch()
        {
            string query = txtQuery.Text.Trim();
            if (query.Length == 0) return;
            if (services.IsBusy) { metaLabel.Text = "有任务正在进行，请稍候..."; return; }

            btnSearch.Text = "搜索中…";
            btnSearch.Enabled = false;
            metaLabel.Text = "搜索中...";
            string batchId = SelectedBatchId;
            worker = new Thread(new ThreadStart(delegate { RunSearch(query, batchId); }));
            worker.IsBackground = true;
            worker.Start();
        }

        private void RunSearch(string query, string batchId)
        {
            try
            {
                var watch = Stopwatch.StartNew();
                IList<SearchResultItem> items = services.Index.Search(query, 500, batchId);
                watch.Stop();
                double ms = watch.Elapsed.TotalMilliseconds;
                Ui(delegate { FillResults(items, ms); });
            }
            catch (Exception ex)
            {
                string message = "搜索失败：" + ex.Message;
                Ui(delegate
                {
                    metaLabel.Text = message;
                    MessageBox.Show(FindForm(), message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
            }
            finally
            {
                Ui(delegate
                {
                    btnSearch.Text = "搜索";
                    btnSearch.Enabled = true;
                });
            }
        }

        private void FillResults(IList<SearchResultItem> items, double ms)
        {
            lvResults.BeginUpdate();
            try
            {
                lvResults.Items.Clear();
                if (items != null)
                {
                    foreach (SearchResultItem item in items)
                    {
                        var lvi = new ListViewItem(item.FileName);
                        lvi.SubItems.Add(item.FileType);
                        lvi.SubItems.Add(FormatSize(item.Size));
                        lvi.SubItems.Add(item.Modified == DateTime.MinValue ? "-" : item.Modified.ToString("yyyy-MM-dd HH:mm"));
                        lvi.SubItems.Add(item.Score.ToString("F2"));
                        lvi.SubItems.Add(item.Path);
                        lvi.Tag = item.Path;
                        lvResults.Items.Add(lvi);
                    }
                }
            }
            finally
            {
                lvResults.EndUpdate();
            }
            metaLabel.Text = (items == null || items.Count == 0)
                ? "没有匹配结果。"
                : string.Format("共 {0} 条结果（{1:F0} 毫秒）。双击打开文件，右键可打开所在文件夹。", items.Count, ms);
        }

        private void OpenSelected(bool openFolder)
        {
            if (lvResults.SelectedItems.Count == 0) return;
            string path = lvResults.SelectedItems[0].Tag as string;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (openFolder)
                {
                    if (System.IO.File.Exists(path))
                        Process.Start("explorer.exe", "/select,\"" + path + "\"");
                    else if (System.IO.Directory.Exists(path))
                        Process.Start("explorer.exe", "\"" + path + "\"");
                }
                else
                {
                    Process.Start(path);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), "无法打开：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Ui(Action action)
        {
            try
            {
                if (IsHandleCreated) BeginInvoke(action);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("F1") + " MB";
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F2") + " GB";
        }

        private sealed class BatchOption
        {
            public readonly string Id;
            private readonly string label;

            public BatchOption(string id, string label)
            {
                Id = id;
                this.label = label;
            }

            public override string ToString() { return label; }
        }
    }
}

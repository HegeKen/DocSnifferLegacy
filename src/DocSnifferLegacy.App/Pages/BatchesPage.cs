using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using DocSnifferLegacy.App.Pages;
using DocSnifferLegacy.Core.Index;
using DocSnifferLegacy.Core.IO;

namespace DocSnifferLegacy.App.Pages
{
    /// <summary>索引管理页：批次列表（文档数/创建时间）与更新、删除、一键清除（对齐 Web 版索引管理页）。</summary>
    internal sealed class BatchesPage : Panel
    {
        private readonly AppServices services;

        private DataGridView grid;
        private Button btnClearAll;
        private Button btnRefresh;
        private Label hintLabel;
        private Label msgLabel;

        public event Action StatusChanged;
        public event Action BatchesChanged;

        public BatchesPage(AppServices services)
        {
            this.services = services;
            Dock = DockStyle.Fill;
            BackColor = Theme.Panel;
            Padding = new Padding(16);
            BuildUi();
        }

        private void BuildUi()
        {
            var title = Theme.SectionTitle("索引管理");
            title.Dock = DockStyle.Top;
            title.Height = 32;

            var toolbar = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Theme.Panel, Padding = new Padding(0, 2, 0, 0) };
            btnClearAll = Theme.DangerButton("一键清除全部索引");
            btnClearAll.Width = 140;
            btnClearAll.Click += delegate { ClearAll(); };
            btnRefresh = Theme.NormalButton("刷新");
            btnRefresh.Width = 74;
            btnRefresh.Click += delegate { Reload(); };
            hintLabel = Theme.Hint("");
            msgLabel = Theme.Hint("");
            msgLabel.ForeColor = Theme.Ok;
            toolbar.Controls.Add(btnClearAll);
            toolbar.Controls.Add(btnRefresh);
            toolbar.Controls.Add(hintLabel);
            toolbar.Controls.Add(msgLabel);
            toolbar.Resize += delegate
            {
                int w = toolbar.ClientSize.Width;
                btnClearAll.Location = new Point(0, 2);
                btnRefresh.Location = new Point(148, 2);
                hintLabel.Location = new Point(230, 10);
                msgLabel.Location = new Point(w - 260, 10);
            };

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = Theme.Panel;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.ColumnHeadersDefaultCellStyle.Font = Theme.Small;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
            grid.DefaultCellStyle.Font = Theme.Body;

            var colName = new DataGridViewTextBoxColumn { HeaderText = "批次名称", FillWeight = 18 };
            var colPath = new DataGridViewTextBoxColumn { HeaderText = "导入目录", FillWeight = 38 };
            var colDocs = new DataGridViewTextBoxColumn { HeaderText = "文档数", FillWeight = 10, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight } };
            var colCreated = new DataGridViewTextBoxColumn { HeaderText = "创建时间", FillWeight = 16 };
            var colUpdate = new DataGridViewButtonColumn { HeaderText = "", Text = "更新", UseColumnTextForButtonValue = true, FillWeight = 8 };
            var colDelete = new DataGridViewButtonColumn { HeaderText = "", Text = "删除", UseColumnTextForButtonValue = true, FillWeight = 8 };
            grid.Columns.AddRange(new DataGridViewColumn[] { colName, colPath, colDocs, colCreated, colUpdate, colDelete });
            grid.CellContentClick += OnCellClick;

            Controls.Add(grid);
            Controls.Add(toolbar);
            Controls.Add(title);
        }

        /// <summary>刷新批次列表与实时文档数。</summary>
        public void Reload()
        {
            msgLabel.Text = "";
            List<BatchInfo> snapshot;
            lock (services.Batches.SyncRoot)
            {
                snapshot = new List<BatchInfo>(services.Batches.Items);
            }

            grid.Rows.Clear();
            long total = 0;
            foreach (BatchInfo b in snapshot)
            {
                long n = 0;
                try { n = services.Index.DocCountForBatch(b.Id); }
                catch (Exception) { }
                total += n;
                grid.Rows.Add(b.Name, b.RootPath, n, b.CreatedAt.ToString("yyyy-MM-dd HH:mm"), b.Id, b.Id);
            }
            hintLabel.Text = snapshot.Count == 0
                ? "暂无导入记录。可在“扫描”页导入一个目录，每次导入会生成一个批次。"
                : "共 " + snapshot.Count + " 个批次 · " + total + " 个文档";
            btnClearAll.Enabled = snapshot.Count > 0;
        }

        private void OnCellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= services.Batches.Items.Count) return;
            BatchInfo batch;
            lock (services.Batches.SyncRoot)
            {
                if (e.RowIndex >= services.Batches.Items.Count) return;
                batch = services.Batches.Items[e.RowIndex];
            }

            if (grid.Columns[e.ColumnIndex].HeaderText == "更新") UpdateBatch(batch);
            else if (grid.Columns[e.ColumnIndex].HeaderText == "删除") RemoveBatch(batch);
        }

        private void UpdateBatch(BatchInfo batch)
        {
            if (MessageBox.Show(FindForm(),
                    "重新扫描「" + batch.Name + "」的目录并更新其索引？将重新索引当前目录下的全部文件。",
                    "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            if (!services.TryBeginWork())
            {
                msgLabel.Text = "";
                msgLabel.ForeColor = Theme.Danger;
                msgLabel.Text = "已有任务正在进行，请稍候";
                return;
            }

            SetBusy(true);
            msgLabel.ForeColor = Theme.Ok;
            msgLabel.Text = "更新中...";
            Thread worker = new Thread(new ThreadStart(delegate
            {
                try
                {
                    AppLog.Info("更新批次 batch=" + batch.Id + " root=" + batch.RootPath);
                    // shouldContinue 约定：返回 false 即中断（抛 OperationCanceledException），此处无取消入口，恒为 true
                    List<FileRecord> files = FileScanner.Scan(batch.RootPath, services.Router.AllExtensions,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { services.Index.IndexPath },
                        delegate { return true; });
                    AppLog.Info("批次目录扫描完成，发现 " + files.Count + " 个待索引文件");
                    IndexRunStats stats = services.Index.IndexFiles(files, false, batch.Id, batch.IncludeContent,
                        services.Router, null, null, 200, 4 * 1024 * 1024, 50L * 1024 * 1024);
                    AppLog.Info("批次索引完成 新增=" + stats.Added + " 更新=" + stats.Updated
                        + " 跳过=" + stats.Skipped + " 失败=" + stats.Errors.Count);
                    Ui(delegate
                    {
                        msgLabel.Text = "已更新批次「" + batch.Name + "」，当前索引 " + stats.TotalDocs + " 个文档。";
                        Reload();
                    });
                    Raise(StatusChanged);
                }
                catch (Exception ex)
                {
                    AppLog.Error("更新批次失败 batch=" + batch.Id, ex);
                    string message = "更新失败：" + ex.Message;
                    Ui(delegate
                    {
                        msgLabel.ForeColor = Theme.Danger;
                        msgLabel.Text = message;
                        MessageBox.Show(FindForm(), message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                }
                finally
                {
                    services.EndWork();
                    Ui(delegate { SetBusy(false); });
                }
            }));
            worker.IsBackground = true;
            worker.Start();
        }

        private void RemoveBatch(BatchInfo batch)
        {
            long n = services.Index.DocCountForBatch(batch.Id);
            if (MessageBox.Show(FindForm(),
                    "确认删除批次「" + batch.Name + "」及其全部 " + n + " 个文档？",
                    "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            if (!services.TryBeginWork())
            {
                msgLabel.Text = "";
                msgLabel.ForeColor = Theme.Danger;
                msgLabel.Text = "已有任务正在进行，请稍候";
                return;
            }

            SetBusy(true);
            msgLabel.ForeColor = Theme.Ok;
            msgLabel.Text = "删除中...";
            Thread worker = new Thread(new ThreadStart(delegate
            {
                try
                {
                    services.Index.DeleteBatch(batch.Id);
                    services.Batches.Remove(batch.Id);
                    Ui(delegate
                    {
                        msgLabel.Text = "已删除批次「" + batch.Name + "」。";
                        Reload();
                    });
                    Raise(StatusChanged);
                    Raise(BatchesChanged);
                }
                catch (Exception ex)
                {
                    string message = "删除失败：" + ex.Message;
                    Ui(delegate
                    {
                        msgLabel.ForeColor = Theme.Danger;
                        msgLabel.Text = message;
                        MessageBox.Show(FindForm(), message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                }
                finally
                {
                    services.EndWork();
                    Ui(delegate { SetBusy(false); });
                }
            }));
            worker.IsBackground = true;
            worker.Start();
        }

        private void ClearAll()
        {
            if (MessageBox.Show(FindForm(), "确认清除现有全部索引和所有批次？此操作不可撤销。",
                    "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            if (!services.TryBeginWork())
            {
                msgLabel.Text = "";
                msgLabel.ForeColor = Theme.Danger;
                msgLabel.Text = "已有任务正在进行，请稍候";
                return;
            }

            SetBusy(true);
            msgLabel.ForeColor = Theme.Ok;
            msgLabel.Text = "清除中...";
            Thread worker = new Thread(new ThreadStart(delegate
            {
                try
                {
                    services.Index.ClearAll();
                    services.Batches.Clear();
                    Ui(delegate
                    {
                        msgLabel.Text = "已清除全部索引和批次。";
                        Reload();
                    });
                    Raise(StatusChanged);
                }
                catch (Exception ex)
                {
                    string message = "清除失败：" + ex.Message;
                    Ui(delegate
                    {
                        msgLabel.ForeColor = Theme.Danger;
                        msgLabel.Text = message;
                        MessageBox.Show(FindForm(), message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                }
                finally
                {
                    services.EndWork();
                    Ui(delegate { SetBusy(false); });
                }
            }));
            worker.IsBackground = true;
            worker.Start();
        }

        private void SetBusy(bool busy)
        {
            btnClearAll.Enabled = !busy && services.Batches.Items.Count > 0;
            btnRefresh.Enabled = !busy;
            grid.Enabled = !busy;
        }

        private void Raise(Action h)
        {
            if (h != null) h();
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
    }
}

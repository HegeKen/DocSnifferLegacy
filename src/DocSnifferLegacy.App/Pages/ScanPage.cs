using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using DocSnifferLegacy.Core.Index;
using DocSnifferLegacy.Core.IO;

namespace DocSnifferLegacy.App.Pages
{
    /// <summary>扫描页：目录导入、批次命名、内容开关、进度与告警（对齐 Web 版扫描页布局）。</summary>
    internal sealed class ScanPage : Panel
    {
        private readonly AppServices services;

        private TextBox txtScanPath;
        private TextBox txtBatchName;
        private CheckBox chkIncludeContent;
        private Button btnBrowse;
        private Button btnStart;
        private Label msgError;
        private Label msgOk;
        private Panel warningPanel;
        private ListBox warningList;
        private Label warningTitle;
        private ProgressBar progress;
        private Label progressLabel;

        public event Action StatusChanged;

        public ScanPage(AppServices services)
        {
            this.services = services;
            Dock = DockStyle.Fill;
            BackColor = Theme.Panel;
            Padding = new Padding(16);
            AutoScroll = true;
            BuildUi();
            if (!string.IsNullOrEmpty(services.Settings.SourceDirectory)) txtScanPath.Text = services.Settings.SourceDirectory;
        }

        private void BuildUi()
        {
            var title = Theme.SectionTitle("全盘 / 目录扫描");
            title.Dock = DockStyle.Top;
            title.Height = 32;

            var row1 = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Theme.Panel };
            txtScanPath = Theme.Input();
            btnBrowse = Theme.NormalButton("浏览...");
            btnBrowse.Width = 78;
            btnBrowse.Click += delegate { BrowseFolder(); };
            row1.Controls.Add(txtScanPath);
            row1.Controls.Add(btnBrowse);
            row1.Resize += delegate
            {
                int w = row1.ClientSize.Width;
                btnBrowse.Location = new Point(w - 78, 2);
                txtScanPath.SetBounds(0, 2, w - 78 - 8, 28);
            };

            var row2 = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Theme.Panel };
            txtBatchName = Theme.Input();
            var batchHint = Theme.Hint("批次名称（可选，留空则自动生成）");
            row2.Controls.Add(txtBatchName);
            row2.Controls.Add(batchHint);
            row2.Resize += delegate
            {
                int w = row2.ClientSize.Width;
                int hintW = batchHint.PreferredWidth;
                batchHint.SetBounds(Math.Max(0, w - hintW), 10, hintW, 16);
                txtBatchName.SetBounds(0, 4, Math.Max(120, w - hintW - 10), 28);
            };

            var row3 = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Panel, Padding = new Padding(0, 6, 0, 0) };
            chkIncludeContent = new CheckBox();
            chkIncludeContent.Text = "索引文件内容（否则仅索引文件名）";
            chkIncludeContent.Checked = true;
            chkIncludeContent.AutoSize = true;
            chkIncludeContent.UseVisualStyleBackColor = true;
            btnStart = Theme.PrimaryButton("开始扫描");
            btnStart.Width = 100;
            btnStart.Click += delegate { StartScan(); };
            row3.Controls.Add(chkIncludeContent);
            row3.Controls.Add(btnStart);
            row3.Resize += delegate { btnStart.Location = new Point(row3.ClientSize.Width - 100, 4); };

            msgError = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Theme.Danger, Font = Theme.Small, Text = "" };
            msgOk = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Theme.Ok, Font = Theme.Small, Text = "" };

            warningPanel = Theme.BorderedPanel();
            warningPanel.Dock = DockStyle.Top;
            warningPanel.Height = 150;
            warningPanel.Padding = new Padding(10, 8, 10, 8);
            warningPanel.Visible = false;
            warningTitle = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Theme.Warning, Font = Theme.Small, Text = "" };
            warningList = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = Theme.Small, IntegralHeight = false };
            warningPanel.Controls.Add(warningList);
            warningPanel.Controls.Add(warningTitle);

            var progressWrap = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Theme.Panel, Padding = new Padding(0, 12, 0, 0) };
            progress = new ProgressBar { Dock = DockStyle.Top, Height = 18 };
            progressLabel = Theme.Hint("");
            progressLabel.Dock = DockStyle.Top;
            progressLabel.Height = 18;
            progressLabel.Padding = new Padding(0, 4, 0, 0);
            progressWrap.Controls.Add(progress);
            progressWrap.Controls.Add(progressLabel);

            // 注意 Dock 顺序：先加入的在最下方
            Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel });
            Controls.Add(progressWrap);
            Controls.Add(warningPanel);
            Controls.Add(msgOk);
            Controls.Add(msgError);
            Controls.Add(row3);
            Controls.Add(row2);
            Controls.Add(row1);
            Controls.Add(title);
        }

        private void BrowseFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.ShowNewFolderButton = false;
                dialog.Description = "选择要扫描并建立索引的目录";
                if (System.IO.Directory.Exists(txtScanPath.Text)) dialog.SelectedPath = txtScanPath.Text;
                if (dialog.ShowDialog(FindForm()) == DialogResult.OK) txtScanPath.Text = dialog.SelectedPath;
            }
        }

        private void StartScan()
        {
            string root = txtScanPath.Text.Trim();
            if (root.Length == 0 || !System.IO.Directory.Exists(root))
            {
                ShowError("请输入要扫描的目录路径");
                return;
            }
            if (!services.TryBeginWork())
            {
                ShowError("已有任务正在进行，请稍候");
                return;
            }

            bool includeContent = chkIncludeContent.Checked;
            services.Settings.SourceDirectory = root;
            services.Settings.Save();

            SetBusy(true);
            ShowError("");
            msgOk.Text = "";
            progress.Value = 0;
            progressLabel.Text = "";
            warningPanel.Visible = false;

            string batchNameInput = txtBatchName.Text.Trim();
            Thread worker = new Thread(new ThreadStart(delegate { RunScan(root, batchNameInput, includeContent); }));
            worker.IsBackground = true;
            worker.Start();
        }

        private void RunScan(string root, string batchNameInput, bool includeContent)
        {
            try
            {
                Ui(delegate { msgOk.Text = "正在扫描目录..."; });

                string batchId = "batch_" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
                string batchName = batchNameInput.Length > 0
                    ? batchNameInput
                    : "批次 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                AppLog.Info("开始扫描 root=" + root + "，批次=" + batchName + "，索引内容=" + includeContent);

                // shouldContinue 约定：返回 false 即中断（抛 OperationCanceledException），本页无取消入口，恒为 true
                List<FileRecord> files = FileScanner.Scan(root, services.Router.AllExtensions,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { services.Index.IndexPath },
                    delegate { return true; });
                AppLog.Info("目录扫描完成，发现 " + files.Count + " 个待索引文件");

                Ui(delegate
                {
                    progress.Maximum = Math.Max(files.Count, 1);
                    msgOk.Text = "发现 " + files.Count + " 个待索引文件，开始处理...";
                });

                DateTime lastUi = DateTime.MinValue;
                IndexRunStats stats = services.Index.IndexFiles(files, false, batchId, includeContent, services.Router,
                    delegate(IndexProgress p)
                    {
                        DateTime now = DateTime.Now;
                        if ((now - lastUi).TotalMilliseconds >= 100 || p.Processed >= p.Total)
                        {
                            lastUi = now;
                            int processed = p.Processed;
                            int total = p.Total;
                            string current = p.CurrentFile;
                            Ui(delegate
                            {
                                progress.Value = Math.Min(processed, progress.Maximum);
                                progressLabel.Text = processed + " / " + total + " · " + current;
                            });
                        }
                    },
                    // 返回 false 会提前 break 中断索引，本页无取消入口，恒为 true
                    delegate { return true; },
                    200, 4 * 1024 * 1024, 50L * 1024 * 1024);

                AppLog.Info("索引完成 新增=" + stats.Added + " 更新=" + stats.Updated
                    + " 跳过=" + stats.Skipped + " 失败=" + stats.Errors.Count);

                services.Batches.Add(new BatchInfo
                {
                    Id = batchId,
                    Name = batchName,
                    RootPath = root,
                    CreatedAt = DateTime.Now,
                    IncludeContent = includeContent
                });

                Ui(delegate
                {
                    string summary = "扫描完成，共索引 " + (stats.Added + stats.Updated) + " 个文件。";
                    if (stats.Skipped > 0) summary += "（跳过未变更 " + stats.Skipped + "）";
                    msgOk.Text = summary;
                    if (stats.Errors.Count > 0)
                    {
                        warningTitle.Text = stats.Errors.Count + " 个条目被跳过或索引失败：";
                        warningList.BeginUpdate();
                        warningList.Items.Clear();
                        int cap = Math.Min(stats.Errors.Count, 200);
                        for (int i = 0; i < cap; i++) warningList.Items.Add(stats.Errors[i]);
                        warningList.EndUpdate();
                        warningPanel.Visible = true;
                    }
                    progress.Value = progress.Maximum;
                    txtBatchName.Text = "";
                });

                Action h = StatusChanged;
                if (h != null) h();
            }
            catch (Exception ex)
            {
                AppLog.Error("扫描失败 root=" + root, ex);
                string message = "扫描失败：" + ex.Message;
                Ui(delegate
                {
                    ShowError(message);
                    MessageBox.Show(FindForm(), message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
            }
            finally
            {
                services.EndWork();
                Ui(delegate { SetBusy(false); });
            }
        }

        private void SetBusy(bool busy)
        {
            btnStart.Text = busy ? "扫描中…" : "开始扫描";
            btnStart.Enabled = !busy;
            btnBrowse.Enabled = !busy;
            chkIncludeContent.Enabled = !busy;
            txtScanPath.Enabled = !busy;
            txtBatchName.Enabled = !busy;
        }

        private void ShowError(string message)
        {
            msgError.Text = message;
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

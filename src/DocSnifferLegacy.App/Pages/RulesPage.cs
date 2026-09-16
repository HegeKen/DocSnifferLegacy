using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using DocSnifferLegacy.Core.Sensitive;

namespace DocSnifferLegacy.App.Pages
{
    /// <summary>敏感检测页：规则表格（增删改+保存）与执行检测（对齐 Web 版规则页布局）。</summary>
    internal sealed class RulesPage : Panel
    {
        private readonly AppServices services;

        private DataGridView grid;
        private Button btnAddRule;
        private Button btnSaveRules;
        private Label dirtyHint;
        private Label msgRules;
        private TextBox txtDetectPath;
        private CheckBox chkDetectContent;
        private Button btnBrowse;
        private Button btnDetect;
        private Label msgDetect;
        private ListView lvHits;
        private bool dirty;

        private static readonly string[] RiskLevels = { "low", "medium", "high", "critical" };

        public RulesPage(AppServices services)
        {
            this.services = services;
            Dock = DockStyle.Fill;
            BackColor = Theme.Panel;
            Padding = new Padding(16);
            AutoScroll = true;
            BuildUi();
            BindRules();
        }

        private void BuildUi()
        {
            var title1 = Theme.SectionTitle("敏感规则配置");
            title1.Dock = DockStyle.Top;
            title1.Height = 32;

            var toolbar = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Theme.Panel, Padding = new Padding(0, 2, 0, 0) };
            btnAddRule = Theme.NormalButton("添加规则");
            btnAddRule.Width = 90;
            btnAddRule.Click += delegate { AddRule(); };
            btnSaveRules = Theme.PrimaryButton("保存规则");
            btnSaveRules.Width = 100;
            btnSaveRules.Click += delegate { SaveRules(); };
            dirtyHint = Theme.Hint("");
            msgRules = Theme.Hint("");
            msgRules.ForeColor = Theme.Ok;
            toolbar.Controls.Add(btnAddRule);
            toolbar.Controls.Add(btnSaveRules);
            toolbar.Controls.Add(dirtyHint);
            toolbar.Controls.Add(msgRules);
            toolbar.Resize += delegate
            {
                int w = toolbar.ClientSize.Width;
                btnSaveRules.Location = new Point(96, 2);
                dirtyHint.Location = new Point(204, 10);
                msgRules.Location = new Point(340, 10);
                btnAddRule.Location = new Point(0, 2);
            };

            grid = new DataGridView();
            grid.Dock = DockStyle.Top;
            grid.Height = 220;
            grid.BackgroundColor = Theme.Panel;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.ReadOnly = false;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.ColumnHeadersDefaultCellStyle.Font = Theme.Small;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
            grid.DefaultCellStyle.Font = Theme.Body;

            var colName = new DataGridViewTextBoxColumn { Name = "name", HeaderText = "名称", FillWeight = 16 };
            var colType = new DataGridViewComboBoxColumn { Name = "type", HeaderText = "类型", FillWeight = 10 };
            colType.Items.Add("regex");
            colType.Items.Add("keyword");
            var colRisk = new DataGridViewComboBoxColumn { Name = "risk", HeaderText = "风险", FillWeight = 10 };
            foreach (string r in RiskLevels) colRisk.Items.Add(r);
            var colFile = new DataGridViewCheckBoxColumn { Name = "file", HeaderText = "文件名", FillWeight = 8 };
            var colContent = new DataGridViewCheckBoxColumn { Name = "content", HeaderText = "内容", FillWeight = 8 };
            var colPattern = new DataGridViewTextBoxColumn { Name = "pattern", HeaderText = "正则 / 关键词", FillWeight = 34 };
            var colDel = new DataGridViewButtonColumn { Name = "del", HeaderText = "", Text = "删除", UseColumnTextForButtonValue = true, FillWeight = 8, FlatStyle = FlatStyle.Flat };
            grid.Columns.AddRange(new DataGridViewColumn[]
            {
                colName, colType, colRisk, colFile, colContent, colPattern, colDel
            });
            grid.CellEndEdit += delegate { SyncFromGrid(); MarkDirty(); };
            grid.CurrentCellDirtyStateChanged += delegate
            {
                if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.CellContentClick += delegate(object s, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0 && grid.Columns[e.ColumnIndex].Name == "del") RemoveRule(e.RowIndex);
            };

            var title2 = Theme.SectionTitle("执行检测");
            title2.Dock = DockStyle.Top;
            title2.Height = 44;

            var detectRow = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Theme.Panel };
            txtDetectPath = Theme.Input();
            btnBrowse = Theme.NormalButton("浏览...");
            btnBrowse.Width = 78;
            btnBrowse.Click += delegate { BrowseFolder(); };
            chkDetectContent = new CheckBox
            {
                Text = "检测文件内容",
                Checked = true,
                AutoSize = true,
                UseVisualStyleBackColor = true
            };
            btnDetect = Theme.PrimaryButton("开始检测");
            btnDetect.Width = 100;
            btnDetect.Click += delegate { RunDetection(); };
            detectRow.Controls.Add(txtDetectPath);
            detectRow.Controls.Add(btnBrowse);
            detectRow.Controls.Add(chkDetectContent);
            detectRow.Controls.Add(btnDetect);
            detectRow.Resize += delegate
            {
                int w = detectRow.ClientSize.Width;
                btnDetect.Location = new Point(w - 100, 2);
                chkDetectContent.Location = new Point(w - 100 - 8 - 110, 6);
                btnBrowse.Location = new Point(w - 100 - 8 - 110 - 8 - 78, 2);
                txtDetectPath.SetBounds(0, 2, w - 100 - 110 - 78 - 24, 28);
            };

            msgDetect = Theme.Hint("对文件名和/或文件内容做规则匹配，输出命中报告。");
            msgDetect.Dock = DockStyle.Top;
            msgDetect.Height = 22;
            msgDetect.Padding = new Padding(0, 4, 0, 0);

            lvHits = new ListView();
            lvHits.View = View.Details;
            lvHits.FullRowSelect = true;
            lvHits.Dock = DockStyle.Fill;
            lvHits.BorderStyle = BorderStyle.FixedSingle;
            lvHits.Columns.Add("风险", 76);
            lvHits.Columns.Add("规则", 110);
            lvHits.Columns.Add("位置", 70);
            lvHits.Columns.Add("匹配内容", 300);
            lvHits.Columns.Add("路径", 360);
            lvHits.DoubleClick += delegate { OpenSelected(); };

            Controls.Add(lvHits);
            Controls.Add(msgDetect);
            Controls.Add(detectRow);
            Controls.Add(title2);
            Controls.Add(grid);
            Controls.Add(toolbar);
            Controls.Add(title1);
        }

        private void BindRules()
        {
            grid.Rows.Clear();
            lock (services.Rules.SyncRoot)
            {
                foreach (SensitiveRule r in services.Rules.Items)
                {
                    grid.Rows.Add(r.Name, r.Type, r.RiskLevel, r.MatchFilename, r.MatchContent, r.Pattern);
                }
            }
            dirty = false;
            dirtyHint.Text = "";
        }

        private void SyncFromGrid()
        {
            var list = new List<SensitiveRule>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow) continue;
                var rule = new SensitiveRule();
                rule.Id = "rule_" + Guid.NewGuid().ToString("N").Substring(0, 12);
                rule.Name = CellText(row.Cells["name"].Value);
                rule.Type = CellText(row.Cells["type"].Value) == "keyword" ? "keyword" : "regex";
                rule.RiskLevel = CellText(row.Cells["risk"].Value);
                if (Array.IndexOf(RiskLevels, rule.RiskLevel) < 0) rule.RiskLevel = "medium";
                rule.MatchFilename = row.Cells["file"].Value is bool && (bool)row.Cells["file"].Value;
                rule.MatchContent = row.Cells["content"].Value is bool && (bool)row.Cells["content"].Value;
                rule.Pattern = CellText(row.Cells["pattern"].Value);
                list.Add(rule);
            }
            lock (services.Rules.SyncRoot)
            {
                services.Rules.Items = list;
            }
        }

        private static string CellText(object value)
        {
            return value == null ? string.Empty : value.ToString().Trim();
        }

        private void MarkDirty()
        {
            dirty = true;
            dirtyHint.Text = "有未保存的修改";
        }

        private void AddRule()
        {
            SyncFromGrid();
            lock (services.Rules.SyncRoot)
            {
                services.Rules.Items.Add(new SensitiveRule
                {
                    Id = "rule_" + Guid.NewGuid().ToString("N").Substring(0, 12),
                    Name = "新规则",
                    Pattern = "",
                    Type = "regex",
                    RiskLevel = "medium",
                    MatchFilename = false,
                    MatchContent = true
                });
            }
            BindRulesKeepDirty();
            MarkDirty();
        }

        private void RemoveRule(int rowIndex)
        {
            SyncFromGrid();
            if (rowIndex >= 0 && rowIndex < services.Rules.Items.Count)
            {
                lock (services.Rules.SyncRoot)
                {
                    services.Rules.Items.RemoveAt(rowIndex);
                }
            }
            BindRulesKeepDirty();
            MarkDirty();
        }

        private void BindRulesKeepDirty()
        {
            bool wasDirty = dirty;
            BindRules();
            dirty = wasDirty;
            dirtyHint.Text = wasDirty ? "有未保存的修改" : "";
        }

        private void SaveRules()
        {
            SyncFromGrid();
            services.Rules.Save();
            dirty = false;
            dirtyHint.Text = "";
            msgRules.Text = "规则已保存。";
        }

        private void BrowseFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.ShowNewFolderButton = false;
                dialog.Description = "选择要检测的目录";
                if (System.IO.Directory.Exists(txtDetectPath.Text)) dialog.SelectedPath = txtDetectPath.Text;
                if (dialog.ShowDialog(FindForm()) == DialogResult.OK) txtDetectPath.Text = dialog.SelectedPath;
            }
        }

        private void RunDetection()
        {
            string path = txtDetectPath.Text.Trim();
            if (path.Length == 0 || (!System.IO.Directory.Exists(path) && !System.IO.File.Exists(path)))
            {
                msgDetect.Text = "请输入要检测的路径（文件或目录）";
                return;
            }
            if (!services.TryBeginWork())
            {
                msgDetect.Text = "已有任务正在进行，请稍候";
                return;
            }

            SyncFromGrid();
            List<SensitiveRule> snapshot;
            lock (services.Rules.SyncRoot)
            {
                snapshot = new List<SensitiveRule>(services.Rules.Items);
            }
            bool includeContent = chkDetectContent.Checked;

            btnDetect.Text = "检测中…";
            btnDetect.Enabled = false;
            txtDetectPath.Enabled = false;
            msgDetect.Text = "检测中...";

            Thread worker = new Thread(new ThreadStart(delegate
            {
                try
                {
                    AppLog.Info("敏感检测 path=" + path + "，规则数=" + snapshot.Count + "，检测内容=" + includeContent);
                    // shouldContinue 约定：返回 false 即中断（抛 OperationCanceledException），本页无取消入口，恒为 true
                    List<SensitiveHit> hits = SensitiveScanner.Scan(path, includeContent, snapshot, services.Router,
                        delegate { return true; }, 4 * 1024 * 1024, 50L * 1024 * 1024);
                    AppLog.Info("敏感检测完成，命中 " + hits.Count + " 处");
                    Ui(delegate
                    {
                        FillHits(hits);
                        msgDetect.Text = "检测完成，发现 " + hits.Count + " 处敏感命中。";
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Error("敏感检测失败 path=" + path, ex);
                    string message = "检测失败：" + ex.Message;
                    Ui(delegate
                    {
                        msgDetect.Text = message;
                        MessageBox.Show(FindForm(), message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                }
                finally
                {
                    services.EndWork();
                    Ui(delegate
                    {
                        btnDetect.Text = "开始检测";
                        btnDetect.Enabled = true;
                        txtDetectPath.Enabled = true;
                    });
                }
            }));
            worker.IsBackground = true;
            worker.Start();
        }

        private void FillHits(List<SensitiveHit> hits)
        {
            lvHits.BeginUpdate();
            try
            {
                lvHits.Items.Clear();
                if (hits == null) return;
                foreach (SensitiveHit h in hits)
                {
                    var lvi = new ListViewItem(h.RiskLevel);
                    lvi.SubItems.Add(h.RuleName);
                    lvi.SubItems.Add(h.MatchType == "filename" ? "文件名" : "内容");
                    lvi.SubItems.Add(h.Matched ?? "");
                    lvi.SubItems.Add(h.Path);
                    lvi.ForeColor = Theme.RiskColor(h.RiskLevel);
                    lvi.Tag = h.Path;
                    lvHits.Items.Add(lvi);
                }
            }
            finally
            {
                lvHits.EndUpdate();
            }
        }

        private void OpenSelected()
        {
            if (lvHits.SelectedItems.Count == 0) return;
            string path = lvHits.SelectedItems[0].Tag as string;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
            try
            {
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch (Exception) { }
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

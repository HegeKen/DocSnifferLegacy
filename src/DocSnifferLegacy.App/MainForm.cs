using System;
using System.Drawing;
using System.Windows.Forms;
using DocSnifferLegacy.App.Pages;

namespace DocSnifferLegacy.App
{
    /// <summary>
    /// 主窗体：品牌头部 + 状态徽章 + 四个标签页（搜索 / 扫描 / 敏感检测 / 索引管理），
    /// 整体布局与 DocSniffer Web 版一致。
    /// </summary>
    public sealed class MainForm : Form
    {
        private readonly AppServices services;
        private Panel statusChip;
        private Label countLabel;
        private Label pathLabel;
        private FlatTabs tabs;
        private SearchPage searchPage;
        private ScanPage scanPage;
        private RulesPage rulesPage;
        private BatchesPage batchesPage;

        public MainForm()
        {
            services = new AppServices();
            // 窗体/任务栏图标：取自 exe 内嵌图标（csproj ApplicationIcon）
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { }
            BuildUi();
            RefreshStatus();
            searchPage.ReloadBatches();
            batchesPage.Reload();
        }

        private void BuildUi()
        {
            Text = "DocSnifferLegacy - 文档嗅探器";
            Font = Theme.Body;
            BackColor = Theme.Background;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(900, 620);
            Size = new Size(1020, 700);

            // ---- 头部：品牌 + 状态徽章 ----
            var header = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Theme.Background, Padding = new Padding(16, 6, 16, 0) };

            var brand = new Label
            {
                Text = "DocSnifferLegacy",
                ForeColor = Theme.Primary,
                Font = Theme.Title,
                AutoSize = true,
                Location = new Point(18, 6)
            };
            var subtitle = new Label
            {
                Text = "文档嗅探器 · 遗留系统版",
                ForeColor = Theme.Muted,
                Font = Theme.Small,
                AutoSize = true,
                Location = new Point(19, 34)
            };
            countLabel = new Label
            {
                Text = "已索引 - 个文件",
                Font = Theme.BodyBold,
                ForeColor = Theme.Text,
                AutoSize = true,
                Location = new Point(12, 4)
            };
            pathLabel = new Label
            {
                Text = "",
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleRight,
                Size = new Size(300, 16),
                Location = new Point(12, 23)
            };
            statusChip = Theme.BorderedPanel();
            statusChip.Size = new Size(420, 44);
            statusChip.Controls.Add(countLabel);
            statusChip.Controls.Add(pathLabel);
            header.Controls.Add(brand);
            header.Controls.Add(subtitle);
            header.Controls.Add(statusChip);
            header.Resize += delegate
            {
                statusChip.Location = new Point(header.ClientSize.Width - 16 - statusChip.Width, 6);
            };

            // ---- 标签页 ----
            tabs = new FlatTabs { Dock = DockStyle.Fill, BackColor = Theme.Background };
            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabs.SizeMode = TabSizeMode.Fixed;
            tabs.ItemSize = new Size(112, 34);
            tabs.Padding = new Point(16, 2);
            tabs.DrawItem += DrawTab;
            tabs.SelectedIndexChanged += delegate { OnTabChanged(); };

            searchPage = new SearchPage(services);
            scanPage = new ScanPage(services);
            rulesPage = new RulesPage(services);
            batchesPage = new BatchesPage(services);

            var tpSearch = new TabPage("搜索") { BackColor = Theme.Panel, Padding = new Padding(0) };
            var tpScan = new TabPage("扫描") { BackColor = Theme.Panel, Padding = new Padding(0) };
            var tpRules = new TabPage("敏感检测") { BackColor = Theme.Panel, Padding = new Padding(0) };
            var tpIndex = new TabPage("索引管理") { BackColor = Theme.Panel, Padding = new Padding(0) };
            tpSearch.Controls.Add(searchPage);
            tpScan.Controls.Add(scanPage);
            tpRules.Controls.Add(rulesPage);
            tpIndex.Controls.Add(batchesPage);
            tabs.TabPages.AddRange(new[] { tpSearch, tpScan, tpRules, tpIndex });

            // ---- 事件联动 ----
            scanPage.StatusChanged += delegate
            {
                RefreshStatus();
                searchPage.ReloadBatches();
                batchesPage.Reload();
            };
            batchesPage.StatusChanged += delegate { RefreshStatus(); };
            batchesPage.BatchesChanged += delegate { searchPage.ReloadBatches(); };

            Controls.Add(tabs);
            Controls.Add(header);

            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (services.IsBusy && e.CloseReason == CloseReason.UserClosing)
                {
                    DialogResult r = MessageBox.Show(this, "有任务正在进行，确定退出吗？未完成的任务将中断。",
                        "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                    if (r != DialogResult.OK) e.Cancel = true;
                }
                services.Dispose();
            };
        }

        private void DrawTab(object sender, DrawItemEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle bounds = e.Bounds;
            bool selected = e.Index == tabs.SelectedIndex;
            using (var bg = new SolidBrush(Theme.Background))
            {
                g.FillRectangle(bg, bounds);
            }
            string title = tabs.TabPages[e.Index].Text;
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var textBrush = new SolidBrush(selected ? Theme.Primary : Theme.Muted))
            {
                g.DrawString(title, selected ? Theme.BodyBold : Theme.Body, textBrush, bounds, format);
            }
            if (selected)
            {
                using (var underline = new SolidBrush(Theme.Primary))
                {
                    g.FillRectangle(underline, bounds.Left, bounds.Bottom - 3, bounds.Width, 3);
                }
            }
        }

        private void OnTabChanged()
        {
            RefreshStatus();
            if (tabs.SelectedIndex == 0) searchPage.ReloadBatches();
            if (tabs.SelectedIndex == 3) batchesPage.Reload();
        }

        private void RefreshStatus()
        {
            try
            {
                countLabel.Text = "已索引 " + services.TotalDocs + " 个文件";
                pathLabel.Text = services.Index.IndexPath;
            }
            catch (Exception)
            {
                countLabel.Text = "已索引 - 个文件";
            }
        }

        /// <summary>平面化标签控件：去掉立体页签，激活页签为蓝字 + 蓝色下划线。</summary>
        private sealed class FlatTabs : TabControl
        {
            public FlatTabs()
            {
                DoubleBuffered = true;
            }
        }
    }
}

using System;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DocSnifferLegacy.App
{
    /// <summary>
    /// 界面主题：对齐 DocSniffer Web 版的配色与控件风格（浅灰底、白面板、蓝色主按钮）。
    /// 字体在 Win7+ 用微软雅黑，XP 回退宋体。
    /// </summary>
    internal static class Theme
    {
        public static readonly Color Background = Color.FromArgb(0xF5, 0xF6, 0xF8);
        public static readonly Color Panel = Color.White;
        public static readonly Color Border = Color.FromArgb(0xE3, 0xE6, 0xEA);
        public static readonly Color Text = Color.FromArgb(0x1F, 0x24, 0x30);
        public static readonly Color Muted = Color.FromArgb(0x6B, 0x72, 0x80);
        public static readonly Color Primary = Color.FromArgb(0x25, 0x63, 0xEB);
        public static readonly Color PrimaryHover = Color.FromArgb(0x1D, 0x4E, 0xD8);
        public static readonly Color Danger = Color.FromArgb(0xDC, 0x26, 0x26);
        public static readonly Color Ok = Color.FromArgb(0x16, 0xA3, 0x4A);
        public static readonly Color Warning = Color.FromArgb(0xB4, 0x53, 0x09);
        public static readonly Color RiskLow = Color.FromArgb(0x6B, 0x72, 0x80);
        public static readonly Color RiskMedium = Color.FromArgb(0xD9, 0x77, 0x06);
        public static readonly Color RiskHigh = Color.FromArgb(0xDC, 0x26, 0x26);
        public static readonly Color RiskCritical = Color.FromArgb(0x7F, 0x1D, 0x1D);
        /// <summary>搜索预览中的关键词高亮底色（浅琥珀）。</summary>
        public static readonly Color Highlight = Color.FromArgb(0xFD, 0xE6, 0x8A);

        public static readonly Font Title;
        public static readonly Font Body;
        public static readonly Font BodyBold;
        public static readonly Font Small;

        static Theme()
        {
            string family = "宋体";
            try
            {
                var installed = new InstalledFontCollection();
                foreach (FontFamily f in installed.Families)
                {
                    if (f.Name == "Microsoft YaHei") { family = "Microsoft YaHei"; break; }
                }
            }
            catch (Exception) { }
            Title = new Font(family, 13.5F, FontStyle.Bold);
            Body = new Font(family, 9F);
            BodyBold = new Font(family, 9F, FontStyle.Bold);
            Small = new Font(family, 8.25F);
        }

        public static Color RiskColor(string level)
        {
            switch ((level ?? string.Empty).ToLowerInvariant())
            {
                case "low": return RiskLow;
                case "medium": return RiskMedium;
                case "high": return RiskHigh;
                case "critical": return RiskCritical;
                default: return Muted;
            }
        }

        public static Button PrimaryButton(string text)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Primary;
            b.ForeColor = Color.White;
            b.FlatAppearance.BorderColor = Primary;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = PrimaryHover;
            b.FlatAppearance.MouseDownBackColor = PrimaryHover;
            b.Height = 30;
            b.UseVisualStyleBackColor = false;
            return b;
        }

        public static Button NormalButton(string text)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Panel;
            b.ForeColor = Text;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.BorderSize = 1;
            b.Height = 30;
            b.UseVisualStyleBackColor = false;
            return b;
        }

        public static Button DangerButton(string text)
        {
            Button b = NormalButton(text);
            b.ForeColor = Danger;
            b.FlatAppearance.BorderColor = Color.FromArgb(0xF3, 0xC4, 0xC4);
            return b;
        }

        public static TextBox Input()
        {
            var t = new TextBox();
            t.BorderStyle = BorderStyle.FixedSingle;
            t.Height = 28;
            t.Font = Body;
            return t;
        }

        public static Label Hint(string text)
        {
            var l = new Label();
            l.Text = text;
            l.ForeColor = Muted;
            l.Font = Small;
            l.AutoSize = true;
            return l;
        }

        public static Label SectionTitle(string text)
        {
            var l = new Label();
            l.Text = text;
            l.ForeColor = Text;
            l.Font = new Font(Body.FontFamily, 11F, FontStyle.Bold);
            l.AutoSize = true;
            return l;
        }

        /// <summary>白底描边容器（对应 Web 版的 status-chip / 卡片）。</summary>
        public static Panel BorderedPanel()
        {
            var p = new Panel();
            p.BackColor = Panel;
            p.Resize += delegate { p.Invalidate(); };
            p.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Border))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
                }
            };
            return p;
        }
    }
}

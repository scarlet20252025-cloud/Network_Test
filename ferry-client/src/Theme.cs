using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Ferry
{
    /// <summary>
    /// 配色与控件小工具。
    ///
    /// 为什么不用默认的 SystemColors：默认控件在 Win11 上是直角 + 系统灰，
    /// 和托盘里那个一眼「正经软件」的印象差很远。这里只做三件事 ——
    /// 圆角面板、扁平按钮、统一字号 —— 就能拉开观感，代码量还很小。
    /// </summary>
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(242, 245, 246);
        public static readonly Color CardBg = Color.White;
        public static readonly Color Line = Color.FromArgb(228, 233, 235);
        public static readonly Color Text = Color.FromArgb(44, 62, 80);
        public static readonly Color Muted = Color.FromArgb(140, 150, 160);
        public static readonly Color Accent = Color.FromArgb(32, 178, 170);
        public static readonly Color Good = Color.FromArgb(32, 178, 170);
        public static readonly Color Warn = Color.FromArgb(22, 150, 143);
        public static readonly Color Idle = Color.FromArgb(149, 165, 166);
        public static readonly Color GoodDark = Color.FromArgb(22, 150, 143);
        public static readonly Color Bad = Color.FromArgb(231, 76, 60);

        /// <summary>
        /// 高 DPI 下窗口会被 AutoScale 放大（150% 时 748 高 → 1122），
        /// 小屏笔记本上会超出工作区，底部按钮点不到。
        /// 这里把窗口收进工作区并开滚动条 —— 内容可达，比「缩放回去」好：
        /// 缩回去等于又把文字裁了。
        /// </summary>
        public static void FitToScreen(Form f)
        {
            float scale = 1f;
            try { scale = f.DeviceDpi / 96f; } catch (Exception) { }
            if (scale <= 0.1f) scale = 1f;

            var wa = Screen.FromControl(f).WorkingArea;
            int wPhys = (int)(f.Width * scale);
            int hPhys = (int)(f.Height * scale);
            if (wPhys <= wa.Width && hPhys <= wa.Height) return;

            f.AutoScroll = true;
            f.Size = new Size((int)((wa.Width - 24) / scale),
                              (int)((wa.Height - 24) / scale));
            f.Location = new Point(wa.Left + 12, wa.Top + 8);
        }

        public static readonly Font FTitle = new Font("Microsoft YaHei", 13f, FontStyle.Bold);
        public static readonly Font FBig = new Font("Microsoft YaHei", 20f, FontStyle.Bold);
        public static readonly Font FBody = new Font("Microsoft YaHei", 9.5f);
        public static readonly Font FSmall = new Font("Microsoft YaHei", 8.5f);
        public static readonly Font FMono = new Font("Consolas", 8.5f);

        /// <summary>给控件画圆角边框。radius=0 表示不画。</summary>

        /// <summary>
        /// 把任意控件裁成圆角。Region 是硬边缘（无抗锯齿），但按钮这种小控件
        /// 在浅色背景上看不出来；换成自定义绘制反而要处理 hover/press 状态，不划算。
        /// </summary>
        public static void RoundControl(Control c, int radius)
        {
            Action clip = delegate
            {
                if (c.Width <= 0 || c.Height <= 0) return;
                var old = c.Region;
                using (var path = Round(new Rectangle(0, 0, c.Width, c.Height), radius))
                    c.Region = new Region(path);
                if (old != null) old.Dispose();
            };
            c.Resize += delegate { clip(); };
            clip();
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            if (radius <= 0) { p.AddRectangle(r); return p; }
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>扁平按钮：没有 3D 边框，hover/press 只换底色。</summary>
        public static Button FlatButton(string text, bool primary)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Line;
            b.BackColor = primary ? Accent : CardBg;
            b.ForeColor = primary ? Color.White : Text;
            b.Font = FBody;
            b.Height = 38;
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            RoundControl(b, 8);
            if (primary)
            {
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 192, 184);
                b.FlatAppearance.MouseDownBackColor = Color.FromArgb(22, 150, 143);
            }
            else
            {
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(240, 243, 247);
                b.FlatAppearance.MouseDownBackColor = Color.FromArgb(228, 232, 238);
            }
            return b;
        }

        /// <summary>
        /// 圆角白底卡片。
        ///
        /// 为什么必须设 Region（而不是只画边框）：
        ///   Panel 的 BackColor 是**矩形**填充，圆角外面那四个角仍然是白的。
        ///   只 DrawPath 的话，画面上就是「白卡 + 一圈细边 + 四个白角」，
        ///   父容器是浅灰时四个角会明显跳出来 —— 用户一眼就看到了（"每个圆角
        ///   窗口下都留了白边"）。
        ///   把 Region 也裁成同一个圆角路径，圆角外的像素根本不属于这个控件，
        ///   才会干净。
        ///
        /// Region 用 (0,0,w,h) 撑满、边框画在 (0,0,w-1,h-1) 的内侧，
        /// 这样 1px 的描边不会被裁掉一半变细。
        /// </summary>
        public static Panel Card(int radius)
        {
            var p = new Panel();
            p.BackColor = CardBg;

            Action clip = delegate
            {
                if (p.Width <= 0 || p.Height <= 0) return;
                var old = p.Region;
                using (var path = Round(new Rectangle(0, 0, p.Width, p.Height), radius))
                    p.Region = new Region(path);
                if (old != null) old.Dispose();
            };

            p.Paint += delegate (object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
                using (var path = Round(rect, radius))
                using (var pen = new Pen(Line))
                    e.Graphics.DrawPath(pen, path);
            };

            // 尺寸变了要重算 Region，否则拉伸后还是旧圆角
            p.Resize += delegate { clip(); };
            clip();
            return p;
        }

        public static Label Text2(string text, Font f, Color c)
        {
            var l = new Label();
            l.Text = text;
            l.Font = f;
            l.ForeColor = c;
            l.AutoSize = false;
            l.BackColor = Color.Transparent;
            return l;
        }
    }
}

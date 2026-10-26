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

        /// <summary>
        /// 建一个文字 Label，**高度由 WinForms 自己算**。
        ///
        /// 踩过的坑（反复改 DPI 模式都没解决，最后靠实测定位）：
        ///   Label 是 AutoSize=false + SetBounds 写死 18px 高，而字体是 pt
        ///   单位、随系统 DPI 放大 —— 125% 下 8.5pt 中文单行就要 20px，
        ///   于是每行字的下沿被裁 2px。各人缩放不同，裁掉的像素也不同，
        ///   看起来就是「文字下面一半被遮挡」。
        ///
        /// 为什么不用 TextRenderer.MeasureText 自己算：
        ///   试过，**中文长句算不准**。WordBreak 依赖空格断行，中文没有空格，
        ///   MeasureText 会认为一行放得下，返回单行高度；而 Label 实际渲染时
        ///   会按字符折行。结果就是「按测量值给高度」依然被裁。
        ///
        /// 所以交给 Label 自己：AutoSize=true + MaximumSize 限定宽度，
        /// 它会用和渲染完全相同的排版逻辑算高度，中英文都准。
        /// 代价是必须在设完 Text 之后再读 Height（见 Place）。
        /// </summary>
        public static Label Text2(string text, Font f, Color c)
        {
            var l = new Label();
            l.Text = text;
            l.Font = f;
            l.ForeColor = c;
            l.BackColor = Color.Transparent;
            l.AutoSize = true;
            // 默认给个宽松的上限，真正的位置/宽度由 Place() 定
            l.MaximumSize = new Size(480, 0);
            l.UseMnemonic = false;   // 文字里的 & 原样显示，不当快捷键
            return l;
        }

        /// <summary>
        /// 把 Label 放到 (x, y)，宽度 w，**高度自动**。
        ///
        /// 用法替换原来的 l.SetBounds(x, y, w, h)：高度不再由调用方猜。
        /// </summary>
        public static void Place(Label l, int x, int y, int w)
        {
            l.MaximumSize = new Size(w, 0);
            l.MinimumSize = new Size(w, 0);
            l.Location = new Point(x, y);
            l.Width = w;
            // AutoSize 的 Label 在改 Text 后高度会变，这里强制重算一次
            l.PerformLayout();
        }

        /// <summary>文字变了之后重新量高度（状态行这种会变的）。</summary>
        public static void Refit(Label l)
        {
            if (l == null || !l.AutoSize) return;
            l.PerformLayout();
        }

        /// <summary>
        /// 量一段文字需要多高 —— 只在需要「预留空间」时用（比如给下面的控件定位）。
        /// 内部靠一个临时 Label，保证和真实渲染一致。
        /// </summary>
        public static int MeasureHeight(string text, Font f, int width)
        {
            using (var probe = new Label())
            {
                probe.AutoSize = true;
                probe.MaximumSize = new Size(Math.Max(16, width), 0);
                probe.MinimumSize = new Size(Math.Max(16, width), 0);
                probe.Font = f;
                probe.Text = string.IsNullOrEmpty(text) ? "国" : text;
                return probe.PreferredHeight;
            }
        }
    }
}

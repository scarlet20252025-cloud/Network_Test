using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;

namespace Ferry
{
    /// <summary>
    /// 界面零件工厂。整套界面只用这里产出的样式，避免每个页面各写一套
    /// Padding / 圆角 / 字号 —— 那种界面乍看能用，细节全不齐。
    ///
    /// 所有圆角按钮都走同一个 ControlTemplate：
    ///   Border（圆角 + 绑定背景/边框）→ Border（绑定内边距）→ ContentPresenter
    /// 再挂两个触发器：悬停降到 88% 不透明度、按下 76%。
    /// 不这么做的话，WPF 默认模板是方角灰渐变，和这套配色完全不搭。
    /// </summary>
    internal static class Ui
    {
        public static Border Card(double padH = 20, double padV = 18)
        {
            return new Border
            {
                Background = Palette.CardGradB,
                BorderBrush = Palette.LineB,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(padH, padV, padH, padV)
            };
        }

        public static TextBlock H1(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = Palette.FsH1,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.TextB,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        public static TextBlock Sub(string text, double top = 3)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, top, 0, 0)
            };
        }

        public static TextBlock Section(string text, double top = 0)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.TextB,
                Margin = new Thickness(0, top, 0, 9)
            };
        }

        public static FrameworkElement Section(string text, string hint, double top = 0)
        {
            var wrap = new StackPanel { Margin = new Thickness(0, top, 0, 9) };
            wrap.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.TextB
            });
            wrap.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = Palette.FsTiny,
                Foreground = Palette.MutedB,
                Margin = new Thickness(0, 2, 0, 0)
            });
            return wrap;
        }

        public static Border Divider(double top = 0, double bottom = 0)
        {
            return new Border
            {
                Height = 1,
                Background = Palette.LineB,
                Margin = new Thickness(0, top, 0, bottom)
            };
        }

        /// <summary>
        /// 全局去掉按钮的键盘焦点虚线框。
        ///
        /// Primary/Secondary 自己设了 FocusVisualStyle=null，但窗口的最小化/关闭、
        /// 反馈窗的胶囊按钮与发送/取消都是直接 new Button —— 逐个补必然漏，
        /// 而漏掉的那颗按钮一点就冒出一个黑虚线框。
        /// 放进 Application 资源里做一条 Button 的隐式样式，一次覆盖全程序。
        /// （只设 FocusVisualStyle 一个属性，其余外观仍由各按钮自己决定。）
        /// </summary>
        public static void InstallNoFocusRing()
        {
            var app = Application.Current;
            if (app == null) return;
            if (app.Resources.Contains(typeof(Button))) return;
            var st = new Style(typeof(Button));
            st.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            app.Resources.Add(typeof(Button), st);
        }

        public static Button Primary(string text, double padH = 18, double padV = 9)
        {
            var b = new Button
            {
                Content = text,
                FontSize = Palette.FsBody,
                Padding = new Thickness(padH, padV, padH, padV),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(0),
                Background = Palette.AccentB,
                Foreground = Palette.OnAccentB,      // 浅绿底上用深字，白字对比不够
                FontWeight = FontWeights.SemiBold,
                // WPF 默认会在获得键盘焦点时画一圈黑色虚线矩形，深色主题下特别突兀
                // （用户截图：点「检查更新」后整颗按钮套了个虚线框）。
                // 这里直接置空 —— 焦点提示靠悬停/按下的亮度变化，不画框。
                FocusVisualStyle = null
            };
            Round(b, 9);
            return b;
        }

        public static Button Secondary(string text, double padH = 16, double padV = 8)
        {
            var b = new Button
            {
                Content = text,
                FontSize = Palette.FsBody,
                Padding = new Thickness(padH, padV, padH, padV),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                BorderBrush = Palette.Line2B,
                // 深色主题下次要按钮用「描边 + 透明」，不铺一块比卡片还亮的底 ——
                // 铺底会让禁用态看起来像一块灰板，也和卡片抢层次。
                Background = Brushes.Transparent,
                Foreground = Palette.TextB,
                FocusVisualStyle = null
            };
            Round(b, 9);
            return b;
        }

        /// <summary>看起来像链接。默认 Button 模板会画出灰框，必须换掉。</summary>
        public static Button Link(string text, Brush color)
        {
            var b = new Button
            {
                Content = text,
                FontSize = Palette.FsSmall,
                Foreground = color,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand
            };
            Flat(b);
            return b;
        }

        /// <summary>带描边的方形图标按钮（标题行右侧的刷新/清空用）。</summary>
        public static Button IconButton(string iconData, string tip, double size = 34)
        {
            var b = new Button
            {
                Width = size,
                Height = size,
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                BorderBrush = Palette.Line2B,
                Background = Palette.CardB,
                ToolTip = tip,
                Content = Icons.Make(iconData, Palette.MutedB, 15)
            };
            Round(b, 9);
            return b;
        }

        public static CheckBox Check(Panel parent, string title, string hint)
        {
            var wrap = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            parent.Children.Add(wrap);

            var c = new CheckBox
            {
                Content = title,
                FontSize = Palette.FsBody,
                Foreground = Palette.TextB,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand
            };
            CheckTemplate(c);
            wrap.Children.Add(c);

            wrap.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = Palette.FsTiny,
                Foreground = Palette.MutedB,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(26, 4, 0, 0)
            });
            return c;
        }

        // ── 模板 ──

        /// <summary>方角默认按钮 → 圆角。悬停/按下用不透明度做反馈，不换色。</summary>
        public static void Round(Button b, double radius)
        {
            var shell = new FrameworkElementFactory(typeof(Border));
            shell.Name = "bd";
            shell.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            shell.SetBinding(Border.BackgroundProperty, Bind("Background"));
            shell.SetBinding(Border.BorderBrushProperty, Bind("BorderBrush"));
            shell.SetBinding(Border.BorderThicknessProperty, Bind("BorderThickness"));
            shell.SetValue(Border.SnapsToDevicePixelsProperty, true);

            var pad = new FrameworkElementFactory(typeof(Border));
            pad.SetBinding(Border.PaddingProperty, Bind("Padding"));

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.Name = "cp";
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);

            pad.AppendChild(cp);
            shell.AppendChild(pad);

            var t = new ControlTemplate(typeof(Button)) { VisualTree = shell };
            t.Triggers.Add(Opacity(UIElement.IsMouseOverProperty, 0.88));
            t.Triggers.Add(Opacity(ButtonBase.IsPressedProperty, 0.76));
            // 禁用态只淡化文字，不整块降透明度 ——
            // 整块降透明度会把深色按钮变成一块灰板，比禁用本身更难看。
            var off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.42, "cp"));
            t.Triggers.Add(off);
            b.Template = t;
        }

        /// <summary>无边框按钮（链接、窗口按钮）。</summary>
        public static void Flat(Button b)
        {
            var shell = new FrameworkElementFactory(typeof(Border));
            shell.Name = "bd";
            shell.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            shell.SetBinding(Border.PaddingProperty, Bind("Padding"));

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);

            shell.AppendChild(cp);
            var t = new ControlTemplate(typeof(Button)) { VisualTree = shell };
            t.Triggers.Add(Opacity(UIElement.IsMouseOverProperty, 0.6));
            b.Template = t;
        }

        /// <summary>复选框：18px 圆角方框 + 勾，选中时填主色。</summary>
        static void CheckTemplate(CheckBox c)
        {
            var box = new FrameworkElementFactory(typeof(Border));
            box.Name = "box";
            box.SetValue(Border.WidthProperty, 18.0);
            box.SetValue(Border.HeightProperty, 18.0);
            box.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            box.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
            box.SetValue(Border.BorderBrushProperty, Palette.Line2B);
            box.SetValue(Border.BackgroundProperty, Palette.Card2B);
            box.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);

            var tick = new FrameworkElementFactory(typeof(Path));
            tick.Name = "tick";
            tick.SetValue(Path.DataProperty, Geometry.Parse("M4 9.2 L7.4 12.6 L14 5.2"));
            tick.SetValue(Path.StrokeProperty, Brushes.White);
            tick.SetValue(Path.StrokeThicknessProperty, 2.0);
            tick.SetValue(Path.StrokeStartLineCapProperty, PenLineCap.Round);
            tick.SetValue(Path.StrokeEndLineCapProperty, PenLineCap.Round);
            tick.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            box.AppendChild(tick);

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.MarginProperty, new Thickness(9, 0, 0, 0));
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);

            var row = new FrameworkElementFactory(typeof(StackPanel));
            row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            row.AppendChild(box);
            row.AppendChild(content);

            var t = new ControlTemplate(typeof(CheckBox)) { VisualTree = row };

            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(new Setter(Border.BackgroundProperty, Palette.AccentB, "box"));
            on.Setters.Add(new Setter(Border.BorderBrushProperty, Palette.AccentB, "box"));
            on.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "tick"));
            t.Triggers.Add(on);

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, Palette.AccentB, "box"));
            t.Triggers.Add(hover);

            c.Template = t;
        }

        static System.Windows.Data.Binding Bind(string path)
        {
            return new System.Windows.Data.Binding(path)
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent
            };
        }

        /// <summary>Trigger 的 Property 只接受 DependencyProperty，这里做一层转换。</summary>
        static Trigger Opacity(DependencyProperty prop, double value, bool when = true)
        {
            var tr = new Trigger { Property = prop, Value = when };
            tr.Setters.Add(new Setter(UIElement.OpacityProperty, value, "bd"));
            return tr;
        }
    }
}

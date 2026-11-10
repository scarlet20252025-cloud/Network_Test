using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Ferry
{
    /// <summary>
    /// 内嵌资源里的一张图。用来放侧栏头像 —— 头像必须跟着 exe 走，
    /// 不能像 app.ico 那样依赖安装目录里恰好有个文件（丢了会静默降级）。
    /// </summary>
    internal static class AppAssets
    {
        static ImageSource _avatar;
        static bool _tried;

        public static ImageSource Avatar
        {
            get
            {
                if (_tried) return _avatar;
                _tried = true;
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream("avatar.png"))
                    {
                        if (s == null) return null;
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;   // 断开流依赖
                        bmp.StreamSource = s;
                        bmp.EndInit();
                        bmp.Freeze();
                        _avatar = bmp;
                    }
                }
                catch (Exception) { _avatar = null; }
                return _avatar;
            }
        }
    }

    /// <summary>
    /// 左侧导航栏：头像 + 五个页面入口 + 底部账号。
    ///
    /// 选中态的视觉是「浅色底 + 左侧 3px 高亮条 + 亮字」，不是整块变色 ——
    /// 深底上整块高亮会盖住图标细节，参考的启动器也是这么处理的。
    /// </summary>
    internal class NavRail : Border
    {
        public readonly TextBlock AccountLabel;
        readonly TextBlock _versionLabel;
        readonly NavItem[] _items;
        int _active = -1;

        public event Action<int> PageChanged;

        public NavRail(string[] titles, string[] icons)
        {
            Width = Palette.SideW;
            Background = Palette.SideGradB;

            var root = new Grid();
            Child = root;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 头像
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 导航
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 底部账号

            // ── 头像 ──
            var head = new StackPanel { Margin = new Thickness(0, 26, 0, 22) };
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            // 头像直接用**透明原图**，不套圆框/圆环。
            //
            // 之前是 Ellipse + ImageBrush + 椭圆 Clip：320px 缩到 98px 时，
            // ImageBrush 的填充走低质量插值（BitmapScalingMode 设在宿主 Ellipse 上
            // 对 brush 填充并不可靠），再叠椭圆遮罩的抗锯齿，看起来就是一片马赛克。
            // 换成 Image 控件后缩放走 DIP 管线，HighQuality 才真正生效；
            // 同时去掉圆环和裁切 —— 图本身是透明底 + 深色描边，直接放就够干净。
            var avatarHost = new Grid
            {
                Width = 132,
                Height = 132,
                HorizontalAlignment = HorizontalAlignment.Center,
                UseLayoutRounding = true
            };

            var img = AppAssets.Avatar;
            if (img != null)
            {
                var photo = new Image
                {
                    Source = img,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(photo, BitmapScalingMode.HighQuality);
                avatarHost.Children.Add(photo);
            }
            else
            {
                // 资源丢了也别留一块空白：画个绿圆兜住
                avatarHost.Children.Add(new Ellipse
                {
                    Width = 96,
                    Height = 96,
                    Fill = Palette.AccentB,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            head.Children.Add(avatarHost);

            head.Children.Add(new TextBlock
            {
                Text = "渡口 Ferry",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.SideTextB,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0)
            });
            head.Children.Add(new TextBlock
            {
                Text = "校园网应急接入",
                FontSize = Palette.FsTiny,
                Foreground = Palette.SideMutedB,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0)
            });

            // ── 导航 ──
            var nav = new StackPanel { Margin = new Thickness(10, 0, 10, 0) };
            Grid.SetRow(nav, 1);
            root.Children.Add(nav);

            _items = new NavItem[titles.Length];
            for (int i = 0; i < titles.Length; i++)
            {
                int idx = i;
                var item = new NavItem(icons[i], titles[i]);
                // 用按下而不是抬起：按下时就要把事件标记为已处理，
                // 否则它会冒泡到根容器、被当成「拖动窗口」，点导航就会拖着窗口跑。
                // 选中和「吃掉事件」必须写在**同一个**处理器里，且先选中再置 Handled。
                // 之前拆成两个处理器（NavItem 里先置 Handled，这里再选中），
                // 结果前者把事件标记掉，后者根本不执行 —— 表现就是左侧导航点不动。
                item.MouseLeftButtonDown += delegate (object s2, MouseButtonEventArgs e)
                {
                    Select(idx);
                    e.Handled = true;
                };
                nav.Children.Add(item);
                _items[i] = item;
            }

            // ── 底部：账号 + 版本 ──
            var foot = new StackPanel { Margin = new Thickness(18, 10, 18, 16) };
            Grid.SetRow(foot, 3);
            root.Children.Add(foot);

            AccountLabel = new TextBlock
            {
                Text = "",
                FontSize = Palette.FsTiny,
                Foreground = Palette.SideMutedB,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            foot.Children.Add(AccountLabel);

            _versionLabel = new TextBlock
            {
                Text = "v" + typeof(NavRail).Assembly.GetName().Version.ToString(3),
                FontSize = Palette.FsTiny,
                Foreground = Palette.SideDimB,
                Margin = new Thickness(0, 4, 0, 0)
            };
            foot.Children.Add(_versionLabel);
        }

        public int ActivePage { get { return _active; } }

        public void Select(int index)
        {
            if (index < 0 || index >= _items.Length) return;
            if (_active == index) return;
            _active = index;
            for (int i = 0; i < _items.Length; i++) _items[i].SetActive(i == index);
            if (PageChanged != null) PageChanged(index);
        }

        class NavItem : Border
        {
            readonly Path _icon;
            readonly TextBlock _label;
            readonly Border _bar;
            bool _hover;

            public NavItem(string iconData, string title)
            {
                Height = 42;
                CornerRadius = new CornerRadius(10);
                Margin = new Thickness(0, 2, 0, 2);
                Background = Brushes.Transparent;
                Cursor = Cursors.Hand;

                var g = new Grid();
                Child = g;
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                _bar = new Border
                {
                    Width = 3,
                    Height = 18,
                    CornerRadius = new CornerRadius(2),
                    Background = Palette.AccentB,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = Visibility.Hidden
                };
                Grid.SetColumn(_bar, 0);
                g.Children.Add(_bar);

                _icon = Icons.Make(iconData, Palette.SideMutedB, 18);
                _icon.Margin = new Thickness(12, 0, 10, 0);
                _icon.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(_icon, 1);
                g.Children.Add(_icon);

                _label = new TextBlock
                {
                    Text = title,
                    FontSize = 13.5,
                    Foreground = Palette.SideMutedB,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(_label, 2);
                g.Children.Add(_label);

                MouseEnter += delegate { _hover = true; Apply(); };
                MouseLeave += delegate { _hover = false; Apply(); };
            }

            public void SetActive(bool on)
            {
                _active = on;
                Apply();
            }

            bool _active;

            void Apply()
            {
                Background = _active ? Palette.SideActiveB
                           : _hover ? Palette.SideBg2B
                           : Brushes.Transparent;
                var fg = _active || _hover ? Palette.SideTextB : Palette.SideMutedB;
                _label.Foreground = fg;
                _icon.Stroke = fg;
                _bar.Visibility = _active ? Visibility.Visible : Visibility.Hidden;
            }
        }
    }
}

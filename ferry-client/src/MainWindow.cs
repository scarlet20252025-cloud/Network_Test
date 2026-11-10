using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Shell;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace Ferry
{
    /// <summary>
    /// 主窗口（WPF 版）。
    ///
    /// 为什么整个界面重做：
    ///   WinForms 的布局是「坐标 + 固定像素」——每个控件的位置和高度都要手算，
    ///   而字体随 DPI 缩放。这两件事天然冲突，我们为此修了六七轮：
    ///   裁字 → 重叠 → 按钮文字消失 → MinimumSize 压制 AutoSize，
    ///   每次都是「算准了」，换个 DPI 又崩。
    ///
    ///   WPF 的布局是「测量 → 排列」两阶段：控件高度由内容决定，
    ///   TextWrapping 自动换行，Grid/StackPanel 自动分配空间。
    ///   同一个界面在 100% / 125% / 150% 下**不需要任何特殊处理**。
    ///
    /// 业务逻辑（借网、代理、TUN、授权、更新）原样复用，
    /// 只有「怎么画」这部分重写。
    /// </summary>
    internal class MainWindow : Window
    {
        // ── 业务对象（与 WinForms 版完全一致）──
        readonly Cfg _cfg;
        readonly string _cfgPath;
        readonly string _appDir;
        readonly string _logPath;
        FerryRuntime _rt;
        LicenseState _lic = new LicenseState();

        // ── 界面元素 ──
        TextBlock _lblAcct, _lblBig, _lblDesc, _lblQuota;
        TextBlock _lblVer;
        TextBlock _lblUpdState;
        Button _btnCheckUpdate;
        TextBlock _rowBridge, _rowProxy, _rowTun;
        Border _card;
        TextBlock _lblActTitle, _lblActHint;
        TextBox _txtCode;
        Button _btnActivate, _btnOn, _btnOff, _btnSettings, _btnUpd, _btnGlobal;
        TextBlock _rowHotspot;
        ToggleSwitch _swHotspot;
        TextBox _log;
        Border _actCard;

        // ── 1.2.0 新界面：侧栏 + 分页 ──
        NavRail _nav;
        Grid _pageHost;
        TextBlock _lblPageTitle, _lblPageHint;
        StackPanel _actionHost;
        FrameworkElement _pageTunnel, _pageLog, _pageSpeed, _pageSettings, _pageAbout;
        Button _btnLogClear, _btnSpeedRun, _btnSaveSettings;
        SpeedTestView _speed;
        TextBlock _speedSummary;
        SettingsView _settingsView;
        bool _noAnim;

        // ── 定时器 ──
        DispatcherTimer _fast, _slow, _hbTimer, _licTimer;
        WF.NotifyIcon _tray;

        // ── 状态 ──
        bool _busy, _hasNet = true, _probingNet;
        bool _hsBusy;
        string _exitVia = "";
        string _updPrompted = "";
        string _latestVer = "";
        int _guardTick;
        string _guardNote = "";

        // 有线上行探测的缓存（见 RefreshUi 里那段注释）。
        //   _wiredCache        —— UI 线程只读这个，O(1)
        //   _wiredProbing      —— 防止后台探测重叠（慢机器上探测可能超过刷新间隔）
        //   _wiredNextProbeAt  —— 下次允许探测的时间点
        //
        // 为什么用 DateTime.UtcNow 而不是 Environment.TickCount：
        // 后者 49 天回绕，虽然这里影响不大，但统一用 UTC 省得以后有人算错。
        volatile bool _wiredCache;
        int _wiredProbing;
        DateTime _wiredNextProbeAt = DateTime.MinValue;
        const int WiredProbeIntervalSec = 15;

        /// <summary>
        /// 后台刷新「有没有有线上行」。**只更新缓存，不碰任何控件。**
        ///
        /// 为什么不需要 Dispatcher.BeginInvoke 回投：这里改的只是一个
        /// volatile bool，不是控件。RefreshUi（UI 线程）下一拍自然会读到新值
        /// 并据此设置控件 —— 控件始终只在 UI 线程被碰。少一次跨线程回投，
        /// 也就少一个「回投时窗口已经关了」的空引用风险。
        /// </summary>
        void RefreshWiredCacheAsync()
        {
            if (DateTime.UtcNow < _wiredNextProbeAt) return;
            if (System.Threading.Interlocked.CompareExchange(ref _wiredProbing, 1, 0) != 0) return;
            _wiredNextProbeAt = DateTime.UtcNow.AddSeconds(WiredProbeIntervalSec);
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                bool v = false;
                try { v = Hotspot.HasWiredUplink(); }
                catch (Exception) { }
                finally { System.Threading.Interlocked.Exchange(ref _wiredProbing, 0); }
                // 缓存是 volatile bool，直接写就是安全的；下次 RefreshUi 会读到。
                _wiredCache = v;
            });
        }

        public bool StartBorrowOnLoad = false;
        public bool AutoUpdateOnLoad = false;

        public MainWindow(string cfgPath, string appDir)
        {
            _cfgPath = cfgPath;
            _appDir = appDir;
            _logPath = Path.Combine(appDir, "assistant.log");
            _cfg = Cfg.Load(cfgPath);

            // 必须在建界面之前：这条隐式样式只管 FocusVisualStyle，
            // 建完再装的话已经创建好的按钮不会重新应用。
            Ui.InstallNoFocusRing();

            BuildUi();
            BuildTray();

            Loaded += delegate
            {
                if (!_noAnim) StartWindowFadeIn();
                Kickoff();
                if (_cfg.StartMinimized) { Hide(); }
                if (StartBorrowOnLoad) { Dispatcher.BeginInvoke(new Action(delegate { DoStart(); })); }
                if (AutoUpdateOnLoad) { SetupAutoUpdateProbe(); }
            };
        }

        // ══════════════════════ 界面构建 ══════════════════════
        //
        // 全部用 StackPanel / Grid —— 高度由内容决定，不写死任何一个数字。
        // 这是与 WinForms 版最本质的区别。

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y,
                                        int cx, int cy, uint flags);

        const int WM_SETICON = 0x0080;
        const int ICON_SMALL = 0;
        const int ICON_BIG = 1;
        const int ICON_SMALL2 = 2;
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002;
        const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

        // HICON 必须由字段持有。局部 Icon 一旦被回收，句柄就失效，
        // 任务栏会退回系统默认图标 —— 这类「换图标不生效」有一半是这个原因。
        System.Drawing.Icon _winBigIcon, _winSmallIcon;

        /// <summary>把内嵌 app.ico 以原生 WM_SETICON 方式贴到窗口上（大/小/小2 三个槽位）。</summary>
        void ApplyNativeWindowIcon()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var st = asm.GetManifestResourceStream("app.ico"))
                {
                    if (st == null) return;
                    var bytes = new byte[st.Length];
                    int off = 0;
                    while (off < bytes.Length)
                    {
                        int n = st.Read(bytes, off, bytes.Length - off);
                        if (n <= 0) break;
                        off += n;
                    }

                    var hwnd = new WindowInteropHelper(this).Handle;
                    if (hwnd == IntPtr.Zero) return;

                    _winBigIcon = new System.Drawing.Icon(new MemoryStream(bytes),
                                                          new System.Drawing.Size(32, 32));
                    _winSmallIcon = new System.Drawing.Icon(new MemoryStream(bytes),
                                                            new System.Drawing.Size(16, 16));

                    SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, _winBigIcon.Handle);
                    SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, _winSmallIcon.Handle);
                    SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL2, _winSmallIcon.Handle);

                    // 不改几何，只让窗口框架重画一次，标题栏图标立刻跟着换。
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                }
            }
            catch (Exception) { }
        }

        void BuildUi()
        {
            ScrollBars.EnsureInstalled();

            Title = "渡口 Ferry";

            // WPF 层的图标：标题栏、Alt-Tab 预览、任务栏跳转列表都用它。
            try
            {
                using (var st = System.Reflection.Assembly.GetExecutingAssembly()
                           .GetManifestResourceStream("app.ico"))
                {
                    if (st != null)
                    {
                        // 取 32×32 那一帧，别直接用「最大帧」——
                        // Icon(stream) 会拿 256×256，缩放给 16px 用时又糊又慢。
                        using (var ico = new System.Drawing.Icon(st, new System.Drawing.Size(32, 32)))
                        {
                            Icon = Imaging.CreateBitmapSourceFromHIcon(
                                ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        }
                    }
                }
            }
            catch (Exception) { }

            // 窗口句柄一创建就再发一次原生 WM_SETICON。
            // 只设 WPF 的 Window.Icon 不够：任务栏按钮取的是 ICON_BIG，
            // 而系统在 AppID 未变时会沿用旧的图标缓存（实测更新 exe 后仍是旧图标）。
            SourceInitialized += delegate { ApplyNativeWindowIcon(); };

            Width = Palette.WinW;
            Height = Palette.WinH;
            MinWidth = Palette.MinW;
            MinHeight = Palette.MinH;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Palette.BgB;
            FontFamily = new FontFamily(Palette.FontUI);
            FontSize = Palette.FsBody;
            Foreground = Palette.TextB;

            // 自绘标题栏（参考的启动器就是无系统边框的）。用 WindowChrome 而
            // 不是 AllowsTransparency：后者会关掉硬件加速，而这个界面每秒都在
            // 刷新状态，掉帧很明显。CaptionHeight=0 表示拖动由我们自己处理，
            // ResizeBorderThickness 保留系统级的边缘缩放和贴边。
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });

            var shell = new Grid();
            // 根容器必须有背景：Window 的 Background 只画窗口表面，
            // 离屏渲染（RenderTargetBitmap 渲染的是 Content）时透明区会变成黑块
            // —— 深色主题下整条标题带发黑就是这么来的。
            shell.Background = Palette.ShellBgB;
            // 必须显式给一行 Star：不写行定义时 Grid 用 Auto 行，
            // 没有内容的 Border（下面两团光斑）会被量成 0 高，整层等于不存在。
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Palette.SideW) });
            shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Content = shell;

            // 右上角一团极淡的环境光。放在最底层、不参与命中测试，
            // 侧栏是半透明的，所以这团光也会轻微透到导航栏上，整块看起来是一张纸。
            var glow = new Border
            {
                Background = Palette.GlowB,
                IsHitTestVisible = false
            };
            Grid.SetColumnSpan(glow, 2);
            shell.Children.Add(glow);

            var glowCool = new Border
            {
                Background = Palette.GlowCoolB,
                IsHitTestVisible = false
            };
            Grid.SetColumnSpan(glowCool, 2);
            shell.Children.Add(glowCool);

            // ── 左侧导航 ──
            _nav = new NavRail(
                new[] { "隧道", "日志", "测速", "设置", "关于" },
                new[] { Icons.Tunnel, Icons.Log, Icons.Speed, Icons.Settings, Icons.About });
            _nav.PageChanged += OnPageChanged;
            _lblAcct = _nav.AccountLabel;      // 账号显示在侧栏底部
            Grid.SetColumn(_nav, 0);
            shell.Children.Add(_nav);

            // ── 右侧：标题行 + 页面 ──
            var main = new Grid();
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(main, 1);
            shell.Children.Add(main);

            var header = new Grid { Margin = new Thickness(Palette.ContentPad, 16, 18, 10) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // 拖动挂在**整窗**上，不再只挂标题行：
            // 之前只有标题那一块能拖，标题上方的 16px 和左侧整条侧栏都拖不动，
            // 用户实际是想「按住窗口任何空白处就能挪」。交互控件（按钮、输入框、
            // 导航项）会自己把按下事件吃掉，所以不会误拖。
            shell.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                if (e.ButtonState == MouseButtonState.Pressed) DragMove();
            };
            Grid.SetRow(header, 0);
            main.Children.Add(header);

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _lblPageTitle = Ui.H1("隧道");
            titleStack.Children.Add(_lblPageTitle);
            _lblPageHint = Ui.Sub("连接状态与出口");
            titleStack.Children.Add(_lblPageHint);
            Grid.SetColumn(titleStack, 0);
            header.Children.Add(titleStack);

            _actionHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(_actionHost, 1);
            header.Children.Add(_actionHost);

            // 更新按钮常驻在标题行：任何时候发现新版本都看得见，
            // 不用先切到「关于」页（用户以前抱怨过「更新按钮藏起来了」）。
            _btnUpd = Ui.Primary("更新", 16, 7);
            _btnUpd.FontSize = Palette.FsSmall;
            _btnUpd.Visibility = Visibility.Collapsed;
            _btnUpd.Margin = new Thickness(0, 0, 10, 0);
            _btnUpd.Click += delegate
            {
                if (_latestVer.Length == 0) { Log("[更新] 还没拿到版本号，忽略点击"); return; }
                DoUpdate(_latestVer);
            };
            _actionHost.Children.Add(_btnUpd);

            var winBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(winBtns, 2);
            header.Children.Add(winBtns);

            var btnMin = WinButton(Icons.Minus);
            btnMin.Click += delegate { WindowState = WindowState.Minimized; };
            winBtns.Children.Add(btnMin);

            var btnClose = WinButton(Icons.Close, true);
            btnClose.Margin = new Thickness(6, 0, 0, 0);
            btnClose.Click += delegate { Close(); };   // 关闭 = 收托盘，见 OnWindowClosing
            winBtns.Children.Add(btnClose);

            // ── 页面容器 ──
            _pageHost = new Grid
            {
                Margin = new Thickness(Palette.ContentPad, 0, Palette.ContentPad, 20)
            };
            Grid.SetRow(_pageHost, 1);
            main.Children.Add(_pageHost);

            BuildPageButtons();
            BuildTunnelPage();
            BuildLogPage();
            BuildSpeedPage();
            BuildSettingsPage();
            BuildAboutPage();

            _pageHost.Children.Add(_pageTunnel);
            _pageHost.Children.Add(_pageLog);
            _pageHost.Children.Add(_pageSpeed);
            _pageHost.Children.Add(_pageSettings);
            _pageHost.Children.Add(_pageAbout);

            Closing += OnWindowClosing;
            _nav.Select(0);
        }

        /// <summary>开窗淡入：160ms 从 0.2 到 1，避免第一帧硬闪一下。</summary>
        void StartWindowFadeIn()
        {
            Opacity = 0;
            BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        }

        /// <summary>离屏渲染工具（tools/ui-shot）切页用，运行时不调用。</summary>
        public void SelectPageForShot(int index)
        {
            _noAnim = true;                     // 别截到动画中间态
            if (_nav != null) _nav.Select(index);
        }

        /// <summary>
        /// 自检：在**直连状态**下真的走一遍「一键测试」的点击处理器。
        ///
        /// 这条路径 1.2.4 及以前会崩：处理器里写的是 `!_rt.Running`，而直连时
        /// _rt 是 null → NullReferenceException → WPF 不接 → 进程整套退出。
        /// 借网状态下 _rt 不为 null，所以自测时永远碰不到，只能靠这个入口复现。
        ///
        /// 返回界面日志文本，调用方可据此确认处理器真的跑到了 RunAll 之前那步。
        /// </summary>
        public string SelfTestSpeedClick()
        {
            if (_btnSpeedRun == null) return "(没有一键测试按钮)";
            _btnSpeedRun.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent, _btnSpeedRun));
            return _log == null ? "" : _log.Text;
        }

        /// <summary>
        /// 自检：真的点一次「检查更新」，把状态文字回给调用方。
        ///
        /// 1.2.5 及以前这条路径没有可见反馈 —— 没新版本或连不上门户时直接 return，
        /// 界面上唯一的变化是按钮多出一圈焦点虚线框，看起来就是「点了没用」。
        /// 这里要断言点完之后状态文字**非空**：空字符串就是那个 bug 复发了。
        /// </summary>
        public string SelfTestUpdateClick()
        {
            if (_btnCheckUpdate == null) return "(没有检查更新按钮)";
            _btnCheckUpdate.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent, _btnCheckUpdate));
            return _lblUpdState == null ? "" : (_lblUpdState.Text ?? "");
        }

        /// <summary>离屏渲染时灌几行长日志，用来检查自动换行与滚动条外观。</summary>
        public void FillLogForShot()
        {
            for (int i = 1; i <= 12; i++)
                Log("2026-11-10 16:58:" + (10 + i) + "  [样例] 第 " + i +
                    " 行日志：这是一条比较长的机器输出，用来确认日志区会自动换行、不再出现横向滚动条，" +
                    "并且右侧滚动条是细的圆角样式。");
        }

        /// <summary>离屏渲染时跑一次状态回写，让截图里是真实文案而不是「检查中」。</summary>
        public void RefreshForShot()
        {
            try { RefreshUi(); } catch (Exception) { }
        }

        /// <summary>标题行右侧的窗口按钮（最小化 / 关闭）。</summary>
        ///
        /// 1.2.6 及以前这两个按钮悬停/按下**没有任何反馈**：模板里那层 Border 的
        /// Background 被写死成 Transparent，既没有 TemplateBinding 也没有触发器，
        /// 鼠标移上去就只是一根不会动的手型光标 —— 用户报「光标放右上角没有反馈」。
        ///
        /// 现在的做法：
        ///   · Border 的底色改成 TemplateBinding Button.Background；
        ///   · 悬停 / 按下交给 Style 触发器改 Background（普通按钮 12% / 21% 白叠层，
        ///     关闭按钮按 Windows 惯例整块变红 #C42B1C）；
        ///   · 图标 Stroke 绑到 Button.Foreground，所以底色一变，线条跟着提亮（关闭变白）。
        ///
        /// ⚠ Background / Foreground 必须写在 Style 的 Setter 里，不能写成本地值 ——
        ///   本地值优先级高于 Style 触发器，写了触发器就永远不生效（这次差点又踩）。
        static Button WinButton(string icon, bool isClose = false)
        {
            var path = Icons.Make(icon, Palette.MutedB, 13);
            path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
                new System.Windows.Data.Binding("Foreground")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1)
                });

            var b = new Button
            {
                Width = 30,
                Height = 26,
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(0),
                Content = path,
                ToolTip = isClose ? "关闭（收到托盘）" : "最小化"
            };

            var t = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty,
                new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            bd.SetValue(Border.SnapsToDevicePixelsProperty, true);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            t.VisualTree = bd;
            b.Template = t;

            var st = new Style(typeof(Button));
            st.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            st.Setters.Add(new Setter(Control.ForegroundProperty, Palette.MutedB));

            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Control.BackgroundProperty,
                isClose ? Palette.CloseHoverB : Palette.WinHoverB));
            over.Setters.Add(new Setter(Control.ForegroundProperty,
                isClose ? Brushes.White : Palette.TextB));
            st.Triggers.Add(over);

            var down = new Trigger { Property = Button.IsPressedProperty, Value = true };
            down.Setters.Add(new Setter(Control.BackgroundProperty,
                isClose ? Palette.ClosePressB : Palette.WinPressB));
            down.Setters.Add(new Setter(Control.ForegroundProperty,
                isClose ? Brushes.White : Palette.TextB));
            st.Triggers.Add(down);

            b.Style = st;
            return b;
        }

        // ══════════════════════ 页面按钮（只建一次，切换时挂到标题行） ══════════════════════

        void BuildPageButtons()
        {
            _btnLogClear = Ui.Secondary("清空日志", 14, 7);
            _btnLogClear.FontSize = Palette.FsSmall;
            _btnLogClear.Click += delegate { try { _log.Clear(); } catch (Exception) { } Log("日志已清空"); };

            _btnSpeedRun = Ui.Primary("一键测试", 18, 7);
            _btnSpeedRun.FontSize = Palette.FsSmall;
            _btnSpeedRun.Click += delegate
            {
                // ⚠ 这里原来写的是 `!_rt.Running`，**没判空**。
                // 直连状态下 _rt 是 null（从未开始借网），点一下就是 NullReferenceException，
                // 而这个异常抛在按钮 Click 里 —— WPF 不接，进程整套退出。
                // 借网时 _rt 不为 null，所以只在这个路径崩，很难靠借网自测发现。
                var rt = _rt;
                if (rt == null || !rt.Running)
                    Log("当前是直连，测的是不经渡口的裸链路；开始借网后再测可对比");
                try { _speed.RunAll(); }
                catch (Exception ex) { Log("测速启动失败：" + ex.Message); }
            };

            _btnSaveSettings = Ui.Primary("保存设置", 18, 7);
            _btnSaveSettings.FontSize = Palette.FsSmall;
            _btnSaveSettings.Click += delegate
            {
                if (_settingsView != null && _settingsView.Apply()) Log("设置已保存");
            };

            // 旧界面遗留的「设置」按钮：保留字段（RefreshUi 会设它的 IsEnabled），
            // 但不再挂到任何容器上 —— 设置入口已经换成侧栏导航。
            _btnSettings = Ui.Secondary("设置", 14, 7);
            _btnSettings.Visibility = Visibility.Collapsed;
        }

        void OnPageChanged(int index)
        {
            var pages = new FrameworkElement[] { _pageTunnel, _pageLog, _pageSpeed, _pageSettings, _pageAbout };
            for (int i = 0; i < pages.Length; i++)
                pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;

            string[] titles = { "隧道", "日志", "测速", "设置", "关于" };
            string[] hints =
            {
                "连接状态、出口与共享方式",
                "运行日志与诊断信息",
                "固定站点的延迟与失败率",
                "沿用渡口原有的设置项",
                "版本、更新与说明"
            };
            _lblPageTitle.Text = titles[index];
            _lblPageHint.Text = hints[index];

            // 标题行只保留更新按钮，其余按页挂载
            while (_actionHost.Children.Count > 1) _actionHost.Children.RemoveAt(1);
            switch (index)
            {
                case 1: _actionHost.Children.Add(_btnLogClear); break;
                case 2: _actionHost.Children.Add(_btnSpeedRun); break;
                case 3: _actionHost.Children.Add(_btnSaveSettings); break;
            }

            if (index == 1) { try { _log.ScrollToEnd(); } catch (Exception) { } }

            AnimateIn(pages[index]);
        }

        /// <summary>
        /// 切页动效：新页面淡入 + 从右侧滑进 14px，200ms EaseOut。
        ///
        /// 只改不透明度和平移，**不改尺寸** —— 改尺寸会触发整页重新测量，
        /// 日志那种几千行的 TextBox 重新折行要几十毫秒，看起来就是卡一下。
        /// 离屏渲染（tools/ui-shot）会把 _noAnim 打开，否则截到动画中间态。
        /// </summary>
        void AnimateIn(FrameworkElement page)
        {
            if (page == null) return;

            if (_noAnim)
            {
                page.Opacity = 1;
                var reset = page.RenderTransform as TranslateTransform;
                if (reset != null) reset.X = 0;
                return;
            }

            var tt = page.RenderTransform as TranslateTransform;
            if (tt == null)
            {
                tt = new TranslateTransform();
                page.RenderTransform = tt;
            }
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(200);
            page.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(14, 0, dur) { EasingFunction = ease });
        }

        // ══════════════════════ 隧道 ══════════════════════

        void BuildTunnelPage()
        {
            var root = new StackPanel();
            _pageTunnel = root;

            // ── 状态卡 ──
            _card = MakeCard();
            root.Children.Add(_card);

            var cardStack = new StackPanel();
            _card.Child = cardStack;

            cardStack.Children.Add(new TextBlock
            {
                Text = "当前状态",
                FontSize = Palette.FsTiny,
                Foreground = Palette.MutedB,
                Margin = new Thickness(0, 0, 0, 4)
            });

            _lblBig = new TextBlock
            {
                Text = "检查中",
                FontSize = Palette.FsBig,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.MutedB,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            };
            cardStack.Children.Add(_lblBig);

            _lblDesc = new TextBlock
            {
                Text = "",
                FontSize = Palette.FsBody,
                Foreground = Palette.MutedB,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            cardStack.Children.Add(_lblDesc);

            _lblQuota = new TextBlock
            {
                Text = "",
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                Margin = new Thickness(0, 0, 0, 10)
            };
            cardStack.Children.Add(_lblQuota);

            cardStack.Children.Add(new Border
            {
                Height = 1,
                Background = Palette.LineB,
                Margin = new Thickness(0, 2, 0, 12)
            });

            _rowBridge = MakeRow(cardStack);
            _rowProxy = MakeRow(cardStack);

            // 「全局模式」行
            var tunRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            tunRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tunRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cardStack.Children.Add(tunRow);

            _rowTun = new TextBlock
            {
                Text = "全局模式    未开启",
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_rowTun, 0);
            tunRow.Children.Add(_rowTun);

            _btnGlobal = MakeButton("开启全局模式", true, 0);
            _btnGlobal.FontSize = Palette.FsSmall;
            _btnGlobal.Padding = new Thickness(14, 6, 14, 6);
            _btnGlobal.Margin = new Thickness(10, 0, 0, 0);
            _btnGlobal.Click += delegate { OnGlobalClicked(); };
            Grid.SetColumn(_btnGlobal, 1);
            tunRow.Children.Add(_btnGlobal);

            // 「移动热点」行
            var hsRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            hsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cardStack.Children.Add(hsRow);

            _rowHotspot = new TextBlock
            {
                Text = "移动热点    未开启",
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_rowHotspot, 0);
            hsRow.Children.Add(_rowHotspot);

            _swHotspot = new ToggleSwitch();
            _swHotspot.Margin = new Thickness(10, 0, 0, 0);
            _swHotspot.Toggled += delegate { OnHotspotToggled(); };
            Grid.SetColumn(_swHotspot, 1);
            hsRow.Children.Add(_swHotspot);

            // ── 主按钮 ──
            var btnRow = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(btnRow);

            _btnOn = MakeButton("开始借网", true, 0);
            _btnOn.FontSize = 14;
            _btnOn.Padding = new Thickness(0, 11, 0, 11);
            _btnOn.Click += delegate { DoStart(); };
            Grid.SetColumn(_btnOn, 0);
            btnRow.Children.Add(_btnOn);

            _btnOff = MakeButton("停止并恢复直连", false, 0);
            _btnOff.FontSize = 14;
            _btnOff.Padding = new Thickness(0, 11, 0, 11);
            _btnOff.Click += delegate { DoStop(); };
            Grid.SetColumn(_btnOff, 2);
            btnRow.Children.Add(_btnOff);

            // ── 激活卡 ──
            _actCard = MakeCard();
            _actCard.Margin = new Thickness(0, 14, 0, 0);
            root.Children.Add(_actCard);

            var actStack = new StackPanel();
            _actCard.Child = actStack;

            _lblActTitle = new TextBlock
            {
                Text = "输入密钥激活",
                FontSize = Palette.FsBody,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.TextB
            };
            actStack.Children.Add(_lblActTitle);

            var actRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            actRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actStack.Children.Add(actRow);

            _txtCode = new TextBox
            {
                FontFamily = new FontFamily(Palette.FontMono),
                FontSize = 15,
                Padding = new Thickness(10, 8, 10, 8),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                CaretBrush = Palette.TextB,
                CharacterCasing = CharacterCasing.Upper,
                MaxLength = 16,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _txtCode.KeyDown += delegate (object s, System.Windows.Input.KeyEventArgs e)
            {
                if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; DoInlineActivate(); }
            };
            var codeBox = new Border
            {
                Background = Palette.Card2B,
                BorderBrush = Palette.Line2B,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Child = _txtCode
            };
            Grid.SetColumn(codeBox, 0);
            actRow.Children.Add(codeBox);

            _btnActivate = MakeButton("激活", true, 0);
            _btnActivate.Padding = new Thickness(20, 7, 20, 7);
            _btnActivate.Margin = new Thickness(10, 0, 0, 0);
            _btnActivate.Click += delegate { DoInlineActivate(); };
            Grid.SetColumn(_btnActivate, 1);
            actRow.Children.Add(_btnActivate);

            _lblActHint = new TextBlock
            {
                Text = "管理员给你的 8 位字母数字，一个密钥只能用一次。",
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            actStack.Children.Add(_lblActHint);

            root.Children.Add(new TextBlock
            {
                Text = "全局模式会接管所有程序的流量（含游戏 / UDP）；" +
                       "移动热点需要有线连接，把当前出口共享给其它设备。",
                FontSize = Palette.FsTiny,
                Foreground = Palette.IdleB,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 14, 2, 0)
            });
        }

        // ══════════════════════ 日志 ══════════════════════

        void BuildLogPage()
        {
            var card = Ui.Card(10, 8);
            var host = new Grid();
            host.Children.Add(card);

            _log = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                // 1.2.3 按用户要求改成自动换行、去掉底部横向滚动条。
                //
                // 代价要知道：TextBox + Wrap 时每次 AppendText 都要按当前宽度
                // 重排整个文本，行数越多越慢（2026-11-02 曾因此拖动窗口卡顿）。
                // 所以下面把保留行数从 800 降到 400 —— 换行后的视觉行数大约翻倍，
                // 400 行折完约等于原来的 800 行，手感回到同一水平。
                TextWrapping = TextWrapping.Wrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily(Palette.FontMono),
                FontSize = Palette.FsMono,
                Foreground = Palette.TextB,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 4, 8, 4)
            };
            card.Child = _log;
            ScrollBars.ApplyTo(_log);          // 模板内部的滚动条才能吃到这个样式
            _pageLog = host;
        }

        // ══════════════════════ 测速 ══════════════════════

        void BuildSpeedPage()
        {
            var root = new StackPanel();
            _pageSpeed = root;

            _speedSummary = new TextBlock
            {
                Text = "点右上角「一键测试」开始。",
                FontSize = Palette.FsBody,
                Foreground = Palette.MutedB,
                Margin = new Thickness(2, 0, 0, 10)
            };
            root.Children.Add(_speedSummary);

            _speed = new SpeedTestView(
                delegate (string host, int port, int timeoutMs)
                {
                    int ms = 0;
                    string route = "";
                    string note = "";
                    bool ok = _rt != null && _rt.Probe(host, port, timeoutMs, out ms, out route, out note);
                    return new ProbeResult { Ok = ok, Ms = ms, Route = route, Note = note };
                },
                _speedSummary, _btnSpeedRun, Log);

            root.Children.Add(_speed.Build());
        }

        // ══════════════════════ 设置 ══════════════════════

        void BuildSettingsPage()
        {
            _settingsView = new SettingsView(_cfg, _cfgPath, _appDir, DoUninstall, DoUpdate);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _settingsView.Build()
            };
            ScrollBars.ApplyTo(scroll);
            _pageSettings = scroll;
        }


        /// <summary>关于页的「标签 — 值」一行。值用等宽字体，IP 和端口对齐好看。</summary>
        void AddInfoRow(Grid g, int row, string label, string value)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var l = new TextBlock
            {
                Text = label,
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                Margin = new Thickness(0, 4, 0, 4)
            };
            Grid.SetColumn(l, 0);
            Grid.SetRow(l, row);
            g.Children.Add(l);

            var v = new TextBlock
            {
                Text = value,
                FontFamily = new FontFamily(Palette.FontMono),
                FontSize = Palette.FsSmall,
                Foreground = Palette.TextB,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4, 0, 4)
            };
            Grid.SetColumn(v, 1);
            Grid.SetRow(v, row);
            g.Children.Add(v);
        }

        // ══════════════════════ 关于 ══════════════════════

        void BuildAboutPage()
        {
            var root = new StackPanel();
            _pageAbout = root;

            var card = Ui.Card(22, 20);
            root.Children.Add(card);
            var stack = new StackPanel();
            card.Child = stack;

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            stack.Children.Add(head);

            var avatar = new Ellipse
            {
                Width = 56,
                Height = 56,
                VerticalAlignment = VerticalAlignment.Center
            };
            var img = AppAssets.Avatar;
            if (img != null)
            {
                RenderOptions.SetBitmapScalingMode(avatar, BitmapScalingMode.HighQuality);
                avatar.Fill = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            }
            else avatar.Fill = Palette.AccentB;
            avatar.Clip = new EllipseGeometry(new Point(28, 28), 28, 28);
            head.Children.Add(avatar);

            var nameBox = new StackPanel
            {
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            head.Children.Add(nameBox);
            nameBox.Children.Add(new TextBlock
            {
                Text = "渡口 Ferry",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.TextB
            });
            _lblVer = new TextBlock
            {
                Text = "v" + typeof(MainWindow).Assembly.GetName().Version.ToString(3),
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                Margin = new Thickness(0, 3, 0, 0)
            };
            nameBox.Children.Add(_lblVer);

            stack.Children.Add(new Border { Height = 1, Background = Palette.LineB, Margin = new Thickness(0, 16, 0, 14) });

            stack.Children.Add(new TextBlock
            {
                Text = "校园网按账号禁网时，借一台能上网的内网主机当出口。" +
                       "国内目标走渡口出口，境外交给本机代理，两条上游互为兜底。",
                FontSize = Palette.FsBody,
                Foreground = Palette.TextB,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22
            });

            // 连接信息：把「这台机器现在连的是谁」摊开，排查时不用去翻配置
            stack.Children.Add(Ui.Divider(16, 4));
            var info = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            stack.Children.Add(info);

            AddInfoRow(info, 0, "门户地址", string.IsNullOrEmpty(_cfg.PortalUrl) ? "未配置" : _cfg.PortalUrl);
            AddInfoRow(info, 1, "本地代理", "127.0.0.1:" + _cfg.SocksPort);
            AddInfoRow(info, 2, "远端出口", string.IsNullOrEmpty(_cfg.RemoteHost)
                ? "未配置" : _cfg.RemoteHost + ":" + _cfg.RelayPort);
            AddInfoRow(info, 3, "账户", string.IsNullOrEmpty(_cfg.AccountIp) ? "未激活" : _cfg.AccountIp);

            var ops = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
            stack.Children.Add(ops);

            _btnCheckUpdate = Ui.Secondary("检查更新");
            _btnCheckUpdate.Click += delegate { CheckUpdate(true); };
            ops.Children.Add(_btnCheckUpdate);

            // 结果必须显示在按钮旁边。
            // 以前这里只往日志写一行「已请求检查更新」，而日志在另一个页面 ——
            // 用户点完看到的唯一变化就是那圈焦点虚线框，自然会认为「按钮没用」。
            _lblUpdState = new TextBlock
            {
                Text = "",
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };

            var fb = Ui.Secondary("意见反馈");
            fb.Margin = new Thickness(10, 0, 0, 0);
            fb.Click += delegate
            {
                var dlg = new FeedbackWindow(_cfg) { Owner = this };
                dlg.ShowDialog();
            };
            ops.Children.Add(fb);

            // 状态文字放在两个按钮**后面**：夹在中间时「检查更新 / 红字 / 意见反馈」
            // 会把一行拆成三段，红字看着像绑定在反馈按钮上。
            ops.Children.Add(_lblUpdState);

            root.Children.Add(new TextBlock
            {
                Text = "渡口仅用于个人学习与内网连通性测试。",
                FontSize = Palette.FsTiny,
                Foreground = Palette.IdleB,
                Margin = new Thickness(2, 16, 0, 0)
            });
        }

        /// <summary>
        /// 圆角卡片。Border 自带 CornerRadius —— 不需要手工画 GraphicsPath，
        /// 也不会像 WinForms 那样用 Region 把子控件裁掉。
        /// </summary>
        static Border MakeCard()
        {
            return new Border
            {
                Background = Palette.CardGradB,
                BorderBrush = Palette.LineB,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18, 16, 18, 16)
            };
        }

        static TextBlock MakeRow(Panel parent)
        {
            var t = new TextBlock
            {
                Text = "",
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                Margin = new Thickness(0, 0, 0, 5),
                TextWrapping = TextWrapping.Wrap
            };
            parent.Children.Add(t);
            return t;
        }

        /// <summary>
        /// 按钮。WPF 的默认模板里文字由 ContentPresenter 渲染，
        /// 永远不会因为「控件高度不够」被裁 —— 高度由内容撑开。
        /// </summary>
        static Button MakeButton(string text, bool primary, double width)
        {
            var b = new Button
            {
                Content = text,
                FontSize = Palette.FsBody,
                Padding = new Thickness(16, 8, 16, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(primary ? 0 : 1),
                BorderBrush = Palette.Line2B,
                Background = primary ? Palette.AccentB : Brushes.Transparent,
                Foreground = primary ? Palette.OnAccentB : Palette.TextB
            };
            // 1.2.0 起整套界面是圆角的，这里必须跟着走，
            // 否则主按钮还是 WPF 默认的方角灰渐变，一眼看出是两套东西。
            Ui.Round(b, 10);
            if (width > 0) b.Width = width;
            return b;
        }

        void BuildTray()
        {
            _tray = new WF.NotifyIcon();
            _tray.Text = "渡口 Ferry";
            // 托盘图标优先用内嵌资源：早先只读安装目录的 app.ico，
            // 那个文件一旦缺失（便携版、被清理工具删掉）就静默变成系统默认图标，
            // 用户只会觉得「图标没换」。内嵌的永远跟着 exe 走。
            try
            {
                System.IO.Stream s = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("app.ico");
                if (s != null)
                {
                    using (s) _tray.Icon = new System.Drawing.Icon(s);
                }
                else
                {
                    var ico = Path.Combine(_appDir, "app.ico");
                    _tray.Icon = File.Exists(ico)
                        ? new System.Drawing.Icon(ico)
                        : System.Drawing.SystemIcons.Application;
                }
            }
            catch (Exception) { _tray.Icon = System.Drawing.SystemIcons.Application; }

            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("显示主界面", null, delegate { ShowMain(); });
            menu.Items.Add("开始借网", null, delegate { DoStart(); });
            menu.Items.Add("停止并恢复直连", null, delegate { DoStop(); });
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { RealQuit(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ShowMain(); };
            _tray.Visible = true;
        }

        void ShowMain()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 关窗口 = 收进托盘，不是退出。退出走托盘菜单或 RealQuit()。
            e.Cancel = true;
            Hide();
        }

        void RealQuit()
        {
            if (_rt != null && _rt.Running)
            {
                var r = MessageBox.Show(this,
                    "当前正在借网。退出会恢复直连，确定吗？",
                    "渡口", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
                if (_weSetProxy)
                {
                    try { FerryCore.RestoreDirect(_cfg, _appDir); } catch (Exception) { }
                    _weSetProxy = false;
                }
                try { _rt.Stop(); _rt = null; } catch (Exception) { }
            }
            // 用户主动退出 = 结束使用，记成直连；下次打开不要自己又去借网。
            SaveLastState(false);
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            Application.Current.Shutdown();
        }

        // ══════════════════════ 业务逻辑 ══════════════════════
        // 以下与 WinForms 版一致，只是把 WinForms API 换成 WPF 等价物。

        void Kickoff()
        {
            Log("渡口已启动 · 账号 " + (_cfg.AccountIp.Length > 0 ? _cfg.AccountIp : "（未激活）"));

            // 先把上一轮异常退出的残骸清掉，再决定要不要借网。
            // 顺序很重要：如果先检查借网状态，会把「上次残留的 FerryTun」误认成
            // 「自己已经在借网」，界面显示已开启、实际网络是断的。
            CleanupLeftovers();

            if (_cfg.Activated) { Log("正在向门户确认授权…"); CheckLicense(true); }
            else { Log("尚未激活 —— 在「输入密钥激活」框里填入管理员给的密钥"); _txtCode.Focus(); }

            _fast = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _fast.Tick += delegate
            {
                // 诊断：量 RefreshUi / GuardSystemProxy 各占多少 UI 线程时间。
                //
                // 为什么加这个：2026-11-02 用户反复报「拖动窗口一顿一顿」。
                // 外部测量（往窗口发 WM_NULL）显示 p50=0.5ms 但 p90=66ms、
                // 20% 的采样都在顿挫 —— 典型的「周期性整块占用」。
                // 与其继续在代码里猜哪个属性赋值慢，不如让程序自己报数。
                //
                // 只在超过一帧预算（15ms）时才写日志，正常情况不产生噪声。
                var sw = System.Diagnostics.Stopwatch.StartNew();
                RefreshUi();
                sw.Stop();
                double uiMs = sw.Elapsed.TotalMilliseconds;

                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                GuardSystemProxy();
                sw2.Stop();
                double guardMs = sw2.Elapsed.TotalMilliseconds;

                if (uiMs > 15 || guardMs > 15)
                {
                    Log(string.Format("[耗时] RefreshUi={0:F1}ms Guard={1:F1}ms 变更={2}次",
                                      uiMs, guardMs, _uiSetCount));
                }
                _uiSetCount = 0;
            };
            _fast.Start();

            _slow = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(10, _cfg.NetCheckSec)) };
            _slow.Tick += delegate { ProbeNet(); };
            _slow.Start();

            FerryCore.Logger = Log;

            try
            {
                NetworkChange.NetworkAvailabilityChanged += delegate { ProbeNet(); };
                NetworkChange.NetworkAddressChanged += delegate { ProbeNet(); };
            }
            catch (Exception) { }

            ProbeNet();

            _hbTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _hbTimer.Tick += delegate { SendHeartbeat(); };
            _hbTimer.Start();

            // 注：1.0.15 曾在这里清理旧版写进 Clash 的配置块。
            // 1.0.18 起去掉 —— 只有极少数人导过那份配置，没必要每次启动都动别人的文件。

            _licTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            _licTimer.Tick += delegate { CheckLicense(false); CheckUpdate(); };
            _licTimer.Start();

            var updDelay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            updDelay.Tick += delegate
            {
                updDelay.Stop();
                CheckUpdate();
            };
            updDelay.Start();

            // 恢复上次的状态：更新 / 手动重启 / 开机自启都走这里。
            // /start 是显式要求，优先级最高；否则看上次离开时是不是在借网。
            if ((StartBorrowOnLoad || WasBorrowingLastTime()) && _cfg.Activated)
            {
                var restore = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                restore.Tick += delegate
                {
                    restore.Stop();
                    if (_rt == null && !_busy) DoStart();
                };
                restore.Start();
            }
        }

        // ── 「上次是什么状态」：借网 / 直连 ──
        //
        // 重启或更新之后要回到用户离开时的状态：他在借网，新实例就自己接上；
        // 他停在直连，就别自作主张去借（那会偷偷占出口额度、也可能让他以为在借）。
        //
        // 存在独立小文件里，不塞 config.json —— 那是用户的配置，不该被频繁改写，
        // 也不该因为「改了个端口」就顺手把借网状态一起改掉。
        // ── 接入方式：抢系统代理，还是只当一个 SOCKS 节点 ──
        //
        // 系统的「代理服务器」只有一个位置。用户如果已经开着 Clash / 游戏加速器，
        // 我们再写进去，两边会每秒互相改回来 —— 现象就是「借上网了但什么都打不开」。
        // 所以：**发现代理已被别人占着，就只提供服务，不碰那个位置**。
        // 用户在自己的 Clash 里把 127.0.0.1:<socks> 加成一个 SOCKS5 节点即可。
        bool _nodeMode = false;

        /// <summary>
        /// 用户明确要求「接管系统代理」（点过开启全局模式并确认）。
        ///
        /// 为什么需要它：节点模式是自动判定的（系统代理被别人占着就退让）。
        /// 但用户点「开启全局模式」的意图就是「我要全局」——这时应该抢回来，
        /// 而不是继续让着，否则那个开关就是空转的（点了没反应）。
        /// </summary>
        bool _forceTakeover = false;

        /// <summary>停止完成后要不要紧接着重新开始（给 DoStop → DoStart 用）。</summary>
        bool _pendingRestart = false;
        bool _weSetProxy = false;
        int _proxyFixTimes = 0;   // 「纠正系统代理」的次数上限，防和别的代理软件死循环

        /// <summary>系统代理是否已被别的程序（Clash / 加速器）占着。</summary>
        bool OtherProxyActive()
        {
            try
            {
                // PAC 模式：Clash / 部分加速器用「自动配置脚本」而不是固定端口。
                // 这时 ProxyServer 是空的，只看它就会误判成「没人占」，然后把用户的
                // PAC 覆盖掉，规则全失效。所以见到 PAC 就当「被别人占着」。
                if (!string.IsNullOrEmpty(FerryCore.ProxyAutoConfig())) return true;

                if (!FerryCore.ProxyEnabled()) return false;
                var srv = FerryCore.ProxyServer();
                if (string.IsNullOrEmpty(srv)) return false;

                // 是不是我们上次设的？端口可能因为被占用而顺延过（FindFree 往后找 40 个），
                // 所以按范围判断，不能只比 SocksPort / HttpPort 两个值。
                for (int p = _cfg.SocksPort; p < _cfg.SocksPort + 40; p++)
                    if (srv.IndexOf("127.0.0.1:" + p, StringComparison.OrdinalIgnoreCase) >= 0) return false;
                return true;
            }
            catch (Exception) { return false; }
        }

        string LastStatePath { get { return Path.Combine(_appDir, "last-state.txt"); } }

        void SaveLastState(bool borrowing)
        {
            try
            {
                File.WriteAllText(LastStatePath, borrowing ? "borrowing" : "direct",
                    new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        bool WasBorrowingLastTime()
        {
            try
            {
                return File.Exists(LastStatePath) &&
                       File.ReadAllText(LastStatePath).Trim() == "borrowing";
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 清理上一次客户端**异常退出**留下的残局。
        ///
        /// 会留下的东西：
        ///   · mihomo 子进程（客户端被任务管理器结束 / 崩溃时不跟着死）
        ///   · FerryTun 虚拟网卡 + 它上面的默认路由（网络列表里显示「无法访问 Internet」，
        ///     严重时整台机器上不了网 —— 用户反馈过「开机不能上网」）
        ///   · 系统代理还指着 127.0.0.1:10801（那个端口已经没人监听）
        ///
        /// 为什么必须在客户端启动时做：这些都是「上一次」留下的，本次进程
        /// 完全不知道；不做就会出现「装了个软件就上不了网」这种最恶性的问题。
        /// </summary>
        void CleanupLeftovers()
        {
            try
            {
                bool tunLeft = TunMode.AdapterPresent("FerryTun");
                bool proxyToUs = FerryCore.ProxyEnabled() &&
                                 FerryCore.ProxyServer() == _cfg.BorrowServer;
                if (!tunLeft && !proxyToUs) return;

                Log("发现上次异常退出留下的残局（网卡=" + (tunLeft ? "FerryTun" : "无") +
                    " 代理=" + (proxyToUs ? "指向渡口" : "正常") + "），正在还原");

                if (proxyToUs)
                {
                    try { FerryCore.RestoreDirect(_cfg, _appDir); } catch (Exception) { }
                }
                if (tunLeft) TunMode.CleanupStale(_appDir, Log);

                Log("残局清理完成");
            }
            catch (Exception) { }
        }

        void ProbeNet()
        {
            if (_probingNet) return;
            _probingNet = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                try { ok = FerryCore.HasNetwork(3000); } catch (Exception) { }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _probingNet = false;
                    _hasNet = ok;
                    RefreshUi();
                }));
            });
        }

        void SendHeartbeat()
        {
            if (!_cfg.Activated) return;
            var rt = _rt;
            var rx = rt != null ? rt.BytesDown : 0;
            var tx = rt != null ? rt.BytesUp : 0;
            var conns = rt != null ? (int)rt.ConnActive : 0;
            var running = rt != null && rt.Running;
            var mode = rt != null ? rt.Mode : (_cfg.DirectSocks ? "direct" : "ssh");

            ThreadPool.QueueUserWorkItem(delegate
            {
                Api.HbResult hb = null;
                try { hb = Api.Heartbeat(_cfg, rx, tx, conns, running, mode); }
                catch (Exception) { return; }

                // 服务端说这个账号不能用了 → 立刻断。
                // 靠心跳而不是等 CheckLicense：后者最长 6 小时才跑一次。
                if (hb != null && hb.Revoked && _rt != null && _rt.Running)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        Log(hb.Expired
                            ? "授权已到期，服务端要求停止借网 —— 正在切回直连"
                            : "账号已被管理员停用 —— 正在切回直连");
                        DoStop();
                        CheckLicense(true);
                    }));
                }
            });
        }

        void CheckLicense(bool verbose)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                LicenseState lic = null;
                try { lic = Api.License(_cfg); } catch (Exception) { }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (lic != null) _lic = lic;
                    if (verbose)
                    {
                        if (_lic.Ok && _lic.Enabled)
                        {
                            if (_lic.ExpiresAt > 0) Log("授权有效，还剩 " + _lic.DaysLeft + " 天");
                            else Log("授权有效（永久）");
                        }
                        else if (_lic.Expired) Log("授权已到期，请向管理员续期");
                        else if (_lic.Suspended) Log("账号已停用，请联系管理员");
                        else if (!_lic.Ok) Log("授权校验失败：" + _lic.Error + "（按本地时间判断）");
                    }

                    // 到期/停用就掐掉借网、恢复直连、清空令牌。
                    //
                    // ⚠ 这里**不能**加 `&& borrowingNow`（原来就有，2026-11-01 修掉）：
                    // 用户只是开着程序挂着、没点「开始借网」时，系统代理也可能
                    // 已经被上一次会话留在 127.0.0.1:10801 上（进程却已经停了）。
                    // 那时浏览器把请求交给一个没人监听的端口 → 连门户都打不开
                    // → 用户连「去哪要新密钥」都看不到，形成死锁。
                    // 用户截图实证：系统代理「开 127.0.0.1:10801」/ 本地代理「未运行」
                    // / 授权「已到期」，三行同时出现在一张卡上。
                    //
                    // 清掉令牌还有一个作用：RefreshUi 走「未激活」分支，
                    // 输入框自然出现，用户可以直接填新密钥续期。
                    if (_cfg.Activated && !_lic.Enabled)
                    {
                        Log("授权失效，正在恢复直连…");
                        try
                        {
                            // 只还原我们写过的那次。TUN 模式下 _weSetProxy 是 false，
                            // 系统代理归用户的 Clash 管，这里绝不能碰
                            // （否则「授权一到期」就把 Clash 顶掉，比到期本身更糟）。
                            if (_weSetProxy)
                            {
                                FerryCore.RestoreDirect(_cfg, _appDir);
                                _weSetProxy = false;
                            }
                            if (_rt != null) { _rt.Stop(); _rt = null; }
                            FerryCore.StopAll(_cfg, _appDir);
                        }
                        catch (Exception) { }
                        _cfg.Token = "";
                        try { _cfg.Save(_cfgPath); } catch (Exception) { }
                        Log(_lic.Expired
                            ? "授权已到期 —— 在输入框里填新密钥即可续期"
                            : "授权已失效 —— 在输入框里填新密钥即可恢复");
                    }

                    RefreshUi();
                }));
            });
        }

        void DoInlineActivate()
        {
            var code = (_txtCode.Text ?? "").Trim();
            if (code.Length == 0) { _txtCode.Focus(); return; }
            _btnActivate.IsEnabled = false;
            _btnActivate.Content = "激活中…";
            Log("正在激活…");

            ThreadPool.QueueUserWorkItem(delegate
            {
                ActivateResult r = null;
                string err = "";
                try { r = Api.Activate(_cfg.PortalUrl, code); }
                catch (Exception e) { err = "激活失败：" + e.Message; }

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _btnActivate.IsEnabled = true;
                    _btnActivate.Content = "激活";

                    if (err.Length > 0 || r == null || !r.Ok)
                    {
                        var msg = err.Length > 0 ? err
                            : (r == null || string.IsNullOrEmpty(r.Error) ? "激活失败，请重试" : r.Error);
                        _lblActHint.Text = msg;
                        _lblActHint.Foreground = Palette.BadB;
                        Log("激活失败：" + msg);
                        RefreshUi();
                        return;
                    }

                    // 把服务端返回的配置写回（和 WinForms 版一致）
                    _cfg.Token = r.Token;
                    if (!string.IsNullOrEmpty(r.RemoteHost)) _cfg.RemoteHost = r.RemoteHost;
                    if (r.SshPort > 0) _cfg.SshPort = r.SshPort;
                    if (r.HttpPort > 0) _cfg.HttpPort = r.HttpPort;
                    if (!string.IsNullOrEmpty(r.PortalUrl)) _cfg.PortalUrl = r.PortalUrl;
                    _cfg.ExpiresAt = r.ExpiresAt;
                    try { _cfg.Save(_cfgPath); } catch (Exception) { }

                    _txtCode.Text = "";
                    Log("激活成功 · 账号 " + _cfg.AccountIp);
                    if (r.ExpiresAt > 0)
                        Log("授权到 " + FromUnix(r.ExpiresAt).ToString("yyyy-MM-dd") + "（" + r.DaysLeft + " 天）");
                    else Log("永久授权");
                    CheckLicense(true);
                    RefreshUi();
                }));
            });
        }

        void DoStart()
        {
            if (_busy) return;
            if (!_cfg.Activated)
            {
                MessageBox.Show(this, "先在下面的框里输入密钥激活。", "渡口");
                _txtCode.Focus();
                return;
            }
            if (!_lic.Enabled && _lic.Ok)
            {
                // 别只说「去找管理员」—— 用户手里可能已经有新密钥了。
                // 把输入框露出来并聚焦，让他当场就能续上。
                MessageBox.Show(this,
                    _lic.Expired
                        ? "授权已到期。\n\n在下面「输入密钥」框里填入新密钥，点「激活」即可直接续期继续用。"
                        : "账号已停用，请联系管理员。",
                    "渡口", MessageBoxButton.OK, MessageBoxImage.Warning);
                if (_lic.Expired)
                {
                    SetVis(_txtCode, Visibility.Visible);
                    SetVis(_btnActivate, Visibility.Visible);
                    SetText(_lblActTitle, "密钥已到期");
                    _txtCode.Focus();
                }
                return;
            }

            // 每次启动都重新判断：用户可能刚开了 Clash，也可能刚关掉。
            //
            // 但「用户勾了全局模式」是更高优先级的意图 —— 那时必须接管系统代理，
            // 否则浏览器还指着 Clash，而 TUN 又把 Clash 的出站吞掉，两边都不通。
            // TUN 模式下接管系统代理不叫「抢」：停止时会原样还原。
            bool globalWanted = _cfg.UseTun && Elevator.IsAdmin();
            _nodeMode = !_forceTakeover && !globalWanted && OtherProxyActive();
            _proxyFixTimes = 0;
            if (_nodeMode)
                Log("系统代理已由其它程序管理，本次不接管（只提供本地端口）");
            else if (globalWanted && OtherProxyActive())
                Log("全局模式：接管系统代理（原设置已备份，停止时会还原）");

            SetBusy(true);
            Log("启动隧道与代理…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                var rt = new FerryRuntime(_cfg, _appDir, Log);
                // 节点模式不分流：用户已经把渡口当节点加进自己的 Clash 了，
                // 再把境外流量送回 Clash 会成环（Clash → 渡口 → Clash）。
                rt.AllowSplit = !_nodeMode;
                // TUN 能不能开，只看「有没有别的 TUN 在抢路由」，
                // 和系统代理归谁无关（那是两个层面的东西）。
                rt.AllowTun = true;
                var err = rt.Start();
                string exitIp = "";
                if (err.Length == 0)
                {
                    _rt = rt;
                    if (!_nodeMode)
                    {
                        if (rt.TunRunning)
                        {
                            // TUN 起来了 → **不抢系统代理**。
                            //
                            // 2026-11-02：这是「渡口全局和 Clash 无法共存」的另一半。
                            // TUN 已经接管全部流量，系统代理是多余的；一旦写进去，
                            // 用户常开的 Clash 就再也收不到请求（它只监听 7897，
                            // 而系统代理被我们改成了 10801）→「开全局 Clash 就废了」。
                            //
                            // 留原样还有个好处：认代理的程序照常走 Clash（它有规则、
                            // 有订阅、有测速），不认代理的程序（游戏、DSH）走 TUN，
                            // 两条路各司其职。停止借网时也不用还原别人的设置。
                            Log("全局模式已用 TUN 接管流量（TCP 仍按目标分流）—— 系统代理保持原样，留给其它代理软件");
                        }
                        else
                        {
                            try
                            {
                                FerryCore.BackupProxyOnce(_cfg, _appDir);
                                FerryCore.SetProxy(true, rt.BorrowServer);
                                _weSetProxy = true;
                            }
                            catch (Exception) { }
                        }
                    }
                    exitIp = rt.ExitIpViaProxy(6000);
                }
                else rt.Stop();

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    SetBusy(false);
                    if (err.Length > 0)
                    {
                        Log("启动失败：" + err);
                        MessageBox.Show(this, err + "\n\n日志：" + _logPath,
                            "渡口", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        Log("隧道 + 代理已就绪（SOCKS " + _rt.SocksPort + " / HTTP " + _rt.HttpPort + "）");
                        if (_nodeMode)
                            Log("未接管系统代理；本地端口 SOCKS " + _rt.SocksPort +
                                " / HTTP " + _rt.HttpPort);
                        else if (_rt.TunRunning)
                            // TUN 模式下我们**故意不碰系统代理**（留给了 Clash 等），
                            // 原来这里还打印「系统代理已切到 xxx」，是句假话 ——
                            // 排查共存问题时会把人也带偏（2026-11-02 实测踩到）。
                            Log("TUN 已接管本机流量；系统代理保持原样（未改动）");
                        else
                            Log("系统代理已切到 " + _rt.BorrowServer);
                        if (_rt.TunRunning)
                            // 1.1.1：别写「所有程序都走渡口」。TUN 保证的是所有程序
                            // **进隧道**，TCP 出站仍由本地代理按目标分流给用户自己的 Clash。
                            Log("全局转发已生效：所有程序（含游戏 / UDP）都进隧道；TCP 仍按目标分流");
                        else if (_rt.TunSkipped)
                            Log("全局转发未启用：" + _rt.TunSkipReason);
                        if (exitIp.Length > 0) { _exitVia = exitIp; Log("出口 IP：" + exitIp); }
                        else Log("出口探测失败（不影响使用，可能是远端暂时不通）");
                        SaveLastState(true);      // 下次启动/更新/开机自启要回到借网
                    }
                    RefreshUi();
                }));
            });
        }

        void DoStop()
        {
            if (_busy) return;
            SetBusy(true);
            Log("切回直连…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                if (_weSetProxy)
                {
                    try { FerryCore.RestoreDirect(_cfg, _appDir); } catch (Exception) { }
                    _weSetProxy = false;
                }
                if (_rt != null) { try { _rt.Stop(); } catch (Exception) { } _rt = null; }
                Thread.Sleep(200);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    SetBusy(false);
                    Log("已恢复直连");
                    _exitVia = "";
                    SaveLastState(false);     // 用户主动停在直连，下次启动别再自作主张借网
                    RefreshUi();

                    // 「停止后马上重来」必须在这里做，不能在调用处写 DoStop(); DoStart();
                    // —— DoStop 是异步的，它自己 SetBusy(true)，同步连调的 DoStart
                    //    进来就会被 `if (_busy) return;` 挡掉，表现是「点了没反应」。
                    if (_pendingRestart)
                    {
                        _pendingRestart = false;
                        DoStart();
                    }
                }));
            });
        }

        void OnGlobalClicked()
        {
            if (_busy) return;
            bool running = _rt != null && _rt.Running;

            // 判据用**用户意图**（_cfg.UseTun），不是 TUN 是否真的在跑。
            //
            // 否则会出现死路：TUN 被跳过时（另一个 TUN 在抢路由 / 缺 wintun），
            // 界面写着「关闭全局模式」，点下去却因为「TUN 没在跑」掉进开启分支，
            // 于是每点一次就重启一次借网，永远关不掉。
            if (_cfg.UseTun)
            {
                _cfg.UseTun = false;
                _forceTakeover = false;      // 关掉全局模式 = 不再抢占系统代理
                try { _cfg.Save(_cfgPath); } catch (Exception) { }
                Log("关闭全局模式…");
                if (running) { _pendingRestart = true; DoStop(); return; }
                RefreshUi();
                return;
            }

            // 节点模式下点「开启全局模式」= 用户明确要求接管系统代理。
            // 这时不能再让着别人（否则这个按钮就是空转的），先问一句再抢。
            if (_nodeMode)
            {
                var r0 = MessageBox.Show(this,
                    "现在有别的程序在管理系统代理（全局模式需要接管它）。\r\n\r\n" +
                    "接管之后那个程序会暂时不生效，直到你关闭全局模式或停止借网。\r\n\r\n" +
                    "确定要接管吗？",
                    "开启全局模式", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r0 != MessageBoxResult.Yes) return;
                _forceTakeover = true;
                Log("用户要求接管系统代理（全局模式）");
            }

            if (!Elevator.IsAdmin())
            {
                var r = MessageBox.Show(this,
                    "「全局模式」要让游戏 / QUIC / 语音也走渡口，需要创建虚拟网卡，" +
                    "必须管理员权限。\r\n\r\n点「是」之后程序会退出并自动以管理员身份重启；" +
                    "如果你的 UAC 是「从不通知」，不会弹任何框。",
                    "开启全局模式", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (r != MessageBoxResult.Yes) return;
                _cfg.UseTun = true;
                try { _cfg.Save(_cfgPath); } catch (Exception) { }
                Log("开启全局模式需要管理员权限，正在以管理员身份重启…");
                ElevateNow();
                return;
            }

            _cfg.UseTun = true;
            try { _cfg.Save(_cfgPath); } catch (Exception) { }
            Log("全局模式已开启");
            if (running) { _pendingRestart = true; DoStop(); return; }
            RefreshUi();
        }

        void ElevateNow()
        {
            try
            {
                if (_rt != null)
                {
                    // 只有「我们自己写过系统代理」才还原。
                    //
                    // 2026-11-02：TUN 模式下 _weSetProxy 恒为 false（我们故意
                    // 不碰系统代理，留给了用户的 Clash）。原来这里无条件调
                    // RestoreDirect，会把一份可能是上次借网留下的**陈旧备份**
                    // 写回注册表 —— 用户正开着的 Clash 就被顶掉了。
                    if (_weSetProxy)
                    {
                        FerryCore.RestoreDirect(_cfg, _appDir);
                        _weSetProxy = false;
                    }
                    _rt.Stop();
                    _rt = null;
                }
            }
            catch (Exception) { }
            Log("以管理员身份重启…（UAC 上点「是」）");
            Elevator.RestartElevated("/allow-wait");
        }

        void OpenSettings()
        {
            var dlg = new SettingsWindow(_cfg, _cfgPath, _appDir, DoUninstall, DoUpdate);
            dlg.Owner = this;
            if (dlg.ShowDialog() != true) return;
            _slow.Interval = TimeSpan.FromSeconds(Math.Max(10, _cfg.NetCheckSec));
            Log("设置已保存");
            RefreshUi();
        }

        void DoUninstall()
        {
            var unins = Path.Combine(_appDir, "unins000.exe");
            if (!File.Exists(unins))
            {
                MessageBox.Show(this, "找不到卸载程序：" + unins, "卸载");
                return;
            }
            var r = MessageBox.Show(this,
                "确定要卸载渡口吗？\r\n\r\n会先恢复直连，然后删除程序。",
                "卸载", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            try
            {
                // 同 ElevateNow：只有我们自己写过系统代理才还原。
                // TUN 模式下 _weSetProxy 恒为 false，系统代理归 Clash 管，
                // 卸载不该去动它（否则用户卸载渡口后 Clash 也被顶掉）。
                if (_weSetProxy)
                {
                    FerryCore.RestoreDirect(_cfg, _appDir);
                    _weSetProxy = false;
                }
                if (_rt != null) { _rt.Stop(); _rt = null; }
                FerryCore.StopAll(_cfg, _appDir);
            }
            catch (Exception) { }

            try
            {
                var psi = new ProcessStartInfo(unins, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART");
                psi.UseShellExecute = false;
                psi.WorkingDirectory = _appDir;
                Process.Start(psi);
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "启动卸载程序失败：" + e.Message, "卸载");
                return;
            }

            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            Thread.Sleep(600);
            Application.Current.Shutdown();
        }

        // ── 移动热点 ──
        //
        // 开关只表达意图；真正启停在后台线程（WinRT 调用会阻塞到热点起来，
        // 放 UI 线程上会冻住界面）。失败时把意图改回 false —— 否则开关停在
        // 「开」的位置、热点其实没起来，用户以为开好了。
        void OnHotspotToggled()
        {
            if (_hsBusy) return;
            bool want = _swHotspot.IsOn;

            if (!want)
            {
                _hsBusy = true;
                _cfg.HotspotOn = false;
                try { _cfg.Save(_cfgPath); } catch (Exception) { }
                Log("正在关闭移动热点…");
                RefreshUi();
                Hotspot.StopAsync(delegate (string err)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _hsBusy = false;
                        if (!string.IsNullOrEmpty(err)) Log("关闭热点失败：" + err);
                        else Log("移动热点已关闭");
                        // 状态变了，让 State() 的缓存失效，界面立刻反映真实状态
                        Hotspot.InvalidateStateCache();
                        RefreshUi();
                    }));
                });
                return;
            }

            _hsBusy = true;

            // 硬闸（第一道）：没有有线上行就根本不许开。
            //
            // 这是 2026-10-30 的新规则，比下面的「风险确认框」更硬 ——
            // 那个还允许用户点「是」强行开，这里不给这个选项。
            // 因为实测下来，单块无线网卡做 SoftAP 不是「有风险」，
            // 而是**一定会把 STA 顶掉**（用户当时的远控就此失联）。
            var locked = Hotspot.LockedReason();
            if (!string.IsNullOrEmpty(locked))
            {
                Log("热点已锁定：" + locked.Replace("\r\n", " "));
                _hsBusy = false;
                _swHotspot.SetSilently(false);
                RefreshUi();
                MessageBox.Show(this, locked, "移动热点不可用",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 先问一句「这么做会不会把你自己的网断掉」。
            //
            // 为什么（2026-10-29 真实事故）：开发时在用户机上直接调
            // StartTetheringAsync，Realtek 8852BE 的 STA 连接被顶掉 ——
            // 他的 WiFi 断了、远控同时失去。软件既然知道单网卡并发有这风险，
            // 就不能再默默下命令。
            var risk = Hotspot.UplinkRisk();
            if (!string.IsNullOrEmpty(risk))
            {
                var r = MessageBox.Show(this,
                    risk + "\r\n\r\n确定要开启热点吗？",
                    "开启移动热点", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes)
                {
                    Log("用户取消开启热点（单网卡并发会让本机断网）");
                    _hsBusy = false;
                    _swHotspot.SetSilently(false);   // 不触发 Toggled，避免再进这个函数
                    RefreshUi();
                    return;
                }
                Log("用户确认在单网卡并发风险下开启热点");
            }

            _cfg.HotspotOn = true;
            try { _cfg.Save(_cfgPath); } catch (Exception) { }
            Log("正在开启移动热点…");
            RefreshUi();

            Hotspot.StartAsync(_cfg.HotspotSsid, _cfg.HotspotPass, delegate (string err)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _hsBusy = false;
                    if (!string.IsNullOrEmpty(err))
                    {
                        _cfg.HotspotOn = false;
                        try { _cfg.Save(_cfgPath); } catch (Exception) { }
                        Log("开启热点失败：" + err);
                    }
                    else
                    {
                        var ssid = Hotspot.CurrentSsid();
                        Log("移动热点已开启" + (string.IsNullOrEmpty(ssid) ? "" : "：" + ssid));
                    }
                    // 同上：状态刚变过，别让界面还读着旧缓存
                    Hotspot.InvalidateStateCache();
                    RefreshUi();
                }));
            });
        }

        // ── 系统代理守护 ──
        //
        // 为什么需要：Clash Verge 启动时会把系统代理设成它自己的 127.0.0.1:7897。
        // 用户的机器是靠渡口借网上网的，Clash 抢走系统代理后，浏览器就只走 Clash；
        // 而 Clash 的节点连不上（机场在境外、它的 DNS 又被 TUN 劫持），于是
        // 表现成「国内网站也打不开、整个网好像断了」。2026-10-28 00:46 实测发生。
        //
        // 守护只在「正在借网」时生效：没借网时系统代理归谁管都无所谓。
        // 每 3 秒看一次（_fast 是 1 秒一跳），避免每秒读注册表。
        //
        // 这里**不强行打开**代理：如果代理被关掉了，可能是用户在手动切直连，
        // 渡口只管「被别人改走」这一种情况。
        void GuardSystemProxy()
        {
            _guardTick++;
            if (_guardTick % 3 != 0) return;
            try
            {
                if (_rt == null || !_rt.Running) return;
                // TUN 在跑时**绝不碰系统代理**。
                //
                // 2026-11-02 用户报「渡口全局和 Clash 无法共存」，根因就在这里：
                // TUN 已经把全部流量接管了，系统代理那个位置对我们完全多余；
                // 但只要我们去写它，用户自己的 Clash 就收不到任何流量 ——
                // 「一开全局，Clash 就哑了」。
                //
                // 正确分工：TUN 管「不认代理的程序」，系统代理位置留给 Clash 管
                // 「认代理的程序」。Clash 自己的出站由 TUN 里的
                // PROCESS-NAME,clash-verge → clash-egress 放行（实测能匹配上，
                // 见 CHANGELOG 1.0.37 的日志）。两层各管各的，不打架。
                if (_rt.TunRunning) return;
                if (!FerryCore.ProxyEnabled()) return;
                var want = _rt.BorrowServer;
                var cur = FerryCore.ProxyServer();
                if (SameEndpoint(cur, want)) return;
                FerryCore.SetProxy(true, want);
                if (_guardNote != cur)
                {
                    _guardNote = cur;
                    Log("系统代理被其他程序改成 [" + cur + "]，已改回 [" + want + "]（借网期间由渡口持有）");
                }
            }
            catch (Exception) { }
        }

        // 两种写法都见过：http://127.0.0.1:10802 与 127.0.0.1:10802。
        static bool SameEndpoint(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            a = a.Trim().ToLowerInvariant();
            b = b.Trim().ToLowerInvariant();
            if (a.StartsWith("http://")) a = a.Substring(7);
            if (b.StartsWith("http://")) b = b.Substring(7);
            if (a.EndsWith("/")) a = a.Substring(0, a.Length - 1);
            if (b.EndsWith("/")) b = b.Substring(0, b.Length - 1);
            return a == b;
        }

        // ── 更新 ──

        /// <summary>自动检查（启动时 + 定时器）：静默，只有真的有新版本才弹窗。</summary>
        void CheckUpdate() { CheckUpdate(false); }

        /// <summary>
        /// manual=true 表示用户点了「检查更新」：**任何**结果都要有可见反馈。
        ///
        /// 原来不分手动/自动，没新版本或连不上门户时直接 return，
        /// 界面上什么都不会发生 —— 用户只能看到自己点出了一圈焦点虚线框。
        /// </summary>
        void CheckUpdate(bool manual)
        {
            if (_cfg.PortalUrl.Length == 0)
            {
                if (manual) SetUpdState("没有配置门户地址", Palette.BadB);
                return;
            }

            if (manual)
            {
                SetUpdState("正在检查…", Palette.MutedB);
                if (_btnCheckUpdate != null) _btnCheckUpdate.IsEnabled = false;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                var v = Api.Version(_cfg.PortalUrl);
                var mine = typeof(MainWindow).Assembly.GetName().Version.ToString();

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_btnCheckUpdate != null) _btnCheckUpdate.IsEnabled = true;

                    if (!v.Ok)
                    {
                        if (manual) SetUpdState("检查失败：连不上门户", Palette.BadB);
                        if (manual) Log("检查更新失败：连不上门户（" + _cfg.PortalUrl + "）");
                        return;
                    }

                    if (Api.CompareVer(v.Latest, mine) <= 0)
                    {
                        if (manual) SetUpdState("已是最新 v" + mine, Palette.GoodB);
                        return;
                    }

                    if (manual) SetUpdState("发现新版本 " + v.Latest, Palette.AccentB);

                    _latestVer = v.Latest;
                    _btnUpd.Content = "更新 " + v.Latest;
                    _btnUpd.Visibility = Visibility.Visible;
                    Log("发现新版本 " + v.Latest + "（当前 " + mine + "）");

                    if (_updPrompted == v.Latest) return;
                    _updPrompted = v.Latest;

                    var r = MessageBox.Show(this,
                        "发现新版本 " + v.Latest + "（当前 " + mine + "）\r\n\r\n" +
                        "现在更新吗？只替换客户端本身（约 130 KB），" +
                        "配置和授权都不受影响，装完会自动重新打开。\r\n" +
                        "选「否」就继续用当前版本，本次运行不再提醒。",
                        "有可用更新", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (r == MessageBoxResult.Yes) DoUpdate(v.Latest);
                    else if (manual) SetUpdState("已跳过 " + v.Latest, Palette.MutedB);
                }));
            });
        }

        /// <summary>关于页「检查更新」右边的状态文字。</summary>
        void SetUpdState(string text, Brush color)
        {
            if (_lblUpdState == null) return;
            _lblUpdState.Text = text;
            _lblUpdState.Foreground = color;
        }

        void UpdateLog(string step)
        {
            Log("[更新] " + step);
        }

        void DoUpdate(string latest)
        {
            UpdateLog("开始（当前 " + typeof(MainWindow).Assembly.GetName().Version + " → " + latest + "）");

            var exe = Process.GetCurrentProcess().MainModule.FileName;
            var old = exe + ".old";
            var tmp = Path.Combine(Path.GetTempPath(), "ferry-client-new.exe");
            var url = _cfg.PortalUrl.TrimEnd('/') + "/download-client";

            _btnUpd.IsEnabled = false;

            // 记下「更新前是不是正在借网」。新实例起来后要恢复同样的状态 ——
            // 否则用户看到的是「更新完网就断了」，还得自己再去点「开始借网」。
            // 这台机器往往没有别的出口，这一步不是体验问题，是能不能用的问题。
            bool wasBorrowing = _rt != null;

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    UpdateLog("下载 " + url);
                    if (File.Exists(tmp)) { try { File.Delete(tmp); } catch (Exception) { } }
                    using (var wc = new System.Net.WebClient())
                    {
                        wc.Proxy = null;
                        wc.DownloadFile(url, tmp);
                    }

                    var len = new FileInfo(tmp).Length;
                    UpdateLog("下载完成 " + len + " 字节");
                    if (len < 50 * 1024) throw new Exception("下载到的文件不完整（" + len + " 字节）");

                    // ── 换成「先落新文件、再改名」的顺序 ──
                    //
                    // 旧顺序是 rename(exe → .old) 再 copy(tmp → exe)：
                    // 这两步之间有几百毫秒到几秒的窗口，目录里**没有可执行的 exe**。
                    // 在这个窗口里进程被杀、断电、杀软拦截、磁盘满，客户端就永远
                    // 起不来了（只有一个 .old，快捷方式指向的文件不存在）——
                    // 用户看到的就是「更新完程序没了」，而且只能重装。
                    //
                    // 新顺序：先把新版本完整复制成 exe.new（同目录、同卷），
                    // 再 rename(exe→.old)、rename(exe.new→exe)。
                    // 两次 rename 都是 NTFS 原子操作，窗口是微秒级；
                    // 而且任何一步失败，磁盘上都还有一份完整可执行的 exe。
                    var staged = exe + ".new";
                    if (File.Exists(old)) { try { File.Delete(old); } catch (Exception) { } }
                    if (File.Exists(staged)) { try { File.Delete(staged); } catch (Exception) { } }

                    UpdateLog("暂存新版本 " + staged);
                    File.Copy(tmp, staged, true);
                    UpdateLog("暂存成功 " + new FileInfo(staged).Length + " 字节");

                    UpdateLog("改名 " + exe + " → " + old);
                    File.Move(exe, old);
                    UpdateLog("改名成功");

                    try
                    {
                        // 紧接上一步，中间不做任何别的操作
                        UpdateLog("启用新版本");
                        File.Move(staged, exe);
                        UpdateLog("启用成功 " + new FileInfo(exe).Length + " 字节");
                    }
                    catch (Exception)
                    {
                        try { File.Move(old, exe); UpdateLog("已回滚"); } catch (Exception) { }
                        throw;
                    }

                    try { File.Delete(tmp); } catch (Exception) { }
                    try { if (File.Exists(staged)) File.Delete(staged); } catch (Exception) { }

                    UpdateLog("还原代理并退出，稍后由延迟启动器拉起新版本");
                    try
                    {
                        // 同其它路径：只有我们写过系统代理才还原。
                        // TUN 模式下系统代理是 Clash 的，更新程序不该顺手把它改掉 ——
                        // 用户更新完发现 Clash 哑了，会以为是新版本弄坏的。
                        if (_weSetProxy)
                        {
                            FerryCore.RestoreDirect(_cfg, _appDir);
                            _weSetProxy = false;
                        }
                        if (_rt != null) { _rt.Stop(); _rt = null; }
                    }
                    catch (Exception) { }

                    var relaunch = Path.Combine(Path.GetTempPath(), "ferry-relaunch.cmd");
                    try
                    {
                        var sb2 = new StringBuilder();
                        sb2.AppendLine("@echo off");
                        sb2.AppendLine("timeout /t 2 /nobreak >nul");
                        sb2.Append("start \"\" \"").Append(exe).Append("\" /allow-wait");
                        if (wasBorrowing) sb2.Append(" /start");
                        sb2.AppendLine();
                        File.WriteAllText(relaunch, sb2.ToString(), Encoding.Default);
                    }
                    catch (Exception e2) { UpdateLog("写延迟启动脚本失败：" + e2.Message); }

                    bool started = Elevator.StartDetached("cmd.exe /c \"" + relaunch + "\"");
                    UpdateLog("延迟启动器 = " + started + " (" + relaunch + ")");

                    Thread.Sleep(300);
                    Environment.Exit(0);
                }
                catch (Exception e)
                {
                    UpdateLog("失败：" + e.GetType().Name + " - " + e.Message);
                    try
                    {
                        if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe);
                    }
                    catch (Exception) { }

                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _btnUpd.IsEnabled = true;
                        MessageBox.Show(this,
                            "更新失败：" + e.Message + "\r\n\r\n程序已保持原状，可以继续使用。",
                            "更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }));
                }
            });
        }

        void SetupAutoUpdateProbe()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            int tries = 0;
            t.Tick += delegate
            {
                tries++;
                bool borrowing = _rt != null && _rt.Running && _rt.ProxyAlive;
                if (!borrowing && tries < 20)
                {
                    UpdateLog("等待借网就绪（第 " + tries + " 次）…");
                    return;
                }
                t.Stop();
                UpdateLog("自动触发（/auto-update）：借网=" + borrowing + "，先查版本");
                ThreadPool.QueueUserWorkItem(delegate
                {
                    var v = Api.Version(_cfg.PortalUrl);
                    if (!v.Ok) { UpdateLog("查版本失败，放弃"); return; }
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        var mine = typeof(MainWindow).Assembly.GetName().Version.ToString();
                        UpdateLog("服务端 " + v.Latest + "，当前 " + mine);
                        if (Api.CompareVer(v.Latest, mine) <= 0) { UpdateLog("已是最新，不更新"); return; }
                        DoUpdate(v.Latest);
                    }));
                });
            };
            t.Start();
        }

        // ══════════════════════ 界面刷新 ══════════════════════

        /// <summary>
        /// 只在值真的变了才写控件属性。
        ///
        /// 为什么需要它：RefreshUi() 每秒跑一次，里面有 40 处 `.Text =` 和
        /// 37 处 `.Foreground =`。WPF 的 Text / Foreground 都是依赖属性，
        /// **赋一个和新值相等的内容也会走完整的属性失效 → measure → arrange → 重绘**。
        /// 而界面上绝大多数内容每秒都是不变的（比如一直是「直连」那张卡）。
        ///
        /// 实测 1.0.47 在「直连、什么都没做」的状态下 5 秒吃掉 4.09 秒 CPU，
        /// 点界面上任何东西都要等很久 —— 就是这套每秒 77 次的重排把 UI 线程占满了。
        ///
        /// 用 SetValue 而不是直接赋值，是因为 TextBlock.Text 和
        /// Control.Foreground 分属不同依赖属性；按名字传更省事，
        /// 拿不到就不写（不抛异常），保证不会因为界面改版把这里变成新坑。
        /// </summary>
        static void SetIfChanged(System.Windows.Controls.TextBlock tb,
            System.Windows.DependencyProperty prop, object val)
        {
            if (tb == null) return;
            var cur = tb.GetValue(prop);
            if (Equals(cur, val)) return;
            tb.SetValue(prop, val);
            _uiSetCount++;
        }

        /// <summary>
        /// 诊断计数器：这一轮 RefreshUi 真正写了几次依赖属性。
        ///
        /// 为什么需要：理论上全部走了「值变了才写」，空闲时应该接近 0。
        /// 如果实测每秒还有很多次，说明有地方在抖动
        /// （比如某个字段在两个值之间来回翻），那才是真正的浪费来源。
        /// </summary>
        static int _uiSetCount;

        static void SetText(System.Windows.Controls.TextBlock tb, string val)
        {
            SetIfChanged(tb, System.Windows.Controls.TextBlock.TextProperty, val ?? "");
        }

        static void SetFg(System.Windows.Controls.TextBlock tb,
            System.Windows.Media.Brush val)
        {
            SetIfChanged(tb, System.Windows.Controls.TextBlock.ForegroundProperty, val);
        }

        /// <summary>
        /// 按钮的 Content / Background 同样要「值变了才写」。
        ///
        /// 第一轮只处理了 TextBlock，实测 CPU 从 82% 只降到 78% ——
        /// 因为 RefreshUi 每秒还在重设 _btnGlobal.Content、_btnGlobal.Background，
        /// 以及 _txtCode.Visibility / _btnActivate.Visibility。
        /// ContentPresenter 会为不同 Content 重建可视树；
        /// Visibility 即使赋同一个值，WPF 也会走一遍布局失效。
        /// </summary>
        static void SetContent(System.Windows.Controls.ContentControl c, object val)
        {
            if (c == null) return;
            if (Equals(c.Content, val)) return;
            c.Content = val;
        }

        static void SetBg(System.Windows.Controls.Control c, System.Windows.Media.Brush val)
        {
            if (c == null) return;
            if (Equals(c.Background, val)) return;
            c.Background = val;
        }

        static void SetVis(System.Windows.UIElement el, System.Windows.Visibility val)
        {
            if (el == null) return;
            if (el.Visibility == val) return;
            el.Visibility = val;
        }

        void RefreshUi()
        {
            // 分段计时（临时诊断，定位那 700ms 到底花在哪）
            var _t0 = System.Diagnostics.Stopwatch.StartNew();
            bool proxyOn = FerryCore.ProxyEnabled();
            string proxySrv = FerryCore.ProxyServer();
            double _tReg = _t0.Elapsed.TotalMilliseconds;

            var rt = _rt;
            bool runtimeUp = rt != null && rt.Running;
            int httpPort = rt != null ? rt.HttpPort : _cfg.HttpPort;
            int socksPort = rt != null ? rt.SocksPort : _cfg.SocksPort;
            bool bridgeUp = runtimeUp && rt.ProxyAlive;
            // tunLive 必须在这里算：下面判「借网中」要用到。
            bool tunLive = rt != null && rt.TunRunning;

            string borrowServer = "http://127.0.0.1:" + httpPort;
            // TUN 在跑时，「借网中」不能再靠系统代理判断。
            //
            // 2026-11-02 改：TUN 模式下我们**故意不碰系统代理**（留给了用户的
            // Clash，见 GuardSystemProxy 的注释）。于是 proxySrv 永远是 Clash 的
            // 7897、永远不等于 borrowServer —— 原来这条判据会把「正在借网」
            // 显示成「直连」，用户以为没连上。
            // TUN 只要在跑就说明流量确实走渡口，直接用 runtime 状态判断。
            bool borrowing = _nodeMode ? (runtimeUp && bridgeUp)
                           : tunLive ? (runtimeUp && bridgeUp)
                                     : (proxyOn && proxySrv == borrowServer && bridgeUp);

            // 纠正「代理指错地方」只在**我们自己是接管模式**时做。
            // 节点模式下那个位置属于用户的 Clash，我们一律不碰。
            // TUN 在跑时不做纠正：TUN 抢的是默认路由，系统代理指向谁都不影响
            // 流量走哪条路。这时还去改它，只会在用户开 Clash 后把状态搅乱
            // （改 3 次 → 转 nodeMode → 界面说「未接管」，可 TUN 明明在跑）。
            if (!_nodeMode && !tunLive && _weSetProxy && runtimeUp && proxyOn && bridgeUp &&
                proxySrv != borrowServer && proxySrv.StartsWith("http://127.0.0.1:"))
            {
                // 限次：如果用户中途开了 Clash/加速器，两边会每秒互相改回来（死循环）。
                // 纠正 3 次还被人改走，就说明这局不归我们管 —— 让出位置，切成节点模式。
                if (_proxyFixTimes < 3)
                {
                    _proxyFixTimes++;
                    try { FerryCore.SetProxy(true, borrowServer); } catch (Exception) { }
                    Log("系统代理指错了地方 " + proxySrv + " → " + borrowServer + "，已纠正");
                    proxySrv = borrowServer;
                    borrowing = true;
                }
                else if (_proxyFixTimes == 3)
                {
                    _proxyFixTimes++;
                    _nodeMode = true;
                    Log("系统代理被反复改走 —— 已停止纠错并转为节点模式。" +
                        "你可能开着 Clash/加速器，它才是系统代理的主人；" +
                        "需要走渡口出口的话，在它里面加 SOCKS5 127.0.0.1:" + socksPort + "。");
                }
            }

            // 激活区
            //
            // 这里有两件必须分开的事：**「有没有令牌」** 和 **「令牌还能不能用」**。
            // 早先只用 _cfg.Activated（= 令牌非空）当判据，于是出现死锁：
            // 授权到期了、界面红字写着「请向管理员续期」，可输入框和激活按钮
            // 是 Collapsed —— 用户手里就算有管理员给的新密钥，也**没有地方输入**。
            // 2026-11-01 用户截图实证：卡片上同时出现「已激活」（绿）
            // 和「授权已到期」（红），而输入框根本不在。
            //
            // 现在的规则：只要授权不可用（过期 / 停用 / 校验失败），
            // 就把输入框放出来，让用户能直接填新密钥续上 ——
            // 续期本来就是「输一个新的 8 位密钥」这么简单的事，
            // 不该要求用户先卸载、先清配置、或者去找管理员手工改库。
            bool canRenew = _cfg.Activated && !_lic.Enabled;
            if (_cfg.Activated)
            {
                SetText(_lblActTitle, canRenew ? "密钥已到期" : "已激活");
                SetFg(_lblActTitle, canRenew ? Palette.BadB : Palette.GoodB);
                SetVis(_txtCode, canRenew ? Visibility.Visible : Visibility.Collapsed);
                SetVis(_btnActivate, canRenew ? Visibility.Visible : Visibility.Collapsed);
                if (canRenew)
                {
                    // 换一个说法：不是「去找管理员」，而是「输入新密钥就能继续用」
                    SetText(_lblActHint, _lic.Expired
                        ? "授权已到期 —— 在上面的框里输入新密钥，直接续期继续用"
                        : "授权不可用 —— 在上面的框里输入新密钥，直接续期继续用");
                    SetFg(_lblActHint, Palette.BadB);
                }
                else if (_lic.ExpiresAt > 0)
                {
                    SetText(_lblActHint, "账号 " + _cfg.AccountIp + " · " + LeftText() + " · " +
                        FromUnix(_lic.ExpiresAt).ToString("yyyy-MM-dd") + " 到期");
                    SetFg(_lblActHint, _lic.DaysLeft <= 3 ? Palette.BadB : Palette.TextB);
                }
                else { SetText(_lblActHint, "账号 " + _cfg.AccountIp + " · 永久授权"); SetFg(_lblActHint, Palette.TextB); }
                SetText(_lblAcct, "账号 " + _cfg.AccountIp);
            }
            else
            {
                SetText(_lblActTitle, "输入密钥激活");
                SetFg(_lblActTitle, Palette.TextB);
                SetVis(_txtCode, Visibility.Visible);
                SetVis(_btnActivate, Visibility.Visible);
                SetText(_lblActHint, "管理员给你的 8 位字母数字，一个密钥只能用一次。");
                SetFg(_lblActHint, Palette.MutedB);
                SetText(_lblAcct, "未激活");
            }

            // 配额行
            if (!_cfg.Activated) { SetText(_lblQuota, "输入密钥后开始计时"); SetFg(_lblQuota, Palette.MutedB); }
            else if (_lic.Expired) { SetText(_lblQuota, "授权已到期，请向管理员续期"); SetFg(_lblQuota, Palette.BadB); }
            else if (_lic.Suspended) { SetText(_lblQuota, "账号已停用，请联系管理员"); SetFg(_lblQuota, Palette.BadB); }
            else if (_lic.ExpiresAt > 0)
            {
                SetText(_lblQuota, LeftText() + " · " + FromUnix(_lic.ExpiresAt).ToString("yyyy-MM-dd") + " 到期");
                SetFg(_lblQuota, _lic.DaysLeft <= 3 ? Palette.BadB : Palette.MutedB);
            }
            else { SetText(_lblQuota, "永久授权"); SetFg(_lblQuota, Palette.MutedB); }

            // 大状态
            if (!_cfg.Activated)
            {
                SetText(_lblBig, "未激活");
                SetFg(_lblBig, Palette.BadB);
                SetText(_lblDesc, "在下面的框里输入管理员给的密钥，即可开通借网。");
                SetFg(_lblDesc, Palette.MutedB);
            }
            else if (borrowing)
            {
                SetText(_lblBig, "正在走渡口");
                SetFg(_lblBig, Palette.GoodB);
                if (_nodeMode)
                {
                    // 只说「端口在哪儿」，不说「把它加进 Clash」——
                    // 这台机器可能不是本人的，界面上不该出现任何「怎么用它翻墙」的字眼。
                    // 需要细节的人去设置里点 🐱（那里有完整说明）。
                    SetText(_lblDesc, "本地端口 " + (rt != null ? rt.SocksPort : _cfg.SocksPort) +
                        " / " + (rt != null ? rt.HttpPort : _cfg.HttpPort) +
                        " · 系统代理未接管" +
                        (_exitVia.Length > 0 ? "\n出口 " + _exitVia : ""));
                }
                else
                {
                    SetText(_lblDesc, _exitVia.Length > 0
                        ? "流量经远端主机中转 · 出口 " + _exitVia : "流量经远端主机中转");
                }
                SetFg(_lblDesc, Palette.MutedB);

                if (_cfg.UseTun && !(rt != null && rt.TunRunning))
                {
                    var terr = rt != null ? rt.TunError : "";
                    var why = rt != null ? rt.TunSkipReason : "";
                    var reason = terr.Length > 0 ? terr : why;
                    SetText(_lblDesc, "已借网，但全局接管（TUN）未启用 —— 只有认代理的程序生效。" +
                        (reason.Length > 0 ? "\n原因：" + reason : ""));
                    SetFg(_lblDesc, Palette.BadB);
                }
            }
            else if (proxyOn && proxySrv == borrowServer)
            {
                SetText(_lblBig, "借网异常");
                SetFg(_lblBig, Palette.BadB);
                SetText(_lblDesc, "系统代理指向渡口，但本地代理端口没响应。点「停止并恢复直连」再重新开始。");
                SetFg(_lblDesc, Palette.MutedB);
            }
            else if (!_hasNet)
            {
                SetText(_lblBig, "无网络");
                SetFg(_lblBig, Palette.BadB);
                SetText(_lblDesc, "本机暂时连不上外网 —— 点「开始借网」借用出口主机。");
                SetFg(_lblDesc, Palette.MutedB);
            }
            else
            {
                SetText(_lblBig, "直连");
                SetFg(_lblBig, Palette.TextB);
                SetText(_lblDesc, "正在用本机网络。需要时点「开始借网」借用出口主机。");
                SetFg(_lblDesc, Palette.MutedB);
            }

            // 明细行
            SetText(_rowBridge, "本地代理    " + (bridgeUp ? "运行中（端口 " + httpPort + "）" : "未运行"));
            SetFg(_rowBridge, bridgeUp ? Palette.TextB : Palette.MutedB);

            SetText(_rowProxy, "系统代理    " + (proxyOn ? "开  " + (proxySrv.Length > 0 ? proxySrv : "（未设置）") : "关"));
            SetFg(_rowProxy, borrowing ? Palette.GoodB : Palette.TextB);

            // ══════════ 全局模式状态 ══════════
            //
            // 这里必须区分**三件不同的事**，之前混在一起导致文案不合理：
            //   ① 用户想要吗（_cfg.UseTun，持久化的意图）
            //   ② TUN 现在真的在跑吗（rt.TunRunning，真实运行状态）
            //   ③ 有能力跑吗（是不是管理员）
            //
            // 踩过的坑：原来只要「没借网」就落到 else 分支显示「未开启」，
            // 而 UseTun=true 且非管理员时又显示「待管理员授权」——
            // 于是「已经授权过、只是想等会儿再借网」的用户也看到「待授权」，
            // 完全不合理。
            bool tunUp = rt != null && rt.TunRunning;
            bool wantTun = _cfg.UseTun;
            var _tAdminSw = System.Diagnostics.Stopwatch.StartNew();
            bool isAdmin = Elevator.IsAdmin();
            _tAdminSw.Stop();
            double _tAdmin = _tAdminSw.Elapsed.TotalMilliseconds;

            if (tunUp)
            {
                // 真的在跑 —— 这是唯一能说「已开启」的情况
                // 1.1.1：别写成「全部流量都走渡口」。TUN 负责**接管**（不认代理的
                // 程序、UDP / 游戏也进隧道），TCP 仍由本地代理按目标分流到用户自己的
                // Clash。旧文案让人以为开了全局模式就不分流了（见 docs/分流架构.md 五）。
                SetText(_rowTun, "全局模式    已开启（TUN 接管；TCP 仍按目标分流）");
                SetFg(_rowTun, Palette.GoodB);
                SetContent(_btnGlobal, "关闭全局模式");
                SetBg(_btnGlobal, Palette.IdleB);
            }
            else if (wantTun && !isAdmin)
            {
                // 想要，但没权限 —— 这时才该说「待授权」
                SetText(_rowTun, "全局模式    已勾选 · 需管理员授权");
                SetFg(_rowTun, Palette.WarnB);
                SetContent(_btnGlobal, "去授权");
                SetBg(_btnGlobal, Palette.AccentB);
            }
            else if (wantTun && runtimeUp && !tunUp)
            {
                // 有权限、正在借网，但 TUN 没在跑。三种情况必须分开显示，
                // 否则「被主动跳过」会一直显示成「正在启用…」（永远卡住）。
                var terr = rt != null ? rt.TunError : "";
                bool skipped = rt != null && rt.TunSkipped;
                var why = rt != null ? rt.TunSkipReason : "";
                if (!string.IsNullOrEmpty(terr))
                {
                    SetText(_rowTun, "全局模式    启用失败：" + terr);
                    SetFg(_rowTun, Palette.BadB);
                }
                else if (skipped)
                {
                    SetText(_rowTun, "全局模式    未启用：" + why);
                    SetFg(_rowTun, Palette.WarnB);
                }
                else
                {
                    SetText(_rowTun, "全局模式    正在启用…");
                    SetFg(_rowTun, Palette.MutedB);
                }
                SetContent(_btnGlobal, "关闭全局模式");
                SetBg(_btnGlobal, Palette.IdleB);
            }
            else if (wantTun)
            {
                // 有权限、已勾选，但**还没借网** —— 这不是「待授权」，
                // 是「已就绪，借网时自动生效」。
                SetText(_rowTun, "全局模式    已开启 · 下次借网时生效");
                SetFg(_rowTun, Palette.GoodB);
                SetContent(_btnGlobal, "关闭全局模式");
                SetBg(_btnGlobal, Palette.IdleB);
            }
            else
            {
                // 用户明确没勾 —— 这才是真的「未开启」
                SetText(_rowTun, "全局模式    未开启");
                SetFg(_rowTun, Palette.MutedB);
                SetContent(_btnGlobal, "开启全局模式");
                SetBg(_btnGlobal, Palette.AccentB);
            }

            // ══════════ 移动热点状态 ══════════
            double _tHotspotState = 0;
            //
            // 和全局模式同一个原则：开关显示**用户意图**（_cfg.HotspotOn），
            // 文字说明**实际状态**（Hotspot.State() / 网卡有没有拿到 192.168.137.1）。
            // 两者不一致时文字要写清楚，否则「开关是开的、热点其实没起来」会让人
            // 以为手机连不上是手机的问题。
            {
                var _ths = System.Diagnostics.Stopwatch.StartNew();
                var hsState = Hotspot.State();
                _ths.Stop();
                _tHotspotState = _ths.Elapsed.TotalMilliseconds;
                // 实测热点起来后状态可能停在 InTransition（已经在广播了），
                // 只认 "On" 会把它显示成「正在开启…」永远转圈。
                bool hsOn = hsState.Length > 0
                            && !string.Equals(hsState, "Off", StringComparison.OrdinalIgnoreCase);
                bool hsNic = hsOn && Hotspot.NicReady();
                _swHotspot.SetSilently(_cfg.HotspotOn);

                if (hsOn && hsNic)
                {
                    var ssid = Hotspot.CurrentSsid();
                    SetText(_rowHotspot, "移动热点    已开启" +
                        (string.IsNullOrEmpty(ssid) ? "" : "（" + ssid + "）"));
                    SetFg(_rowHotspot, Palette.GoodB);
                }
                else if (hsOn && !hsNic)
                {
                    // API 说开着，但网卡没拿到地址 —— 手机连得上也上不了网。
                    SetText(_rowHotspot, "移动热点    已开启但网卡未就绪（重启电脑可恢复）");
                    SetFg(_rowHotspot, Palette.WarnB);
                }
                else if (_cfg.HotspotOn)
                {
                    SetText(_rowHotspot, "移动热点    正在开启…");
                    SetFg(_rowHotspot, Palette.MutedB);
                }
                else if (hsState.Length == 0)
                {
                    SetText(_rowHotspot, "移动热点    本机不支持");
                    SetFg(_rowHotspot, Palette.MutedB);
                }
                else
                {
                    SetText(_rowHotspot, "移动热点    未开启");
                    SetFg(_rowHotspot, Palette.MutedB);
                }

                // 硬闸：没有有线上行时直接锁住开关，不给点。
                //
                // 2026-10-30 需求：只有检测到以太网连接才允许用热点。
                // 理由见 Hotspot.LockedReason() 的注释 —— 单块无线网卡
                // 同时做 STA + SoftAP 会把用户正在用的 WiFi 顶掉。
                //
                // 只在「热点没开」时锁。已经开着的话（比如网线刚被拔）
                // 不能让开关动不了 —— 那样用户就没法关掉它了。
                //
                // ⚠ 绝对不能在 RefreshUi 里同步调 Hotspot.HasWiredUplink()。
                //
                // RefreshUi 挂在 1 秒的 DispatcherTimer 上（UI 线程）。
                // HasWiredUplink 内部要遍历 NetworkInterface.GetAllNetworkInterfaces()
                // 并对每个以太网卡取 GetIPProperties() —— 这走的是 Windows 的
                // 网络配置 API，实测单片就要 **70~126 ms**（本机 6 个网卡）。
                // 也就是每秒有 70~126 ms UI 线程被同步占住：拖动窗口、点按钮、
                // 滚动日志全部会顿一下；网卡多/装了 VPN 虚拟网卡的机器更糟。
                //
                // 2026-10-30 用户报「1.0.51 界面十分卡顿、操作困难」——
                // 1.0.49 刚把 UI CPU 从 82% 降到 7.2%，1.0.51 加热点硬闸时
                // 又把这个 O(网卡数) 的重调用塞回了每秒路径。
                //
                // 修法：用缓存 + 后台线程刷新。
                //   · 界面这一帧只读 _wiredCache（O(1)）
                //   · 真正探测放到 ThreadPool，最多每 15 秒一次
                //   · 探测完 Dispatcher.BeginInvoke 回 UI 更新缓存
                // 15 秒足够：网线插拔不是高频操作，而且热点开关本身会走
                // LockedReason() 的实时判定（见 Hotspot.Start），闸门不受影响。
                bool wired = _wiredCache;
                RefreshWiredCacheAsync();
                if (!hsOn && !wired)
                {
                    SetText(_rowHotspot, "移动热点    需有线连接（当前只有 WiFi）");
                    SetFg(_rowHotspot, Palette.MutedB);
                }
                SetEnabled(_swHotspot, !_busy && !_hsBusy && (hsOn || wired));
            }

            // IsEnabled 也是依赖属性，赋同一个值一样会触发 IsEnabledChanged
            // 并向下传播到整个可视子树（1.0.49 的教训：Visibility 就是这样）。
            // 这里每秒无条件写一次，是「系统性扫描依赖属性赋值」时漏掉的最后一类。
            SetEnabled(_btnOn, !_busy && !borrowing);
            SetEnabled(_btnOff, !_busy && (borrowing || runtimeUp || (proxyOn && proxySrv == borrowServer)));

            // 分段计时输出（临时诊断）：只在总耗时异常时打印，定位那 700ms
            _t0.Stop();
            double _tTotal = _t0.Elapsed.TotalMilliseconds;
            if (_tTotal > 50)
            {
                Log(string.Format("[分段] 总={0:F0}ms 注册表={1:F1} IsAdmin={2:F1} HotspotState={3:F1}",
                                  _tTotal, _tReg, _tAdmin, _tHotspotState));
            }
        }

        /// <summary>
        /// 「值变了才写」版的 IsEnabled。
        ///
        /// 为什么需要：RefreshUi 挂在 1 秒的 DispatcherTimer 上，里面任何
        /// 无条件的依赖属性赋值都会被每秒重放一次。IsEnabled 变化会触发
        /// IsEnabledChanged 路由事件并递归传播到所有子元素 —— 自定义控件
        /// （比如 ToggleSwitch 里的 Ellipse + Border）还要重算命中测试。
        /// 1.0.49 已经因为这个原因把 CPU 从 82% 压到 7.2%，但当时只扫了
        /// Text/Content/Background/Visibility/Foreground 五类，漏了 IsEnabled。
        /// </summary>
        static void SetEnabled(System.Windows.UIElement el, bool val)
        {
            if (el == null) return;
            if (el.IsEnabled == val) return;
            el.IsEnabled = val;
        }

        void SetBusy(bool b)
        {
            _busy = b;
            // SetBusy 本身不高频（只在开始/结束操作时调），但既然改成了
            // 统一的「值变了才写」，这几处也一起走 SetEnabled —— 免得下次
            // 有人把 SetBusy 挪进定时器路径后又踩同一个坑。
            SetEnabled(_btnOn, !b);
            SetEnabled(_btnOff, !b);
            SetEnabled(_btnSettings, !b);
        }

        public void Log(string line)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<string>(Log), line);
                return;
            }
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            var text = "[" + stamp + "] " + line;

            // 界面上只保留最近若干行。
            //
            // 为什么必须截断：TextBox 的 AppendText 是追加到末尾，但布局会随行数
            // 增长而变慢；1.2.3 起日志开了自动换行，同一段文字占的视觉行更多，
            // 重排成本也跟着涨，所以上限从 800 收到 400。
            if (_logLineCount > 400)
            {
                _log.Clear();
                _logLineCount = 0;
            }
            _log.AppendText(text + Environment.NewLine);
            _logLineCount++;
            _log.ScrollToEnd();

            // 磁盘写入绝不能放在 UI 线程上。
            //
            // 原来这里是同步的 File.AppendAllText + FileInfo.Length：
            // 每次日志都做一次写 + 一次 stat。网卡抖动、mihomo 刷警告时
            // 日志会成串出现，UI 线程就被磁盘 IO 卡住 —— 表现就是拖动窗口一顿一顿。
            // 更糟的是超过 200 KB 时会 ReadAllLines + WriteAllLines，
            // 那是整个文件的读写，一次能卡上百毫秒。
            //
            // 改成排队 + 后台线程批量写：UI 只做字符串入队（O(1)）。
            EnqueueLogFileLine(DateTime.Now.ToString("yyyy-MM-dd ") + text);
        }

        // ── 日志落盘（后台批量）──
        readonly object _logFileLock = new object();
        readonly System.Collections.Generic.List<string> _logFileQueue =
            new System.Collections.Generic.List<string>();
        bool _logFileWorkerBusy;
        int _logLineCount;

        void EnqueueLogFileLine(string full)
        {
            lock (_logFileLock) { _logFileQueue.Add(full); }
            if (_logFileWorkerBusy) return;
            _logFileWorkerBusy = true;
            ThreadPool.QueueUserWorkItem(delegate { DrainLogFileQueue(); });
        }

        void DrainLogFileQueue()
        {
            while (true)
            {
                string[] batch;
                lock (_logFileLock)
                {
                    if (_logFileQueue.Count == 0) { _logFileWorkerBusy = false; return; }
                    batch = _logFileQueue.ToArray();
                    _logFileQueue.Clear();
                }
                try
                {
                    File.AppendAllText(_logPath, string.Join(Environment.NewLine, batch) + Environment.NewLine,
                                       new UTF8Encoding(false));
                    var fi = new FileInfo(_logPath);
                    if (fi.Length > 204800)
                    {
                        var all = File.ReadAllLines(_logPath);
                        var keep = new string[Math.Min(500, all.Length)];
                        Array.Copy(all, all.Length - keep.Length, keep, 0, keep.Length);
                        File.WriteAllLines(_logPath, keep, new UTF8Encoding(false));
                    }
                }
                catch (Exception) { }
            }
        }

        string LeftText()
        {
            if (_lic.ExpiresAt <= 0) return "永久授权";
            int d = _lic.DaysLeft;
            if (d > 0) return "剩余 " + d + " 天";
            var span = FromUnix(_lic.ExpiresAt) - DateTime.Now;
            if (span.TotalHours > 0) return "剩余 " + Math.Max(1, (int)span.TotalHours) + " 小时";
            return "已到期";
        }

        static DateTime FromUnix(long ts)
        {
            return DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
        }
    }
}

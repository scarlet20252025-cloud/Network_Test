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
using System.Windows.Media;
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
        TextBlock _rowBridge, _rowProxy, _rowTun;
        Border _card;
        TextBlock _lblActTitle, _lblActHint;
        TextBox _txtCode;
        Button _btnActivate, _btnOn, _btnOff, _btnRefresh, _btnSettings, _btnUpd, _btnGlobal, _btnClash;
        TextBox _log;
        Border _actCard;
        StackPanel _mainStack;

        // ── 定时器 ──
        DispatcherTimer _fast, _slow, _hbTimer, _licTimer;
        WF.NotifyIcon _tray;

        // ── 状态 ──
        bool _busy, _hasNet = true, _probingNet;
        string _exitVia = "";
        string _updPrompted = "";
        string _latestVer = "";

        public bool StartBorrowOnLoad = false;
        public bool AutoUpdateOnLoad = false;

        public MainWindow(string cfgPath, string appDir)
        {
            _cfgPath = cfgPath;
            _appDir = appDir;
            _logPath = Path.Combine(appDir, "assistant.log");
            _cfg = Cfg.Load(cfgPath);

            BuildUi();
            BuildTray();

            Loaded += delegate
            {
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

        void BuildUi()
        {
            Title = "渡口 Ferry";
            Width = 520;
            Height = 780;
            MinWidth = 520;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Palette.BgB;
            FontFamily = new FontFamily(Palette.FontUI);
            FontSize = Palette.FsBody;
            Foreground = Palette.TextB;
            ResizeMode = ResizeMode.CanMinimize;

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Content = scroll;

            _mainStack = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
            scroll.Content = _mainStack;

            // ── 顶部：标题 + 账号 ──
            var header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _mainStack.Children.Add(header);

            var titleStack = new StackPanel();
            Grid.SetColumn(titleStack, 0);
            header.Children.Add(titleStack);

            titleStack.Children.Add(new TextBlock
            {
                Text = "渡口",
                FontSize = Palette.FsTitle,
                FontWeight = FontWeights.Bold,
                Foreground = Palette.TextB
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = "内网应急接入 · 断网时借一台能出网的主机中转",
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });

            var rightStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(rightStack, 1);
            header.Children.Add(rightStack);

            _lblAcct = new TextBlock
            {
                Text = "",
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            rightStack.Children.Add(_lblAcct);

            _btnUpd = MakeButton("更新", false, 0);
            _btnUpd.FontSize = Palette.FsSmall;
            _btnUpd.Padding = new Thickness(12, 4, 12, 4);
            _btnUpd.Margin = new Thickness(0, 6, 0, 0);
            _btnUpd.Visibility = Visibility.Collapsed;
            _btnUpd.Click += delegate
            {
                if (_latestVer.Length == 0) { Log("[更新] 还没拿到版本号，忽略点击"); return; }
                DoUpdate(_latestVer);
            };
            rightStack.Children.Add(_btnUpd);

            // ── 状态卡 ──
            _card = MakeCard();
            _mainStack.Children.Add(_card);

            var cardStack = new StackPanel();
            _card.Child = cardStack;

            _lblBig = new TextBlock
            {
                Text = "检查中",
                FontSize = Palette.FsBig,
                FontWeight = FontWeights.Bold,
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
                TextWrapping = TextWrapping.Wrap,      // ← 自动换行，高度自动
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

            _rowBridge = MakeRow(cardStack);
            _rowProxy = MakeRow(cardStack);

            // 「全局模式」行：左边文字 + 右边按钮，用 Grid 自动分配
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

            // ── 激活卡 ──
            _actCard = MakeCard();
            _actCard.Margin = new Thickness(0, 12, 0, 0);
            _mainStack.Children.Add(_actCard);

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
                Padding = new Thickness(10, 7, 10, 7),
                BorderBrush = Palette.Line2B,
                BorderThickness = new Thickness(1),
                CharacterCasing = CharacterCasing.Upper,
                MaxLength = 16,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _txtCode.KeyDown += delegate (object s, System.Windows.Input.KeyEventArgs e)
            {
                if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; DoInlineActivate(); }
            };
            Grid.SetColumn(_txtCode, 0);
            actRow.Children.Add(_txtCode);

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

            // ── 主按钮 ──
            var btnRow = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _mainStack.Children.Add(btnRow);

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

            // ── 次按钮 ──
            var subRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _mainStack.Children.Add(subRow);

            _btnRefresh = MakeButton("刷新", false, 0);
            _btnRefresh.FontSize = Palette.FsSmall;
            _btnRefresh.Padding = new Thickness(22, 6, 22, 6);
            _btnRefresh.Click += delegate { RefreshUi(); Log("已刷新"); };
            Grid.SetColumn(_btnRefresh, 0);
            subRow.Children.Add(_btnRefresh);

            // 一键把渡口作为一个 SOCKS5 节点写进 Clash 的全局扩展配置。
            // 中间这一列本来是空的，正好放它，不挤占「刷新 / 设置」的位置。
            _btnClash = MakeButton("接入 Clash", false, 0);
            _btnClash.FontSize = Palette.FsSmall;
            _btnClash.Padding = new Thickness(22, 6, 22, 6);
            _btnClash.Click += delegate { DoClashMerge(); };
            Grid.SetColumn(_btnClash, 1);
            subRow.Children.Add(_btnClash);

            _btnSettings = MakeButton("设置", false, 0);
            _btnSettings.FontSize = Palette.FsSmall;
            _btnSettings.Padding = new Thickness(22, 6, 22, 6);
            _btnSettings.Click += delegate { OpenSettings(); };
            Grid.SetColumn(_btnSettings, 2);
            subRow.Children.Add(_btnSettings);

            // ── 日志 ──
            var logCard = MakeCard();
            logCard.Margin = new Thickness(0, 14, 0, 0);
            logCard.Padding = new Thickness(12);
            _mainStack.Children.Add(logCard);

            _log = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 220,
                FontFamily = new FontFamily(Palette.FontMono),
                FontSize = Palette.FsMono,
                Foreground = Palette.MutedB,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2)
            };
            logCard.Child = _log;

            // ── 底部署名 ──
            _mainStack.Children.Add(new TextBlock
            {
                Text = "渡口仅用于个人学习与内网连通性测试",
                FontSize = Palette.FsSmall,
                Foreground = Palette.IdleB,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 14, 0, 0)
            });

            Closing += OnWindowClosing;
        }

        /// <summary>
        /// 圆角卡片。Border 自带 CornerRadius —— 不需要手工画 GraphicsPath，
        /// 也不会像 WinForms 那样用 Region 把子控件裁掉。
        /// </summary>
        static Border MakeCard()
        {
            return new Border
            {
                Background = Palette.CardB,
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
                Background = primary ? Palette.AccentB : Palette.CardB,
                Foreground = primary ? Brushes.White : Palette.TextB
            };
            if (width > 0) b.Width = width;
            return b;
        }

        void BuildTray()
        {
            _tray = new WF.NotifyIcon();
            _tray.Text = "渡口 Ferry";
            try
            {
                var ico = Path.Combine(_appDir, "app.ico");
                _tray.Icon = File.Exists(ico) ? new System.Drawing.Icon(ico) : System.Drawing.SystemIcons.Application;
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
            _fast.Tick += delegate { RefreshUi(); };
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
        bool _weSetProxy = false;

        /// <summary>系统代理是否已被别的程序（Clash / 加速器）占着。</summary>
        bool OtherProxyActive()
        {
            try
            {
                if (!FerryCore.ProxyEnabled()) return false;
                var srv = FerryCore.ProxyServer();
                if (string.IsNullOrEmpty(srv)) return false;
                var ours = "127.0.0.1:" + _cfg.HttpPort;
                var ours2 = "127.0.0.1:" + _cfg.SocksPort;
                if (srv.IndexOf(ours, StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (srv.IndexOf(ours2, StringComparison.OrdinalIgnoreCase) >= 0) return false;
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

                    // 到期/停用就掐掉借网并清空令牌 —— 强制回到「未激活」。
                    // 只断网却留着令牌的话，界面还显示"已激活"，用户会反复点
                    // 开始借网然后失败（WinForms 版踩过）。
                    var borrowingNow = (_rt != null && _rt.Running);
                    if (_cfg.Activated && !_lic.Enabled && borrowingNow)
                    {
                        Log("授权失效，正在恢复直连…");
                        try
                        {
                            FerryCore.RestoreDirect(_cfg, _appDir);
                            if (_rt != null) { _rt.Stop(); _rt = null; }
                            FerryCore.StopAll(_cfg, _appDir);
                        }
                        catch (Exception) { }
                        _cfg.Token = "";
                        try { _cfg.Save(_cfgPath); } catch (Exception) { }
                        Log("授权已失效，请重新输入密钥激活");
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
                MessageBox.Show(this,
                    _lic.Expired ? "授权已到期，请向管理员续期。" : "账号已停用，请联系管理员。",
                    "渡口", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 每次启动都重新判断：用户可能刚开了 Clash，也可能刚关掉。
            _nodeMode = OtherProxyActive();
            if (_nodeMode)
                Log("检测到系统代理已被其他程序占用 → 只提供 SOCKS 端口，不去抢（节点模式）");

            SetBusy(true);
            Log("启动隧道与代理…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                var rt = new FerryRuntime(_cfg, _appDir, Log);
                var err = rt.Start();
                string exitIp = "";
                if (err.Length == 0)
                {
                    _rt = rt;
                    if (!_nodeMode)
                    {
                        try
                        {
                            FerryCore.BackupProxyOnce(_cfg, _appDir);
                            FerryCore.SetProxy(true, rt.BorrowServer);
                            _weSetProxy = true;
                        }
                        catch (Exception) { }
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
                            Log("节点模式：不改系统代理。在 Clash/加速器里加 SOCKS5 127.0.0.1:" +
                                _rt.SocksPort + "（HTTP " + _rt.HttpPort + "）");
                        else
                            Log("系统代理已切到 " + _rt.BorrowServer);
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
                }));
            });
        }

        void OnGlobalClicked()
        {
            if (_busy) return;
            bool running = _rt != null && _rt.Running;
            bool tunUp = running && _rt.TunRunning;

            if (tunUp)
            {
                _cfg.UseTun = false;
                try { _cfg.Save(_cfgPath); } catch (Exception) { }
                Log("关闭全局模式…");
                if (running) { DoStop(); DoStart(); return; }
                RefreshUi();
                return;
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
            if (running) { DoStop(); DoStart(); return; }
            RefreshUi();
        }

        void ElevateNow()
        {
            try
            {
                if (_rt != null)
                {
                    FerryCore.RestoreDirect(_cfg, _appDir);
                    _rt.Stop();
                    _rt = null;
                }
            }
            catch (Exception) { }
            Log("以管理员身份重启…（UAC 上点「是」）");
            Elevator.RestartElevated("/allow-wait");
        }

        void DoClashMerge()
        {
            int port = _rt != null ? _rt.SocksPort : _cfg.SocksPort;
            var msg = ClashMerge.Install(port);
            Log("接入 Clash（端口 " + port + "）：" + (msg.Length > 50 ? msg.Substring(0, 50) + "…" : msg));
            MessageBox.Show(this, msg, "接入 Clash",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void OpenSettings()
        {
            var dlg = new SettingsWindow(_cfg, _cfgPath, _appDir, DoUninstall);
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
                FerryCore.RestoreDirect(_cfg, _appDir);
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

        // ── 更新 ──

        void CheckUpdate()
        {
            if (_cfg.PortalUrl.Length == 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var v = Api.Version(_cfg.PortalUrl);
                if (!v.Ok) return;
                var mine = typeof(MainWindow).Assembly.GetName().Version.ToString();
                if (Api.CompareVer(v.Latest, mine) <= 0) return;

                Dispatcher.BeginInvoke(new Action(delegate
                {
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
                }));
            });
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
                        if (_rt != null) { FerryCore.RestoreDirect(_cfg, _appDir); _rt.Stop(); _rt = null; }
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

        void RefreshUi()
        {
            bool proxyOn = FerryCore.ProxyEnabled();
            string proxySrv = FerryCore.ProxyServer();

            var rt = _rt;
            bool runtimeUp = rt != null && rt.Running;
            int httpPort = rt != null ? rt.HttpPort : _cfg.HttpPort;
            int socksPort = rt != null ? rt.SocksPort : _cfg.SocksPort;
            bool bridgeUp = runtimeUp && rt.ProxyAlive;

            string borrowServer = "http://127.0.0.1:" + httpPort;
            bool borrowing = _nodeMode ? (runtimeUp && bridgeUp)
                                       : (proxyOn && proxySrv == borrowServer && bridgeUp);

            // 纠正「代理指错地方」只在**我们自己是接管模式**时做。
            // 节点模式下那个位置属于用户的 Clash，我们一律不碰。
            if (!_nodeMode && _weSetProxy && runtimeUp && proxyOn && bridgeUp &&
                proxySrv != borrowServer && proxySrv.StartsWith("http://127.0.0.1:"))
            {
                try { FerryCore.SetProxy(true, borrowServer); } catch (Exception) { }
                Log("系统代理指错了地方 " + proxySrv + " → " + borrowServer + "，已纠正");
                proxySrv = borrowServer;
                borrowing = true;
            }

            // 激活区
            if (_cfg.Activated)
            {
                _lblActTitle.Text = "已激活";
                _lblActTitle.Foreground = Palette.GoodB;
                _txtCode.Visibility = Visibility.Collapsed;
                _btnActivate.Visibility = Visibility.Collapsed;
                if (_lic.Expired) { _lblActHint.Text = "授权已到期 —— 请向管理员续期"; _lblActHint.Foreground = Palette.BadB; }
                else if (_lic.Suspended) { _lblActHint.Text = "账号已停用 —— 请联系管理员"; _lblActHint.Foreground = Palette.BadB; }
                else if (_lic.ExpiresAt > 0)
                {
                    _lblActHint.Text = "账号 " + _cfg.AccountIp + " · " + LeftText() + " · " +
                        FromUnix(_lic.ExpiresAt).ToString("yyyy-MM-dd") + " 到期";
                    _lblActHint.Foreground = _lic.DaysLeft <= 3 ? Palette.BadB : Palette.TextB;
                }
                else { _lblActHint.Text = "账号 " + _cfg.AccountIp + " · 永久授权"; _lblActHint.Foreground = Palette.TextB; }
                _lblAcct.Text = "账号 " + _cfg.AccountIp;
            }
            else
            {
                _lblActTitle.Text = "输入密钥激活";
                _lblActTitle.Foreground = Palette.TextB;
                _txtCode.Visibility = Visibility.Visible;
                _btnActivate.Visibility = Visibility.Visible;
                _lblActHint.Text = "管理员给你的 8 位字母数字，一个密钥只能用一次。";
                _lblActHint.Foreground = Palette.MutedB;
                _lblAcct.Text = "未激活";
            }

            // 配额行
            if (!_cfg.Activated) { _lblQuota.Text = "输入密钥后开始计时"; _lblQuota.Foreground = Palette.MutedB; }
            else if (_lic.Expired) { _lblQuota.Text = "授权已到期，请向管理员续期"; _lblQuota.Foreground = Palette.BadB; }
            else if (_lic.Suspended) { _lblQuota.Text = "账号已停用，请联系管理员"; _lblQuota.Foreground = Palette.BadB; }
            else if (_lic.ExpiresAt > 0)
            {
                _lblQuota.Text = LeftText() + " · " + FromUnix(_lic.ExpiresAt).ToString("yyyy-MM-dd") + " 到期";
                _lblQuota.Foreground = _lic.DaysLeft <= 3 ? Palette.BadB : Palette.MutedB;
            }
            else { _lblQuota.Text = "永久授权"; _lblQuota.Foreground = Palette.MutedB; }

            // 大状态
            if (!_cfg.Activated)
            {
                _lblBig.Text = "未激活";
                _lblBig.Foreground = Palette.BadB;
                _lblDesc.Text = "在下面的框里输入管理员给的密钥，即可开通借网。";
                _lblDesc.Foreground = Palette.MutedB;
            }
            else if (borrowing)
            {
                _lblBig.Text = "正在走渡口";
                _lblBig.Foreground = Palette.GoodB;
                if (_nodeMode)
                {
                    // 节点模式：系统代理归用户的 Clash 管，我们只提供端口。
                    // 这里必须把「该怎么接」说清楚，否则用户只会看到「借网成功但上不了网」。
                    _lblDesc.Text = "节点模式 · 在 Clash / 加速器里添加 SOCKS5 127.0.0.1:" +
                        (rt != null ? rt.SocksPort : _cfg.SocksPort) +
                        "（HTTP " + (rt != null ? rt.HttpPort : _cfg.HttpPort) + "）" +
                        ( _exitVia.Length > 0 ? "\n出口 " + _exitVia : "");
                }
                else
                {
                    _lblDesc.Text = _exitVia.Length > 0
                        ? "流量经远端主机中转 · 出口 " + _exitVia : "流量经远端主机中转";
                }
                _lblDesc.Foreground = Palette.MutedB;

                if (_cfg.UseTun && !(rt != null && rt.TunRunning))
                {
                    var terr = rt != null ? rt.TunError : "";
                    _lblDesc.Text = "已借网，但全局接管（TUN）未启用 —— 只有认代理的程序生效。" +
                        (terr.Length > 0 ? "\n原因：" + terr : "");
                    _lblDesc.Foreground = Palette.BadB;
                }
            }
            else if (proxyOn && proxySrv == borrowServer)
            {
                _lblBig.Text = "借网异常";
                _lblBig.Foreground = Palette.BadB;
                _lblDesc.Text = "系统代理指向渡口，但本地代理端口没响应。点「停止并恢复直连」再重新开始。";
                _lblDesc.Foreground = Palette.MutedB;
            }
            else if (!_hasNet)
            {
                _lblBig.Text = "无网络";
                _lblBig.Foreground = Palette.BadB;
                _lblDesc.Text = "本机暂时连不上外网 —— 点「开始借网」借用出口主机。";
                _lblDesc.Foreground = Palette.MutedB;
            }
            else
            {
                _lblBig.Text = "直连";
                _lblBig.Foreground = Palette.TextB;
                _lblDesc.Text = "正在用本机网络。需要时点「开始借网」借用出口主机。";
                _lblDesc.Foreground = Palette.MutedB;
            }

            // 明细行
            _rowBridge.Text = "本地代理    " + (bridgeUp ? "运行中（端口 " + httpPort + "）" : "未运行");
            _rowBridge.Foreground = bridgeUp ? Palette.TextB : Palette.MutedB;

            _rowProxy.Text = "系统代理    " + (proxyOn ? "开  " + (proxySrv.Length > 0 ? proxySrv : "（未设置）") : "关");
            _rowProxy.Foreground = borrowing ? Palette.GoodB : Palette.TextB;

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
            bool isAdmin = Elevator.IsAdmin();

            if (tunUp)
            {
                // 真的在跑 —— 这是唯一能说「已开启」的情况
                _rowTun.Text = "全局模式    已开启（UDP / 游戏都走渡口）";
                _rowTun.Foreground = Palette.GoodB;
                _btnGlobal.Content = "关闭全局模式";
                _btnGlobal.Background = Palette.IdleB;
            }
            else if (wantTun && !isAdmin)
            {
                // 想要，但没权限 —— 这时才该说「待授权」
                _rowTun.Text = "全局模式    已勾选 · 需管理员授权";
                _rowTun.Foreground = Palette.WarnB;
                _btnGlobal.Content = "去授权";
                _btnGlobal.Background = Palette.AccentB;
            }
            else if (wantTun && runtimeUp && !tunUp)
            {
                // 有权限、正在借网，但 TUN 还没起来（启动中或失败）
                var terr = rt != null ? rt.TunError : "";
                if (!string.IsNullOrEmpty(terr))
                {
                    _rowTun.Text = "全局模式    启用失败：" + terr;
                    _rowTun.Foreground = Palette.BadB;
                }
                else
                {
                    _rowTun.Text = "全局模式    正在启用…";
                    _rowTun.Foreground = Palette.MutedB;
                }
                _btnGlobal.Content = "关闭全局模式";
                _btnGlobal.Background = Palette.IdleB;
            }
            else if (wantTun)
            {
                // 有权限、已勾选，但**还没借网** —— 这不是「待授权」，
                // 是「已就绪，借网时自动生效」。
                _rowTun.Text = "全局模式    已开启 · 下次借网时生效";
                _rowTun.Foreground = Palette.GoodB;
                _btnGlobal.Content = "关闭全局模式";
                _btnGlobal.Background = Palette.IdleB;
            }
            else
            {
                // 用户明确没勾 —— 这才是真的「未开启」
                _rowTun.Text = "全局模式    未开启";
                _rowTun.Foreground = Palette.MutedB;
                _btnGlobal.Content = "开启全局模式";
                _btnGlobal.Background = Palette.AccentB;
            }

            _btnOn.IsEnabled = !_busy && !borrowing;
            _btnOff.IsEnabled = !_busy && (borrowing || runtimeUp || (proxyOn && proxySrv == borrowServer));
        }

        void SetBusy(bool b)
        {
            _busy = b;
            _btnOn.IsEnabled = !b;
            _btnOff.IsEnabled = !b;
            _btnRefresh.IsEnabled = !b;
            _btnSettings.IsEnabled = !b;
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
            _log.AppendText(text + Environment.NewLine);
            _log.ScrollToEnd();

            try
            {
                File.AppendAllText(_logPath,
                    DateTime.Now.ToString("yyyy-MM-dd ") + text + Environment.NewLine,
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

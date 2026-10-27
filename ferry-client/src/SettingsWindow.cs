using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WF = System.Windows.Forms;

namespace Ferry
{
    /// <summary>
    /// 设置窗口（WPF 版）。
    ///
    /// 和主窗口一样：所有控件高度由内容决定，不写死任何像素。
    /// WinForms 版在这里踩过最多坑（勾选框标题被裁、说明文字重叠、
    /// 卡片撑不住要手工下移下方控件），WPF 的 StackPanel 全部自动处理。
    /// </summary>
    internal class SettingsWindow : Window
    {
        readonly Cfg _cfg;
        readonly string _cfgPath;
        readonly string _appDir;
        readonly Action _onUninstall;

        CheckBox _chkAutoStart, _chkAutoBorrow, _chkMinimized, _chkReconnect;
        NumericUpDownAdapter _numInterval;
        TextBlock _lblHint, _lblVer;
        Button _ok, _cancel, _fbBtn;

        const string TaskName = "FerryClientAutostart";

        public SettingsWindow(Cfg cfg, string cfgPath, string appDir, Action onUninstall)
        {
            _cfg = cfg;
            _cfgPath = cfgPath;
            _appDir = appDir;
            _onUninstall = onUninstall;

            BuildUi();
        }

        void BuildUi()
        {
            Title = "设置";
            Width = 500;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 820;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Palette.BgB;
            FontFamily = new FontFamily(Palette.FontUI);
            FontSize = Palette.FsBody;
            Foreground = Palette.TextB;

            var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
            Content = root;

            // 标题
            root.Children.Add(new TextBlock
            {
                Text = "设置",
                FontSize = Palette.FsTitle,
                FontWeight = FontWeights.Bold,
                Foreground = Palette.TextB
            });
            root.Children.Add(new TextBlock
            {
                Text = "改动立即生效，不需要重启",
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                Margin = new Thickness(0, 2, 0, 14)
            });

            // 开关卡片：StackPanel 自动堆叠，不会重叠
            var card = new Border
            {
                Background = Palette.CardB,
                BorderBrush = Palette.LineB,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18, 14, 18, 16)
            };
            root.Children.Add(card);

            var cardStack = new StackPanel();
            card.Child = cardStack;

            _chkAutoStart = MakeCheck(cardStack, "开机自动启动",
                "登录 Windows 后自动就绪（收在托盘，不弹窗）。用计划任务实现，不会弹 UAC");
            _chkAutoStart.IsChecked = AutoStartOn();

            _chkAutoBorrow = MakeCheck(cardStack, "断网时自动借网",
                "检测到本机连不上网时，自动开隧道并切换系统代理");
            _chkAutoBorrow.IsChecked = _cfg.AutoBorrow;

            _chkMinimized = MakeCheck(cardStack, "启动后最小化到托盘",
                "双击图标时不显示主窗口，直接驻留托盘");
            _chkMinimized.IsChecked = _cfg.StartMinimized;

            _chkReconnect = MakeCheck(cardStack, "隧道断开时自动重连",
                "远端重启、网络中抖时自动恢复（退避 5 秒起，最长 60 秒）");
            _chkReconnect.IsChecked = _cfg.AutoReconnect;

            // 断网检测间隔
            var ivRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            root.Children.Add(ivRow);

            ivRow.Children.Add(new TextBlock
            {
                Text = "断网检测间隔",
                FontSize = Palette.FsSmall,
                Foreground = Palette.TextB,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            });

            _numInterval = new NumericUpDownAdapter(_cfg.NetCheckSec, 10, 300, 5, 90);
            ivRow.Children.Add(_numInterval.Box);
            ivRow.Children.Add(new TextBlock
            {
                Text = "秒",
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });

            // 「接入 Clash」：小、灰、没有边框，靠在这一行的右端。
            // 需要的人会找到（Clash 的图标就是一只猫），不需要的人当它不存在。
            // 重复点不会重复写配置 —— 已经接入而且端口没变时只弹一句提示。
            var catBtn = new Button
            {
                Content = "🐱",
                FontSize = 13,
                Foreground = Palette.MutedB,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(3, 0, 3, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0),
                ToolTip = "Clash 说明：一般不需要配置，渡口会自己分流（点开看细节）"
            };
            catBtn.Click += delegate
            {
                // 只显示说明，**不改任何文件**。理由见 ClashMerge 的注释：
                // 每个用户的 Clash 客户端不一样，写配置文件是无底洞，写错了还会毁人家的配置。
                MessageBox.Show(this, ClashMerge.Help(_cfg.SocksPort), "Clash 说明",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            };
            ivRow.Children.Add(catBtn);

            _lblHint = new TextBlock
            {
                Text = "间隔越短越灵敏，但每次检测要发一次 DNS 查询；断网后大约一个间隔内会自动开始借网。",
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            root.Children.Add(_lblHint);

            // 底部：卸载 / 版本 / 检查更新
            var footRow = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            footRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.Children.Add(footRow);

            var lnkUninstall = MakeLink("卸载渡口…", Palette.MutedB);
            lnkUninstall.Click += delegate
            {
                if (_onUninstall == null) return;
                DialogResult = false;
                Close();
                _onUninstall();
            };
            Grid.SetColumn(lnkUninstall, 0);
            footRow.Children.Add(lnkUninstall);

            _lblVer = new TextBlock
            {
                Text = "当前版本 " + typeof(SettingsWindow).Assembly.GetName().Version.ToString(3),
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_lblVer, 1);
            footRow.Children.Add(_lblVer);

            var lnkCheck = MakeLink("检查更新", Palette.AccentB);
            lnkCheck.Click += delegate { CheckUpdateManual(); };
            Grid.SetColumn(lnkCheck, 2);
            footRow.Children.Add(lnkCheck);

            // 底部按钮行：意见反馈（左）+ 保存/取消（右）
            var btnRow = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.Children.Add(btnRow);

            _fbBtn = MakeButton("💬 意见反馈", false);
            _fbBtn.Padding = new Thickness(18, 8, 18, 8);
            _fbBtn.Click += delegate { OpenFeedback(); };
            Grid.SetColumn(_fbBtn, 0);
            btnRow.Children.Add(_fbBtn);

            var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(rightBtns, 2);
            btnRow.Children.Add(rightBtns);

            _ok = MakeButton("保存", true);
            _ok.Padding = new Thickness(28, 8, 28, 8);
            _ok.Click += delegate { SaveAll(); };
            rightBtns.Children.Add(_ok);

            _cancel = MakeButton("取消", false);
            _cancel.Padding = new Thickness(28, 8, 28, 8);
            _cancel.Margin = new Thickness(10, 0, 0, 0);
            _cancel.Click += delegate { DialogResult = false; Close(); };
            rightBtns.Children.Add(_cancel);
        }

        /// <summary>
        /// 一个「勾选框 + 说明」。用 StackPanel 竖向排列 ——
        /// 说明文字自动换行、自动撑高，勾选框不会压住它。
        /// </summary>
        static CheckBox MakeCheck(Panel parent, string title, string hint)
        {
            var wrap = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            parent.Children.Add(wrap);

            var c = new CheckBox
            {
                Content = title,
                FontSize = Palette.FsBody,
                Foreground = Palette.TextB,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            wrap.Children.Add(c);

            wrap.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 3, 0, 0)
            });

            return c;
        }

        static Button MakeButton(string text, bool primary)
        {
            return new Button
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
        }

        /// <summary>看起来像链接的按钮（WPF 没有 WinForms 的 LinkLabel）。</summary>
        static Button MakeLink(string text, Brush color)
        {
            return new Button
            {
                Content = text,
                FontSize = Palette.FsSmall,
                Foreground = color,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
        }

        // ── 保存 ──

        void SaveAll()
        {
            _cfg.AutoBorrow = _chkAutoBorrow.IsChecked == true;
            _cfg.StartMinimized = _chkMinimized.IsChecked == true;
            _cfg.AutoReconnect = _chkReconnect.IsChecked == true;
            _cfg.NetCheckSec = _numInterval.Value;

            try { _cfg.Save(_cfgPath); }
            catch (Exception e)
            {
                MessageBox.Show(this, "保存配置失败：" + e.Message, "设置");
                return;
            }

            SetAutoStart(_chkAutoStart.IsChecked == true);
            DialogResult = true;
            Close();
        }

        // ── 开机自启（计划任务）──

        public static void SetAutoStart(bool on)
        {
            try
            {
                var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                var lnk = Path.Combine(startup, "渡口自动启动.lnk");
                if (File.Exists(lnk)) { try { File.Delete(lnk); } catch (Exception) { } }

                if (!on)
                {
                    RunHidden("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F");
                    return;
                }

                var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "渡口助手.exe");
                if (!File.Exists(exe)) return;

                var rl = Elevator.IsAdmin() ? " /RL HIGHEST" : "";
                var args = "/Create /TN \"" + TaskName + "\" /TR \"\\\"" + exe + "\\\"\" " +
                           "/SC ONLOGON" + rl + " /F";
                var code = RunHidden("schtasks.exe", args);

                if (code != 0 && !Elevator.IsAdmin())
                {
                    // 普通用户建不了计划任务 —— 退回启动文件夹（asInvoker 后不弹 UAC）
                    try
                    {
                        var t = Type.GetTypeFromProgID("WScript.Shell");
                        if (t != null)
                        {
                            dynamic sh = Activator.CreateInstance(t);
                            dynamic sc = sh.CreateShortcut(lnk);
                            sc.TargetPath = exe;
                            sc.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                            sc.Description = "渡口 · 开机自动就绪";
                            sc.WindowStyle = 7;
                            sc.Save();
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        static bool AutoStartOn()
        {
            try
            {
                var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                if (File.Exists(Path.Combine(startup, "渡口自动启动.lnk"))) return true;
                return RunHidden("schtasks.exe", "/Query /TN \"" + TaskName + "\"") == 0;
            }
            catch (Exception) { return false; }
        }

        static int RunHidden(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    return p.HasExited ? p.ExitCode : -1;
                }
            }
            catch (Exception) { return -1; }
        }

        void CheckUpdateManual()
        {
            _lblVer.Text = "正在检查…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                var v = Api.Version(_cfg.PortalUrl);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    var mine = typeof(SettingsWindow).Assembly.GetName().Version.ToString(3);
                    _lblVer.Text = "当前版本 " + mine;
                    if (!v.Ok) { MessageBox.Show(this, "拿不到版本信息", "更新"); return; }
                    if (Api.CompareVer(v.Latest, mine) <= 0)
                    {
                        MessageBox.Show(this, "已经是最新版本（" + mine + "）", "检查更新");
                        return;
                    }
                    var r = MessageBox.Show(this,
                        "发现新版本 " + v.Latest + "（当前 " + mine + "）\r\n\r\n现在更新吗？",
                        "有可用更新", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (r == MessageBoxResult.Yes)
                    {
                        MessageBox.Show(this,
                            "请在主界面点「更新 " + v.Latest + "」按钮完成更新。",
                            "更新");
                    }
                }));
            });
        }

        // ── 意见反馈 ──

        void OpenFeedback()
        {
            var dlg = new FeedbackWindow(_cfg) { Owner = this };
            dlg.ShowDialog();
        }
    }

    /// <summary>
    /// WPF 没有 NumericUpDown —— 用 TextBox + 两个小按钮凑一个。
    /// 比引 WinForms 的 NumericUpDown 干净（那个在 WPF 里要放 WindowsFormsHost，
    /// 而且 DPI 缩放又是一套麻烦）。
    /// </summary>
    internal class NumericUpDownAdapter
    {
        public TextBox Box { get; private set; }
        readonly int _min, _max, _step;

        public NumericUpDownAdapter(int value, int min, int max, int step, double width)
        {
            _min = min; _max = max; _step = step;

            Box = new TextBox
            {
                Text = value.ToString(),
                Width = width,
                FontFamily = new FontFamily(Palette.FontMono),
                FontSize = Palette.FsBody,
                Padding = new Thickness(8, 5, 8, 5),
                BorderBrush = Palette.Line2B,
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            Box.PreviewTextInput += delegate (object s, System.Windows.Input.TextCompositionEventArgs e)
            {
                int dummy;
                e.Handled = !int.TryParse(e.Text, out dummy);
            };
            Box.LostFocus += delegate { Normalize(); };
        }

        public int Value
        {
            get
            {
                int v;
                if (!int.TryParse(Box.Text, out v)) v = _min;
                if (v < _min) v = _min;
                if (v > _max) v = _max;
                return v;
            }
            set { Box.Text = Math.Max(_min, Math.Min(_max, value)).ToString(); }
        }

        void Normalize() { Value = Value; }
    }
}

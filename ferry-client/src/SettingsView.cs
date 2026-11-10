using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ferry
{
    /// <summary>
    /// 设置面板。1.2.0 起从「独立弹窗」抽出来，主界面的「设置」页直接嵌这一份，
    /// 弹窗只是它的一层壳 —— 否则同一个设置项有两套控件，改一处忘一处。
    ///
    /// 设置项完全沿用渡口原来的（没有照搬任何第三方启动器）：
    ///   开机自启 / 断网自动借网 / 启动最小化 / 断线重连 / 断网检测间隔
    ///   以及卸载、检查更新、意见反馈这三个维护入口。
    /// </summary>
    internal class SettingsView
    {
        readonly Cfg _cfg;
        readonly string _cfgPath;
        readonly string _appDir;
        readonly Action _onUninstall;
        readonly Action<string> _onUpdate;

        CheckBox _chkAutoStart, _chkAutoBorrow, _chkMinimized, _chkReconnect;
        NumericUpDownAdapter _numInterval;
        TextBlock _lblVer;

        const string TaskName = "FerryClientAutostart";

        public SettingsView(Cfg cfg, string cfgPath, string appDir,
                            Action onUninstall, Action<string> onUpdate)
        {
            _cfg = cfg;
            _cfgPath = cfgPath;
            _appDir = appDir;
            _onUninstall = onUninstall;
            _onUpdate = onUpdate;
        }

        public FrameworkElement Build()
        {
            var root = new StackPanel();

            root.Children.Add(Ui.Section("启动与自动化",
                "改动点「保存」后生效，不需要重启"));

            var card = Ui.Card();
            root.Children.Add(card);
            var stack = new StackPanel();
            card.Child = stack;

            _chkAutoStart = Ui.Check(stack, "开机自动启动",
                "登录 Windows 后自动就绪（收在托盘，不弹窗）。用计划任务实现，不会弹 UAC");
            _chkAutoStart.IsChecked = AutoStartOn();

            _chkAutoBorrow = Ui.Check(stack, "断网时自动借网",
                "检测到本机连不上网时，自动开隧道并切换系统代理");
            _chkAutoBorrow.IsChecked = _cfg.AutoBorrow;

            _chkMinimized = Ui.Check(stack, "启动后最小化到托盘",
                "双击图标时不显示主窗口，直接驻留托盘");
            _chkMinimized.IsChecked = _cfg.StartMinimized;

            _chkReconnect = Ui.Check(stack, "隧道断开时自动重连",
                "远端重启、网络中抖时自动恢复（退避 5 秒起，最长 60 秒）");
            _chkReconnect.IsChecked = _cfg.AutoReconnect;

            // ── 断网检测间隔 ──
            var ivRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0)
            };
            stack.Children.Add(ivRow);

            ivRow.Children.Add(new TextBlock
            {
                Text = "断网检测间隔",
                FontSize = Palette.FsBody,
                Foreground = Palette.TextB,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            });

            _numInterval = new NumericUpDownAdapter(_cfg.NetCheckSec, 10, 300, 5, 74);
            ivRow.Children.Add(_numInterval.Box);
            ivRow.Children.Add(new TextBlock
            {
                Text = "秒",
                FontSize = Palette.FsSmall,
                Foreground = Palette.MutedB,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });

            var catBtn = Ui.Link("Clash 说明", Palette.MutedB);
            catBtn.FontSize = Palette.FsTiny;
            catBtn.Margin = new Thickness(18, 0, 0, 0);
            catBtn.VerticalAlignment = VerticalAlignment.Center;
            catBtn.Click += delegate
            {
                // 只显示说明，**不改任何文件**：每个用户的 Clash 客户端不一样，
                // 写别人的配置是无底洞，写错了还会毁掉人家的规则。
                MessageBox.Show(ClashMerge.Help(_cfg.SocksPort), "Clash 说明",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            };
            ivRow.Children.Add(catBtn);

            stack.Children.Add(new TextBlock
            {
                Text = "间隔越短越灵敏，但每次检测要发一次 DNS 查询；断网后大约一个间隔内会自动开始借网。",
                FontSize = Palette.FsTiny,
                Foreground = Palette.MutedB,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            });

            // ── 维护 ──
            root.Children.Add(Ui.Section("维护", 22));

            var mcard = Ui.Card();
            root.Children.Add(mcard);
            var mstack = new StackPanel();
            mcard.Child = mstack;

            var verRow = new Grid();
            verRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            verRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            mstack.Children.Add(verRow);

            _lblVer = new TextBlock
            {
                Text = "当前版本 " + typeof(SettingsView).Assembly.GetName().Version.ToString(3),
                FontSize = Palette.FsBody,
                Foreground = Palette.TextB,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_lblVer, 0);
            verRow.Children.Add(_lblVer);

            var check = Ui.Link("检查更新", Palette.AccentB);
            check.Click += delegate { CheckUpdateManual(); };
            Grid.SetColumn(check, 1);
            verRow.Children.Add(check);

            var ops = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 14, 0, 0)
            };
            mstack.Children.Add(ops);

            var fb = Ui.Secondary("意见反馈");
            fb.Click += delegate { OpenFeedback(); };
            ops.Children.Add(fb);

            var un = Ui.Link("卸载渡口…", Palette.MutedB);
            un.Margin = new Thickness(18, 0, 0, 0);
            un.VerticalAlignment = VerticalAlignment.Center;
            un.Click += delegate
            {
                if (_onUninstall != null) _onUninstall();
            };
            ops.Children.Add(un);

            return root;
        }

        /// <summary>把界面选项写进配置并落盘。返回 false 表示写盘失败。</summary>
        public bool Apply()
        {
            _cfg.AutoBorrow = _chkAutoBorrow.IsChecked == true;
            _cfg.StartMinimized = _chkMinimized.IsChecked == true;
            _cfg.AutoReconnect = _chkReconnect.IsChecked == true;
            _cfg.NetCheckSec = _numInterval.Value;

            try { _cfg.Save(_cfgPath); }
            catch (Exception e)
            {
                MessageBox.Show("保存配置失败：" + e.Message, "设置");
                return false;
            }

            SetAutoStart(_chkAutoStart.IsChecked == true);
            return true;
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
            var mine = typeof(SettingsView).Assembly.GetName().Version.ToString(3);
            _lblVer.Text = "正在检查…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                var v = Api.Version(_cfg.PortalUrl);
                _lblVer.Dispatcher.BeginInvoke(new Action(delegate
                {
                    _lblVer.Text = "当前版本 " + mine;
                    if (!v.Ok)
                    {
                        MessageBox.Show(
                            "连不上门户（" + _cfg.PortalUrl + "）。\r\n\r\n" +
                            "如果正在借网，先断开再试；也可能是门户临时不可用。",
                            "检查更新");
                        return;
                    }
                    if (Api.CompareVer(v.Latest, mine) <= 0)
                    {
                        MessageBox.Show("已经是最新版本（" + mine + "）", "检查更新");
                        return;
                    }
                    var r = MessageBox.Show(
                        "发现新版本 " + v.Latest + "（当前 " + mine + "）\r\n\r\n现在更新吗？",
                        "有可用更新", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (r != MessageBoxResult.Yes) return;

                    if (_onUpdate == null)
                    {
                        MessageBox.Show("请回主界面点「更新 " + v.Latest + "」按钮。", "更新");
                        return;
                    }

                    // 更新前先落盘 —— 更新会让进程退出，不保存的话刚改的选项就丢了
                    Apply();
                    _onUpdate(v.Latest);
                }));
            });
        }

        void OpenFeedback()
        {
            var dlg = new FeedbackWindow(_cfg);
            dlg.ShowDialog();
        }
    }
}

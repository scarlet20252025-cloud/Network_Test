using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Ferry
{
    /// <summary>
    /// 设置面板。
    ///
    /// 为什么用独立对话框而不是内嵌页：主窗口已经 526×777，再塞一页会挤；
    /// 而且设置是低频操作，弹窗关掉就完事，不需要在主界面占位置。
    ///
    /// 「开机自启」这个开关**不存 config.json**，而是实时读写 Startup 目录的快捷方式。
    /// 理由：安装程序（Inno 的 [Tasks] autostart）也会建同一个快捷方式，
    /// 如果两边各记一个状态，必然出现「软件里显示没勾、实际开机却启动了」。
    /// 以文件系统为准，只有一个真相。
    /// </summary>
    class SettingsDialog : Form
    {
        readonly Cfg _cfg;
        readonly string _cfgPath;
        readonly string _appDir;
        readonly Action _onUninstall;

        CheckBox _chkAutoStart;
        CheckBox _chkAutoBorrow;
        CheckBox _chkMinimized;
        CheckBox _chkReconnect;
        CheckBox _chkTun;
        NumericUpDown _numInterval;
        Label _lblHint, _lblVer;

        public SettingsDialog(Cfg cfg, string cfgPath, string appDir)
            : this(cfg, cfgPath, appDir, null) { }

        public SettingsDialog(Cfg cfg, string cfgPath, string appDir, Action onUninstall)
        {
            _cfg = cfg;
            _cfgPath = cfgPath;
            _appDir = appDir;
            _onUninstall = onUninstall;
            BuildUi();
        }

        static string StartupDir()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        }

        static string StartupLnk()
        {
            return Path.Combine(StartupDir(), "渡口自动启动.lnk");
        }

        // 计划任务名。为什么用计划任务而不是启动文件夹快捷方式：
        //
        // 客户端的 app.manifest 声明了 requireAdministrator（TUN 要建网卡）。
        // 如果自启还是往启动文件夹放 .lnk，每次开机都会弹一个 UAC 窗口 ——
        // 用户必须点「是」才能用，这跟「便捷」完全相反，而且离开电脑就借不了网。
        //
        // 计划任务勾「以最高权限运行」后，由 Task Scheduler 直接以提权令牌启动，
        // **不弹 UAC**。前提是这个任务本身是由管理员创建的 —— 安装/首次保存设置
        // 时客户端已经是管理员（manifest 保证），所以能建。
        const string TaskName = "FerryAutoStart";

        /// <summary>开机自启是不是已经开着 —— 计划任务或旧的启动文件夹快捷方式，任一存在即算。</summary>
        public static bool AutoStartOn()
        {
            try
            {
                if (File.Exists(StartupLnk())) return true;
                return TaskExists();
            }
            catch (Exception) { return false; }
        }

        static bool TaskExists()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/Query /TN \"" + TaskName + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(8000);
                    return p.ExitCode == 0;
                }
            }
            catch (Exception) { return false; }
        }

        public static void SetAutoStart(bool on)
        {
            try
            {
                // 无论开还是关，先把旧的启动文件夹快捷方式清掉 ——
                // 那是弹 UAC 的元凶，留着就会和计划任务打架。
                var lnk = StartupLnk();
                if (File.Exists(lnk)) { try { File.Delete(lnk); } catch (Exception) { } }

                if (!on)
                {
                    RunHidden("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F");
                    return;
                }

                var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "渡口助手.exe");
                if (!File.Exists(exe)) return;

                // /RL HIGHEST = 以最高权限运行（这就是免 UAC 的关键）
                // /SC ONLOGON = 登录时触发
                // /F = 已存在就覆盖，保证重复保存设置不会失败
                var args = "/Create /TN \"" + TaskName + "\" /TR \"\\\"" + exe + "\\\"\" " +
                           "/SC ONLOGON /RL HIGHEST /F";
                var code = RunHidden("schtasks.exe", args);

                if (code != 0)
                {
                    // 计划任务建失败（极少见：组策略禁用了任务计划）。
                    // 退回到启动文件夹 —— 会弹 UAC，但至少能用。
                    // 这种情况必须告诉用户，不能静默降级。
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
                    MessageBox.Show(
                        "无法创建计划任务（可能被组策略禁用），已退回启动文件夹方式。\r\n\r\n" +
                        "区别：启动文件夹方式每次开机都会弹一次「是否允许此应用更改你的设备」，" +
                        "需要你点「是」才能就绪。\r\n\r\n" +
                        "如果不想每次点，可以在「设置 → 是否开启全局接管」里关掉 TUN，\r\n" +
                        "改用普通权限运行本程序（此时不需要管理员，也不会弹窗）。",
                        "渡口", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception) { }
        }

        /// <summary>跑一个不显示窗口的命令，返回退出码。失败返回 -1。</summary>
        static int RunHidden(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
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

        void BuildUi()
        {
            Text = "设置";
            ClientSize = new Size(470, 566);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Bg;
            Font = Theme.FBody;

            var t = Theme.Text2("设置", Theme.FTitle, Theme.Text);
            t.SetBounds(22, 16, 200, 28);
            Controls.Add(t);

            var sub = Theme.Text2("改动立即生效，不需要重启", Theme.FSmall, Theme.Muted);
            sub.SetBounds(22, 44, 400, 18);
            Controls.Add(sub);

            var card = Theme.Card(14);
            card.SetBounds(20, 74, 430, 274);
            Controls.Add(card);

            _chkAutoStart = MakeCheck("开机自动启动",
                "登录 Windows 后自动就绪（收在托盘，不弹窗）。用计划任务实现，不会弹 UAC", 16);
            _chkAutoStart.Checked = AutoStartOn();
            card.Controls.Add(_chkAutoStart);

            _chkAutoBorrow = MakeCheck("断网时自动借网",
                "检测到本机连不上网时，自动开隧道并切换系统代理", 68);
            _chkAutoBorrow.Checked = _cfg.AutoBorrow;
            card.Controls.Add(_chkAutoBorrow);

            _chkMinimized = MakeCheck("启动后最小化到托盘",
                "双击图标时不显示主窗口，直接驻留托盘", 120);
            _chkMinimized.Checked = _cfg.StartMinimized;
            card.Controls.Add(_chkMinimized);

            _chkReconnect = MakeCheck("隧道断开时自动重连",
                "远端重启、网络中抖时自动恢复（退避 5 秒起，最长 60 秒）", 172);
            _chkReconnect.Checked = _cfg.AutoReconnect;
            card.Controls.Add(_chkReconnect);

            // TUN：让不认系统代理的程序也走渡口，并且让 UDP 走低延迟链路。
            // 需要管理员权限 —— 客户端的 manifest 已声明 requireAdministrator，
            // 但用户可能是从旧快捷方式/命令行起的，所以运行时还要再判一次。
            _chkTun = MakeCheck("UDP低延迟模式（全局接管）",
                "在网络层截获全部流量，不认代理的程序也走渡口，UDP 走低延迟链路。" +
                "需要管理员权限；开启后短暂断网属正常（路由表重建）", 224);
            _chkTun.Checked = _cfg.UseTun;
            card.Controls.Add(_chkTun);

            var lblIv = Theme.Text2("断网检测间隔", Theme.FSmall, Theme.Text);
            lblIv.SetBounds(24, 368, 130, 20);
            Controls.Add(lblIv);

            _numInterval = new NumericUpDown();
            _numInterval.Minimum = 10;
            _numInterval.Maximum = 300;
            _numInterval.Increment = 5;
            _numInterval.Value = Math.Max(10, Math.Min(300, _cfg.NetCheckSec));
            _numInterval.SetBounds(166, 366, 90, 26);
            Controls.Add(_numInterval);

            var lblSec = Theme.Text2("秒", Theme.FSmall, Theme.Muted);
            lblSec.SetBounds(264, 368, 30, 20);
            Controls.Add(lblSec);

            _lblHint = Theme.Text2("间隔越短越灵敏，但每次检测要发一次 DNS 查询；" + Environment.NewLine +
                                   "断网后大约一个间隔内会自动开始借网。", Theme.FSmall, Theme.Muted);
            _lblHint.SetBounds(24, 400, 424, 34);
            Controls.Add(_lblHint);

            var ok = Theme.FlatButton("保存", true);
            ok.SetBounds(238, 512, 104, 34);
            ok.Click += delegate { SaveAll(); };
            Controls.Add(ok);

            var cancel = Theme.FlatButton("取消", false);
            cancel.SetBounds(350, 512, 104, 34);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            // 卸载放这里而不是主界面 —— 它是低频且不可逆的操作，
            // 摆在「开始借网」旁边太容易被误点。
            var lnkUninstall = new LinkLabel();
            lnkUninstall.Text = "卸载渡口…";
            lnkUninstall.Font = Theme.FSmall;
            lnkUninstall.LinkColor = Theme.Muted;
            lnkUninstall.ActiveLinkColor = Theme.Bad;
            lnkUninstall.LinkBehavior = LinkBehavior.HoverUnderline;
            lnkUninstall.SetBounds(24, 458, 110, 20);
            lnkUninstall.LinkClicked += delegate
            {
                if (_onUninstall == null) return;
                DialogResult = DialogResult.Abort;   // 让调用方知道要卸载
                Close();
                _onUninstall();
            };
            Controls.Add(lnkUninstall);

            // 主动检查更新的入口。主界面那个「更新」按钮只在**有新版本时**出现，
            // 平时看不到，用户会以为这软件没有更新功能。
            _lblVer = new Label();
            _lblVer.Text = "当前版本 " + Application.ProductVersion;
            _lblVer.Font = Theme.FSmall;
            _lblVer.ForeColor = Theme.Muted;
            _lblVer.AutoSize = true;
            _lblVer.SetBounds(142, 459, 150, 18);
            Controls.Add(_lblVer);

            var lnkCheck = new LinkLabel();
            lnkCheck.Text = "检查更新";
            lnkCheck.Font = Theme.FSmall;
            lnkCheck.LinkColor = Theme.Accent;
            lnkCheck.ActiveLinkColor = Theme.Accent;
            lnkCheck.LinkBehavior = LinkBehavior.HoverUnderline;
            lnkCheck.SetBounds(300, 458, 80, 20);
            lnkCheck.LinkClicked += delegate { CheckUpdateManual(); };
            Controls.Add(lnkCheck);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        CheckBox MakeCheck(string title, string hint, int y)
        {
            var c = new CheckBox();
            c.Text = title;
            c.Font = Theme.FBody;
            c.ForeColor = Theme.Text;
            c.SetBounds(20, y, 390, 22);
            c.BackColor = Color.Transparent;

            var h = Theme.Text2(hint, Theme.FSmall, Theme.Muted);
            h.SetBounds(40, y + 22, 370, 18);
            h.Tag = "hint:" + title;
            // 提示挂在同一个父容器上，随 checkbox 一起显示
            c.Tag = h;
            c.ParentChanged += delegate { };
            return c;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // 把每条 checkbox 的说明文字挂到同一个父容器
            foreach (var c in new[] { _chkAutoStart, _chkAutoBorrow, _chkMinimized, _chkReconnect, _chkTun })
            {
                var h = c.Tag as Label;
                if (h != null && c.Parent != null && h.Parent == null)
                {
                    var p = c.Parent;
                    // 位置相对 checkbox 的父容器
                    var pt = c.Location;
                    h.Location = new Point(pt.X + 20, pt.Y + 22);
                    p.Controls.Add(h);
                    h.BringToFront();
                }
            }
        }

        void CheckUpdateManual()
        {
            if (_cfg.PortalUrl.Length == 0)
            {
                MessageBox.Show(this, "配置里没有门户地址，无法检查更新。", "检查更新",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Cursor = Cursors.WaitCursor;
            var v = Api.Version(_cfg.PortalUrl);
            Cursor = Cursors.Default;

            if (!v.Ok)
            {
                MessageBox.Show(this, "检查失败：连不上门户，或门户没有版本信息。" + Environment.NewLine + _cfg.PortalUrl,
                    "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var mine = Application.ProductVersion;
            if (Api.CompareVer(v.Latest, mine) > 0)
            {
                var r = MessageBox.Show(this,
                    "发现新版本 " + v.Latest + "（当前 " + mine + "）。" + Environment.NewLine + Environment.NewLine +
                    "要现在下载安装吗？" + Environment.NewLine +
                    "安装会静默覆盖程序，配置和授权不受影响。",
                    "有新版本", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r == DialogResult.Yes) DoUpdateInline(v);
            }
            else
            {
                MessageBox.Show(this, "已经是最新版本（" + mine + "）。", "检查更新",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        void DoUpdateInline(VersionInfo v)
        {
            try
            {
                var url = v.Url.StartsWith("http") ? v.Url : _cfg.PortalUrl.TrimEnd('/') + v.Url;
                var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Ferry-Setup-" + v.Latest + ".exe");
                Cursor = Cursors.WaitCursor;
                var wc = new System.Net.WebClient();
                wc.Proxy = null;
                wc.DownloadFile(url, tmp);
                Cursor = Cursors.Default;
                var psi = new ProcessStartInfo(tmp, "/SILENT /NORESTART /CLOSEAPPLICATIONS");
                psi.UseShellExecute = false;
                Process.Start(psi);
                DialogResult = DialogResult.Abort;
                Close();
                Application.Exit();
            }
            catch (Exception e)
            {
                Cursor = Cursors.Default;
                MessageBox.Show(this, "下载失败：" + e.Message, "更新",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void SaveAll()
        {
            _cfg.AutoBorrow = _chkAutoBorrow.Checked;
            _cfg.StartMinimized = _chkMinimized.Checked;
            _cfg.AutoReconnect = _chkReconnect.Checked;
            _cfg.NetCheckSec = (int)_numInterval.Value;

            // TUN 勾了但没管理员权限：明确告诉用户会怎样，不要静默保存。
            // 静默保存的结果是「用户以为开了，实际每次借网都失败」。
            if (_chkTun.Checked && !TunMode.IsAdmin())
            {
                var r = MessageBox.Show(this,
                    "「UDP低延迟模式（全局接管）」需要管理员权限。\r\n\r\n" +
                    "当前不是管理员，开启后借网时 TUN 会启动失败（浏览器等认代理的程序仍能正常借网，" +
                    "但不认代理的程序和 UDP 走不了渡口）。\r\n\r\n" +
                    "建议：关闭本程序，右键「以管理员身份运行」后重新勾选。\r\n\r\n" +
                    "要现在就保存这个设置吗？",
                    "需要管理员权限", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) { return; }
            }
            _cfg.UseTun = _chkTun.Checked;

            try { _cfg.Save(_cfgPath); }
            catch (Exception e)
            {
                MessageBox.Show(this, "保存配置失败：" + e.Message, "设置",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SetAutoStart(_chkAutoStart.Checked);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}

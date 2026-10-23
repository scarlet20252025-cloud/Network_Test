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

        /// <summary>开机自启是不是已经开着 —— 以 Startup 目录里有没有那个快捷方式为准。</summary>
        public static bool AutoStartOn()
        {
            try { return File.Exists(StartupLnk()); }
            catch (Exception) { return false; }
        }

        public static void SetAutoStart(bool on)
        {
            try
            {
                var lnk = StartupLnk();
                if (!on)
                {
                    if (File.Exists(lnk)) File.Delete(lnk);
                    return;
                }
                var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "渡口助手.exe");
                if (!File.Exists(exe)) return;

                // 用 WScript.Shell 建快捷方式 —— 这是 .NET Framework 里唯一不引第三方库的写法
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                dynamic sh = Activator.CreateInstance(t);
                dynamic sc = sh.CreateShortcut(lnk);
                sc.TargetPath = exe;
                sc.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                sc.Description = "渡口 · 开机自动就绪";
                sc.WindowStyle = 7;   // 7 = 最小化启动
                sc.Save();
            }
            catch (Exception) { }
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
                "登录 Windows 后自动就绪（收在托盘，不弹窗）", 16);
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
            foreach (var c in new[] { _chkAutoStart, _chkAutoBorrow, _chkMinimized, _chkReconnect })
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

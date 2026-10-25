using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
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

        /// <summary>这次保存有没有改「UDP低延迟模式（全局接管）」。
        /// 主窗口靠它决定要不要按新设置重启借网链路。</summary>
        public bool TunToggleChanged { get; private set; }

        /// <summary>保存后需要以管理员身份重启（用户勾了 TUN 但当前不是管理员）。
        /// 重启动作交给主窗口做 —— 它才知道怎么先把借网停干净。</summary>
        public bool NeedElevate { get; private set; }

        public SettingsDialog(Cfg cfg, string cfgPath, string appDir)
            : this(cfg, cfgPath, appDir, null) { }

        public SettingsDialog(Cfg cfg, string cfgPath, string appDir, Action onUninstall)
        {
            _cfg = cfg;
            _cfgPath = cfgPath;
            _appDir = appDir;
            _onUninstall = onUninstall;
            // 同上：不声明 Dpi 缩放，高 DPI 下文字会被裁（设置页最明显，
            // 因为每个勾选项下面还有一行 8.5pt 的说明）。
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            BuildUi();
            Load += delegate { Theme.FitToScreen(this); };
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

                // /SC ONLOGON = 登录时触发；/F = 已存在就覆盖。
                //
                // /RL HIGHEST 只在当前进程已经是管理员时才加：
                // 普通用户建「最高权限」任务会被拒绝访问（schtasks 返回非 0）。
                // 0.9.6 起客户端是 asInvoker，纯代理模式普通权限就够。
                var rl = Elevator.IsAdmin() ? " /RL HIGHEST" : "";
                var args = "/Create /TN \"" + TaskName + "\" /TR \"\\\"" + exe + "\\\"\" " +
                           "/SC ONLOGON" + rl + " /F";
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
            ClientSize = new Size(470, 666);
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
            card.SetBounds(20, 74, 430, 374);
            Controls.Add(card);

            _chkAutoStart = MakeCheck("开机自动启动",
                "登录 Windows 后自动就绪（收在托盘，不弹窗）。用计划任务实现，不会弹 UAC", 16);
            _chkAutoStart.Checked = AutoStartOn();
            card.Controls.Add(_chkAutoStart);

            _chkAutoBorrow = MakeCheck("断网时自动借网",
                "检测到本机连不上网时，自动开隧道并切换系统代理", 82);
            _chkAutoBorrow.Checked = _cfg.AutoBorrow;
            card.Controls.Add(_chkAutoBorrow);

            _chkMinimized = MakeCheck("启动后最小化到托盘",
                "双击图标时不显示主窗口，直接驻留托盘", 148);
            _chkMinimized.Checked = _cfg.StartMinimized;
            card.Controls.Add(_chkMinimized);

            _chkReconnect = MakeCheck("隧道断开时自动重连",
                "远端重启、网络中抖时自动恢复（退避 5 秒起，最长 60 秒）", 214);
            _chkReconnect.Checked = _cfg.AutoReconnect;
            card.Controls.Add(_chkReconnect);

            // 意见反馈入口。
            // 放在设置里而不是主界面：反馈是低频操作，主界面要留给
            // 「借网/停止」这个高频动作。但入口必须好找 ——
            // 所以做成一个带说明的按钮，而不是藏在二级菜单里。
            // 意见反馈按钮放在窗口左下角，和「保存 / 取消」同一行。
            //
            // 它一开始是塞在开关卡片里的（y=224），结果卡片被切成两半、
            // 还和「UDP低延迟模式」那一项重叠过一次。它本来就是低频入口，
            // 占着卡片中间只会把四个开关挤开 —— 挪到左下角，一眼能找到，
            // 又不打断「开关 → 时长 → 保存」这条阅读顺序。
            var fbBtn = new Button();
            fbBtn.Text = "💬 意见反馈";
            fbBtn.SetBounds(20, 612, 180, 34);
            fbBtn.FlatStyle = FlatStyle.Flat;
            fbBtn.FlatAppearance.BorderColor = Theme.Line;
            fbBtn.FlatAppearance.BorderSize = 1;
            fbBtn.BackColor = Theme.CardBg;
            fbBtn.ForeColor = Theme.Text;
            fbBtn.Font = Theme.FBody;
            fbBtn.TextAlign = ContentAlignment.MiddleCenter;
            fbBtn.Cursor = Cursors.Hand;
            fbBtn.Click += delegate { OpenFeedback(); };
            Controls.Add(fbBtn);

            // TUN：让不认系统代理的程序也走渡口，并且让 UDP 走低延迟链路。
            // 需要管理员权限 —— 客户端的 manifest 已声明 requireAdministrator，
            // 但用户可能是从旧快捷方式/命令行起的，所以运行时还要再判一次。
            _chkTun = MakeCheck("UDP低延迟模式（全局接管）",
                "网络层接管全部流量（含不认代理的程序），UDP 走低延迟链路。" +
                "需管理员权限；开启后短暂断网属正常", 280);
            _chkTun.Checked = _cfg.UseTun;
            card.Controls.Add(_chkTun);

            // 不是管理员时给一条直达路径 —— 让用户自己「右键 → 以管理员身份运行」
            // 不现实：多数人想不到右键，更想不到得先把程序关掉。
            var lnkElev = new LinkLabel();
            lnkElev.Text = "以管理员身份重启程序…";
            lnkElev.Font = Theme.FSmall;
            lnkElev.LinkColor = Theme.Muted;
            lnkElev.ActiveLinkColor = Theme.Accent;
            lnkElev.LinkBehavior = LinkBehavior.HoverUnderline;
            lnkElev.SetBounds(40, 340, 220, 18);
            lnkElev.Visible = !Elevator.IsAdmin();
            lnkElev.LinkClicked += delegate
            {
                _chkTun.Checked = true;
                SaveAll();
            };
            card.Controls.Add(lnkElev);

            var lblIv = Theme.Text2("断网检测间隔", Theme.FSmall, Theme.Text);
            lblIv.SetBounds(24, 468, 130, 20);
            Controls.Add(lblIv);

            _numInterval = new NumericUpDown();
            _numInterval.Minimum = 10;
            _numInterval.Maximum = 300;
            _numInterval.Increment = 5;
            _numInterval.Value = Math.Max(10, Math.Min(300, _cfg.NetCheckSec));
            _numInterval.SetBounds(166, 466, 90, 26);
            Controls.Add(_numInterval);

            var lblSec = Theme.Text2("秒", Theme.FSmall, Theme.Muted);
            lblSec.SetBounds(264, 468, 30, 20);
            Controls.Add(lblSec);

            _lblHint = Theme.Text2("间隔越短越灵敏，但每次检测要发一次 DNS 查询；" + Environment.NewLine +
                                   "断网后大约一个间隔内会自动开始借网。", Theme.FSmall, Theme.Muted);
            _lblHint.SetBounds(24, 500, 424, 34);
            Controls.Add(_lblHint);

            var ok = Theme.FlatButton("保存", true);
            ok.SetBounds(238, 612, 104, 34);
            ok.Click += delegate { SaveAll(); };
            Controls.Add(ok);

            var cancel = Theme.FlatButton("取消", false);
            cancel.SetBounds(350, 612, 104, 34);
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
            lnkUninstall.SetBounds(24, 558, 110, 20);
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
            _lblVer.SetBounds(142, 559, 150, 18);
            Controls.Add(_lblVer);

            var lnkCheck = new LinkLabel();
            lnkCheck.Text = "检查更新";
            lnkCheck.Font = Theme.FSmall;
            lnkCheck.LinkColor = Theme.Accent;
            lnkCheck.ActiveLinkColor = Theme.Accent;
            lnkCheck.LinkBehavior = LinkBehavior.HoverUnderline;
            lnkCheck.SetBounds(300, 558, 80, 20);
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
            h.SetBounds(40, y + 22, 384, 34);
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

        /// <summary>
        /// 打开意见反馈窗体。
        ///
        /// 做成模态子窗体而不是弹 MessageBox：要填的内容有三项
        /// （类型 / 联系方式 / 描述），MessageBox 装不下。
        /// </summary>
        void OpenFeedback()
        {
            using (var f = new Form())
            {
                f.Text = "意见反馈";
                f.ClientSize = new Size(460, 430);
                f.StartPosition = FormStartPosition.CenterParent;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.BackColor = Theme.Bg;
                f.Font = Theme.FBody;
                f.AutoScaleMode = AutoScaleMode.Dpi;
                f.AutoScaleDimensions = new SizeF(96F, 96F);

                var title = Theme.Text2("遇到问题，或者有想让它做到的事？", Theme.FBody, Theme.Text);
                title.SetBounds(20, 16, 420, 24);
                f.Controls.Add(title);

                var sub = new Label();
                sub.Text = "每一条都会被读到。能修的会修，暂时做不到的也会告诉你为什么。";
                sub.SetBounds(20, 44, 420, 20);
                sub.ForeColor = Theme.Muted;
                sub.Font = Theme.FSmall;
                f.Controls.Add(sub);

                var l1 = new Label();
                l1.Text = "反馈类型";
                l1.SetBounds(20, 82, 420, 18);
                l1.ForeColor = Theme.Muted;
                l1.Font = Theme.FSmall;
                f.Controls.Add(l1);

                var cmb = new ComboBox();
                cmb.DropDownStyle = ComboBoxStyle.DropDownList;
                cmb.Items.AddRange(new object[] { "出了问题 / 报错", "想要新功能", "太慢 / 卡顿", "使用方法咨询", "其他" });
                cmb.SelectedIndex = 0;
                cmb.SetBounds(20, 104, 420, 26);
                f.Controls.Add(cmb);

                var l2 = new Label();
                l2.Text = "联系方式（选填，方便回复你）";
                l2.SetBounds(20, 140, 420, 18);
                l2.ForeColor = Theme.Muted;
                l2.Font = Theme.FSmall;
                f.Controls.Add(l2);

                var txtContact = new TextBox();
                txtContact.SetBounds(20, 162, 420, 26);
                f.Controls.Add(txtContact);

                var l3 = new Label();
                l3.Text = "详细描述";
                l3.SetBounds(20, 198, 420, 18);
                l3.ForeColor = Theme.Muted;
                l3.Font = Theme.FSmall;
                f.Controls.Add(l3);

                var txt = new TextBox();
                txt.Multiline = true;
                txt.ScrollBars = ScrollBars.Vertical;
                txt.SetBounds(20, 220, 420, 120);
                f.Controls.Add(txt);

                var msg = new Label();
                msg.SetBounds(20, 348, 420, 20);
                msg.ForeColor = Theme.Muted;
                msg.Font = Theme.FSmall;
                f.Controls.Add(msg);

                var btnSend = new Button();
                btnSend.Text = "提交反馈";
                btnSend.SetBounds(20, 376, 200, 36);
                btnSend.FlatStyle = FlatStyle.Flat;
                btnSend.FlatAppearance.BorderSize = 0;
                btnSend.BackColor = Theme.Accent;
                btnSend.ForeColor = Color.White;
                btnSend.Font = Theme.FBody;
                btnSend.Cursor = Cursors.Hand;
                f.Controls.Add(btnSend);

                var btnCancel = new Button();
                btnCancel.Text = "取消";
                btnCancel.SetBounds(240, 376, 200, 36);
                btnCancel.FlatStyle = FlatStyle.Flat;
                btnCancel.FlatAppearance.BorderColor = Theme.Line;
                btnCancel.FlatAppearance.BorderSize = 1;
                btnCancel.BackColor = Theme.CardBg;
                btnCancel.ForeColor = Theme.Text;
                btnCancel.Font = Theme.FBody;
                btnCancel.Cursor = Cursors.Hand;
                btnCancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(btnCancel);
                f.CancelButton = btnCancel;

                // 提交。放线程池里，避免门户不通时界面卡住。
                btnSend.Click += delegate
                {
                    string kind = "other";
                    switch (cmb.SelectedIndex)
                    {
                        case 0: kind = "bug"; break;
                        case 1: kind = "feature"; break;
                        case 2: kind = "perf"; break;
                        case 3: kind = "ask"; break;
                    }
                    string body = txt.Text;
                    string contact = txtContact.Text;

                    if (body.Trim().Length < 4)
                    {
                        msg.ForeColor = Theme.Bad;
                        msg.Text = "描述太短了，至少写 4 个字";
                        return;
                    }

                    btnSend.Enabled = false;
                    btnSend.Text = "提交中…";
                    msg.ForeColor = Theme.Muted;
                    msg.Text = "";

                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        string err = "";
                        try { err = Api.Feedback(_cfg, kind, body, contact); }
                        catch (Exception ex) { err = "提交失败：" + ex.Message; }

                        try
                        {
                            f.BeginInvoke(new Action(delegate
                            {
                                btnSend.Enabled = true;
                                btnSend.Text = "提交反馈";
                                if (err.Length == 0)
                                {
                                    msg.ForeColor = Theme.Good;
                                    msg.Text = "已收到，谢谢反馈";
                                    var t = new System.Windows.Forms.Timer();
                                    t.Interval = 1200;
                                    t.Tick += delegate { t.Stop(); try { f.Close(); } catch (Exception) { } };
                                    t.Start();
                                }
                                else
                                {
                                    msg.ForeColor = Theme.Bad;
                                    msg.Text = err;
                                }
                            }));
                        }
                        catch (Exception) { }
                    });
                };

                f.ShowDialog(this);
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
            if (_chkTun.Checked && !Elevator.IsAdmin())
            {
                var r = MessageBox.Show(this,
                    "「UDP低延迟模式（全局接管）」要创建虚拟网卡、接管 UDP，必须管理员权限。\r\n\r\n" +
                    "保存后会以管理员身份重启一次（弹 UAC，点「是」）。\r\n" +
                    "暂时不需要也可以选「否」：设置照样保存，只是这一项不生效 —— " +
                    "浏览器、下载工具这些认代理的程序仍然能借网。\r\n\r\n" +
                    "现在保存并以管理员重启吗？",
                    "需要管理员权限", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                NeedElevate = (r == DialogResult.Yes);
            }
            TunToggleChanged = _cfg.UseTun != _chkTun.Checked;
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

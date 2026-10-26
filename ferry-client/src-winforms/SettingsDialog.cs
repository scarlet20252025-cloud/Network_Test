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

        /// <summary>高 DPI 下开关卡片比设计高度高出的像素数（下方控件要跟着下移）。</summary>
        int _cardGrow;

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
            // 同上：不声明 Dpi 缩放，高 DPI 下文字会被裁（设置页最明显，
            // 因为每个勾选项下面还有一行 8.5pt 的说明）。
            AutoScaleMode = AutoScaleMode.None;
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

                // 计划任务只在**已经是管理员**时才尝试。
                //
                // 为什么：普通用户建 /SC ONLOGON 的任务会被拒绝（要「作为批处理
                // 作业登录」之类的权限，实测 schtasks 直接返回非 0），
                // 而 0.9.6 起客户端是 asInvoker —— 启动文件夹里的快捷方式
                // **不会再弹 UAC**，反而比计划任务更适合普通用户。
                var code = 1;
                if (Elevator.IsAdmin())
                {
                    var args = "/Create /TN \"" + TaskName + "\" /TR \"\\\"" + exe + "\\\"\" " +
                               "/SC ONLOGON /RL HIGHEST /F";
                    code = RunHidden("schtasks.exe", args);
                }

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
                    // 普通用户走启动文件夹是**预期路径**，不是故障，不该弹框打扰；
                    // 只有「已经是管理员却建任务失败」（组策略禁用任务计划）才提示一次。
                    if (Elevator.IsAdmin())
                    {
                        MessageBox.Show(
                            "无法创建计划任务（可能被组策略禁用），已退回启动文件夹方式。\r\n\r\n" +
                            "开机时可能会弹一次 UAC。",
                            "渡口", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
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
            ClientSize = new Size(470, 586);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Bg;
            Font = Theme.FBody;

            var t = Theme.Text2("设置", Theme.FTitle, Theme.Text);
            Theme.Place(t, 22, 16, 200);
            Controls.Add(t);

            var sub = Theme.Text2("改动立即生效，不需要重启", Theme.FSmall, Theme.Muted);
            Theme.Place(sub, 22, 44, 400);
            Controls.Add(sub);

            var card = Theme.Card(14);
            card.SetBounds(20, 74, 430, 294);
            Controls.Add(card);

            // y 坐标**不再写死**，由前一项的实际高度累加。
            //
            // 踩过的坑：原来固定 16/82/148/214（每项 66px），在 125% 缩放下
            // 标题和说明都变高，相邻两项就叠在一起 —— 表现为「勾选框的标题
            // 被下一项的说明压住一半」。
            int cy = 16;
            _chkAutoStart = MakeCheck("开机自动启动",
                "登录 Windows 后自动就绪（收在托盘，不弹窗）。用计划任务实现，不会弹 UAC", cy);
            _chkAutoStart.Checked = AutoStartOn();
            card.Controls.Add(_chkAutoStart);
            cy += CheckBlockHeight(_chkAutoStart, "登录 Windows 后自动就绪（收在托盘，不弹窗）。用计划任务实现，不会弹 UAC");

            _chkAutoBorrow = MakeCheck("断网时自动借网",
                "检测到本机连不上网时，自动开隧道并切换系统代理", cy);
            _chkAutoBorrow.Checked = _cfg.AutoBorrow;
            card.Controls.Add(_chkAutoBorrow);
            cy += CheckBlockHeight(_chkAutoBorrow, "检测到本机连不上网时，自动开隧道并切换系统代理");

            _chkMinimized = MakeCheck("启动后最小化到托盘",
                "双击图标时不显示主窗口，直接驻留托盘", cy);
            _chkMinimized.Checked = _cfg.StartMinimized;
            card.Controls.Add(_chkMinimized);
            cy += CheckBlockHeight(_chkMinimized, "双击图标时不显示主窗口，直接驻留托盘");

            _chkReconnect = MakeCheck("隧道断开时自动重连",
                "远端重启、网络中抖时自动恢复（退避 5 秒起，最长 60 秒）", cy);
            _chkReconnect.Checked = _cfg.AutoReconnect;
            card.Controls.Add(_chkReconnect);
            cy += CheckBlockHeight(_chkReconnect, "远端重启、网络中抖时自动恢复（退避 5 秒起，最长 60 秒）");

            // 卡片高度跟着内容走。下方控件在 BuildUi 末尾统一按增量下移
            // （那时它们才创建好，见 RelayoutBelowCard）。
            _cardGrow = Math.Max(0, (cy + 10) - 294);
            card.Height = 294 + _cardGrow;

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
            fbBtn.SetBounds(20, 532, 180, 34);
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
            // 「全局模式」在主界面状态卡里（「开启 / 关闭」按钮）。
            // 这里**不再放第二个开关** —— 两处状态一定会不同步：
            // 配置里 useTun=true 但进程没提权时，勾子是勾着的、TUN 却没起来，
            // 用户不知道该信哪个，只能先取消再勾一次才触发。

            var lblIv = Theme.Text2("断网检测间隔", Theme.FSmall, Theme.Text);
            Theme.Place(lblIv, 24, 388, 130);
            Controls.Add(lblIv);

            _numInterval = new NumericUpDown();
            _numInterval.Minimum = 10;
            _numInterval.Maximum = 300;
            _numInterval.Increment = 5;
            _numInterval.Value = Math.Max(10, Math.Min(300, _cfg.NetCheckSec));
            _numInterval.SetBounds(166, 386, 90, 26);
            Controls.Add(_numInterval);

            var lblSec = Theme.Text2("秒", Theme.FSmall, Theme.Muted);
            Theme.Place(lblSec, 264, 388, 30);
            Controls.Add(lblSec);

            _lblHint = Theme.Text2("间隔越短越灵敏，但每次检测要发一次 DNS 查询；" + Environment.NewLine +
                                   "断网后大约一个间隔内会自动开始借网。", Theme.FSmall, Theme.Muted);
            Theme.Place(_lblHint, 24, 420, 424);
            Controls.Add(_lblHint);

            var ok = Theme.FlatButton("保存", true);
            ok.SetBounds(238, 532, 104, 34);
            ok.Click += delegate { SaveAll(); };
            Controls.Add(ok);

            var cancel = Theme.FlatButton("取消", false);
            cancel.SetBounds(350, 532, 104, 34);
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
            lnkUninstall.SetBounds(24, 478, 110, 20);
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
            Theme.Place(_lblVer, 142, 479, 150);
            Controls.Add(_lblVer);

            var lnkCheck = new LinkLabel();
            lnkCheck.Text = "检查更新";
            lnkCheck.Font = Theme.FSmall;
            lnkCheck.LinkColor = Theme.Accent;
            lnkCheck.ActiveLinkColor = Theme.Accent;
            lnkCheck.LinkBehavior = LinkBehavior.HoverUnderline;
            lnkCheck.SetBounds(300, 478, 80, 20);
            lnkCheck.LinkClicked += delegate { CheckUpdateManual(); };
            Controls.Add(lnkCheck);

            RelayoutBelowCard();

            AcceptButton = ok;
            CancelButton = cancel;
        }

        /// <summary>
        /// 卡片因为 DPI 变高之后，把卡片下方的控件整体下移，并把窗口加高。
        /// 不做这一步的话，125% 缩放下卡片会盖住「断网检测间隔」那一行。
        /// </summary>
        void RelayoutBelowCard()
        {
            if (_cardGrow <= 0) return;
            foreach (Control c in Controls)
            {
                if (c.Top > 294) c.Top += _cardGrow;
            }
            ClientSize = new Size(ClientSize.Width, ClientSize.Height + _cardGrow);
        }

        /// <summary>
        /// 一个「勾选框 + 说明」占多高（含与下一项的间距）。
        ///
        /// ⚠ 不能用 `(c.Tag as Label).Height` —— AutoSize 的 Label 在还没有
        /// 父容器时高度是 0（要等 PerformLayout 之后才算出真实值）。
        /// 实测踩到：读出来是 0，于是每项只占 39px，相邻两项直接叠在一起。
        /// 所以这里用 Theme.MeasureHeight 另起一个探针 Label 量。
        /// </summary>
        int CheckBlockHeight(CheckBox c, string hint)
        {
            int hh = Theme.MeasureHeight(hint, Theme.FSmall, 384);
            return c.Height + 6 + hh + 12;
        }

        CheckBox MakeCheck(string title, string hint, int y)
        {
            var c = new CheckBox();
            c.Text = title;
            c.Font = Theme.FBody;
            c.ForeColor = Theme.Text;
            // 复选框也要 AutoSize。
            // 写死高度（哪怕按 MeasureHeight 算）依然会裁 —— 因为 CheckBox
            // 的文字是在「扣除勾选框图形宽度」之后的区域里排版的，
            // 用 Label 的算法量出来的高度对它偏小。交给它自己算才准。
            c.AutoSize = true;
            c.MaximumSize = new Size(390, 0);
            c.Location = new Point(20, y);
            c.BackColor = Color.Transparent;

            var h = Theme.Text2(hint, Theme.FSmall, Theme.Muted);
            Theme.Place(h, 40, y + c.Height + 2, 384);
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
                f.ClientSize = new Size(460, 536);
                f.StartPosition = FormStartPosition.CenterParent;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.BackColor = Theme.Bg;
                f.Font = Theme.FBody;
                f.AutoScaleMode = AutoScaleMode.None;

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

                // ── 附件区 ──
                // 客户端也要能带图：很多问题（界面错位、报错弹窗）一句话说不清。
                var l4 = new Label();
                l4.Text = "截图或文件（选填，可多选）";
                l4.SetBounds(20, 348, 420, 18);
                l4.ForeColor = Theme.Muted;
                l4.Font = Theme.FSmall;
                f.Controls.Add(l4);

                var btnFile = new Button();
                btnFile.Text = "选择文件…";
                btnFile.SetBounds(20, 370, 120, 30);
                btnFile.FlatStyle = FlatStyle.Flat;
                btnFile.FlatAppearance.BorderColor = Theme.Line;
                btnFile.FlatAppearance.BorderSize = 1;
                btnFile.BackColor = Theme.CardBg;
                btnFile.ForeColor = Theme.Text;
                btnFile.Font = Theme.FSmall;
                btnFile.Cursor = Cursors.Hand;
                f.Controls.Add(btnFile);

                var lblFiles = new Label();
                lblFiles.Text = "未选择";
                lblFiles.SetBounds(150, 374, 290, 22);
                lblFiles.ForeColor = Theme.Muted;
                lblFiles.Font = Theme.FSmall;
                lblFiles.AutoEllipsis = true;
                f.Controls.Add(lblFiles);

                // 已选文件 + 上传后拿到的 id（提交反馈时一起带上去绑定）
                var picked = new System.Collections.Generic.List<string>();
                var uploaded = new System.Collections.Generic.List<string>();

                var msg = new Label();
                msg.SetBounds(20, 412, 420, 20);
                msg.ForeColor = Theme.Muted;
                msg.Font = Theme.FSmall;
                f.Controls.Add(msg);

                var btnSend = new Button();
                btnSend.Text = "提交反馈";
                btnSend.SetBounds(20, 440, 200, 36);
                btnSend.FlatStyle = FlatStyle.Flat;
                btnSend.FlatAppearance.BorderSize = 0;
                btnSend.BackColor = Theme.Accent;
                btnSend.ForeColor = Color.White;
                btnSend.Font = Theme.FBody;
                btnSend.Cursor = Cursors.Hand;
                f.Controls.Add(btnSend);

                btnFile.Click += delegate
                {
                    using (var ofd = new OpenFileDialog())
                    {
                        ofd.Multiselect = true;
                        ofd.Title = "选择要附上的截图或文件";
                        ofd.Filter = "图片或文档|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.txt;*.log;*.json;*.zip;*.csv";
                        if (ofd.ShowDialog(f) != DialogResult.OK) return;

                        foreach (var path in ofd.FileNames)
                        {
                            if (picked.Count >= 6)
                            {
                                MessageBox.Show(f, "最多 6 个附件。", "渡口");
                                break;
                            }
                            try
                            {
                                var fi = new FileInfo(path);
                                if (fi.Length > 8 * 1024 * 1024)
                                {
                                    MessageBox.Show(f, Path.GetFileName(path) + " 超过 8 MB，跳过。", "渡口");
                                    continue;
                                }
                            }
                            catch (Exception) { continue; }
                            picked.Add(path);
                        }
                        lblFiles.Text = picked.Count == 0 ? "未选择"
                            : string.Join("、", picked.ConvertAll(Path.GetFileName).ToArray());
                    }
                };

                var btnCancel = new Button();
                btnCancel.Text = "取消";
                btnCancel.SetBounds(240, 440, 200, 36);
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
                        try
                        {
                            // 先逐个上传附件，拿到 id 列表，再带着它们提交反馈。
                            // 上传失败不阻断提交 —— 反馈正文本身比附件重要。
                            var ids = new System.Collections.Generic.List<string>();
                            foreach (var path in picked)
                            {
                                try
                                {
                                    var up = Api.UploadAttachment(_cfg, path);
                                    if (up.Length > 0) ids.Add(up);
                                }
                                catch (Exception) { }
                            }
                            err = Api.Feedback(_cfg, kind, body, contact,
                                               string.Join(",", ids.ToArray()));
                        }
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

            // useTun 归主界面的「全局模式」按钮管，这里不碰。

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

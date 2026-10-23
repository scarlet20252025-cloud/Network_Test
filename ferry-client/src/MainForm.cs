using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Ferry
{
    class MainForm : Form
    {
        readonly string _cfgPath;
        readonly string _appDir;
        readonly string _logPath;
        Cfg _cfg;
        LicenseState _lic = new LicenseState();

        Label _lblAcct, _lblBig, _lblDesc, _lblQuota, _rowTunnel, _rowBridge, _rowProxy;
        Panel _card, _logCard, _actCard;
        Label _lblActTitle, _lblActHint;
        TextBox _txtCode;
        Button _btnActivate, _btnOn, _btnOff, _btnRefresh, _btnUninstall, _btnUpd, _btnSettings;
        TextBox _log;
        NotifyIcon _tray;
        System.Windows.Forms.Timer _fast, _slow, _licTimer, _hbTimer;
        bool _busy, _activating;
        string _exitVia = "";
        bool _hasNet = true, _probingNet;
        /// <summary>借网运行时（隧道 + 本地代理，都在本进程内）</summary>
        FerryRuntime _rt;
        string _lastProxySet = "";

        public MainForm(string cfgPath, string appDir)
        {
            _cfgPath = cfgPath;
            _appDir = appDir;
            _logPath = Path.Combine(appDir, "assistant.log");
            _cfg = Cfg.Load(cfgPath);
            BuildUi();
            Load += delegate
            {
                Kickoff();
                if (_cfg.StartMinimized)
                {
                    // 开机自启时不该弹窗，直接驻留托盘
                    BeginInvoke(new Action(delegate { Hide(); }));
                }
            };
        }

        // ── 界面 ────────────────────────────────────────────────

        void BuildUi()
        {
            Text = "渡口 Ferry";
            ClientSize = new Size(520, 748);
            MinimumSize = new Size(520, 748);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            Font = Theme.FBody;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            DoubleBuffered = true;

            var title = Theme.Text2("渡口", Theme.FTitle, Theme.Text);
            title.SetBounds(24, 16, 120, 28);
            Controls.Add(title);

            var sub = Theme.Text2("在借网与直连之间切换", Theme.FSmall, Theme.Muted);
            sub.SetBounds(24, 44, 300, 18);
            Controls.Add(sub);

            _lblAcct = Theme.Text2("", Theme.FSmall, Theme.Muted);
            _lblAcct.SetBounds(240, 22, 256, 20);
            _lblAcct.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(_lblAcct);

            _btnUpd = Theme.FlatButton("更新", false);
            _btnUpd.SetBounds(394, 48, 102, 26);
            _btnUpd.Visible = false;
            _btnUpd.Click += delegate { DoUpdate(); };
            Controls.Add(_btnUpd);

            // ── 状态卡 ──
            _card = Theme.Card(14);
            _card.SetBounds(20, 80, 480, 190);
            Controls.Add(_card);

            _lblBig = Theme.Text2("检查中", Theme.FBig, Theme.Muted);
            _lblBig.SetBounds(24, 12, 430, 40);
            _card.Controls.Add(_lblBig);

            _lblDesc = Theme.Text2("", Theme.FBody, Theme.Muted);
            _lblDesc.SetBounds(24, 54, 430, 36);
            _card.Controls.Add(_lblDesc);

            _lblQuota = Theme.Text2("", Theme.FSmall, Theme.Muted);
            _lblQuota.SetBounds(24, 92, 430, 20);
            _card.Controls.Add(_lblQuota);

            _rowTunnel = Row(_card, 120);
            _rowBridge = Row(_card, 140);
            _rowProxy = Row(_card, 160);

            // ── 激活卡：输入框直接放在主界面，不弹窗 ──
            _actCard = Theme.Card(14);
            _actCard.SetBounds(20, 282, 480, 92);
            Controls.Add(_actCard);

            _lblActTitle = Theme.Text2("输入密钥激活", Theme.FBody, Theme.Text);
            _lblActTitle.SetBounds(20, 10, 300, 22);
            _actCard.Controls.Add(_lblActTitle);

            _txtCode = new TextBox();
            _txtCode.Font = new Font("Consolas", 12f, FontStyle.Bold);
            _txtCode.CharacterCasing = CharacterCasing.Upper;
            _txtCode.MaxLength = 16;
            _txtCode.TextAlign = HorizontalAlignment.Center;
            _txtCode.SetBounds(20, 38, 300, 28);
            _txtCode.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoInlineActivate(); }
            };
            _actCard.Controls.Add(_txtCode);

            _btnActivate = Theme.FlatButton("激活", true);
            _btnActivate.SetBounds(332, 37, 128, 30);
            _btnActivate.Click += delegate { DoInlineActivate(); };
            _actCard.Controls.Add(_btnActivate);

            _lblActHint = Theme.Text2("管理员给你的 8 位字母数字，一个密钥只能用一次。",
                Theme.FSmall, Theme.Muted);
            _lblActHint.SetBounds(20, 68, 440, 18);
            _actCard.Controls.Add(_lblActHint);

            // ── 主按钮 ──
            _btnOn = Theme.FlatButton("开始借网", true);
            _btnOn.SetBounds(20, 390, 232, 40);
            _btnOn.Click += delegate { DoStart(); };
            Controls.Add(_btnOn);

            _btnOff = Theme.FlatButton("停止并恢复直连", false);
            _btnOff.SetBounds(268, 390, 232, 40);
            _btnOff.Click += delegate { DoStop(); };
            Controls.Add(_btnOff);

            _btnRefresh = Theme.FlatButton("刷新", false);
            _btnRefresh.SetBounds(20, 438, 110, 30);
            _btnRefresh.Click += delegate { RefreshUi(); Log("已刷新"); };
            Controls.Add(_btnRefresh);

            _btnSettings = Theme.FlatButton("设置", false);
            _btnSettings.SetBounds(148, 438, 96, 30);
            _btnSettings.Click += delegate { OpenSettings(); };
            Controls.Add(_btnSettings);

            _btnUninstall = Theme.FlatButton("卸载", false);
            _btnUninstall.SetBounds(390, 438, 110, 30);
            _btnUninstall.ForeColor = Theme.Muted;
            _btnUninstall.Click += delegate { DoUninstall(); };
            Controls.Add(_btnUninstall);

            // ── 日志 ──
            _logCard = Theme.Card(10);
            _logCard.SetBounds(20, 478, 480, 236);
            Controls.Add(_logCard);

            _log = new TextBox();
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BorderStyle = BorderStyle.None;
            _log.BackColor = Theme.CardBg;
            _log.ForeColor = Theme.Muted;
            _log.Font = Theme.FMono;
            _log.SetBounds(16, 14, 448, 208);
            _logCard.Controls.Add(_log);

            var foot = Theme.Text2("渡口仅用于个人学习与内网连通性测试", Theme.FSmall, Color.FromArgb(170, 176, 186));
            foot.SetBounds(20, 722, 480, 18);
            foot.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(foot);

            BuildTray();
        }

        static Label Row(Panel parent, int y)
        {
            var l = Theme.Text2("", Theme.FSmall, Theme.Muted);
            l.SetBounds(24, y, 430, 18);
            parent.Controls.Add(l);
            return l;
        }

        void BuildTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Text = "渡口 Ferry";
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowMain(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add("打开面板", null, delegate { ShowMain(); });
            menu.Items.Add("借网 / 直连切换", null, delegate { Toggle(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { RealQuit(); });
            _tray.ContextMenuStrip = menu;
        }

        void ShowMain()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            RefreshUi();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 关窗口 = 收到托盘。直接退出的话，用户下一次想关掉借网
            // 会找不到任何入口，而系统代理还指着已经死掉的端口 ——
            // 表现就是「全网上不去」。
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _tray.ShowBalloonTip(1500, "渡口还在运行", "双击托盘图标可以打开面板", ToolTipIcon.Info);
                return;
            }
            base.OnFormClosing(e);
        }

        void RealQuit()
        {
            if (_rt != null && _rt.Running)
            {
                var r = MessageBox.Show(this, "当前正在借网。退出会恢复直连，确定吗？",
                    "渡口", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
                FerryCore.RestoreDirect(_cfg, _appDir);
                _rt.Stop();
                _rt = null;
            }
            _tray.Visible = false;
            _tray.Dispose();
            Environment.Exit(0);
        }

        // ── 启动 ────────────────────────────────────────────────

        void Kickoff()
        {
            Log("渡口已启动 · 账号 " + (_cfg.AccountIp.Length > 0 ? _cfg.AccountIp : "（未激活）"));
            if (_cfg.Activated) { Log("正在向门户确认授权…"); CheckLicense(true); }
            else { Log("尚未激活 —— 在「输入密钥激活」框里填入管理员给的密钥"); _txtCode.Focus(); }

            _fast = new System.Windows.Forms.Timer(); _fast.Interval = 1000;
            _fast.Tick += delegate { RefreshUi(); };
            _fast.Start();

            // 网络探测单独走 10 秒一次 —— 它要发 DNS 查询，塞进 1 秒心跳里太重
            _slow = new System.Windows.Forms.Timer();
            _slow.Interval = Math.Max(10, _cfg.NetCheckSec) * 1000;
            _slow.Tick += delegate { ProbeNet(); };
            _slow.Start();

            // 事件驱动：系统网络状态一变就立刻探一次。
            // 有了它，定时器只是兜底（Wi-Fi 断了但系统没发通知这种边缘情况），
            // 正常情况下 15 秒一次的定时器几乎不产生真实 DNS 流量。
            try
            {
                System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged +=
                    delegate { ProbeNet(); };
                System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged +=
                    delegate { ProbeNet(); };
            }
            catch (Exception) { }

            ProbeNet();

            // 心跳：5 秒一次，让管理页能显示「每个用户的实时上下行」。
            // Windows 拿不到 per-connection 的网络字节，只有客户端自己知道。
            _hbTimer = new System.Windows.Forms.Timer(); _hbTimer.Interval = 5000;
            _hbTimer.Tick += delegate { SendHeartbeat(); };
            _hbTimer.Start();

            _licTimer = new System.Windows.Forms.Timer(); _licTimer.Interval = 6 * 3600 * 1000;
            _licTimer.Tick += delegate { CheckLicense(false); CheckUpdate(); };
            _licTimer.Start();

            CheckUpdate();
        }

        void ProbeNet()
        {
            if (_probingNet) return;
            _probingNet = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                try
                {
                    ok = FerryCore.HasNetwork(3000);
                }
                catch (Exception)
                {
                    ok = _hasNet;   // 探测本身炸了就当没变化，别乱切状态
                }
                finally
                {
                    // 必须放在 finally：HasNetwork 要是抛了而这里没复位，
                    // _probingNet 会永远为 true，网络检测从此静默停摆。
                    _probingNet = false;
                }

                var changed = ok != _hasNet;
                var wasNet = _hasNet;
                _hasNet = ok;
                if (!changed) return;

                try { BeginInvoke(new Action(RefreshUi)); } catch (Exception) { }

                // 断网自动借网：只在「有网 → 无网」这一跳触发，且不重复开。
                // 不在恢复联网时自动停 —— 网络抖动很常见，来回切系统代理
                // 比一直挂着更容易出事；用户想停就点一下按钮。
                if (!ok && wasNet && _cfg.AutoBorrow && _cfg.Activated && !_busy)
                {
                    var rt = _rt;
                    var already = rt != null && rt.Running;
                    if (!already)
                    {
                        try
                        {
                            BeginInvoke(new Action(delegate
                            {
                                Log("检测到断网，自动开始借网…");
                                DoStart();
                            }));
                        }
                        catch (Exception) { }
                    }
                }
            });
        }

        // ── 激活 ────────────────────────────────────────────────

        void DoInlineActivate()
        {
            if (_activating) return;
            if (_cfg.PortalUrl.Length == 0)
            {
                MessageBox.Show(this, "配置里没有门户地址，请向管理员索取正确的安装包。",
                    "渡口", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var code = (_txtCode.Text ?? "").Trim().ToUpperInvariant();
            if (code.Length < 6)
            {
                _lblActHint.ForeColor = Theme.Bad;
                _lblActHint.Text = "密钥至少 6 位";
                _txtCode.Focus();
                return;
            }

            _activating = true;
            _txtCode.Enabled = false;
            _btnActivate.Enabled = false;
            _btnActivate.Text = "验证中";
            _lblActHint.ForeColor = Theme.Muted;
            _lblActHint.Text = "正在向门户验证…";
            Application.DoEvents();

            ThreadPool.QueueUserWorkItem(delegate
            {
                var r = Api.Activate(_cfg.PortalUrl, code);
                BeginInvoke(new Action(delegate
                {
                    _activating = false;
                    _txtCode.Enabled = true;
                    _btnActivate.Enabled = true;
                    _btnActivate.Text = "激活";

                    if (r == null || !r.Ok)
                    {
                        _lblActHint.ForeColor = Theme.Bad;
                        _lblActHint.Text = r == null || string.IsNullOrEmpty(r.Error) ? "激活失败，请重试" : r.Error;
                        return;
                    }

                    _cfg.Token = r.Token;
                    if (!string.IsNullOrEmpty(r.RemoteHost)) _cfg.RemoteHost = r.RemoteHost;
                    if (r.SshPort > 0) _cfg.SshPort = r.SshPort;
                    if (r.HttpPort > 0) _cfg.HttpPort = r.HttpPort;
                    if (!string.IsNullOrEmpty(r.PortalUrl)) _cfg.PortalUrl = r.PortalUrl;
                    _cfg.ExpiresAt = r.ExpiresAt;
                    _cfg.Save(_cfgPath);

                    _txtCode.Text = "";
                    Log("激活成功 · 账号 " + _cfg.AccountIp);
                    if (r.ExpiresAt > 0)
                        Log("授权到 " + FromUnix(r.ExpiresAt).ToString("yyyy-MM-dd") + "（" + r.DaysLeft + " 天）");
                    else Log("永久授权");
                    CheckLicense(true);
                }));
            });
        }

        void CheckLicense(bool verbose)
        {
            LicenseState st;
            try { st = Api.License(_cfg); }
            catch (Exception e) { Log("授权检查异常：" + e.Message); return; }

            _lic = st;
            if (!st.Ok && verbose) Log("授权检查失败：" + st.Error + "（按本地时间判断）");
            else if (st.Ok && verbose)
            {
                if (st.Expired) Log("授权已到期 —— 借网已停用，请续期");
                else if (st.Suspended) Log("账号被管理员停用");
                else if (st.ExpiresAt > 0) Log("授权有效，还剩 " + st.DaysLeft + " 天");
                else Log("授权有效（永久）");
            }

            // 到期/停用就把借网掐掉 —— 不能等用户自己发现。
            if (_cfg.Activated && !st.Enabled && FerryCore.IsBorrowing(_cfg))
            {
                Log("授权失效，正在恢复直连…");
                FerryCore.StopAll(_cfg, _appDir);
            }

            try { BeginInvoke(new Action(RefreshUi)); } catch (Exception) { }
        }

        // ── 借网 ────────────────────────────────────────────────

        void DoStart()
        {
            if (_busy) return;
            if (!_cfg.Activated) { _txtCode.Focus(); MessageBox.Show(this, "先在下面的框里输入密钥激活。", "渡口"); return; }
            if (!_lic.Enabled && _lic.Ok)
            {
                MessageBox.Show(this, _lic.Expired ? "授权已到期，请向管理员续期。" : "账号已停用，请联系管理员。",
                    "渡口", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetBusy(true);
            Log("启动隧道与代理…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                var rt = new FerryRuntime(_cfg, _appDir, Log);
                var err = rt.Start();
                string exitIp = "";
                if (err.Length == 0)
                {
                    // 先挂上运行时，UI 立刻就能看到「正在借网」。
                    // 放到出口探测之后的话，那 6 秒里界面会显示「借网异常」（因为
                    // 系统代理已经改了、但 _rt 还是 null），用户会以为坏了。
                    _rt = rt;
                    // 备份原代理 → 再切渡口。顺序反了会在中途把用户原设置覆盖掉。
                    FerryCore.BackupProxyOnce(_cfg, _appDir);
                    FerryCore.SetProxy(true, rt.BorrowServer);
                    // 出口探测慢（最长 6 秒）且可能失败，放到最后，不影响状态显示
                    exitIp = rt.ExitIpViaProxy(6000);
                }
                else
                {
                    rt.Stop();
                }

                BeginInvoke(new Action(delegate
                {
                    SetBusy(false);
                    if (err.Length > 0)
                    {
                        Log("启动失败：" + err);
                        MessageBox.Show(this, err + "\n\n日志：" + _logPath,
                            "渡口", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    else
                    {
                        Log("隧道 + 代理已就绪（SOCKS " + _rt.SocksPort + " / HTTP " + _rt.HttpPort + "）");
                        Log("系统代理已切到 " + _rt.BorrowServer);
                        if (exitIp.Length > 0) { _exitVia = exitIp; Log("出口 IP：" + exitIp); }
                        else Log("出口探测失败（不影响使用，可能是远端暂时不通）");
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
                // 顺序：先还原代理，再停服务。
                // 反过来的话，代理会短暂指向一个已停掉的端口，那几秒全网上不去。
                FerryCore.RestoreDirect(_cfg, _appDir);
                if (_rt != null) { _rt.Stop(); _rt = null; }
                Thread.Sleep(200);
                BeginInvoke(new Action(delegate
                {
                    SetBusy(false);
                    Log("已恢复直连");
                    _exitVia = "";
                    RefreshUi();
                }));
            });
        }

        void Toggle()
        {
            if (_rt != null && _rt.Running) DoStop(); else DoStart();
        }

        /// <summary>
        /// 上报心跳。失败静默 —— 断网时本来就连不上门户，不该刷错误日志。
        /// </summary>
        void SendHeartbeat()
        {
            if (!_cfg.Activated) return;
            var rt = _rt;
            var rx = rt != null ? rt.BytesDown : 0;   // 下行 = 远端 → 本机
            var tx = rt != null ? rt.BytesUp : 0;     // 上行 = 本机 → 远端
            var conns = rt != null ? (int)rt.ConnActive : 0;
            var running = rt != null && rt.Running;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Api.Heartbeat(_cfg, rx, tx, conns, running);
            });
        }

        void OpenSettings()
        {
            using (var dlg = new SettingsDialog(_cfg, _cfgPath, _appDir))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                // 间隔可能改了，热更新定时器
                _slow.Interval = Math.Max(10, _cfg.NetCheckSec) * 1000;
                Log("设置已保存（断网自动借网 " + (_cfg.AutoBorrow ? "开" : "关") +
                    "，检测间隔 " + _cfg.NetCheckSec + " 秒）");
            }
        }

        void DoUninstall()
        {
            var r = MessageBox.Show(this,
                "卸载会：\n· 切回直连\n· 停止隧道与代理\n· 删除开机自启\n\n（不会自动删文件，方便你保留日志）\n\n继续？",
                "卸载渡口", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            SetBusy(true);
            ThreadPool.QueueUserWorkItem(delegate
            {
                FerryCore.RestoreDirect(_cfg, _appDir);
                if (_rt != null) { _rt.Stop(); _rt = null; }
                try
                {
                    var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                    foreach (var pat in new[] { "*渡口*", "*Ferry*" })
                        foreach (var f in Directory.GetFiles(startup, pat))
                            try { File.Delete(f); } catch (Exception) { }
                }
                catch (Exception) { }

                BeginInvoke(new Action(delegate
                {
                    SetBusy(false);
                    Log("已卸载（文件保留在 " + _appDir + "）");
                    MessageBox.Show(this, "已切回直连并删除自启。\n\n如果完全不需要了，可以手动删除目录：\n" + _appDir,
                        "渡口", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    RefreshUi();
                }));
            });
        }

        // ── 更新 ────────────────────────────────────────────────

        void CheckUpdate()
        {
            if (_cfg.PortalUrl.Length == 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var v = Api.Version(_cfg.PortalUrl);
                if (!v.Ok) return;
                var mine = Application.ProductVersion;
                if (CompareVer(v.Latest, mine) > 0)
                    BeginInvoke(new Action(delegate
                    {
                        _btnUpd.Text = "更新 " + v.Latest;
                        _btnUpd.Visible = true;
                        Log("发现新版本 " + v.Latest + "（当前 " + mine + "）");
                    }));
            });
        }

        void DoUpdate()
        {
            var v = Api.Version(_cfg.PortalUrl);
            if (!v.Ok) { MessageBox.Show(this, "拿不到版本信息", "渡口"); return; }
            if (MessageBox.Show(this, "下载并安装 " + v.Latest + "？\n\n安装程序会静默覆盖现有版本，配置和授权不受影响。",
                "更新", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            try
            {
                var url = v.Url.StartsWith("http") ? v.Url : _cfg.PortalUrl.TrimEnd('/') + v.Url;
                var tmp = Path.Combine(Path.GetTempPath(), "Ferry-Setup-" + v.Latest + ".exe");
                var wc = new System.Net.WebClient();
                wc.Proxy = null;
                wc.DownloadFile(url, tmp);
                var psi = new ProcessStartInfo(tmp, "/SILENT /NORESTART /CLOSEAPPLICATIONS");
                psi.UseShellExecute = false;
                Process.Start(psi);
                Log("已启动安装程序，本程序即将退出");
                Thread.Sleep(600);
                RealQuit();
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "下载失败：" + e.Message, "更新");
            }
        }

        static int CompareVer(string a, string b)
        {
            var pa = (a ?? "").Split('.');
            var pb = (b ?? "").Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int x = i < pa.Length ? ParseInt(pa[i]) : 0;
                int y = i < pb.Length ? ParseInt(pb[i]) : 0;
                if (x != y) return x > y ? 1 : -1;
            }
            return 0;
        }

        static int ParseInt(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in s) { if (char.IsDigit(c)) sb.Append(c); else break; }
            int v;
            return int.TryParse(sb.ToString(), out v) ? v : 0;
        }

        // ── 刷新 ────────────────────────────────────────────────

        /// <summary>
        /// 只在内容真的变了才赋值。
        ///
        /// Label.Text 的 setter 不做等值判断 —— 赋同样的字符串也会触发
        /// OnTextChanged → Invalidate → 重绘。1 秒一次心跳里更新七八个 Label，
        /// 界面静止时也在白烧 CPU（笔记本上能看出风扇）。
        /// </summary>
        static void SetText(Label l, string text, Color color)
        {
            if (l.Text != text) l.Text = text;
            if (l.ForeColor != color) l.ForeColor = color;
        }

        void RefreshUi()
        {
            bool proxyOn = FerryCore.ProxyEnabled();
            string proxySrv = FerryCore.ProxyServer();

            // 端口由运行时自己分配并持有，不再靠读文件反推
            var rt = _rt;
            bool runtimeUp = rt != null && rt.Running;
            int httpPort = rt != null ? rt.HttpPort : _cfg.HttpPort;
            int socksPort = rt != null ? rt.SocksPort : _cfg.SocksPort;
            bool bridgeUp = runtimeUp && rt.ProxyAlive;
            bool tunnelUp = runtimeUp && rt.TunnelAlive;

            string borrowServer = "http://127.0.0.1:" + httpPort;
            bool borrowing = proxyOn && proxySrv == borrowServer && bridgeUp;

            // 系统代理指着一个 "本机但不对" 的端口时纠回来。
            // 场景：用户手动改过设置，或者上次崩溃残留。不让它变成
            // "显示正在借网但网页打不开"。
            if (runtimeUp && proxyOn && bridgeUp && proxySrv != borrowServer &&
                proxySrv.StartsWith("http://127.0.0.1:"))
            {
                FerryCore.SetProxy(true, borrowServer);
                Log("系统代理指错了地方 " + proxySrv + " → " + borrowServer + "，已纠正");
                proxySrv = borrowServer;
                borrowing = true;
            }

            // 激活卡：未激活显示输入框，已激活显示账号与剩余时长
            if (_cfg.Activated)
            {
                _lblActTitle.Text = "已激活";
                _lblActTitle.ForeColor = Theme.Good;
                _txtCode.Visible = false;
                _btnActivate.Visible = false;
                if (_lic.Expired) { _lblActHint.Text = "授权已到期 —— 请向管理员续期"; _lblActHint.ForeColor = Theme.Bad; }
                else if (_lic.Suspended) { _lblActHint.Text = "账号已停用 —— 请联系管理员"; _lblActHint.ForeColor = Theme.Bad; }
                else if (_lic.ExpiresAt > 0)
                {
                    _lblActHint.Text = "账号 " + _cfg.AccountIp + " · " + LeftText() + " · " +
                        FromUnix(_lic.ExpiresAt).ToString("yyyy-MM-dd") + " 到期";
                    _lblActHint.ForeColor = _lic.DaysLeft <= 3 ? Theme.Bad : Theme.Text;
                }
                else { _lblActHint.Text = "账号 " + _cfg.AccountIp + " · 永久授权"; _lblActHint.ForeColor = Theme.Text; }
                SetText(_lblAcct, "账号 " + _cfg.AccountIp, Theme.Muted);
            }
            else
            {
                _lblActTitle.Text = "输入密钥激活";
                _lblActTitle.ForeColor = Theme.Text;
                _txtCode.Visible = true;
                _btnActivate.Visible = true;
                _lblActHint.Text = "管理员给你的 8 位字母数字，一个密钥只能用一次。";
                _lblActHint.ForeColor = Theme.Muted;
                SetText(_lblAcct, "未激活", Theme.Bad);
            }

            // 授权行（状态卡内）
            if (!_cfg.Activated) { _lblQuota.Text = "输入密钥后开始计时"; _lblQuota.ForeColor = Theme.Muted; }
            else if (_lic.Expired) { _lblQuota.Text = "授权已到期，请向管理员续期"; _lblQuota.ForeColor = Theme.Bad; }
            else if (_lic.Suspended) { _lblQuota.Text = "账号已停用，请联系管理员"; _lblQuota.ForeColor = Theme.Bad; }
            else if (_lic.ExpiresAt > 0)
            {
                _lblQuota.Text = LeftText() + " · " + FromUnix(_lic.ExpiresAt).ToString("yyyy-MM-dd") + " 到期";
                _lblQuota.ForeColor = _lic.DaysLeft <= 3 ? Theme.Bad : Theme.Muted;
            }
            else { _lblQuota.Text = "永久授权"; _lblQuota.ForeColor = Theme.Muted; }

            // 大状态
            if (!_cfg.Activated)
            {
                SetText(_lblBig, "未激活", Theme.Bad);
                SetText(_lblDesc, "在下面的框里输入管理员给的密钥，即可开通借网。", Theme.Muted);
            }
            else if (borrowing)
            {
                SetText(_lblBig, "正在走渡口", Theme.Good);
                SetText(_lblDesc, _exitVia.Length > 0
                    ? "流量经远端主机中转 · 出口 " + _exitVia : "流量经远端主机中转", Theme.Muted);
            }
            else if (proxyOn && proxySrv == borrowServer)
            {
                SetText(_lblBig, "借网异常", Theme.Bad);
                SetText(_lblDesc, "系统代理指向渡口，但本地代理端口没响应。点「停止并恢复直连」再重新开始。", Theme.Muted);
            }
            else if (!_hasNet)
            {
                SetText(_lblBig, "无网络", Theme.Bad);
                SetText(_lblDesc, "本机连不上网 —— 试试开启渡口。", Theme.Muted);
            }
            else
            {
                SetText(_lblBig, "直连", Theme.Idle);
                SetText(_lblDesc, "正常使用本机网络。断网时点「开始借网」。", Theme.Muted);
            }

            // 三行明细（原版就有，看得出卡在哪一环）
            SetText(_rowTunnel, "隧道        " + (tunnelUp ? "已连接（端口 " + socksPort + "）"
                    : (runtimeUp ? "重连中…" : "未连接")), tunnelUp ? Theme.Text : Theme.Muted);

            SetText(_rowBridge, "本地代理    " + (bridgeUp ? "运行中（端口 " + httpPort + "）" : "未运行"),
                    bridgeUp ? Theme.Text : Theme.Muted);

            SetText(_rowProxy, "系统代理    " + (proxyOn ? "开  " + (proxySrv.Length > 0 ? proxySrv : "（未设置）") : "关"),
                    borrowing ? Theme.Good : Theme.Text);

            _btnOn.Enabled = !_busy && !borrowing;
            _btnOff.Enabled = !_busy && (borrowing || runtimeUp || (proxyOn && proxySrv == borrowServer));
        }

        void SetBusy(bool b)
        {
            _busy = b;
            _btnOn.Enabled = !b;
            _btnOff.Enabled = !b;
            _btnRefresh.Enabled = !b;
            _btnUninstall.Enabled = !b;
            _btnActivate.Enabled = !b && !_activating;
            Cursor = b ? Cursors.WaitCursor : Cursors.Default;
            // DoEvents：让「启动中…」这类文字在长操作前先画出来，
            // 否则界面在操作结束前完全静止，看起来像死掉。
            Application.DoEvents();
        }

        // ── 日志 ────────────────────────────────────────────────

        public void Log(string line)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), line); return; }
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            var text = "[" + stamp + "] " + line;
            _log.AppendText(text + "\r\n");
            try
            {
                File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd ") + text + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
                // 超过 200 KB 就只留最后 500 行 —— 长期挂着不轮转会涨到几十 MB。
                var fi = new FileInfo(_logPath);
                if (fi.Length > 204800)
                {
                    var all = File.ReadAllLines(_logPath);
                    var keep = new string[Math.Min(500, all.Length)];
                    Array.Copy(all, all.Length - keep.Length, keep, 0, keep.Length);
                    File.WriteAllLines(_logPath, keep, new System.Text.UTF8Encoding(false));
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 剩余时长文案。不足一天时说小时 —— 显示「剩余 0 天」会让用户以为已经过期。
        /// </summary>
        string LeftText()
        {
            if (_lic.ExpiresAt <= 0) return "永久授权";
            if (_lic.Expired) return "已到期";
            if (_lic.DaysLeft > 0) return "剩余 " + _lic.DaysLeft + " 天";
            int h = _lic.HoursLeft > 0 ? _lic.HoursLeft : 1;
            return "剩余 " + h + " 小时";
        }

        static DateTime FromUnix(long sec)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(sec).ToLocalTime();
        }
    }
}

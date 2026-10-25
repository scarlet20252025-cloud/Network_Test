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

        Label _lblAcct, _lblBig, _lblDesc, _lblQuota, _rowTunnel, _rowBridge, _rowProxy, _rowTun;
        Panel _card, _logCard, _actCard;
        Label _lblActTitle, _lblActHint;
        TextBox _txtCode;
        Button _btnActivate, _btnOn, _btnOff, _btnRefresh, _btnUpd, _btnSettings;
        TextBox _log;
        NotifyIcon _tray;
        System.Windows.Forms.Timer _fast, _slow, _licTimer, _hbTimer;
        bool _busy, _activating;

        // 设置里改了全局接管时，停完借网要立刻按新设置重开一次。
        // 用一个标记跨异步回调传递，而不是嵌套线程 —— DoStop 的回调本来就在 UI 线程。
        bool _restartAfterStop;
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
            // 高 DPI：让 WinForms 按系统缩放比例整体放大控件。
            //
            // 不写这两行 = 默认 AutoScaleMode.Font：字体 pt 固定、和设计时一模一样，
            // WinForms 就认为「不需要缩放」，而 Windows 已经按 DPI 放大了字体绘制，
            // 于是控件高度还是 96 DPI 的像素值 —— 文字被裁掉半行。
            // 所以同一个 exe 在 100% 缩放下正常、125% / 150% 的机器上就被挤压，
            // 用户看到的是「有些人窗口尺寸和文字高度都不对」。
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            BuildUi();
            Load += delegate
            {
                Theme.FitToScreen(this);
                Kickoff();
                if (_cfg.StartMinimized)
                {
                    // 开机自启时不该弹窗，直接驻留托盘
                    BeginInvoke(new Action(delegate { Hide(); }));
                }
                // /start 参数：启动即借网。
                // 用途：桌面快捷方式一键借网（不用先打开界面再点按钮），
                // 也让自动化测试能可靠触发，不依赖窗口坐标。
                if (StartBorrowOnLoad)
                {
                    BeginInvoke(new Action(delegate { DoStart(); }));
                }
            };
        }

        /// <summary>启动后立即借网（由 Program 解析 /start 参数设置）。</summary>
        public bool StartBorrowOnLoad = false;

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

            var sub = Theme.Text2("断网时借一台能上网的内网主机中转流量", Theme.FSmall, Theme.Muted);
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
            // 高度 200：四行明细最后一行在 y=180，行高 18，到 198，留 2px 余量。
            // 踩过的坑：加第四行（TUN）时忘了同步加高卡片，那一行被底边裁掉一半 ——
            // 界面看着像坏了，其实数据都对。
            _card.SetBounds(20, 80, 480, 200);
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
            // 第四行：TUN 状态。默认隐藏（只在设置里勾了全局接管时才显示），
            // 避免普通用户看到一行「未开启」以为是故障。
            _rowTun = Row(_card, 180);
            _rowTun.Visible = false;

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
            _btnSettings.SetBounds(274, 438, 96, 30);
            _btnSettings.Click += delegate { OpenSettings(); };
            Controls.Add(_btnSettings);

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
            // 用 exe 自己的图标。SystemIcons.Application 在 Win11 托盘里
            // 经常渲染成一个空白方块 —— 那个是通用占位图，不是我们的图标。
            Icon trayIcon = null;
            try { trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { }
            _tray.Icon = trayIcon != null ? trayIcon : SystemIcons.Application;
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

            // 把日志出口接给 FerryCore —— 它还原系统代理时要能说清楚做了什么
            // （尤其「原代理不可达所以关了代理」这条，用户必须看到，否则
            //  他会以为"恢复直连"就是能上网，实际直连上不了外网却不知道为什么）
            FerryCore.Logger = Log;

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

            // 启动后延迟 4 秒再查更新。
            // 为什么不立刻查：程序刚起来时界面还在初始化，
            // 如果马上弹一个「有新版本」对话框，会盖住主窗口、看起来像崩了。
            // 4 秒足够让主界面画完。
            var updDelay = new System.Windows.Forms.Timer(); updDelay.Interval = 4000;
            updDelay.Tick += delegate
            {
                updDelay.Stop();
                updDelay.Dispose();
                CheckUpdate();
            };
            updDelay.Start();
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
            // 已激活就别再走一遍。输入框和按钮本来就隐藏了，但 Enter 键
            // 可能还挂在上面（焦点没移走），按一下就会重复提交一次激活。
            if (_cfg.Activated)
            {
                Log("已经激活过了，不需要重复输入密钥");
                return;
            }
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
                    _txtCode.Text = "";   // 激活后立刻清空 —— 别让旧密钥留在框里
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

            // 到期/停用就把借网掐掉，并清空本地令牌 —— 强制回到「未激活」，
            // 用户必须重新输入密钥。不能只断网却留着令牌：
            // 那样界面还显示"已激活"，用户会反复点开始借网然后失败。
            var borrowingNow = FerryCore.IsBorrowing(_cfg) || (_rt != null && _rt.Running);
            if (_cfg.Activated && !st.Enabled && borrowingNow)
            {
                Log("授权失效，正在恢复直连…");
                FerryCore.RestoreDirect(_cfg, _appDir);   // 先还原代理，避免指向死端口
                if (_rt != null) { try { _rt.Stop(); } catch (Exception) { } _rt = null; }
                FerryCore.StopAll(_cfg, _appDir);         // 清残留进程
                _cfg.Token = "";
                try { _cfg.Save(_cfgPath); } catch (Exception) { }
                Log("授权已失效，请重新输入密钥激活");
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
                    _exitVia = "";
                    // 设置里改了全局接管：停完马上按新设置重开，
                    // 用户不用自己再点一次「开始借网」，状态也会立刻刷新。
                    if (_restartAfterStop)
                    {
                        _restartAfterStop = false;
                        Log("按新设置重新开始借网…");
                        DoStart();
                        return;
                    }
                    Log("已恢复直连");
                    RefreshUi();
                    StartProxyHeal();
                }));
            });
        }

        // ── 停止借网后的「原代理看护」──
        //
        // 解决什么：用户原本靠 Clash 上外网。停止借网那一刻 Clash 可能还没起来
        // （或者刚好在重启），系统代理就被关掉了。等用户自己打开 Clash，
        // 系统代理却还是关的 —— 表现是「恢复直连之后 GitHub 打不开了」。
        //
        // 只在停止借网后 5 分钟内、每 10 秒看一眼，且判据很严：
        //   · 系统代理必须是关着的（用户手动开了就不动）
        //   · 备份里必须有过原代理
        //   · 那个原代理现在必须真的可达
        // 三条都满足才接回去。任何一条不满足就继续等或直接放弃。
        System.Windows.Forms.Timer _healTimer;
        DateTime _healDeadline;

        void StartProxyHeal()
        {
            var st = FerryCore.LoadState(_appDir);
            if (st == null || !st.PrevEnabled || string.IsNullOrEmpty(st.PrevServer)) return;
            if (FerryCore.ProxyEnabled()) return;   // 代理已经开着，不用管

            _healDeadline = DateTime.Now.AddMinutes(5);
            if (_healTimer == null)
            {
                _healTimer = new System.Windows.Forms.Timer();
                _healTimer.Interval = 10000;
                _healTimer.Tick += delegate { TryHealProxy(); };
            }
            _healTimer.Start();
        }

        void TryHealProxy()
        {
            if (DateTime.Now > _healDeadline) { _healTimer.Stop(); return; }
            if (FerryCore.ProxyEnabled()) { _healTimer.Stop(); return; }

            var st = FerryCore.LoadState(_appDir);
            if (st == null || !st.PrevEnabled || string.IsNullOrEmpty(st.PrevServer))
            {
                _healTimer.Stop();
                return;
            }
            if (!FerryCore.IsProxyAlive(st.PrevServer)) return;   // 还没起来，继续等

            FerryCore.SetProxy(true, st.PrevServer);
            Log("检测到原代理 " + st.PrevServer + " 已就绪，已自动接回系统代理");
            _healTimer.Stop();
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
            // 连接模式：运行时优先（它反映真实走的是哪条路），
            // 没在借网时用配置值，面板上不会空着。
            var mode = rt != null ? rt.Mode : (_cfg.DirectSocks ? "direct" : "ssh");
            ThreadPool.QueueUserWorkItem(delegate
            {
                var hb = Api.Heartbeat(_cfg, rx, tx, conns, running, mode);

                // 服务端说这个账号不能用了 → 立刻断。
                //
                // 为什么靠心跳而不是等 CheckLicense：后者最长 6 小时才跑一次，
                // 管理员封了人之后那段时间用户完全无感、还在正常借网。
                // 心跳 5 秒一次，捎带这个状态等于零成本快速生效。
                if (hb.Revoked && _rt != null && _rt.Running)
                {
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            Log(hb.Expired
                                ? "授权已到期，服务端要求停止借网 —— 正在切回直连"
                                : "账号已被管理员停用 —— 正在切回直连");
                            DoStop();
                            _licTimer.Stop();
                            _licTimer.Start();   // 立刻重新拉一次授权状态，把界面切到「未激活」
                        }));
                    }
                    catch (Exception) { }
                }
            });
        }

        void OpenSettings()
        {
            using (var dlg = new SettingsDialog(_cfg, _cfgPath, _appDir, DoUninstall))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                // 间隔可能改了，热更新定时器
                _slow.Interval = Math.Max(10, _cfg.NetCheckSec) * 1000;
                Log("设置已保存（断网自动借网 " + (_cfg.AutoBorrow ? "开" : "关") +
                    "，检测间隔 " + _cfg.NetCheckSec + " 秒）");

                // 原来保存完什么都不做：正在借网时，隧道还是按旧设置起的，
                // 界面也不刷新 —— 用户以为「勾了没生效」。
                // 全局接管变了且正在借网：顺手重启链路；其他改动只刷新界面。
                // 用户选了「以管理员身份重启」：这是最优先的动作。
                if (dlg.NeedElevate)
                {
                    ElevateNow();
                    return;
                }

                if (dlg.TunToggleChanged && _rt != null && _rt.Running && !_busy)
                {
                    Log("全局接管设置已改，按新设置重启借网链路…");
                    _restartAfterStop = true;
                    DoStop();
                    return;
                }
                RefreshUi();
            }
        }

        /// <summary>
        /// 卸载 = 调 Inno 生成的 unins000.exe，不是「自己删几个文件」。
        ///
        /// 之前只做了三件事（还原代理、停进程、删自启），程序本体一动没动 ——
        /// 用户点「卸载」期待的是**真的消失**，结果发现还在，当然会说「根本不卸载」。
        ///
        /// 正确分工：
        ///   · 本方法：清掉卸载器管不了的东西（系统代理、隧道/代理进程），
        ///     然后启动 unins000.exe 并退出自己（不然文件被占用删不掉）
        ///   · unins000.exe：`[UninstallRun]` 会再调一次 `渡口助手.exe /uninstall`
        ///     做同样的清理，然后删文件、删快捷方式、删注册表卸载项
        /// 两次清理都幂等，重复执行没有副作用。
        /// </summary>
        /// <summary>
        /// 以管理员身份重启本程序。
        /// 先把借网停干净 —— 否则新进程起来时系统代理还指着旧进程的端口，
        /// 那几秒全网上不去，用户会以为「重启一下就坏了」。
        /// </summary>
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
            Log("以管理员身份重启…");
            Elevator.RestartElevated();
        }

        void DoUninstall()
        {
            var unins = Path.Combine(_appDir, "unins000.exe");
            if (!File.Exists(unins))
            {
                MessageBox.Show(this,
                    "找不到卸载程序 unins000.exe。\n\n" +
                    "如果你用的是绿色版（直接拷 exe 过来跑的），\n" +
                    "手动删除这个目录即可：\n" + _appDir,
                    "卸载渡口", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var r = MessageBox.Show(this,
                "卸载渡口会：\n" +
                "· 切回直连（还原你原来的系统代理设置）\n" +
                "· 停止隧道与本地代理\n" +
                "· 删除程序、开始菜单/桌面快捷方式、开机自启\n" +
                "· 删除账号配置（下次安装需要重新输密钥）\n\n" +
                "继续？",
                "卸载渡口", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);   // 默认选「否」，防误触
            if (r != DialogResult.Yes) return;

            SetBusy(true);
            Log("正在卸载：先恢复直连，再交给卸载程序…");

            // 这一步必须在启动 unins000.exe 之前做完 ——
            // 一旦程序退出就没人还原代理了，用户会直接断网。
            try
            {
                FerryCore.RestoreDirect(_cfg, _appDir);
                if (_rt != null) { try { _rt.Stop(); } catch (Exception) { } _rt = null; }
                FerryCore.StopAll(_cfg, _appDir);
            }
            catch (Exception e) { Log("清理失败（继续卸载）：" + e.Message); }

            try
            {
                var psi = new ProcessStartInfo(unins, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART");
                psi.UseShellExecute = false;
                psi.WorkingDirectory = _appDir;
                Process.Start(psi);
            }
            catch (Exception e)
            {
                SetBusy(false);
                MessageBox.Show(this, "启动卸载程序失败：" + e.Message + "\n\n可以手动运行：\n" + unins,
                    "卸载渡口", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Log("已启动卸载程序，本程序即将退出");
            Thread.Sleep(600);

            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            Environment.Exit(0);
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
                if (Api.CompareVer(v.Latest, mine) <= 0)
                {
                    // 没有更新就完全静默 —— 不留任何痕迹，不刷日志。
                    // 用户的要求是「检查完没更新就静默后台」。
                    return;
                }

                BeginInvoke(new Action(delegate
                {
                    _btnUpd.Text = "更新 " + v.Latest;
                    _btnUpd.Visible = true;
                    Log("发现新版本 " + v.Latest + "（当前 " + mine + "）");

                    // 同一个版本只弹一次。
                    // 为什么需要这个：定时器每 6 小时查一次，如果每次都弹，
                    // 用户选了「以后再说」还是会被反复打扰。
                    if (_updPrompted == v.Latest) return;
                    _updPrompted = v.Latest;

                    var r = MessageBox.Show(this,
                        "发现新版本 " + v.Latest + "（当前 " + mine + "）\n\n" +
                        "现在更新吗？安装会静默覆盖，配置和授权都不受影响。\n" +
                        "选「否」就继续用当前版本，本次运行不再提醒。",
                        "有可用更新", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (r == DialogResult.Yes) DoUpdate();
                }));
            });
        }

        /// <summary>已经弹过提示的版本号。同一个版本不重复弹。</summary>
        string _updPrompted = "";

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

                // TUN 是 borrowing 的附加信息，不是独立分支。
                // 踩过的坑：一开始把它写成独立的 `if (...) { } else if (proxyOn ...)`，
                // 结果那个 else 挂到了新加的 if 上，把「正在走渡口」覆盖成「借网异常」——
                // 界面明明在正常工作却报错。教训：else-if 链中间不能插新分支。
                if (_cfg.UseTun && !(rt != null && rt.TunRunning))
                {
                    var terr = rt != null ? rt.TunError : "";
                    SetText(_lblDesc, "已借网，但全局接管（TUN）未启用 —— 只有认代理的程序生效。" +
                        (terr.Length > 0 ? Environment.NewLine + "原因：" + terr : ""), Theme.Bad);
                }
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

            // 明细行
            //
            // 「隧道」这一行在直连模式下必须换文案。
            // 踩过的坑：直连模式压根不建 SSH 隧道（_tunnel 是 null），
            // 而旧逻辑只要 tunnelUp=false 又 runtimeUp=true 就显示「重连中…」——
            // 用户看到永远在重连，以为坏了，其实一切正常，只是不需要隧道。
            if (_cfg.DirectSocks)
            {
                SetText(_rowTunnel, "隧道        直连模式（不走 SSH）", Theme.Muted);
            }
            else
            {
                SetText(_rowTunnel, "隧道        " + (tunnelUp ? "已连接（端口 " + socksPort + "）"
                        : (runtimeUp ? "重连中…" : "未连接")), tunnelUp ? Theme.Text : Theme.Muted);
            }

            SetText(_rowBridge, "本地代理    " + (bridgeUp ? "运行中（端口 " + httpPort + "）" : "未运行"),
                    bridgeUp ? Theme.Text : Theme.Muted);

            SetText(_rowProxy, "系统代理    " + (proxyOn ? "开  " + (proxySrv.Length > 0 ? proxySrv : "（未设置）") : "关"),
                    borrowing ? Theme.Good : Theme.Text);

            // TUN 状态行。为什么单独一行而不是混在系统代理里：
            // 这两件事是独立的 —— 系统代理可能开着但 TUN 没开（游戏不走），
            // 也可能 TUN 开着但用户手动关了系统代理。分开显示才看得清。
            if (_cfg.UseTun && !Elevator.IsAdmin())
            {
                // 勾了却没提权：不是「故障」，是差一步动作 ——
                // 所以说清楚差什么、去哪儿点，别只写「未开启」。
                SetText(_rowTun, "全局接管    已勾选 · 需管理员（设置里点「以管理员身份重启」）", Theme.Bad);
                _rowTun.Visible = true;
            }
            else if (_cfg.UseTun)
            {
                bool tunUp = rt != null && rt.TunRunning;
                SetText(_rowTun, "全局接管    " + (tunUp ? "已开启（UDP低延迟模式）" : "未开启"),
                        tunUp ? Theme.Good : Theme.Muted);
                _rowTun.Visible = true;
            }
            else
            {
                // 没开全局接管时**不要藏掉这一行**：
                // 藏起来的结果是用户根本不知道有「UDP低延迟模式」这个能力，
                // 于是游戏 / QUIC / 语音一直走不了渡口也说不清为什么。
                SetText(_rowTun, "全局接管    未启用 · 设置里开启「UDP低延迟模式」", Theme.Muted);
                _rowTun.Visible = true;
            }

            _btnOn.Enabled = !_busy && !borrowing;
            _btnOff.Enabled = !_busy && (borrowing || runtimeUp || (proxyOn && proxySrv == borrowServer));
        }

        void SetBusy(bool b)
        {
            _busy = b;
            _btnOn.Enabled = !b;
            _btnOff.Enabled = !b;
            _btnRefresh.Enabled = !b;
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

using System;
using System.IO;
using System.Threading;

namespace Ferry
{
    /// <summary>
    /// 借网的运行时：管住「隧道 + 本地代理」这对组件的生命周期。
    ///
    /// 相比 v0.7 的做法（起一个 node 进程跑 dual-proxy.mjs，再起一个 powershell
    /// 跑 ssh-tunnel-keeper.ps1，然后读 .proxy-state/ports.json 反推端口），
    /// 现在是**同进程内**的 Tunnel + ProxyServer 两个对象：
    ///   · 端口是这里自己分配的，不存在「代理挪了号但系统代理不知道」
    ///   · 停止是同步的，不用去 WMI 里按命令行特征找进程
    ///   · 目标机器不需要装 Node.js
    /// 唯一的外部依赖是 Windows 自带的 ssh.exe。
    /// </summary>
    class FerryRuntime
    {
        readonly Cfg _cfg;
        readonly string _appDir;
        readonly Action<string> _log;

        Tunnel _tunnel;
        ProxyServer _proxy;
        TunMode _tun;
        int _httpPort, _socksPort;
        volatile bool _running;

        public FerryRuntime(Cfg cfg, string appDir, Action<string> log)
        {
            _cfg = cfg;
            _appDir = appDir;
            _log = log ?? delegate { };
        }

        public bool Running { get { return _running; } }
        public int HttpPort { get { return _httpPort; } }
        public int SocksPort { get { return _socksPort; } }
        public bool TunnelAlive { get { return _tunnel != null && _tunnel.Alive; } }
        public bool ProxyAlive { get { return _proxy != null && _proxy.Running; } }
        public int TunnelRestarts { get { return _tunnel == null ? 0 : _tunnel.Restarts; } }
        public long ConnTotal { get { return _proxy == null ? 0 : _proxy.ConnTotal; } }
        /// <summary>下行累计字节（远端 → 本机）。</summary>
        public long BytesDown { get { return _proxy == null ? 0 : _proxy.BytesDown; } }
        /// <summary>上行累计字节（本机 → 远端）。</summary>
        public long BytesUp { get { return _proxy == null ? 0 : _proxy.BytesUp; } }
        public long ConnActive { get { return _proxy == null ? 0 : _proxy.ConnActive; } }

        /// <summary>TUN 是否在跑（全局接管模式）。</summary>
        public bool TunRunning { get { return _tun != null && _tun.Running; } }

        /// <summary>
        /// TUN 是「被主动跳过」而不是「启动失败」。
        ///
        /// 为什么需要区分：界面原来只看 TunError 是否为空 ——
        /// 跳过的场景下没有错误信息，于是一直显示「正在启用…」，永远卡住。
        /// 用户看到的就是「全局模式一直卡在正在启用」。
        /// </summary>
        public bool TunSkipped { get { return _tunSkipped; } }
        bool _tunSkipped = false;
        /// <summary>TUN 启动失败的原因。空字符串 = 没失败或没开。</summary>
        public string TunError { get { return _tunError; } }
        string _tunError = "";

        /// <summary>
        /// 实际用的连接模式："direct"（直连远端 SOCKS5）或 "ssh"（SSH 隧道）。
        ///
        /// 注意这是**运行时**的真相，不是配置意图：直连模式下如果远端连不上，
        /// Start() 会直接返回错误，不会悄悄退回 SSH。所以两者一致。
        /// 管理面板用这个值显示「这台走的是哪条路」。
        /// </summary>
        public string Mode { get { return _cfg.DirectSocks ? "direct" : "ssh"; } }

        /// <summary>系统代理该指向的地址。</summary>
        public string BorrowServer { get { return "http://127.0.0.1:" + _httpPort; } }

        /// <summary>
        /// 启动整套。返回空字符串表示成功，否则是错误原因。
        ///
        /// 端口从 config 的 httpPort 起找空闲的 —— 用户可以在 config 里指定，
        /// 但被占用时会自动往上挪（Clash 常占 10808/10809）。
        /// </summary>
        /// <summary>
        /// 是否允许「国内走渡口 / 境外交给本机代理」的分流。
        ///
        /// 节点模式下必须设成 false：那时用户是把渡口当成一个节点加进他自己的
        /// Clash 的，流量由 Clash 主动送来；再按「境外」送回 Clash 就成环。
        /// </summary>
        public bool AllowSplit = true;

        /// <summary>
        /// 是否允许启用 TUN（全局接管）。默认允许。
        ///
        /// 这里**不再**由「系统代理是否被别人占用」决定 —— 那是两回事：
        ///   · 系统代理是应用层的，只影响认代理的程序；
        ///   · TUN 是网卡层的，抢的是默认路由。
        /// Clash 只开系统代理（tun.enable: false）时，和渡口的 TUN 完全不冲突，
        /// 之前的写法让「开着 Clash」= 「永远不能全局接管」，直接把核心功能废掉了。
        ///
        /// 真正的冲突只有一种：**别的 TUN 网卡也在抢路由**。
        /// 那个由 TunMode.OtherTunAdapter() 检测，检测到就跳过并说明原因。
        /// </summary>
        public bool AllowTun = true;

        /// <summary>被跳过时的原因（给界面显示用）。</summary>
        public string TunSkipReason { get { return _tunSkipReason; } }
        string _tunSkipReason = "";

        /// <summary>本次启动探测到的「别的 TUN」，只探一次（两次结果可能不一致）。</summary>
        string _otherTun = null;
        string OtherTunHere()
        {
            if (_otherTun == null) _otherTun = TunMode.OtherTunAdapter() ?? "";
            return _otherTun.Length == 0 ? null : _otherTun;
        }

        public string Start()
        {
            if (_running) return "";

            _tunSkipped = false;
            _tunSkipReason = "";
            _tunError = "";
            _otherTun = null;

            // SOCKS 和 HTTP 要连续两个空闲端口。先找 SOCKS，再找 HTTP（跳过刚占的）。
            _socksPort = ProxyServer.FindFree(Math.Max(10800, _cfg.SocksPort), 40);
            _httpPort = ProxyServer.FindFree(_socksPort + 1, 40);
            if (_httpPort == _socksPort) { _httpPort = ProxyServer.FindFree(_socksPort + 2, 40); }

            if (_socksPort == _cfg.SocksPort && _httpPort == _cfg.HttpPort)
                _log("端口：SOCKS " + _socksPort + " / HTTP " + _httpPort);
            else
                _log("端口被占用，已调整：SOCKS " + _socksPort + " → HTTP " + _httpPort +
                     "（原设定 " + _cfg.SocksPort + "/" + _cfg.HttpPort + "）");

            // 上游 SOCKS 的位置：直连模式在远端，隧道模式在本机。
            string upstreamHost = "127.0.0.1";
            int upstreamPort = _socksPort;

            if (_cfg.DirectSocks)
            {
                // ── 直连模式（A 方案）──
                //
                // 不启 ssh，直接把上游指向远端 pcs 的 SOCKS5。
                // 这样服务端看到的源 IP 就是本机，能按机器限速。
                // 一个上游同时提供 TCP 和 UDP，比「SSH 管 TCP、另开一路管 UDP」简单。
                //
                // 代价是明文 —— 想加密就关掉 DirectSocks 退回隧道模式。
                upstreamHost = _cfg.RemoteHost;
                upstreamPort = _cfg.RelayPort > 0 ? _cfg.RelayPort : 1080;

                _log("直连模式：上游 " + upstreamHost + ":" + upstreamPort + "（不走 SSH，服务端可限速）");

                bool reach = false;
                for (int i = 0; i < 20; i++)
                {
                    if (PortOpen(upstreamHost, upstreamPort, 600)) { reach = true; break; }
                    Thread.Sleep(300);
                }
                if (!reach)
                    return "连不上远端 SOCKS " + upstreamHost + ":" + upstreamPort +
                           "（确认远端中继在跑、端口没被防火墙挡）";
            }
            else
            {
                // ① 隧道（老路径，加密但限速不生效）
                _tunnel = new Tunnel(_cfg, _socksPort, _log);
                if (!_tunnel.Start()) { _tunnel = null; return "隧道启动失败"; }

                // ② 等 SOCKS 端口真的能连（ssh 建隧道要一点时间）
                bool ok = false;
                for (int i = 0; i < 40; i++)
                {
                    Thread.Sleep(500);
                    if (PortOpen(_socksPort, 300)) { ok = true; break; }
                    if (_tunnel == null || !_tunnel.Alive) break;
                }
                if (!ok)
                {
                    _tunnel.Stop();
                    _tunnel = null;
                    return "隧道没能在 20 秒内建立（检查远端是否可达、密钥是否已授权）";
                }
            }

            // ③ 本地代理：入口还是本机，只是上游可能是远端
            //
            // 直连模式下本地**不再单独开 SOCKS 端口** —— ProxyServer 那个混合端口
            // （_httpPort）本身就同时吃 HTTP 和 SOCKS5，再开一个纯 SOCKS 端口是浪费。
            // 所以把 _socksPort 设成 _httpPort，让 UI 和 ports.json 反映真实情况
            // （以前这里会另外 FindFree 一个端口，但根本没监听它，日志里那个号是假的）。
            if (_cfg.DirectSocks) _socksPort = _httpPort;
            _proxy = new ProxyServer(_httpPort, upstreamPort, upstreamHost, _log);

            // ④ 分流：用户本来就在用 Clash/加速器的话，境外交给它，渡口只管国内。
            //
            // 为什么这么做：用户往往已经有一套翻墙代理。渡口接管系统代理之后，
            // 「墙外」也走渡口出口 —— 而渡口是校内主机，出不去，结果是「借网之后
            // 反而上不了网」。反过来让渡口不接管，用户又得手动配节点，每个 Clash
            // 客户端写法都不一样（Verge 是 Merge.yaml，CFW 是 Parsers，绿色版
            // 路径还不固定），教不会也没法自动化。
            //
            // 所以让渡口自己分流：国内目标走渡口，其余交给用户那个代理。他什么都
            // 不用配，Clash 照常开着就行。
            // TUN 模式下不分流：mihomo 自己已经有分流规则（内网直连 + MATCH ferry），
            // 它送过来的流量就是要走渡口。这里再按「境外」分一次，会把本该走渡口的
            // 流量送回本机代理，绕一圈还可能成环。
            int clashPort = (AllowSplit && !_cfg.UseTun) ? FerryCore.DetectLocalProxy(_httpPort) : 0;
            if (clashPort > 0)
            {
                _proxy.SetForeignProxy(clashPort, "127.0.0.1");
                _log("检测到本机代理 127.0.0.1:" + clashPort +
                     "：国内流量走渡口，境外交给它（不需要在它里面加节点）");
            }
            else
            {
                _log("没检测到其它代理：全部流量走渡口");
            }

            if (!_proxy.Start())
            {
                _proxy = null;
                if (_tunnel != null) { _tunnel.Stop(); _tunnel = null; }
                return "本地代理端口 " + _httpPort + " 起不来";
            }

            _running = true;

            // ⑤ TUN（可选）：在代理已经跑通之后再加一层。
            //
            // 为什么放在最后：TUN 的上游就是本机代理端口。代理没起来就开 TUN，
            //   等于把所有流量导进一个死端口 —— 那几秒会彻底断网。
            //   放在最后，失败了也只是「TUN 不可用」，原有代理照常工作。
            //
            // 为什么上游传 _httpPort 而不是 _socksPort：
            //   _httpPort 是本地 ProxyServer 的混合端口（同时听 HTTP 和 SOCKS5），
            //   让 TUN 走它能把这个层级的字节数也算进去 —— 心跳上报的流量才准。
            //   直接传 _socksPort 会绕过 ProxyServer，TUN 那部分流量面板上看不到。
            //
            // 为什么失败不算整体失败：
            //   TUN 需要管理员 + wintun.dll。缺一个就开不了，但用户借网
            //   本身（浏览器/下载）不依赖它。所以只记 TunError，不 return。
            if (_cfg.UseTun && !AllowTun)
            {
                _tunSkipped = true;
                _tunSkipReason = "已被要求不启用";
                _log("全局接管未启用：本次运行被要求不接管");
            }
            else if (_cfg.UseTun && OtherTunHere() != null)
            {
                // 真正会打架的情况：另一个 TUN 也在抢默认路由
                var other = OtherTunHere();
                _tunSkipped = true;
                _tunSkipReason = "另一个 TUN 正在接管（" + other + "）";
                _log("全局接管未启用：检测到 " + other +
                     " 也在抢默认路由，两个 TUN 会互相覆盖。关掉它再点一次全局模式。");

                // 降级：既然 TUN 起不来，就别再把「境外交给本机代理」也关掉。
                // 否则用户以为开了全局，实际境内外全走渡口 —— 境外必然打不开，
                // 而且没有任何提示。
                if (AllowSplit)
                {
                    int cp = FerryCore.DetectLocalProxy(_httpPort);
                    if (cp > 0)
                    {
                        _proxy.SetForeignProxy(cp, "127.0.0.1");
                        _log("全局起不来，改用分流：国内走渡口，境外交给 127.0.0.1:" + cp);
                    }
                }
            }
            else if (_cfg.UseTun)
            {
                _tunError = "";
                _tun = new TunMode(_cfg, _appDir, _log);
                var terr = _tun.Start(_httpPort, _cfg.UdpRelayHost, _cfg.UdpRelayPort);
                if (terr.Length > 0)
                {
                    _tunError = terr;
                    _log("TUN 未启用：" + terr);
                    _tun = null;
                }
                else
                {
                    _log("TUN 已启用（网卡 " + _tun.DeviceName + "）—— 全局接管生效，UDP 走低延迟链路");
                }
            }

            // ⑥ 写一份 ports.json，让管理员版 PS 工具和诊断脚本也能读到
            WritePortState();
            return "";
        }

        public void Stop()
        {
            _running = false;
            // 顺序很关键，每一层都要在「它依赖的东西」之前停：
            //   TUN（截流量）→ 代理（接连接）→ 隧道（出口）
            // 反过来的话，会出现「TUN 还在截流量、但下面的代理已经停了」的窗口，
            // 那几秒整机断网。
            try { if (_tun != null) _tun.Stop(); } catch (Exception) { }
            _tun = null;
            try { if (_proxy != null) _proxy.Stop(); } catch (Exception) { }
            _proxy = null;
            try { if (_tunnel != null) _tunnel.Stop(); } catch (Exception) { }
            _tunnel = null;
        }

        /// <summary>
        /// 端口状态文件。位置沿用 dual-proxy 的约定：
        /// &lt;appDir 的父目录&gt;/.proxy-state/ports.json
        /// 这样管理员那套 PS 工具（tools/渡口助手.ps1）不用改就能读到。
        /// </summary>
        void WritePortState()
        {
            try
            {
                var p = PortState.FilePath(_appDir);
                var dir = Path.GetDirectoryName(p);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var json =
                    "{\n" +
                    "  \"socks\": " + _socksPort + ",\n" +
                    "  \"http\": " + _httpPort + ",\n" +
                    "  \"panel\": 0,\n" +
                    "  \"mode\": \"client\",\n" +
                    "  \"updatedAt\": \"" + DateTime.UtcNow.ToString("o") + "\"\n" +
                    "}\n";
                File.WriteAllText(p, json, new System.Text.UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        static bool PortOpen(int port, int timeoutMs)
        {
            return PortOpen("127.0.0.1", port, timeoutMs);
        }

        /// <summary>探任意主机的端口。直连模式要探远端，不能只探回环。</summary>
        static bool PortOpen(string host, int port, int timeoutMs)
        {
            try
            {
                using (var c = new System.Net.Sockets.TcpClient())
                {
                    System.Net.IPAddress addr;
                    if (!System.Net.IPAddress.TryParse(host ?? "", out addr))
                        addr = System.Net.IPAddress.Loopback;
                    var ar = c.BeginConnect(addr, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>走代理拿出口 IP —— 和直连对比是「借网到底生效没有」的唯一硬证据。</summary>
        public string ExitIpViaProxy(int timeoutMs)
        {
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("http://myip.ipip.net");
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.KeepAlive = false;
                req.UserAgent = "FerryClient/1.0";
                req.Proxy = new System.Net.WebProxy(BorrowServer);
                using (var resp = req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var r = new StreamReader(s, System.Text.Encoding.UTF8))
                {
                    var text = r.ReadToEnd();
                    // myip.ipip.net 返回「当前 IP：1.2.3.4 来自于：…」，只取 IP
                    var m = System.Text.RegularExpressions.Regex.Match(
                        text, @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})");
                    return m.Success ? m.Groups[1].Value : text.Trim();
                }
            }
            catch (Exception) { return ""; }
        }
    }
}

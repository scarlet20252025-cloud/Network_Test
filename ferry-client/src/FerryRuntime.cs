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
        /// <summary>TUN 启动失败的原因。空字符串 = 没失败或没开。</summary>
        public string TunError { get { return _tunError; } }
        string _tunError = "";

        /// <summary>系统代理该指向的地址。</summary>
        public string BorrowServer { get { return "http://127.0.0.1:" + _httpPort; } }

        /// <summary>
        /// 启动整套。返回空字符串表示成功，否则是错误原因。
        ///
        /// 端口从 config 的 httpPort 起找空闲的 —— 用户可以在 config 里指定，
        /// 但被占用时会自动往上挪（Clash 常占 10808/10809）。
        /// </summary>
        public string Start()
        {
            if (_running) return "";

            // SOCKS 和 HTTP 要连续两个空闲端口。先找 SOCKS，再找 HTTP（跳过刚占的）。
            _socksPort = ProxyServer.FindFree(Math.Max(10800, _cfg.SocksPort), 40);
            _httpPort = ProxyServer.FindFree(_socksPort + 1, 40);
            if (_httpPort == _socksPort) { _httpPort = ProxyServer.FindFree(_socksPort + 2, 40); }

            if (_socksPort == _cfg.SocksPort && _httpPort == _cfg.HttpPort)
                _log("端口：SOCKS " + _socksPort + " / HTTP " + _httpPort);
            else
                _log("端口被占用，已调整：SOCKS " + _socksPort + " → HTTP " + _httpPort +
                     "（原设定 " + _cfg.SocksPort + "/" + _cfg.HttpPort + "）");

            // ① 隧道
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

            // ③ 本地代理
            _proxy = new ProxyServer(_httpPort, _socksPort, _log);
            if (!_proxy.Start())
            {
                _proxy = null;
                _tunnel.Stop();
                _tunnel = null;
                return "本地代理端口 " + _httpPort + " 起不来";
            }

            _running = true;

            // ⑤ TUN（可选）：在 SOCKS 已经跑通之后再加一层。
            //
            // 为什么放在最后而不是最前：
            //   TUN 的上游就是这个 SOCKS 端口。SOCKS 没起来就开 TUN，
            //   等于把所有流量导进一个死端口 —— 那几秒会彻底断网。
            //   放在最后，失败了也只是「TUN 不可用」，原有 SOCKS 照常工作。
            //
            // 为什么失败不算整体失败：
            //   TUN 需要管理员 + wintun.dll。缺一个就开不了，但用户借网
            //   本身（浏览器/下载）不依赖它。所以只记 TunError，不 return。
            if (_cfg.UseTun)
            {
                _tunError = "";
                _tun = new TunMode(_cfg, _appDir, _log);
                var terr = _tun.Start(_socksPort, _cfg.UdpRelayHost, _cfg.UdpRelayPort);
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
            try
            {
                using (var c = new System.Net.Sockets.TcpClient())
                {
                    var ar = c.BeginConnect(System.Net.IPAddress.Loopback, port, null, null);
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ferry
{
    /// <summary>
    /// 本地双协议代理：同一个端口既能接 HTTP 也能接 SOCKS5，上游是 `ssh -D` 的 SOCKS 端口。
    ///
    /// 为什么必须双协议同端口（这是整个项目最反直觉的一处）：
    ///   Windows 系统代理（HKCU Internet Settings 的 ProxyServer）**只认 HTTP 代理**。
    ///   把 ProxyServer 写成 socks5:// 时，浏览器 / Electron / .NET / git 都会把
    ///   SOCKS5 端口当 HTTP 代理去连 —— 秒失败（实测 1.4 ms 返回 000）。
    ///   所以要么让用户装 SwitchyOmega，要么自己在一个端口上识别两种协议。选了后者。
    ///
    /// 识别方式：读首字节。SOCKS5 的 VER 恒为 0x05，HTTP 请求方法首字母是
    /// G/P/H/D/O/C —— 不会撞。
    ///
    /// 为什么用 C# 重写而不是继续用 dual-proxy.mjs：
    ///   原版要目标机器装 Node.js。用户要的是「exe 直接打开包含所有功能」，
    ///   多一个运行时依赖就多一类「装不上/版本不对/被策略拦」的故障。
    ///   Windows 10/11 自带 OpenSSH 客户端，所以只需要 ssh.exe 这一个外部程序。
    /// </summary>
    class ProxyServer
    {
        readonly int _listenPort;
        readonly int _socksPort;
        readonly string _socksHost;
        readonly Action<string> _log;

        /// <summary>
        /// 用户自己的 Clash 的 SOCKS 端口（0 = 没有，不做分流）。
        ///
        /// 为什么要它：用户往往已经有一套翻墙代理（Clash/Verge/加速器）。渡口借网时
        /// 会把系统代理抢过来，结果他连「墙外」也用渡口的出口 —— 而渡口是校内主机，
        /// 出不去。反过来让渡口完全不接管，用户又得手动配节点，每个客户端写法还不一样。
        ///
        /// 折中：渡口照常接管系统代理，但**只把国内流量走渡口**，境外的交给用户
        /// 原本就在用的那个代理。这样他什么都不用配，Clash 照常开着就行。
        /// </summary>
        int _clashPort;
        string _clashHost = "127.0.0.1";

        TcpListener _listener;
        volatile bool _running;
        long _connTotal, _connActive, _bytesUp, _bytesDown;
        long _connRejected;

        /// <summary>同时存在的 UDP 关联数。每条关联要 2 个线程 + 2 个 socket，
        /// 不设上限的话一个坏掉的客户端（反复重连不停）能把线程刷爆。
        /// 32 条足够覆盖「游戏 + 语音 + 几个 P2P」的并发。</summary>
        int _udpAssoc;
        const int MaxUdpAssoc = 32;

        /// <summary>
        /// 同时处理的连接上限。
        ///
        /// 为什么是 400：每个连接占 3 个线程（1 个 conn + 2 个 copy），
        /// 400 连接 = 1200 线程，已经接近「上下文切换开始明显拖慢」的量级。
        /// 正常使用（浏览器几十个标签 + 几个后台程序）远到不了这个数；
        /// 真到了说明有别的东西在刷连接，这时候拒绝比拖死整机好。
        /// </summary>
        const int MaxConns = 400;

        public long ConnRejected { get { return Interlocked.Read(ref _connRejected); } }

        public ProxyServer(int listenPort, int socksPort, Action<string> log)
            : this(listenPort, socksPort, "127.0.0.1", log)
        {
        }

        /// <param name="socksHost">上游 SOCKS 所在主机。直连模式填远端 IP。</param>
        public ProxyServer(int listenPort, int socksPort, string socksHost, Action<string> log)
        {
            _listenPort = listenPort;
            _socksPort = socksPort;
            _socksHost = string.IsNullOrEmpty(socksHost) ? "127.0.0.1" : socksHost;
            _log = log ?? delegate { };
        }

        /// <summary>设置「境外流量交给谁」。port=0 表示不分流（全部走渡口）。</summary>
        public void SetForeignProxy(int port, string host)
        {
            _clashPort = port;
            if (!string.IsNullOrEmpty(host)) _clashHost = host;
        }

        public int Port { get { return _listenPort; } }
        public bool Running { get { return _running; } }
        public long ConnTotal { get { return Interlocked.Read(ref _connTotal); } }
        public long ConnActive { get { return Interlocked.Read(ref _connActive); } }
        public long BytesUp { get { return Interlocked.Read(ref _bytesUp); } }
        public long BytesDown { get { return Interlocked.Read(ref _bytesDown); } }

        /// <summary>
        /// 找一个能监听的端口。从 preferred 开始往后试 40 个 ——
        /// 客户机上 Clash / v2rayN 常占 10808/10809，写死就是「软件起不来」。
        /// </summary>
        public static int FindFree(int preferred, int range)
        {
            for (int p = preferred; p < preferred + range && p < 65535; p++)
            {
                try
                {
                    var l = new TcpListener(IPAddress.Loopback, p);
                    l.Start();
                    l.Stop();
                    return p;
                }
                catch (Exception) { }
            }
            return preferred;
        }

        public bool Start()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, _listenPort);
                _listener.Start();
                _running = true;
                var t = new Thread(AcceptLoop);
                t.IsBackground = true;
                t.Name = "ferry-accept";
                t.Start();
                _log("代理已监听 127.0.0.1:" + _listenPort + "（上游 SOCKS " +
                     _socksHost + ":" + _socksPort + "）");
                return true;
            }
            catch (Exception e)
            {
                _log("代理监听失败（端口 " + _listenPort + "）：" + e.Message);
                return false;
            }
        }

        public void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch (Exception) { }
            _listener = null;
            _log("代理已停止");
        }

        void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try { client = _listener.AcceptTcpClient(); }
                catch (Exception) { if (!_running) return; continue; }

                var c = client;
                Interlocked.Increment(ref _connTotal);

                // 并发上限。超过就**直接拒绝**，不要无限制开线程。
                //
                // 为什么需要：TUN 全局接管时，本机所有程序的所有连接都进这里。
                // 一个浏览器开 20 个标签就是几百连接，每个连接要
                // 1 个 HandleConn 线程 + 2 个 copy 线程 —— 无上限的话
                // 很容易上千线程，上下文切换把整机拖慢，
                // 而且进程句柄数会顶到系统上限（实测 40 连接已 984 句柄）。
                //
                // 拒绝对协议是安全的：HTTP 客户端会看到连接被关并重试，
                // SOCKS5 会失败，都比让整机卡死好。
                long active = Interlocked.Increment(ref _connActive);
                if (active > MaxConns)
                {
                    Interlocked.Decrement(ref _connActive);
                    Interlocked.Increment(ref _connRejected);
                    try { c.Close(); } catch (Exception) { }
                    continue;
                }

                var t = new Thread(delegate () { HandleConn(c); });
                t.IsBackground = true;
                t.Name = "ferry-conn";
                t.Start();
            }
        }

        void HandleConn(TcpClient client)
        {
            NetworkStream ns = null;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 120000;
                client.SendTimeout = 120000;
                ns = client.GetStream();

                // 只读 1 字节判协议，读完还要把这一字节交给对应的处理逻辑
                var first = new byte[1];
                if (!ReadExact(ns, first, 1)) return;

                if (first[0] == 0x05)
                    HandleSocks5(ns, first[0], client.Client.RemoteEndPoint as IPEndPoint);
                else
                    HandleHttp(ns, first[0]);
            }
            catch (Exception) { }
            finally
            {
                Interlocked.Decrement(ref _connActive);
                try { if (ns != null) ns.Close(); } catch (Exception) { }
                try { client.Close(); } catch (Exception) { }
            }
        }

        // ── SOCKS5（客户端 → 我）──────────────────────────────

        void HandleSocks5(NetworkStream ns, byte ver, IPEndPoint clientEp)
        {
            // 我们已经读了 VER，接着读 NMETHODS + METHODS
            var buf = new byte[1];
            if (!ReadExact(ns, buf, 1)) return;
            int nMethods = buf[0];
            var methods = new byte[nMethods];
            if (nMethods > 0 && !ReadExact(ns, methods, nMethods)) return;

            // 只支持「无认证」。我们只听 127.0.0.1，不需要账号密码。
            ns.Write(new byte[] { 0x05, 0x00 }, 0, 2);

            var head = new byte[4];
            if (!ReadExact(ns, head, 4)) return;
            if (head[0] != 0x05) return;
            byte cmd = head[1];
            byte atyp = head[3];

            string host;
            if (atyp == 0x01)
            {
                var a = new byte[4];
                if (!ReadExact(ns, a, 4)) return;
                host = new IPAddress(a).ToString();
            }
            else if (atyp == 0x03)
            {
                var l = new byte[1];
                if (!ReadExact(ns, l, 1)) return;
                var d = new byte[l[0]];
                if (l[0] > 0 && !ReadExact(ns, d, l[0])) return;
                host = Encoding.ASCII.GetString(d);
            }
            else if (atyp == 0x04)
            {
                var a = new byte[16];
                if (!ReadExact(ns, a, 16)) return;
                host = new IPAddress(a).ToString();
            }
            else { ReplySocks5(ns, 0x08); return; }

            var pb = new byte[2];
            if (!ReadExact(ns, pb, 2)) return;
            int port = (pb[0] << 8) | pb[1];

            // RFC 1928：0x01 = CONNECT，0x03 = UDP ASSOCIATE。
            // 远端 SOCKS5 服务器从 v2 起就支持 UDP ASSOCIATE 了，本地这一跳以前
            // 直接回「命令不支持」—— 所以「节点模式」「游戏加速」一直没 UDP 可用。
            if (cmd == 0x03)
            {
                StartUdpAssociate(ns, clientEp);
                return;
            }
            if (cmd != 0x01) { ReplySocks5(ns, 0x07); return; } // 其余命令不支持

            Socket upstream = null;
            try
            {
                upstream = ConnectSmart(host, port, 8000);
                if (upstream == null) { ReplySocks5(ns, 0x05); return; }
                ReplySocks5(ns, 0x00);
                Pump(ns, new NetworkStream(upstream, true));
            }
            catch (Exception) { try { ReplySocks5(ns, 0x01); } catch (Exception) { } }
            finally { try { if (upstream != null) upstream.Close(); } catch (Exception) { } }
        }

        /// <summary>
        /// SOCKS5 UDP ASSOCIATE（RFC 1928 CMD=0x03）。
        ///
        /// 链路：客户端 → 本地中继(127.0.0.1:随机) → 上游 SOCKS5 分配的 UDP 端点 → 目标。
        ///
        /// **为什么本地要再向上游发一次 ASSOCIATE**：上游（`ferry-socks5-server.py`）
        /// 不是固定监听一个 UDP 端口，而是**每建立一条 ASSOCIATE 就新开一个随机 UDP
        /// socket**，把端口放在 BND 里告诉你。所以直接把报文往 1080 发是没人收的
        /// （实测：收到 ICMP port unreachable）。本地这一跳必须同时当「服务端」和
        /// 「客户端」：对上给客户端一个端口，对下向上游要一个端口。
        ///
        /// 两个要点：
        ///   ① 只放行「建立关联的那个客户端地址」——否则就是一个开放 UDP 中继，
        ///      同一局域网任何人都能拿它当出口；
        ///   ② 客户端 TCP 控制连接一断就回收（RFC 要求，也防止崩溃后中继一直挂着）。
        /// </summary>
        void StartUdpAssociate(NetworkStream ns, IPEndPoint clientEp)
        {
            if (Interlocked.Increment(ref _udpAssoc) > MaxUdpAssoc)
            {
                Interlocked.Decrement(ref _udpAssoc);
                _log("UDP 关联数已达上限 " + MaxUdpAssoc + "，拒绝新关联（" + clientEp + "）");
                ReplySocks5(ns, 0x07);
                return;
            }
            try { UdpAssociateCore(ns, clientEp); }
            finally { Interlocked.Decrement(ref _udpAssoc); }
        }

        void UdpAssociateCore(NetworkStream ns, IPEndPoint clientEp)
        {
            Socket upCtrl = null, clientSock = null, remoteSock = null;
            try
            {
                // ① 先向上游要一个 UDP 端点
                IPEndPoint upEp;
                upCtrl = AssociateUpstream(_socksPort, _socksHost, out upEp, 8000);
                if (upCtrl == null)
                {
                    _log("UDP 关联失败：上游不支持或连不上 " + _socksHost + ":" + _socksPort);
                    ReplySocks5(ns, 0x01);
                    return;
                }

                // ② 给客户端准备本地中继端口
                clientSock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                clientSock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int relayPort = ((IPEndPoint)clientSock.LocalEndPoint).Port;

                remoteSock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                remoteSock.Bind(new IPEndPoint(IPAddress.Any, 0));

                var bnd = new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1,
                                       (byte)(relayPort >> 8), (byte)(relayPort & 0xFF) };
                ns.Write(bnd, 0, bnd.Length);
                ns.Flush();
                _log("UDP 关联 " + clientEp + " → 127.0.0.1:" + relayPort + " ⇒ 上游 " + upEp);

                var cs = clientSock; var rs = remoteSock;
                var ctrl = upCtrl;
                bool stop = false;

                // 客户端发 UDP 用的端口和它 TCP 控制连接的端口**不是同一个**
                // （TCP 是它连我们时随机选的，UDP 又另选一个）。回包必须发给
                // 实际收到包的那个地址，否则客户端永远收不到 —— 实测踩过：
                // 用 TCP 的 clientEp 回包时，客户端干等到超时。
                IPEndPoint udpPeer = null;

                var upThread = new Thread(delegate ()
                {
                    var buf = new byte[65535];
                    while (!stop && _running)
                    {
                        try
                        {
                            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                            int n = cs.ReceiveFrom(buf, ref from);
                            var fe = from as IPEndPoint;
                            // 只接受关联的客户端（同 IP 即可，端口由客户端自己决定）
                            if (fe != null && fe.Address.Equals(clientEp.Address) && n > 0)
                            {
                                udpPeer = fe;          // 记下真实 UDP 源地址
                                rs.SendTo(buf, 0, n, SocketFlags.None, upEp);
                            }
                        }
                        catch (Exception) { break; }
                    }
                });
                upThread.IsBackground = true;
                upThread.Start();

                var downThread = new Thread(delegate ()
                {
                    var buf = new byte[65535];
                    while (!stop && _running)
                    {
                        try
                        {
                            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                            int n = rs.ReceiveFrom(buf, ref from);
                            var peer = udpPeer ?? clientEp;
                            if (n > 0) cs.SendTo(buf, 0, n, SocketFlags.None, peer);
                        }
                        catch (Exception) { break; }
                    }
                });
                downThread.IsBackground = true;
                downThread.Start();

                // 阻塞在客户端的控制连接上：它一关，关联就结束
                try
                {
                    var sink = new byte[16];
                    while (ns.Read(sink, 0, sink.Length) > 0) { }
                }
                catch (Exception) { }

                stop = true;
                try { cs.Close(); } catch (Exception) { }
                try { rs.Close(); } catch (Exception) { }
                try { ctrl.Close(); } catch (Exception) { }
                _log("UDP 关联结束：" + clientEp);
            }
            catch (Exception)
            {
                try { if (clientSock != null) clientSock.Close(); } catch (Exception) { }
                try { if (remoteSock != null) remoteSock.Close(); } catch (Exception) { }
                try { if (upCtrl != null) upCtrl.Close(); } catch (Exception) { }
            }
        }

        /// <summary>
        /// 向上游 SOCKS5 发 UDP ASSOCIATE，返回远端分配的 UDP 端点。
        /// 返回的 Socket 是**控制连接**，必须保持打开 —— 关掉它远端的 UDP 中继就回收了。
        /// </summary>
        static Socket AssociateUpstream(int socksPort, string socksHost, out IPEndPoint udpEp, int timeoutMs)
        {
            udpEp = null;
            Socket s = null;
            try
            {
                IPAddress up = null;
                if (!IPAddress.TryParse(socksHost, out up))
                {
                    foreach (var a in Dns.GetHostAddresses(socksHost))
                        if (a.AddressFamily == AddressFamily.InterNetwork) { up = a; break; }
                }
                if (up == null) return null;

                s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                s.NoDelay = true;
                var ar = s.BeginConnect(new IPEndPoint(up, socksPort), null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) { s.Close(); return null; }
                s.EndConnect(ar);

                var sns = new NetworkStream(s, false);
                sns.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);
                var r = new byte[2];
                if (!ReadExact(sns, r, 2) || r[0] != 0x05) { s.Close(); return null; }

                // CMD=0x03，DST 填 0.0.0.0:0 —— 「我不知道自己会从哪个端口发」
                sns.Write(new byte[] { 0x05, 0x03, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, 0, 10);

                var head = new byte[4];
                if (!ReadExact(sns, head, 4) || head[1] != 0x00) { s.Close(); return null; }

                string bhost; int bport;
                if (!ReadSocksAddr(sns, head[3], out bhost, out bport) || bport == 0)
                {
                    s.Close();
                    return null;
                }

                IPAddress bip;
                if (!IPAddress.TryParse(bhost, out bip) || bip.Equals(IPAddress.Any)) bip = up;
                udpEp = new IPEndPoint(bip, bport);
                return s;
            }
            catch (Exception)
            {
                try { if (s != null) s.Close(); } catch (Exception) { }
                return null;
            }
        }

        /// <summary>读 SOCKS5 回复里的地址部分（ATYP 已在 head[3]）。</summary>
        static bool ReadSocksAddr(NetworkStream sns, byte atyp, out string host, out int port)
        {
            host = ""; port = 0;
            if (atyp == 0x01)
            {
                var a = new byte[4];
                if (!ReadExact(sns, a, 4)) return false;
                host = new IPAddress(a).ToString();
            }
            else if (atyp == 0x03)
            {
                var l = new byte[1];
                if (!ReadExact(sns, l, 1)) return false;
                var d = new byte[l[0]];
                if (l[0] > 0 && !ReadExact(sns, d, l[0])) return false;
                host = Encoding.ASCII.GetString(d);
            }
            else if (atyp == 0x04)
            {
                var a = new byte[16];
                if (!ReadExact(sns, a, 16)) return false;
                host = new IPAddress(a).ToString();
            }
            else return false;

            var pb = new byte[2];
            if (!ReadExact(sns, pb, 2)) return false;
            port = (pb[0] << 8) | pb[1];
            return true;
        }

        static void ReplySocks5(NetworkStream ns, byte rep)
        {
            // 0x00 REP | 0x00 RSV | 0x01 ATYP(IPv4) | 0.0.0.0 | 0
            ns.Write(new byte[] { 0x05, rep, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, 0, 10);
        }

        // ── HTTP 代理（客户端 → 我）───────────────────────────

        // ══════════════════ 分流：国内走渡口，境外交给用户的 Clash ══════════════════

        /// <summary>
        /// 按目标决定走哪条上游。连不上就换另一条（互为兜底），所以任何一侧挂了
        /// 都不至于整个断网。
        /// </summary>
        Socket ConnectSmart(string host, int port, int timeoutMs)
        {
            bool cn = IsDomestic(host);

            if (_clashPort <= 0)
                return ConnectSocks(_socksPort, host, port, timeoutMs, _socksHost);

            if (cn)
            {
                var s1 = ConnectSocks(_socksPort, host, port, timeoutMs, _socksHost);
                if (s1 != null) return s1;
                _log("国内目标连不上，改走本机代理：" + host);
                return ConnectSocks(_clashPort, host, port, timeoutMs, _clashHost);
            }

            var s2 = ConnectSocks(_clashPort, host, port, timeoutMs, _clashHost);
            if (s2 != null) return s2;
            _log("本机代理连不上，改走渡口：" + host);
            return ConnectSocks(_socksPort, host, port, timeoutMs, _socksHost);
        }

        /// <summary>
        /// 这个目标是「国内」的吗？
        ///
        /// 判据刻意做得粗糙且保守：**只有明确解析得到国内 IP 才算国内**。
        /// 因为判错的代价不对称 —— 把国内站判成境外，走 Clash 也还是能上（慢一点）；
        /// 把墙外站判成国内，走渡口就永远打不开。
        /// </summary>
        bool IsDomestic(string host)
        {
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip))
                return IsDomesticIp(ip);

            // 域名：问国内 DNS。解析得到 → 这域名在国内是通的 → 走渡口。
            // 解析不到（被墙的域名在国内 DNS 上通常也是空的或污染）→ 交给 Clash。
            var addrs = ResolveCn(host);
            if (addrs == null || addrs.Count == 0) return false;
            foreach (var a in addrs)
                if (IsDomesticIp(a)) return true;
            return false;
        }

        static readonly string[] CnDns = new string[] { "223.5.5.5", "119.29.29.29", "114.114.114.114" };

        static List<IPAddress> ResolveCn(string host)
        {
            foreach (var dns in CnDns)
            {
                try
                {
                    var req = BuildDnsQuery(host);
                    var ep = new IPEndPoint(IPAddress.Parse(dns), 53);
                    using (var u = new System.Net.Sockets.UdpClient())
                    {
                        u.Client.ReceiveTimeout = 1200;
                        u.Send(req, req.Length, ep);
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        var resp = u.Receive(ref from);
                        var got = ParseDnsAnswers(resp);
                        if (got.Count > 0) return got;
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        static byte[] BuildDnsQuery(string host)
        {
            var ms = new MemoryStream();
            ms.Write(new byte[] { 0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, 0, 12);
            foreach (var part in host.Split('.'))
            {
                var b = Encoding.ASCII.GetBytes(part);
                if (b.Length > 63) b = new byte[0];
                ms.WriteByte((byte)b.Length);
                ms.Write(b, 0, b.Length);
            }
            ms.Write(new byte[] { 0, 0, 0x01, 0, 0x01 }, 0, 5);
            return ms.ToArray();
        }

        static List<IPAddress> ParseDnsAnswers(byte[] resp)
        {
            var list = new List<IPAddress>();
            try
            {
                if (resp == null || resp.Length < 12) return list;
                int qd = (resp[4] << 8) | resp[5];
                int an = (resp[6] << 8) | resp[7];
                int p = 12;
                for (int i = 0; i < qd; i++)
                {
                    while (p < resp.Length && resp[p] != 0)
                        p += (resp[p] & 0xC0) == 0xC0 ? 2 : resp[p] + 1;
                    if (p < resp.Length && resp[p] == 0) p++;
                    p += 4;
                }
                for (int i = 0; i < an && p + 12 <= resp.Length; i++)
                {
                    if ((resp[p] & 0xC0) == 0xC0) p += 2;
                    else { while (p < resp.Length && resp[p] != 0) p += resp[p] + 1; p++; }
                    if (p + 10 > resp.Length) break;
                    int type = (resp[p] << 8) | resp[p + 1];
                    int rdlen = (resp[p + 8] << 8) | resp[p + 9];
                    p += 10;
                    if (type == 1 && rdlen == 4 && p + 4 <= resp.Length)
                        list.Add(new IPAddress(new byte[] { resp[p], resp[p + 1], resp[p + 2], resp[p + 3] }));
                    p += rdlen;
                }
            }
            catch (Exception) { }
            return list;
        }

        /// <summary>
        /// 粗判「这个 IP 是不是国内」。不追求精确（那要 GeoIP 库，几十 MB），
        /// 只把「几乎肯定不是国内」的排掉：私网、保留段、以及已知的境外大块。
        /// 剩下的当作国内 —— 因为判成国内的代价（走渡口）对国内站是**正确**的，
        /// 而真正的墙外站基本都是 Cloudflare/Google/AWS 这些一眼能认的段。
        /// </summary>
        static bool IsDomesticIp(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = ip.GetAddressBytes();

            // 私网 / 回环 / 链路本地 / 保留：都当国内（直连语义）
            if (b[0] == 10 || b[0] == 127) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            if (b[0] >= 224) return true;

            // 已知境外大块（这些段里的服务在国内基本要翻墙，或者翻墙更快）
            if (b[0] == 8 && b[1] == 8) return false;                       // 8.8.8.8
            if (b[0] == 1 && b[1] == 1) return false;                        // 1.1.1.1
            if (b[0] == 104 && b[1] >= 16 && b[1] <= 31) return false;       // Cloudflare
            if (b[0] == 172 && b[1] == 67) return false;                     // Cloudflare
            if (b[0] == 162 && b[1] == 159) return false;                    // Cloudflare
            if (b[0] == 142 && b[1] == 250) return false;                    // Cloudflare
            if (b[0] == 188 && b[1] == 114) return false;                    // Cloudflare DNS
            if (b[0] == 31 && b[1] >= 13 && b[1] <= 14) return false;        // 31.13.x  Facebook
            if (b[0] == 157 && b[1] == 240) return false;                    // Facebook
            if (b[0] == 74 && b[1] >= 86 && b[1] <= 125) return false;       // Facebook
            if (b[0] == 216 && b[1] == 58) return false;                     // Google
            if (b[0] == 142 && b[1] >= 250 && b[1] <= 251) return false;     // Google
            if (b[0] == 34 && b[1] >= 64 && b[1] <= 128) return false;       // Google Cloud
            if (b[0] == 35 && b[1] >= 184 && b[1] <= 255) return false;      // Google Cloud
            if (b[0] == 13 && b[1] >= 32 && b[1] <= 35) return false;        // AWS
            if (b[0] == 52 && b[1] >= 0 && b[1] <= 95) return false;         // AWS
            if (b[0] == 54 && b[1] >= 64 && b[1] <= 255) return false;       // AWS
            if (b[0] == 18 && b[1] >= 128 && b[1] <= 255) return false;      // AWS
            if (b[0] == 151 && b[1] == 101) return false;                    // Discord
            if (b[0] == 162 && b[1] == 125) return false;                    // Discord
            if (b[0] == 199 && b[1] == 232) return false;                    // GitHub
            if (b[0] == 140 && b[1] == 82) return false;                     // GitHub
            if (b[0] == 185 && b[1] == 199) return false;                    // GitHub
            if (b[0] == 91 && b[1] == 108) return false;                     // Telegram
            if (b[0] == 149 && b[1] == 154) return false;                    // Telegram
            if (b[0] == 66 && b[1] == 220) return false;                     // Wikipedia
            if (b[0] == 208 && b[1] == 80) return false;                     // Wikipedia

            return true;
        }

        void HandleHttp(NetworkStream ns, byte firstByte)
        {
            var header = ReadHeader(ns, firstByte, 65536);
            if (header == null || header.Length == 0) return;
            var text = Encoding.ASCII.GetString(header);

            int nl = text.IndexOf("\r\n");
            if (nl < 0) return;
            var requestLine = text.Substring(0, nl);
            var parts = requestLine.Split(' ');
            if (parts.Length < 3) return;

            var method = parts[0].ToUpperInvariant();
            var target = parts[1];

            string host;
            int port;
            bool isConnect = method == "CONNECT";

            if (isConnect)
            {
                // CONNECT example.com:443 HTTP/1.1
                if (!SplitHostPort(target, 443, out host, out port)) return;
            }
            else
            {
                // GET http://example.com/path HTTP/1.1  （绝对 URI 形式）
                if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    var rest = target.Substring(7);
                    int slash = rest.IndexOf('/');
                    var hostpart = slash >= 0 ? rest.Substring(0, slash) : rest;
                    if (!SplitHostPort(hostpart, 80, out host, out port)) return;
                }
                else
                {
                    // 相对路径：只能从 Host 头拿（浏览器直连代理时一般不会这样）
                    host = FindHeader(text, "Host");
                    port = 80;
                    if (string.IsNullOrEmpty(host) || !SplitHostPort(host, 80, out host, out port)) return;
                }
            }

            Socket upstream = null;
            try
            {
                upstream = ConnectSmart(host, port, 8000);
                if (upstream == null)
                {
                    var err = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 24\r\nConnection: close\r\n\r\n上游隧道不可用。请稍后再试。");
                    ns.Write(err, 0, err.Length);
                    return;
                }
                var ups = new NetworkStream(upstream, true);

                if (isConnect)
                {
                    var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                    ns.Write(ok, 0, ok.Length);
                    Pump(ns, ups);
                }
                else
                {
                    // 把绝对 URI 改写成相对路径再转发 —— 标准 HTTP 代理行为
                    var rewritten = RewriteAbsoluteUri(text, target);
                    var body = Encoding.ASCII.GetBytes(rewritten);
                    ups.Write(body, 0, body.Length);
                    Interlocked.Add(ref _bytesUp, body.Length);

                    // 请求体（POST）也要搬过去：Content-Length 决定的字节数
                    var lenStr = FindHeader(text, "Content-Length");
                    int len;
                    if (!string.IsNullOrEmpty(lenStr) && int.TryParse(lenStr, out len) && len > 0)
                    {
                        var bodyBuf = new byte[Math.Min(len, 1 << 20)];
                        int remaining = len;
                        while (remaining > 0)
                        {
                            int want = Math.Min(remaining, bodyBuf.Length);
                            int got = ns.Read(bodyBuf, 0, want);
                            if (got <= 0) break;
                            ups.Write(bodyBuf, 0, got);
                            Interlocked.Add(ref _bytesUp, got);
                            remaining -= got;
                        }
                    }

                    Pump(ns, ups);
                }
            }
            catch (Exception) { }
            finally { try { if (upstream != null) upstream.Close(); } catch (Exception) { } }
        }

        static string RewriteAbsoluteUri(string head, string absolute)
        {
            // 只替换请求行里的 URI，其余原样
            int nl = head.IndexOf("\r\n");
            if (nl < 0) return head;
            var line = head.Substring(0, nl);
            var rest = head.Substring(nl);
            var parts = line.Split(' ');
            if (parts.Length < 3) return head;
            string path = "/";
            if (absolute.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                var r = absolute.Substring(7);
                int slash = r.IndexOf('/');
                path = slash >= 0 ? r.Substring(slash) : "/";
            }
            else if (absolute.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var r = absolute.Substring(8);
                int slash = r.IndexOf('/');
                path = slash >= 0 ? r.Substring(slash) : "/";
            }
            return parts[0] + " " + path + " " + parts[2] + rest;
        }

        // ── SOCKS5 客户端（我 → ssh -D）───────────────────────

        /// <summary>
        /// 连本地 ssh -D 的 SOCKS5 端口并 CONNECT 到目标。
        /// 返回已建立的 Socket，失败返回 null。
        ///
        /// socksHost 默认 127.0.0.1（SSH 隧道的本地端口）。
        /// 直连模式传远端 IP —— 这时上游是 pcs 上的 ferry-socks5-server.py。
        ///
        /// 为什么直连模式要单独一路：SSH 隧道转发时远端看到的源 IP 全是
        /// 127.0.0.1，服务端没法按机器限速。直连才能让服务端识别是谁。
        /// </summary>
        public static Socket ConnectSocks(int socksPort, string host, int port, int timeoutMs,
                                          string socksHost = "127.0.0.1")
        {
            Socket s = null;
            try
            {
                s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                s.NoDelay = true;
                IPAddress up;
                if (!IPAddress.TryParse(socksHost ?? "127.0.0.1", out up))
                    up = IPAddress.Loopback;
                var ar = s.BeginConnect(up, socksPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) { s.Close(); return null; }
                s.EndConnect(ar);

                var ns = new NetworkStream(s, true);
                s.ReceiveTimeout = timeoutMs;

                // 握手：05 01 00（版本/1种方法/无认证）
                ns.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);
                var resp = new byte[2];
                if (!ReadExact(ns, resp, 2) || resp[0] != 0x05 || resp[1] != 0x00) { s.Close(); return null; }

                // CONNECT 请求
                var req = new List<byte>();
                req.Add(0x05); req.Add(0x01); req.Add(0x00);

                IPAddress ip;
                if (IPAddress.TryParse(host, out ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    req.Add(0x01);
                    req.AddRange(ip.GetAddressBytes());
                }
                else
                {
                    var hb = Encoding.ASCII.GetBytes(host);
                    if (hb.Length > 255) { s.Close(); return null; }
                    req.Add(0x03);
                    req.Add((byte)hb.Length);
                    req.AddRange(hb);
                }
                req.Add((byte)(port >> 8));
                req.Add((byte)(port & 0xFF));
                ns.Write(req.ToArray(), 0, req.Count);

                var head = new byte[4];
                if (!ReadExact(ns, head, 4) || head[1] != 0x00) { s.Close(); return null; }

                // 吃掉 BND.ADDR + BND.PORT
                int skip;
                if (head[3] == 0x01) skip = 4 + 2;
                else if (head[3] == 0x03)
                {
                    var l = new byte[1];
                    if (!ReadExact(ns, l, 1)) { s.Close(); return null; }
                    skip = l[0] + 2;
                }
                else if (head[3] == 0x04) skip = 16 + 2;
                else { s.Close(); return null; }

                var junk = new byte[skip];
                if (skip > 0 && !ReadExact(ns, junk, skip)) { s.Close(); return null; }

                s.ReceiveTimeout = 120000;
                return s;
            }
            catch (Exception)
            {
                try { if (s != null) s.Close(); } catch (Exception) { }
                return null;
            }
        }

        // ── 双向搬运 ───────────────────────────────────────────

        void Pump(NetworkStream a, NetworkStream b)
        {
            // 关键：任一方向结束就收尾，绝不等两个方向都 EOF。
            // HTTP 请求体传完后客户端就不再写、只等响应；
            // 如果等两个方向都结束，会一直卡到超时（实测卡了 90 秒以上）。
            Action<NetworkStream, NetworkStream, bool> copy = delegate (NetworkStream src, NetworkStream dst, bool up)
            {
                try
                {
                    // 64 KB。原来 16 KB 在千兆/局域网里的单流吞吐上不去 ——
                    // 每次 Read/Write 系统调用有固定开销，块越大同样的带宽下
                    // 调用次数越少。实测把单流上限从 ~5 MB/s 抬到 10 MB/s 以上。
                    var buf = new byte[65536];
                    for (; ; )
                    {
                        int n = src.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        dst.Write(buf, 0, n);
                        if (up) Interlocked.Add(ref _bytesUp, n);
                        else Interlocked.Add(ref _bytesDown, n);
                    }
                }
                catch (Exception) { }
            };

            // ⚠ 用**专用线程**，不用 Task.Factory.StartNew。
            //
            // 踩过的坑：原来这里两个方向都走 Task.Factory.StartNew，而 copy()
            // 里是同步阻塞的 src.Read —— 这是线程池反模式。
            // 线程池遇到「一堆阻塞任务」会每秒只注入 1~2 个新线程，
            // 连接一多就严重积压：新连接迟迟拿不到执行机会，
            // 而同一线程池上还有心跳上报、授权检查、UI 回调在排队 ——
            // 整机都跟着卡。实测 40 个连接时客户端已经 61 线程。
            //
            // 换成未托管的专用线程：每个方向一个，生命周期明确，
            // 不占用线程池配额，卡也只卡它自己那条连接。
            var anyDone = new ManualResetEventSlim(false);

            Thread th1 = new Thread(delegate ()
            {
                try { copy(a, b, true); } catch (Exception) { } finally { anyDone.Set(); }
            });
            Thread th2 = new Thread(delegate ()
            {
                try { copy(b, a, false); } catch (Exception) { } finally { anyDone.Set(); }
            });
            th1.IsBackground = true;
            th2.IsBackground = true;
            th1.Name = "ferry-copy-up";
            th2.Name = "ferry-copy-down";
            th1.Start();
            th2.Start();

            // 任一方向结束就收尾，绝不等两个都 EOF。
            // 超长兜底 6 小时：防止某个 socket 卡死在半开状态导致线程永久泄漏。
            anyDone.Wait(TimeSpan.FromHours(6));

            // 一端结束 → 两端都关，另一端自然退出
            try { a.Close(); } catch (Exception) { }
            try { b.Close(); } catch (Exception) { }
            th1.Join(3000);
            th2.Join(3000);
            anyDone.Dispose();
        }

        // ── 小工具 ──────────────────────────────────────────────

        static bool ReadExact(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n;
                try { n = s.Read(buf, off, count - off); }
                catch (Exception) { return false; }
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        /// <summary>读到 \r\n\r\n 为止（含）。首字节已经读掉了，先塞回去。</summary>
        static byte[] ReadHeader(Stream s, byte firstByte, int maxLen)
        {
            var ms = new MemoryStream();
            ms.WriteByte(firstByte);
            int state = 0;
            for (; ; )
            {
                int v;
                try { v = s.ReadByte(); }
                catch (Exception) { return null; }
                if (v < 0) break;
                ms.WriteByte((byte)v);
                if (ms.Length > maxLen) break;
                // 匹配 \r\n\r\n
                if (v == '\n')
                {
                    state++;
                    if (state == 2)
                    {
                        var arr = ms.ToArray();
                        // 末尾必须是 \r\n\r\n 四字节。
                        // 踩过的坑：原来写成 arr[Len-2]=='\n' 恒不成立
                        //（'...\r\n\r\n' 的 Len-2 其实是 '\r'），于是请求头
                        // 永远读不完，客户端一路等到超时（实测 90 秒+）。
                        int L = arr.Length;
                        if (L >= 4 && arr[L - 4] == '\r' && arr[L - 3] == '\n' &&
                                      arr[L - 2] == '\r' && arr[L - 1] == '\n')
                            return arr;
                    }
                }
                else if (v != '\r') state = 0;
            }
            return ms.ToArray();
        }

        static bool SplitHostPort(string s, int defaultPort, out string host, out int port)
        {
            host = "";
            port = defaultPort;
            if (string.IsNullOrEmpty(s)) return false;
            if (s[0] == '[') // [::1]:443
            {
                int close = s.IndexOf(']');
                if (close < 0) return false;
                host = s.Substring(1, close - 1);
                var rest = s.Substring(close + 1);
                if (rest.StartsWith(":")) { int p; if (int.TryParse(rest.Substring(1), out p)) port = p; }
                return host.Length > 0;
            }
            int colon = s.LastIndexOf(':');
            if (colon > 0 && s.IndexOf(':') == colon)
            {
                host = s.Substring(0, colon);
                int p;
                if (int.TryParse(s.Substring(colon + 1), out p)) port = p;
            }
            else host = s;
            return host.Length > 0;
        }

        static string FindHeader(string head, string name)
        {
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c <= 0) continue;
                if (string.Equals(lines[i].Substring(0, c).Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return lines[i].Substring(c + 1).Trim();
            }
            return "";
        }
    }
}

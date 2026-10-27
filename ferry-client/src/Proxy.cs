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

        /// <summary>已经记过日志的告警 key（见 LogOnce）。</summary>
        readonly HashSet<string> _loggedOnce = new HashSet<string>();

        TcpListener _listener;
        volatile bool _running;
        long _connTotal, _connActive, _bytesUp, _bytesDown;
        long _connRejected;

        /// <summary>同时存在的 UDP 关联数。每条关联要 2 个线程 + 2 个 socket，
        /// 不设上限的话一个坏掉的客户端（反复重连不停）能把线程刷爆。
        /// 32 条足够覆盖「游戏 + 语音 + 几个 P2P」的并发。</summary>
        int _udpAssoc;
        const int MaxUdpAssoc = 96;

        // ── UDP 关联的统计与降噪 ──
        //
        // mihomo 给每个 UDP 流建一个关联（语音、游戏、浏览器的 QUIC 各算各的），
        // 每秒能建好几个。如果每条都写日志，界面会被整屏刷掉，真正要看的东西
        // （失败、被拒）反而看不见 —— 用户反馈的「怎么一直刷这个」就是这个。
        // 改成 60 秒汇总一行。
        static int _udpAssocNow;
        static int _udpAssocPeak;
        static long _udpPktsUp, _udpPktsDown;
        static DateTime _udpStatsNext = DateTime.UtcNow.AddSeconds(60);
        static readonly object _udpStatsLock = new object();

        void UdpAssocOpened()
        {
            int now = Interlocked.Increment(ref _udpAssocNow);
            lock (_udpStatsLock) { if (now > _udpAssocPeak) _udpAssocPeak = now; }
            UdpStatsTick();
        }

        void UdpAssocClosed()
        {
            Interlocked.Decrement(ref _udpAssocNow);
            UdpStatsTick();
        }

        void UdpStatsTick()
        {
            lock (_udpStatsLock)
            {
                if (DateTime.UtcNow < _udpStatsNext) return;
                _udpStatsNext = DateTime.UtcNow.AddSeconds(60);
                _log("UDP 关联 " + _udpAssocNow + " 个（峰值 " + _udpAssocPeak + "），"
                     + "累计上下行包 " + Interlocked.Read(ref _udpPktsUp) + " / "
                     + Interlocked.Read(ref _udpPktsDown));
                _udpAssocPeak = _udpAssocNow;
            }
        }

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

        /// <summary>
        /// 设置「境外流量交给谁」。port=0 表示不分流（全部走渡口）。
        ///
        /// 什么时候**不**分流（port 传 0）：
        ///   · 节点模式：用户把渡口当成一个节点加进了他自己的 Clash，流量是 Clash
        ///     主动送过来的。这时候再按「境外」把流量送回 Clash，就成环了
        ///     （Clash → 渡口 → Clash）。
        ///   · 本机没检测到别的代理：没有可以交接的对象。
        /// </summary>
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
                // ① 先向上游要一个 UDP 端点。
                //
                // 为什么 UDP 不跟 TCP 一样按目标分流：SOCKS5 的 UDP ASSOCIATE
                // 是**一个关联承载所有目标**（每条数据报自己带目标地址），
                // 建立关联时根本不知道后面会发给谁。所以 UDP 只能选一条上游，
                // 这里选渡口（用户要的是「借网打游戏」，那是国内游戏服务器）。
                // 境外 UDP 走不通——但渡口本来就不做翻墙，这是明确的边界。
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

                // 不再每条都写日志：mihomo 给每个 UDP 流建一个关联，
                // 语音 + 游戏 + QUIC 一秒钟能建好几个，界面会被整屏刷掉，
                // 真正要看的东西（失败、被拒）反而看不见。
                // 改成 60 秒汇总一行，见 UdpStatsTick。
                UdpAssocOpened();

                // 空闲回收：mihomo 用完不会主动关 TCP 控制连接，
                // 我们自己盯着 —— 20 秒没包就收摊，把名额还给后面的人。
                // 语音/游戏的流是持续的，不会被误杀。
                var lastActive = DateTime.UtcNow;

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
                                lastActive = DateTime.UtcNow;
                                Interlocked.Add(ref _udpPktsUp, 1);
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
                    lastActive = DateTime.UtcNow;
                    Interlocked.Add(ref _udpPktsDown, 1);
                            if (n > 0) cs.SendTo(buf, 0, n, SocketFlags.None, peer);
                        }
                        catch (Exception) { break; }
                    }
                });
                downThread.IsBackground = true;
                downThread.Start();

                // 阻塞在客户端的控制连接上：它一关，关联就结束。
                // 但 mihomo 不会主动关它，所以还要看空闲时间 ——
                // 20 秒一个 UDP 包都没有，说明这条流早就不用了。
                try
                {
                    var sink = new byte[16];
                    ns.ReadTimeout = 1000;          // 1 秒超时，用来看空闲
                    for (; ; )
                    {
                        if ((DateTime.UtcNow - lastActive).TotalSeconds > 20) break;
                        try
                        {
                            if (ns.Read(sink, 0, sink.Length) <= 0) break;
                            lastActive = DateTime.UtcNow;
                        }
                        catch (System.IO.IOException) { }   // 超时，回到循环顶看空闲
                    }
                }
                catch (Exception) { }

                stop = true;
                try { cs.Close(); } catch (Exception) { }
                try { rs.Close(); } catch (Exception) { }
                try { ctrl.Close(); } catch (Exception) { }
                UdpAssocClosed();
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
                return ConnectSocks(_socksPort, PreferIp(host), port, timeoutMs, _socksHost);

            // 国内目标走渡口时不要等满 8 秒：国内 RTT 普遍 < 100ms，
            // 建连 2.5 秒还没成基本就是那个目标从出口不可达。等满会卡住页面。
            int quickMs = Math.Min(timeoutMs, CnConnectMs);

            if (cn)
            {
                // 刚失败过的目标直接走另一条 —— 否则每次重连都要重等一遍。
                if (RecentlyFailed("ferry", host, port))
                    return ConnectSocks(_clashPort, host, port, timeoutMs, _clashHost);

                var s1 = ConnectSocks(_socksPort, PreferIp(host), port, quickMs, _socksHost);
                if (s1 != null) return s1;
                MarkFailed("ferry", host, port);
                LogOnce("国内目标连不上，改走本机代理", host);
                return ConnectSocks(_clashPort, host, port, timeoutMs, _clashHost);
            }

            // 境外：交给用户本机的代理，**传原始域名**。
            // 不能用 PreferIp：国内 DNS 解析墙外域名拿到的是污染地址，
            // 传过去 Clash 就会去连那个假 IP（明明它自己有干净的解析）。
            if (RecentlyFailed("clash", host, port))
                return ConnectSocks(_socksPort, host, port, timeoutMs, _socksHost);

            var s2 = ConnectSocks(_clashPort, host, port, timeoutMs, _clashHost);
            if (s2 != null) return s2;
            MarkFailed("clash", host, port);
            LogOnce("本机代理连不上，改走渡口", host);
            return ConnectSocks(_socksPort, host, port, timeoutMs, _socksHost);
        }

        /// <summary>国内目标连渡口最多等这么久（见 ConnectSmart 注释）。</summary>
        const int CnConnectMs = 2500;

        /// <summary>失败记忆保留多久。太短起不到作用，太长会让「临时抖动」变成长期绕路。</summary>
        const int FailMemoSec = 300;

        static readonly Dictionary<string, DateTime> _recentFail =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        static bool RecentlyFailed(string via, string host, int port)
        {
            string key = via + "|" + host + ":" + port;
            lock (_recentFail)
            {
                DateTime until;
                return _recentFail.TryGetValue(key, out until) && until > DateTime.UtcNow;
            }
        }

        static void MarkFailed(string via, string host, int port)
        {
            string key = via + "|" + host + ":" + port;
            lock (_recentFail)
            {
                if (_recentFail.Count > 4000) _recentFail.Clear();   // 别无限长
                _recentFail[key] = DateTime.UtcNow.AddSeconds(FailMemoSec);
            }
        }

        /// <summary>
        /// 同一类告警只记一次（按 key 去重）。
        ///
        /// 为什么：分流是**每条连接**都要判的。浏览器打开一个页面会建几十条连接，
        /// 代理挂掉时如果每条都写一行日志，日志文件几分钟就几 MB，真正的错误反而
        /// 被冲掉了。去重之后「某个目标一直走不通」这件事仍然看得见。
        /// </summary>
        static string RootDomain(string host)
        {
            if (string.IsNullOrEmpty(host)) return host;
            int dot = host.LastIndexOf('.');
            if (dot <= 0) return host;
            int prev = host.LastIndexOf('.', dot - 1);
            if (prev < 0) return host;
            return host.Substring(prev + 1);
        }

        void LogOnce(string what, string key)
        {
            // 按一级域名去重：一次开几十个页面时，同一类问题会以几十个不同
            // 子域名的形式出现（img1.cdn.example.com / img2.cdn.example.com…），
            // 按完整 host 去重等于没去重，表刷满 500 条后又从头开始刷屏。
            string id = what + "|" + RootDomain(key);
            lock (_loggedOnce)
            {
                if (_loggedOnce.Contains(id)) return;
                if (_loggedOnce.Count > 500) _loggedOnce.Clear();   // 别无限长
                _loggedOnce.Add(id);
            }
            _log(what + "：" + key);
        }

        /// <summary>
        /// 能本地解析成 IP 就把域名换成 IP 再交给上游。
        ///
        /// 为什么：远端（pcs）的 DNS 被污染，把域名丢给上游解析等于让它拿假地址
        /// （google.com → 31.13.92.37）。本地解析虽然也可能被污染，但至少是
        /// 「客户端所在网络看到的结果」，而且借网期间客户端的 DNS 是通的。
        /// 解析不出来就退回原域名 —— 有些站只认 SNI 里的域名，传 IP 反而错。
        /// </summary>
        static string PreferIp(string host)
        {
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip))
            {
                // 已经是 IP 了，原样返回。
                // （fake-ip 也是 IP —— 但调用方传进来的 host 如果是 fake-ip，
                //  说明上游没还原域名，这种情况只能原样交给上游处理。）
                return host;
            }

            // ResolveLocal 内部已经滤掉 fake-ip 了，所以这里拿到的要么是真实
            // 地址，要么是空。注意 TUN 模式下**永远是空**（DNS 被 hijack），
            // 于是退回域名 —— 这正是我们想要的：让远端或 Clash 自己解析。
            var addrs = ResolveLocal(host);
            if (addrs == null || addrs.Count == 0) return host;

            return addrs[0].ToString();
        }

        /// <summary>
        /// 这个目标是「国内」的吗？
        ///
        /// 判据：**只有解析出的地址明确落在国内分配段里才算国内**（段表来自 APNIC）。
        ///
        /// 为什么保守：判错的代价不对称。
        ///   · 国内站判成境外 → 走用户的代理，还是能上，只是多绕一跳；
        ///   · 墙外站判成国内 → 走渡口出口，远端 DNS 被污染 + 目标 IP 不可达，
        ///     永远打不开，而且没有任何报错可查。
        ///
        /// 为什么必须在**客户端**解析域名：远端（pcs）的 DNS 被污染，
        /// google.com 会解析成 31.13.92.37（Facebook 的段）。拿污染结果判断
        /// 会得出「这是国内」的错误结论，然后走渡口 → 永远打不开。
        /// 本地解析之后把 IP 交给上游，还顺带绕开了污染本身。
        /// </summary>
        bool IsDomestic(string host)
        {
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip))
            {
                // fake-ip 只是占位符，不含地理信息。当国内等于把所有域名送去
                // 渡口（那是 1.0.21 的「全网变慢」），所以一律当境外。
                if (IsFakeIp(ip)) return false;
                return CnIpRanges.Contains(ip);
            }

            return IsDomesticDomain(host);
        }

        /// <summary>
        /// 域名是不是国内站点 —— **只查后缀表，不做 DNS 解析**。
        ///
        /// 为什么不能解析：TUN 开着 `dns-hijack: any:53`，客户端自己发的 DNS
        /// 查询（不管问系统 DNS 还是直接问 223.5.5.5）也会被 mihomo 接管，
        /// 回来的永远是 fake-ip。所以「本地解析出真实 IP 再判断」在 TUN 模式下
        /// **根本不可能成功**，只会把所有域名都判成境外，然后
        /// Clash 连不上 → 回退渡口 → 再超时一次，日志里就是「转来转去」。
        ///
        /// 判错的代价不对称（见文件头），所以这张表只收「确定是国内」的。
        /// </summary>
        static bool IsDomesticDomain(string host)
        {
            string h = host.ToLowerInvariant();
            if (h.EndsWith(".cn") || h == "cn") return true;

            // 从左往右剥标签，每剥一次查一次表：
            //   m701.music.126.net → music.126.net → 126.net（命中）
            //   baidu.com → 一次就命中
            // 比线性扫 130 条 EndsWith 快得多，而且「点的边界」天然正确
            // （notbaidu.com 剥出来是 com，不会误命中 baidu.com）。
            string probe = h;
            for (int guard = 0; guard < 8; guard++)
            {
                if (_cnDomainSet.Contains(probe)) return true;
                int dot = probe.IndexOf('.');
                if (dot < 0) break;
                probe = probe.Substring(dot + 1);
            }
            return false;
        }

        /// <summary>白名单的哈希版本。启动时建一次，之后每个连接只查 1~3 次。</summary>
        static readonly string[] CnDomains = new string[]
        {
            // 头部互联网
            "baidu.com", "bdstatic.com", "bdimg.com", "bdurl.net",
            "qq.com", "gtimg.com", "qpic.cn", "qlogo.cn", "tencent.com",
            "weixin.com", "qcloud.com", "myqcloud.com", "tencentcloudapi.com",
            "taobao.com", "tmall.com", "alicdn.com", "aliyun.com", "aliyuncs.com",
            "alipay.com", "aliyuncs.net", "taobaocdn.com",
            "jd.com", "360buyimg.com", "3.cn",
            "bilibili.com", "hdslb.com", "biliapi.net", "bilivideo.com",
            "163.com", "126.net", "127.net", "netease.com", "126.com",
            "music.163.com", "youdao.com",
            "sohu.com", "sina.com", "sina.cn", "weibo.com", "weibo.cn",
            "zhihu.com", "zhimg.com", "csdn.net", "gitee.com", "cnblogs.com",
            "360.cn", "360safe.com", "qhimg.com", "qihoo.com",
            "xiaomi.com", "mi.com", "miui.com", "huawei.com", "hicloud.com",
            "douyin.com", "bytedance.com", "toutiao.com", "pstatp.com",
            "ixigua.com", "snssdk.com", "byteimg.com",
            "iqiyi.com", "youku.com", "douban.com", "doubanio.com",
            "meituan.com", "dianping.com", "ctrip.com", "12306.cn",
            "ele.me", "alibaba.com", "1688.com", "alimama.com",
            "kuaishou.com", "kwaicdn.com", "yximgs.com",
            "xunlei.com", "pconline.com.cn", "zol.com.cn",
            "lagou.com", "zhipin.com", "58.com", "ganji.com",
            "autohome.com.cn", "dongchedi.com", "mafengwo.cn",
            "cnzz.com", "umeng.com", "jomodns.com", "tanzhen.com",
            // 出口探测用的就是 myip.ipip.net —— 它在白名单里，探测才会走渡口，
            // 显示出来的才是真实的借网出口（否则会显示成用户自己 Clash 的出口）。
            "ipip.net", "3322.net", "ip138.com",
            // 开发者/工具
            "aliyuncdn.com", "getui.com", "growingio.com",
            "umengcloud.com", "dcore.org.cn",
            // 云存储 / CDN（用户实际会遇到，漏了就是「图片加载慢」）
            "ksyun.com", "ksyuncs.com", "myqcloud.com", "cos.ap-guangzhou.myqcloud.com",
            "aliyuncs.com", "oss-cn-hangzhou.aliyuncs.com", "bcebos.com",
            "bytecdntp.com", "douyinvod.com", "ixiguavideo.com", "pstatp.com",
            "qhres.com", "qhmsg.com", "360pany.com",
            // 更多日常站点
            "pinduoduo.com", "yangkeduo.com", "xiaohongshu.com", "xhscdn.com",
            "kuaishou.com", "kwai.com", "didiglobal.com", "xiaojukeji.com",
            "wps.cn", "wps.com", "kdocs.com", "youdao.com", "xunlei.com",
            "mail.qq.com", "foxmail.com", "dingtalk.com", "feishu.cn",
            "larksuite.com", "yuque.com", "jianshu.com", "segmentfault.com",
            "oschina.net", "51cto.com", "runoob.com", "w3school.com.cn",
            "tencentgames.com", "game.qq.com", "wegame.com", "smoba.com",
            "kingsoft.com", "cmcm.com", "cheetahmobile.com",
        };

        static readonly HashSet<string> _cnDomainSet = BuildCnDomainSet();

        static HashSet<string> BuildCnDomainSet()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in CnDomains) set.Add(d);
            return set;
        }

        /// <summary>
        /// 国内的常见域名后缀。加新条目的标准：**去掉它会有大量用户访问不到**。
        /// 宁可漏（漏了只是多绕一跳 Clash），也不要错收（错了直接打不开）。
        /// </summary>


        /// <summary>
        /// 这是不是 mihomo / Clash 的 fake-ip 地址（198.18.0.0/15）？
        ///
        /// 为什么必须单独认它：TUN 模式开着 fake-ip 时，**系统 DNS 被 mihomo 接管**，
        /// 任何域名都解析成 198.18.x.x。拿这个假地址去查「是不是国内 IP」永远是否，
        /// 于是国内站被判成境外 —— 再把 198.18.x.x 传给远端，远端根本连不上，
        /// 结果就是「开了借网反而上不了网」。
        ///
        /// 见到 fake-ip 就说明「DNS 已经由 TUN 那层接管了，它送来的流量就是要走渡口」，
        /// 所以这里一律按国内处理，并且不要把这个假 IP 传给上游（见 PreferIp）。
        /// </summary>
        static bool IsFakeIp(IPAddress ip)
        {
            if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = ip.GetAddressBytes();
            return b[0] == 198 && (b[1] == 18 || b[1] == 19);
        }

        /// <summary>
        /// 在客户端本地解析域名：先问系统 DNS（快，国内域名基本都能拿到国内 IP），
        /// 拿不到再问国内公共 DNS。
        ///
        /// 不做 DoH：用户来借网的前提就是「上不了网」，DoH 在这种环境下也不通；
        /// 而借网期间系统 DNS 走校园网，解析国内域名没问题。
        /// </summary>
        /// <summary>
        /// 本地解析域名，带缓存。**过滤掉 fake-ip**。
        ///
        /// 为什么要滤掉：TUN 开着 fake-ip 时系统 DNS 被 mihomo 接管，任何域名都
        /// 解析成 198.18.x.x。那个地址里没有任何地理信息 —— 拿它去查 CN 段表，
        /// 结论永远是「不是国内」，再被 IsFakeIp 特判成「是国内」，就把所有境外
        /// 域名都送进了渡口出口，每个都白等一次超时（1.0.21 的「全网变慢」）。
        ///
        /// 为什么必须有缓存：分流是**每条连接**都要判一次。浏览器开一个页面几十条
        /// 连接，没有缓存就是几十次 DNS 查询，页面自然慢半拍。
        /// </summary>
        static List<IPAddress> ResolveLocal(string host)
        {
            lock (_dnsCache)
            {
                CacheEntry hit;
                if (_dnsCache.TryGetValue(host, out hit) && hit.Expires > DateTime.UtcNow)
                    return hit.Addrs;
            }

            var list = new List<IPAddress>();
            try
            {
                foreach (var a in Dns.GetHostAddresses(host))
                    if (a.AddressFamily == AddressFamily.InterNetwork && !IsFakeIp(a))
                        list.Add(a);
            }
            catch (Exception) { }

            // 系统 DNS 被 TUN 接管（只给 fake-ip）时，只能自己去问。
            if (list.Count == 0)
            {
                var cn = ResolveCn(host);
                if (cn != null)
                    foreach (var a in cn)
                        if (!IsFakeIp(a)) list.Add(a);
            }

            lock (_dnsCache)
            {
                if (_dnsCache.Count > 4000) _dnsCache.Clear();   // 别无限长
                _dnsCache[host] = new CacheEntry
                {
                    Addrs = list,
                    // 解析失败也缓存，但只缓存 30 秒：失败多半是临时抖动，
                    // 缓太久会让「刚恢复的站点」一直被判境外。
                    Expires = DateTime.UtcNow.AddSeconds(list.Count > 0 ? 300 : 30)
                };
            }
            return list;
        }

        class CacheEntry
        {
            public List<IPAddress> Addrs;
            public DateTime Expires;
        }

        static readonly Dictionary<string, CacheEntry> _dnsCache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);



        static readonly string[] CnDns = new string[] { "223.5.5.5", "119.29.29.29", "114.114.114.114" };

        static List<IPAddress> ResolveCn(string host)
        {
            // 三个 DNS 并发问，谁先给出答案用谁。
            //
            // 原来是串行：每个 1200ms 超时，解析不了就依次等满三个 ——
            // 最坏 3.6 秒。分流是**每个新域名**都要判一次的，这个等待直接
            // 变成页面卡顿（用户反馈的「偶发性的小卡顿」）。
            // 并发之后最坏只等 1.2 秒。
            var req = BuildDnsQuery(host);
            var results = new List<IPAddress>();
            var done = new ManualResetEvent(false);
            int pending = CnDns.Length;
            object gate = new object();

            foreach (var dns in CnDns)
            {
                var server = dns;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        var ep = new IPEndPoint(IPAddress.Parse(server), 53);
                        using (var u = new System.Net.Sockets.UdpClient())
                        {
                            u.Client.ReceiveTimeout = 1200;
                            u.Send(req, req.Length, ep);
                            var from = new IPEndPoint(IPAddress.Any, 0);
                            var resp = u.Receive(ref from);
                            var got = ParseDnsAnswers(resp);
                            if (got.Count > 0)
                            {
                                lock (gate) { if (results.Count == 0) results.AddRange(got); }
                                done.Set();
                            }
                        }
                    }
                    catch (Exception) { }
                    finally
                    {
                        if (Interlocked.Decrement(ref pending) == 0) done.Set();
                    }
                });
            }

            done.WaitOne(1400);
            lock (gate) { return results.Count > 0 ? results : null; }
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

        /// <summary>
        /// 双向转发。**异步实现**，等待期间不占线程。
        ///
        /// 为什么必须异步：TUN 全局接管后，本机所有程序的连接都进这里。
        /// 同步版每个连接要 3 个线程（1 个 HandleConn + 上下行各 1 个 copy），
        /// 浏览器 keep-alive 挂 100 个连接就是 300 线程 —— 实测用户机器上
        /// 到了 298 线程、145 MB 工作集，然后整机变慢。
        /// ReadAsync 在等数据时把线程还给系统（IOCP），所以这里只留
        /// HandleConn 那 1 个线程。
        ///
        /// 注意：代码里其他地方警告过「别用 Task 跑阻塞 copy」—— 那说的是
        /// 把同步阻塞代码丢进线程池（每秒只扩容 1~2 个线程，会积压）。
        /// 这里是真异步，语义完全不同。
        /// </summary>
        async Task PumpAsync(NetworkStream a, NetworkStream b)
        {
            var t1 = CopyAsync(a, b, true);
            var t2 = CopyAsync(b, a, false);

            // 任一方向结束就收尾，绝不等两个都 EOF ——
            // HTTP 请求体传完后客户端就不再写、只等响应，
            // 等两个方向都结束会一直卡到超时（实测卡了 90 秒以上）。
            await Task.WhenAny(t1, t2).ConfigureAwait(false);

            try { a.Close(); } catch (Exception) { }
            try { b.Close(); } catch (Exception) { }

            // 另一端读完剩下的（最多 3 秒），别让它永远挂着。
            // 不能用 Task.WaitAsync —— 那是 .NET 6 才有的，这里是 .NET Framework 4.8。
            try { await Task.WhenAny(Task.WhenAll(t1, t2), Task.Delay(3000)).ConfigureAwait(false); }
            catch (Exception) { }
        }

        async Task CopyAsync(Stream src, Stream dst, bool up)
        {
            // 64 KB。原来 16 KB 在千兆/局域网里的单流吞吐上不去 ——
            // 每次 Read/Write 系统调用有固定开销，块越大同样的带宽下
            // 调用次数越少。实测把单流上限从 ~5 MB/s 抬到 10 MB/s 以上。
            var buf = new byte[65536];
            try
            {
                for (; ; )
                {
                    int n = await src.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                    if (n <= 0) break;
                    await dst.WriteAsync(buf, 0, n).ConfigureAwait(false);
                    if (up) Interlocked.Add(ref _bytesUp, n);
                    else Interlocked.Add(ref _bytesDown, n);
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 同步外壳：调用方（HandleSocks5 / HandleHttp）仍是同步代码，
        /// 这里只等一个已经跑在线程池上的异步任务，不再额外开 copy 线程。
        /// </summary>
        void Pump(NetworkStream a, NetworkStream b)
        {
            try { PumpAsync(a, b).GetAwaiter().GetResult(); }
            catch (Exception) { }
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

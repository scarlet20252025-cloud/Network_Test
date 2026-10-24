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

        TcpListener _listener;
        volatile bool _running;
        long _connTotal, _connActive, _bytesUp, _bytesDown;

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
                Interlocked.Increment(ref _connActive);
                var t = new Thread(delegate () { HandleConn(c); });
                t.IsBackground = true;
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
                    HandleSocks5(ns, first[0]);
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

        void HandleSocks5(NetworkStream ns, byte ver)
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

            if (cmd != 0x01) { ReplySocks5(ns, 0x07); return; } // 只支持 CONNECT

            Socket upstream = null;
            try
            {
                upstream = ConnectSocks(_socksPort, host, port, 8000, _socksHost);
                if (upstream == null) { ReplySocks5(ns, 0x05); return; }
                ReplySocks5(ns, 0x00);
                Pump(ns, new NetworkStream(upstream, true));
            }
            catch (Exception) { try { ReplySocks5(ns, 0x01); } catch (Exception) { } }
            finally { try { if (upstream != null) upstream.Close(); } catch (Exception) { } }
        }

        static void ReplySocks5(NetworkStream ns, byte rep)
        {
            // 0x00 REP | 0x00 RSV | 0x01 ATYP(IPv4) | 0.0.0.0 | 0
            ns.Write(new byte[] { 0x05, rep, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, 0, 10);
        }

        // ── HTTP 代理（客户端 → 我）───────────────────────────

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
                upstream = ConnectSocks(_socksPort, host, port, 8000, _socksHost);
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

            var t1 = Task.Factory.StartNew(delegate () { copy(a, b, true); });
            var t2 = Task.Factory.StartNew(delegate () { copy(b, a, false); });
            Task.WaitAny(new Task[] { t1, t2 });
            // 一端结束 → 两端都关，另一端自然退出
            try { a.Close(); } catch (Exception) { }
            try { b.Close(); } catch (Exception) { }
            try { Task.WaitAll(new Task[] { t1, t2 }, 3000); } catch (Exception) { }
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

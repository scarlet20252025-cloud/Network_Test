// 代理功能的独立验证程序。
//
// 为什么需要它：GUI 里点「开始借网」要真的连上远端 SSH 主机才走得通，
// 而开发机上通常没有那台主机的密钥。可代理逻辑（SOCKS5 客户端 + HTTP 转发 +
// 双协议识别）是本项目最容易写错的部分，不测等于没写。
//
// 做法：起一个**假的 SOCKS5 服务器**（收到 CONNECT 就直接连目标），
// 让 ProxyServer 指向它，然后经 HTTP 代理访问一个真实网站。
// 能拿到内容就证明整条链路是对的。
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Ferry
{
    static class TestProxy
    {
        static int _fakeSocksPort;

        [STAThread]
        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== 渡口代理自测 ===");

            // ① 假 SOCKS5 服务器
            _fakeSocksPort = 19999;
            var t = new Thread(FakeSocksLoop);
            t.IsBackground = true;
            t.Start();
            Thread.Sleep(300);
            Console.WriteLine("[1] 假 SOCKS5 已监听 127.0.0.1:" + _fakeSocksPort);

            // ② 被测对象
            int httpPort = 19998;
            var proxy = new ProxyServer(httpPort, _fakeSocksPort, delegate (string m) { Console.WriteLine("    > " + m); });
            if (!proxy.Start()) { Console.WriteLine("代理启动失败"); return 1; }
            Console.WriteLine("[2] ProxyServer 已监听 127.0.0.1:" + httpPort);

            int fail = 0;

            // ③ HTTP 代理：访问明文 HTTP 站点
            var body = HttpVia(httpPort, "http://www.baidu.com/", false);
            if (string.IsNullOrEmpty(body)) { Console.WriteLine("[3] ❌ HTTP 明文转发失败"); fail++; }
            else Console.WriteLine("[3] ✅ HTTP 明文转发成功（baidu），响应 " + body.Length + " 字节");

            // ④ CONNECT 隧道：访问 HTTPS 站点
            var http = HttpVia(httpPort, "https://www.baidu.com/", true);
            if (string.IsNullOrEmpty(http)) { Console.WriteLine("[4] ❌ HTTPS CONNECT 失效"); fail++; }
            else Console.WriteLine("[4] ✅ HTTPS CONNECT 成功（baidu），响应 " + http.Length + " 字节");

            // ⑤ 直连 SOCKS5（把 ProxyServer 当 SOCKS5 用）
            var socksIp = SocksVia(httpPort, "www.baidu.com", 80);
            if (string.IsNullOrEmpty(socksIp)) { Console.WriteLine("[5] ❌ 同端口 SOCKS5 失效"); fail++; }
            else Console.WriteLine("[5] ✅ 同端口 SOCKS5 成功");

            Console.WriteLine("统计：连接 " + proxy.ConnTotal + "，活跃 " + proxy.ConnActive +
                              "，上行 " + proxy.BytesUp + " B，下行 " + proxy.BytesDown + " B");
            proxy.Stop();

            Console.WriteLine(fail == 0 ? "=== 全部通过 ===" : "=== 有 " + fail + " 项失败 ===");
            return fail;
        }

        static string HttpVia(int proxyPort, string url, bool https)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Proxy = new WebProxy("http://127.0.0.1:" + proxyPort);
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.KeepAlive = false;
                req.UserAgent = "FerryTest/1.0";
                using (var resp = req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var r = new System.IO.StreamReader(s, Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch (Exception e)
            {
                Console.WriteLine("    异常：" + e.Message);
                return "";
            }
        }

        static string SocksVia(int socksPort, string host, int port)
        {
            try
            {
                using (var s = ProxyServer.ConnectSocks(socksPort, host, port, 8000))
                {
                    if (s == null) { Console.WriteLine("    异常：SOCKS 连接返回 null"); return ""; }
                    var req = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: " + host +
                                                      "\r\nConnection: close\r\n\r\n");
                    s.Send(req);
                    var buf = new byte[4096];
                    int n = s.Receive(buf);
                    return n > 0 ? Encoding.ASCII.GetString(buf, 0, n) : "";
                }
            }
            catch (Exception e) { Console.WriteLine("    异常：" + e.Message); return ""; }
        }

        static bool ReadN(NetworkStream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n;
                try { n = s.Read(buf, off, count - off); } catch (Exception) { return false; }
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        // ── 假 SOCKS5 服务器：只实现「无认证 + CONNECT 就直接连」──

        static void FakeSocksLoop()
        {
            var l = new TcpListener(IPAddress.Loopback, _fakeSocksPort);
            l.Start();
            for (; ; )
            {
                TcpClient c = null;
                try { c = l.AcceptTcpClient(); } catch (Exception) { return; }
                var cc = c;
                var th = new Thread(delegate () { FakeSocksServe(cc); });
                th.IsBackground = true;
                th.Start();
            }
        }

        static void FakeSocksServe(TcpClient c)
        {
            try
            {
                var ns = c.GetStream();
                var buf = new byte[260];
                if (!ReadN(ns, buf, 2)) return;           // VER NMETHODS
                int nm = buf[1];
                if (nm > 0 && !ReadN(ns, buf, nm)) return;
                ns.Write(new byte[] { 0x05, 0x00 }, 0, 2);

                var head = new byte[4];
                if (!ReadN(ns, head, 4)) return;          // VER CMD RSV ATYP
                string host;
                if (head[3] == 0x01)
                {
                    var a = new byte[4]; if (!ReadN(ns, a, 4)) return;
                    host = new IPAddress(a).ToString();
                }
                else if (head[3] == 0x03)
                {
                    var l = new byte[1]; if (!ReadN(ns, l, 1)) return;
                    var d = new byte[l[0]]; if (l[0] > 0 && !ReadN(ns, d, l[0])) return;
                    host = Encoding.ASCII.GetString(d);
                }
                else return;
                var pb = new byte[2]; if (!ReadN(ns, pb, 2)) return;
                int port = (pb[0] << 8) | pb[1];

                Socket up = null;
                try
                {
                    up = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    up.Connect(host, port);
                    ns.Write(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, 0, 10);

                    var ups = new NetworkStream(up, true);
                    var t2 = new Thread(delegate ()
                    {
                        try { var b = new byte[16384]; for (; ; ) { int n = ups.Read(b, 0, b.Length); if (n <= 0) break; ns.Write(b, 0, n); } }
                        catch (Exception) { }
                        finally { try { c.Close(); } catch (Exception) { } }
                    });
                    t2.IsBackground = true; t2.Start();

                    var b2 = new byte[16384];
                    for (; ; ) { int n = ns.Read(b2, 0, b2.Length); if (n <= 0) break; ups.Write(b2, 0, n); }
                }
                catch (Exception) { }
                finally { try { if (up != null) up.Close(); } catch (Exception) { } }
            }
            catch (Exception) { }
            finally { try { c.Close(); } catch (Exception) { } }
        }
    }
}

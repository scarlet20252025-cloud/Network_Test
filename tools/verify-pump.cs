using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ferry;

// 1.0.27 功能验证：异步改造后 HTTP/SOCKS5 转发还能不能正常工作。
// 只连 127.0.0.1 上的临时代理，不碰系统代理设置。
static class PumpFuncCheck
{
    static int fails;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static int ReadN(NetworkStream s, byte[] b, int n)
    {
        int got = 0;
        while (got < n)
        {
            int r = s.Read(b, got, n - got);
            if (r <= 0) return got;
            got += r;
        }
        return got;
    }

    static void Main()
    {
        var log = new StringBuilder();
        var proxy = new ProxyServer(17995, 1080, "10.13.24.1",
            delegate (string s) { lock (log) log.AppendLine(s); });
        Ok(proxy.Start(), "proxy started on 17995");

        try
        {
            // ── 1) SOCKS5 转发 ──
            using (var tcp = new TcpClient())
            {
                tcp.Connect("127.0.0.1", 17995);
                tcp.ReceiveTimeout = 15000;
                var ns = tcp.GetStream();
                ns.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);
                var hello = new byte[2];
                Ok(ReadN(ns, hello, 2) == 2 && hello[1] == 0x00, "socks handshake");

                var host = Encoding.ASCII.GetBytes("www.baidu.com");
                var req = new byte[5 + host.Length + 2 + 1];
                int k = 0;
                req[k++] = 0x05; req[k++] = 0x01; req[k++] = 0x00; req[k++] = 0x03;
                req[k++] = (byte)host.Length;
                Array.Copy(host, 0, req, k, host.Length); k += host.Length;
                req[k++] = 0x00; req[k++] = 0x50;   // port 80
                ns.Write(req, 0, k);

                var rep = new byte[10];
                Ok(ReadN(ns, rep, 10) == 10 && rep[1] == 0x00, "socks connect success");

                var http = Encoding.ASCII.GetBytes(
                    "GET / HTTP/1.1\r\nHost: www.baidu.com\r\nConnection: close\r\nUser-Agent: Mozilla/5.0\r\n\r\n");
                ns.Write(http, 0, http.Length);

                var buf = new byte[8192];
                int total = 0;
                string head = "";
                try
                {
                    for (; ; )
                    {
                        int n = ns.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        if (total < 400)
                        {
                            head += Encoding.ASCII.GetString(buf, 0, Math.Min(n, 400));
                        }
                        total += n;
                        if (total > 20000) break;   // 够证明通了
                    }
                }
                catch (Exception) { }

                Console.WriteLine("   bytes received: " + total);
                Ok(total > 100, "got response body through the async pump");
                Ok(head.IndexOf("200", StringComparison.Ordinal) >= 0 ||
                   head.IndexOf("HTTP/1", StringComparison.Ordinal) >= 0,
                   "response looks like HTTP");
            }

            // ── 2) 并发 20 条连接同时转发（async 改动最容易在这里出问题）──
            int okCount = 0;
            var done = new System.Threading.ManualResetEvent(false);
            int pending = 20;
            for (int i = 0; i < 20; i++)
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        using (var tcp = new TcpClient())
                        {
                            tcp.Connect("127.0.0.1", 17995);
                            tcp.ReceiveTimeout = 12000;
                            var ns = tcp.GetStream();
                            ns.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);
                            var h = new byte[2];
                            if (ReadN(ns, h, 2) != 2) return;

                            var host = Encoding.ASCII.GetBytes("www.baidu.com");
                            var req = new byte[5 + host.Length + 2 + 1];
                            int k = 0;
                            req[k++] = 0x05; req[k++] = 0x01; req[k++] = 0x00; req[k++] = 0x03;
                            req[k++] = (byte)host.Length;
                            Array.Copy(host, 0, req, k, host.Length); k += host.Length;
                            req[k++] = 0x00; req[k++] = 0x50;
                            ns.Write(req, 0, k);

                            var rep = new byte[10];
                            if (ReadN(ns, rep, 10) != 10 || rep[1] != 0x00) return;

                            var http = Encoding.ASCII.GetBytes(
                                "HEAD / HTTP/1.1\r\nHost: www.baidu.com\r\nConnection: close\r\n\r\n");
                            ns.Write(http, 0, http.Length);
                            var buf = new byte[1024];
                            int n = ns.Read(buf, 0, buf.Length);
                            if (n > 0) System.Threading.Interlocked.Increment(ref okCount);
                        }
                    }
                    catch (Exception) { }
                    finally
                    {
                        if (System.Threading.Interlocked.Decrement(ref pending) == 0) done.Set();
                    }
                });
            }
            done.WaitOne(35000);
            Console.WriteLine("   concurrent ok: " + okCount + "/20");
            Ok(okCount >= 15, "at least 15 of 20 concurrent connections succeeded");
        }
        finally
        {
            try { proxy.Stop(); } catch { }
        }

        if (fails > 0)
        {
            Console.WriteLine("--- proxy log tail ---");
            lock (log)
            {
                var lines = log.ToString().Split('\n');
                for (int i = Math.Max(0, lines.Length - 8); i < lines.Length; i++)
                    if (lines[i].Trim().Length > 0) Console.WriteLine("   " + lines[i].Trim());
            }
        }

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

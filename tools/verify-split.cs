using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Ferry;

// 分流验证：用**原始 SOCKS5 客户端**发请求，不用 HttpWebRequest。
//
// 为什么换掉 HttpWebRequest：它有连接池和 ServicePoint 缓存，第二个请求可能
// 复用了第一个的连接或者被静默跳过，导致「没看到日志」但又不报错，干扰判断。
// 原始 SOCKS5 完全可控 —— 而且 Clash 连我们用的就是这个协议，更接近真实场景。
static class RawSocksTest
{
    static int fails = 0;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static void Main()
    {
        Ok(CnIpRanges.Contains(IPAddress.Parse("183.2.172.177")), "baidu ip is CN");
        Ok(!CnIpRanges.Contains(IPAddress.Parse("104.244.42.197")), "twitter ip is foreign");
        Console.WriteLine("   ranges = " + CnIpRanges.Count);

        var clashPort = PickFree(19600);
        var fake = new FakeSocks(clashPort);
        fake.Start();
        Ok(FerryCore.PortOpen(clashPort, 800), "fake clash listening on " + clashPort);

        var appDir = @"C:\ferry-test\rawsocks";
        Directory.CreateDirectory(appDir);
        var cfg = new Cfg();
        cfg.RemoteHost = "10.13.24.1";
        cfg.RelayPort = 1080;
        cfg.AccountIp = "10.113.66.6";
        cfg.Token = "TEST-XT-0001";
        cfg.HttpPort = PickFree(19700);
        cfg.UseTun = false;
        cfg.DirectSocks = true;

        var rt = new FerryRuntime(cfg, appDir, delegate (string m) { Console.WriteLine("   [rt] " + m); });
        var err = rt.Start();
        Ok(err.Length == 0, "runtime started");
        if (err.Length != 0) { Console.WriteLine("   " + err); fake.Stop(); Environment.Exit(1); }

        var proxy = rt.GetType()
            .GetField("_proxy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .GetValue(rt);
        proxy.GetType().GetMethod("SetForeignProxy").Invoke(proxy, new object[] { clashPort, "127.0.0.1" });
        Console.WriteLine("   local proxy = 127.0.0.1:" + rt.HttpPort + "   foreign -> 127.0.0.1:" + clashPort);

        // ① 国内 IP：走渡口，假 Clash 不该被碰
        fake.Reset();
        var r1 = SocksConnect(rt.HttpPort, "183.2.172.177", 80);
        Console.WriteLine("   domestic socks rep = " + r1);
        Ok(fake.Hits == 0, "domestic did NOT touch local proxy (hits=" + fake.Hits + ")");

        // ② 境外 IP：必须送到假 Clash
        fake.Reset();
        var r2 = SocksConnect(rt.HttpPort, "104.244.42.197", 80);
        Console.WriteLine("   foreign socks rep = " + r2 + "   clash hits = " + fake.Hits + "  targets =" + fake.Targets);
        Ok(fake.Hits > 0, "foreign went to local proxy");
        Ok(fake.Targets.IndexOf("104.244.42.197") >= 0, "local proxy saw the foreign ip");

        // ③ 域名：本地解析后传 IP（不是把域名丢给上游）
        fake.Reset();
        var r3 = SocksConnect(rt.HttpPort, "www.google.com", 80);
        Console.WriteLine("   domain socks rep = " + r3 + "   clash hits = " + fake.Hits + "  targets =" + fake.Targets);
        Ok(fake.Targets.IndexOf("google.com", StringComparison.OrdinalIgnoreCase) < 0,
           "domain was resolved locally (upstream saw an IP)");

        rt.Stop();
        fake.Stop();
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }

    /// <summary>原始 SOCKS5 CONNECT，返回 REP 字节（-1 = 建连失败）。</summary>
    static int SocksConnect(int proxyPort, string host, int port)
    {
        try
        {
            using (var c = new TcpClient())
            {
                c.Connect("127.0.0.1", proxyPort);
                c.ReceiveTimeout = 8000;
                var ns = c.GetStream();
                ns.Write(new byte[] { 5, 1, 0 }, 0, 3);
                var g = new byte[2];
                if (ReadN(ns, g, 2) != 2) return -2;

                var req = new System.Collections.Generic.List<byte>();
                req.Add(5); req.Add(1); req.Add(0);
                IPAddress ip;
                if (IPAddress.TryParse(host, out ip))
                {
                    req.Add(1);
                    req.AddRange(ip.GetAddressBytes());
                }
                else
                {
                    var hb = Encoding.ASCII.GetBytes(host);
                    req.Add(3); req.Add((byte)hb.Length); req.AddRange(hb);
                }
                req.Add((byte)(port >> 8)); req.Add((byte)(port & 0xFF));
                ns.Write(req.ToArray(), 0, req.Count);

                var head = new byte[4];
                if (ReadN(ns, head, 4) != 4) return -3;
                // 吃掉 BND
                if (head[3] == 1) { var t = new byte[6]; ReadN(ns, t, 6); }
                else if (head[3] == 3) { var l = new byte[1]; ReadN(ns, l, 1); var t = new byte[l[0] + 2]; ReadN(ns, t, t.Length); }
                else { var t = new byte[18]; ReadN(ns, t, 18); }
                return head[1];
            }
        }
        catch (Exception e) { Console.WriteLine("   socks err: " + e.Message); return -1; }
    }

    static int ReadN(NetworkStream ns, byte[] b, int n)
    {
        int off = 0;
        while (off < n) { int r = ns.Read(b, off, n - off); if (r <= 0) break; off += r; }
        return off;
    }

    static int PickFree(int start)
    {
        for (int p = start; p < start + 60; p++)
            if (!FerryCore.PortOpen(p, 120)) return p;
        return start;
    }

    class FakeSocks
    {
        readonly int _port; TcpListener _l; volatile bool _run;
        public int Hits; public string Targets = "";
        public FakeSocks(int p) { _port = p; }
        public void Reset() { Hits = 0; Targets = ""; }
        public void Start()
        {
            _l = new TcpListener(IPAddress.Loopback, _port);
            _l.Start(); _run = true;
            var t = new Thread(delegate ()
            {
                while (_run)
                {
                    try
                    {
                        var c = _l.AcceptTcpClient();
                        var th = new Thread(delegate () { Serve(c); });
                        th.IsBackground = true; th.Start();
                    }
                    catch (Exception) { break; }
                }
            });
            t.IsBackground = true; t.Start();
        }
        public void Stop() { _run = false; try { _l.Stop(); } catch (Exception) { } }
        void Serve(TcpClient c)
        {
            try
            {
                var ns = c.GetStream();
                var b = new byte[3]; ReadN(ns, b, 3);
                ns.Write(new byte[] { 5, 0 }, 0, 2);
                var h = new byte[4]; ReadN(ns, h, 4);
                string host;
                if (h[3] == 1) { var a = new byte[4]; ReadN(ns, a, 4); host = new IPAddress(a).ToString(); }
                else if (h[3] == 3) { var l = new byte[1]; ReadN(ns, l, 1); var d = new byte[l[0]]; ReadN(ns, d, l[0]); host = Encoding.ASCII.GetString(d); }
                else { var a = new byte[16]; ReadN(ns, a, 16); host = new IPAddress(a).ToString(); }
                var pb = new byte[2]; ReadN(ns, pb, 2);
                Interlocked.Increment(ref Hits);
                Targets = Targets + " " + host;
                ns.Write(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10);
            }
            catch (Exception) { }
            finally { try { c.Close(); } catch (Exception) { } }
        }
        static void ReadN(NetworkStream ns, byte[] b, int n)
        { int off = 0; while (off < n) { int r = ns.Read(b, off, n - off); if (r <= 0) break; off += r; } }
    }
}

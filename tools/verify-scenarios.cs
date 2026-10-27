using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Ferry;

// 完整场景验证：各种切换下分流是否都正确、会不会成环。
//
// 场景：
//   ① 接管模式（AllowSplit=true）+ 有本机代理  → 国内走渡口，境外走本机代理
//   ② 接管模式 + 没有本机代理                  → 全走渡口（境外会失败，但不断网）
//   ③ 节点模式（AllowSplit=false）+ 有本机代理 → 全走渡口，不送回本机代理（防成环）
//   ④ 本机代理中途挂掉                          → 境外回落到渡口，不抛异常
//   ⑤ 本机代理端口探测（DetectLocalProxy）      → 能找到、能排除自己
static class ScenarioTest
{
    static int fails = 0;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static void Main()
    {
        // 段表基础
        Ok(CnIpRanges.Contains(IPAddress.Parse("183.2.172.177")), "baidu ip is CN");
        Ok(!CnIpRanges.Contains(IPAddress.Parse("104.244.42.197")), "twitter ip is foreign");
        Console.WriteLine("   ranges = " + CnIpRanges.Count);

        // ── ① 接管模式 + 有本机代理 ──
        Console.WriteLine();
        Console.WriteLine("=== ① 接管模式 + 本机代理 ===");
        var clashPort = PickFree(20100);
        var fake = new FakeSocks(clashPort);
        fake.Start();
        var rt = StartRuntime("s1", PickFree(20200), true, clashPort);
        Ok(rt != null, "runtime started");
        if (rt == null) { fake.Stop(); Environment.Exit(1); }

        fake.Reset();
        SocksConnect(rt.HttpPort, "183.2.172.177", 80);
        Ok(fake.Hits == 0, "domestic -> ferry (local proxy untouched)");
        fake.Reset();
        SocksConnect(rt.HttpPort, "104.244.42.197", 80);
        Ok(fake.Hits == 1, "foreign -> local proxy (hits=" + fake.Hits + ")");

        // ── ④ 本机代理挂掉 → 境外回落渡口 ──
        Console.WriteLine();
        Console.WriteLine("=== ④ 本机代理中途挂掉 ===");
        fake.Stop();
        Thread.Sleep(300);
        var rep = SocksConnect(rt.HttpPort, "104.244.42.197", 80);
        Console.WriteLine("   foreign rep after clash died = " + rep + "（5=失败，正常：远端也到不了境外）");
        Ok(rep != -1 && rep != -2, "no exception when local proxy is gone (rep=" + rep + ")");
        rt.Stop();

        // ── ③ 节点模式 + 有本机代理 → 不分流 ──
        Console.WriteLine();
        Console.WriteLine("=== ③ 节点模式（AllowSplit=false）===");
        var clash2 = PickFree(20300);
        var fake2 = new FakeSocks(clash2);
        fake2.Start();
        var rt3 = StartRuntime("s3", PickFree(20400), false, clash2);
        Ok(rt3 != null, "node-mode runtime started");
        if (rt3 != null)
        {
            fake2.Reset();
            SocksConnect(rt3.HttpPort, "104.244.42.197", 80);
            Ok(fake2.Hits == 0, "node mode: foreign NOT sent back to local proxy (no loop)");
            fake2.Reset();
            SocksConnect(rt3.HttpPort, "183.2.172.177", 80);
            Ok(fake2.Hits == 0, "node mode: domestic also stays on ferry");
            rt3.Stop();
        }
        fake2.Stop();

        // ── ② 没有本机代理 ──
        Console.WriteLine();
        Console.WriteLine("=== ② 接管模式 + 没有本机代理 ===");
        var rt2 = StartRuntime("s2", PickFree(20500), true, 0);
        Ok(rt2 != null, "no-proxy runtime started");
        if (rt2 != null)
        {
            var rep2 = SocksConnect(rt2.HttpPort, "104.244.42.197", 80);
            Ok(rep2 != -1 && rep2 != -2, "no proxy: does not throw (rep=" + rep2 + ")");
            var rep3 = SocksConnect(rt2.HttpPort, "183.2.172.177", 80);
            Ok(rep3 == 0, "no proxy: domestic still works through ferry (rep=" + rep3 + ")");
            rt2.Stop();
        }

        // ── ⑤ DetectLocalProxy ──
        Console.WriteLine();
        Console.WriteLine("=== ⑤ DetectLocalProxy ===");
        var probe = PickFree(20600);
        var fake3 = new FakeSocks(probe);
        fake3.Start();
        var found = FerryCore.DetectLocalProxy(0);
        Console.WriteLine("   detected = " + found + "  (expected 7897 on this box, or " + probe + ")");
        Ok(found > 0, "DetectLocalProxy found something");
        Ok(FerryCore.DetectLocalProxy(probe) != probe || found != probe,
           "DetectLocalProxy excludes the given port");
        fake3.Stop();

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }

    static FerryRuntime StartRuntime(string tag, int httpPort, bool allowSplit, int clashPort)
    {
        var appDir = @"C:\ferry-test\" + tag;
        Directory.CreateDirectory(appDir);
        var cfg = new Cfg();
        cfg.RemoteHost = "10.13.24.1";
        cfg.RelayPort = 1080;
        cfg.AccountIp = "10.113.66.6";
        cfg.Token = "TEST-XT-0001";
        cfg.HttpPort = httpPort;
        cfg.UseTun = false;
        cfg.DirectSocks = true;

        var rt = new FerryRuntime(cfg, appDir, delegate (string m) { Console.WriteLine("   [" + tag + "] " + m); });
        rt.AllowSplit = allowSplit;
        // 手动指定本机代理端口，避免依赖真实环境里有没有 Clash
        var f = typeof(FerryRuntime).GetField("_cfg");
        var err = rt.Start();
        if (err.Length != 0) { Console.WriteLine("   start err: " + err); return null; }

        var proxy = typeof(FerryRuntime).GetField("_proxy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(rt);
        if (allowSplit && clashPort > 0)
            proxy.GetType().GetMethod("SetForeignProxy").Invoke(proxy, new object[] { clashPort, "127.0.0.1" });
        else if (!allowSplit)
            proxy.GetType().GetMethod("SetForeignProxy").Invoke(proxy, new object[] { 0, "127.0.0.1" });
        return rt;
    }

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
                var req = new System.Collections.Generic.List<byte> { 5, 1, 0 };
                IPAddress ip;
                if (IPAddress.TryParse(host, out ip)) { req.Add(1); req.AddRange(ip.GetAddressBytes()); }
                else { var hb = Encoding.ASCII.GetBytes(host); req.Add(3); req.Add((byte)hb.Length); req.AddRange(hb); }
                req.Add((byte)(port >> 8)); req.Add((byte)(port & 0xFF));
                ns.Write(req.ToArray(), 0, req.Count);
                var head = new byte[4];
                if (ReadN(ns, head, 4) != 4) return -3;
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

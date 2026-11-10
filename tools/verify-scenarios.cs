using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Ferry;

// 完整场景验证：各种切换下分流是否都正确、会不会成环。
//
// 自包含（1.1.1 起）：上游 SOCKS 与本机代理全部用本地 fake，端口段按 PID 错开。
// 早先上游写死真实远端 10.13.24.1:1080、本机代理依赖这台机器有没有开 Clash，
// 于是「国内第一跳能否连上」取决于当时的网络 —— 一旦失败就会按设计回退到本机
// 代理，让 `fake.Hits == 0` 偶发变红；连跑时固定端口还会撞上一轮 TIME_WAIT。
// 这些都是验证器的环境依赖，不是产品行为，别再拿它们去改产品实现。
//
// 场景：
//   ① 接管模式（AllowSplit=true）+ 有本机代理  → 国内走渡口，境外走本机代理
//   ② 接管模式 + 没有本机代理                  → 全走渡口，不抛异常
//   ③ 节点模式（AllowSplit=false）+ 有本机代理 → 全走渡口，不送回本机代理（防成环）
//   ④ 本机代理中途挂掉                          → 境外回落到渡口，不抛异常
//   ⑤ 本机代理端口探测（DetectLocalProxy）      → 返回值合法、能排除自己
static class ScenarioTest
{
    static int fails = 0;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    // 连跑同一 exe 时上轮的监听端口可能还没释放，用 PID 错开整段端口。
    static readonly int PortBase = 20000 +
        (System.Diagnostics.Process.GetCurrentProcess().Id % 40) * 100;

    static void Main()
    {
        // 段表基础
        Ok(CnIpRanges.Contains(IPAddress.Parse("183.2.172.177")), "baidu ip is CN");
        Ok(!CnIpRanges.Contains(IPAddress.Parse("104.244.42.197")), "twitter ip is foreign");
        Console.WriteLine("   ranges = " + CnIpRanges.Count);

        // 上游 fake 回 REP=0（连接成功）并保持连接，代表「渡口这一跳是好的」；
        // 各场景里代表本机代理的 fake 回 REP=5（目标不可达）。
        int upstreamPort = PickFree(PortBase + 500);
        var upstream = new FakeSocks(upstreamPort) { Reply = 0, HoldOpen = true };
        upstream.Start();

        // ── ① 接管模式 + 有本机代理 ──
        Console.WriteLine();
        Console.WriteLine("=== ① 接管模式 + 本机代理 ===");
        var clashPort = PickFree(PortBase);
        var fake = new FakeSocks(clashPort);
        fake.Start();
        var rt = StartRuntime("s1", PickFree(PortBase + 100), true, clashPort, upstreamPort);
        Ok(rt != null, "runtime started");
        if (rt == null) { fake.Stop(); upstream.Stop(); Environment.Exit(1); }

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
        Console.WriteLine("   foreign rep after clash died = " + rep + "（0=回落到渡口成功）");
        Ok(rep != -1 && rep != -2, "no exception when local proxy is gone (rep=" + rep + ")");
        rt.Stop();

        // ── ③ 节点模式 + 有本机代理 → 不分流 ──
        Console.WriteLine();
        Console.WriteLine("=== ③ 节点模式（AllowSplit=false）===");
        var clash2 = PickFree(PortBase + 200);
        var fake2 = new FakeSocks(clash2);
        fake2.Start();
        var rt3 = StartRuntime("s3", PickFree(PortBase + 300), false, clash2, upstreamPort);
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
        var rt2 = StartRuntime("s2", PickFree(PortBase + 400), true, 0, upstreamPort);
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
        // 这台机器不一定开着 Clash。found==0 本来就是合法结果，不能判 FAIL ——
        // 旧断言 `found > 0` 依赖本机环境，曾让本验证器偶发变红而产品全 PASS。
        // 也不再往 7897 起 fake：本机 Clash 可能确实在监听，只是 200ms 探测
        // 那一刻没探到，TcpListener.Start() 会抛未处理异常（CLR 崩溃码）。
        Console.WriteLine();
        Console.WriteLine("=== ⑤ DetectLocalProxy ===");
        var candidates = new int[] { 7897, 7890, 7891, 10808, 10809,
                                     1080, 2080, 8889, 1087, 7899 };
        var found = FerryCore.DetectLocalProxy(0);
        Console.WriteLine("   detected = " + found + "  (0 = no candidate listening, valid)");
        Ok(found == 0 || Array.IndexOf(candidates, found) >= 0,
           "DetectLocalProxy returns 0 or a documented candidate port");
        if (found > 0)
        {
            // 真正覆盖 `if (p == excludePort) continue;`：必须传一个确实在
            // 监听的候选端口，传不在候选表里的端口是恒真的假测试。
            var excluded = FerryCore.DetectLocalProxy(found);
            Ok(excluded != found,
               "DetectLocalProxy excludes the given port (" + found +
               ", got " + excluded + ")");
        }
        else
        {
            Console.WriteLine("   SKIP exclude-branch: no candidate listening on this box right now");
        }

        upstream.Stop();
        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }

    static FerryRuntime StartRuntime(string tag, int httpPort, bool allowSplit,
                                     int clashPort, int upstreamPort)
    {
        var appDir = @"C:\ferry-test\" + tag;
        Directory.CreateDirectory(appDir);
        var cfg = new Cfg();
        // 上游指向本地可控 fake：不再依赖真实远端 10.13.24.1:1080 是否可达。
        cfg.RemoteHost = "127.0.0.1";
        cfg.RelayPort = upstreamPort;
        cfg.AccountIp = "10.113.66.6";
        cfg.Token = "TEST-XT-0001";
        cfg.HttpPort = httpPort;
        cfg.UseTun = false;
        cfg.DirectSocks = true;

        var rt = new FerryRuntime(cfg, appDir, delegate (string m) { Console.WriteLine("   [" + tag + "] " + m); });
        rt.AllowSplit = allowSplit;
        var err = rt.Start();
        if (err.Length != 0) { Console.WriteLine("   start err: " + err); return null; }

        // 手动指定本机代理端口，避免依赖真实环境里有没有 Clash。
        var proxy = typeof(FerryRuntime).GetField("_proxy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(rt);
        if (allowSplit && clashPort > 0)
            proxy.GetType().GetMethod("SetForeignProxy").Invoke(proxy, new object[] { clashPort, "127.0.0.1" });
        else if (!allowSplit)
            proxy.GetType().GetMethod("SetForeignProxy").Invoke(proxy, new object[] { 0, "127.0.0.1" });
        return rt;
    }

    static int SocksConnect(int proxyPort, string host, int port)
    {
        // Windows 下连续连同一个本地端口时，可能复用上一连接的临时端口而撞上
        // TIME_WAIT 四元组，c.Connect 抛 WSAEADDRINUSE。重试**只包 Connect 这一步**：
        // 若把整个握手也放进 catch(SocketException)，读响应出错时会重发整条
        // SOCKS 请求，一条流被 fake 数两次，把「国内没碰本机代理」误判成 FAIL。
        TcpClient c = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                c = new TcpClient();
                c.Connect("127.0.0.1", proxyPort);
                break;
            }
            catch (SocketException e)
            {
                if (c != null) { try { c.Close(); } catch (Exception) { } }
                c = null;
                if (attempt == 2) { Console.WriteLine("   socks err: " + e.Message); return -1; }
                Thread.Sleep(80);
            }
            catch (Exception e) { Console.WriteLine("   socks err: " + e.Message); return -1; }
        }
        if (c == null) return -1;

        using (c)
        {
            try
            {
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
            catch (Exception e) { Console.WriteLine("   socks err: " + e.Message); return -1; }
        }
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
        /// <summary>SOCKS REP：5=目标不可达（本机代理），0=成功（渡口上游）。</summary>
        public byte Reply = 5;
        /// <summary>回完 REP 后保持连接，模拟一条可用的上游，而不是立刻 RST。</summary>
        public bool HoldOpen = false;
        public FakeSocks(int p) { _port = p; }
        public void Reset() { Hits = 0; Targets = ""; }
        public void Start()
        {
            for (int i = 0; i < 5; i++)
            {
                try { _l = new TcpListener(IPAddress.Loopback, _port); _l.Start(); break; }
                catch (SocketException)
                {
                    try { _l.Stop(); } catch (Exception) { }
                    if (i == 4) throw;
                    Thread.Sleep(100);
                }
            }
            _run = true;
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
                ns.Write(new byte[] { 5, Reply, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10);
                if (HoldOpen)
                {
                    var buf = new byte[256];
                    while (_run)
                    {
                        try
                        {
                            if (ns.DataAvailable) { if (ns.Read(buf, 0, buf.Length) <= 0) break; }
                            else Thread.Sleep(20);
                        }
                        catch (Exception) { break; }
                    }
                }
            }
            catch (Exception) { }
            finally { try { c.Close(); } catch (Exception) { } }
        }
        static void ReadN(NetworkStream ns, byte[] b, int n)
        { int off = 0; while (off < n) { int r = ns.Read(b, off, n - off); if (r <= 0) break; off += r; } }
    }
}

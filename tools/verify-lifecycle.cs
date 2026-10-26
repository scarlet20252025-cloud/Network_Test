using System;
using System.IO;
using Ferry;

// 生命周期的鲁棒性验证（不建窗口，纯业务层），模拟用户真实会遇到的组合：
//   ① 借网 → 停 → 再借（反复启停，幂等）
//   ② 开着 Clash（系统代理=7897）→ 借网（覆盖）→ 停（必须还原回 7897）
//   ③ 关掉 Clash（代理为空）→ 借网 → 停（必须清空，不能留 10821 这种死指针）
//   ④ 上次被强杀留下的残局（代理指向我们 + 残留 pid）→ CleanupStale 能清掉
static class LifecycleTest
{
    static int fails = 0;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }
    static void Log(string s) { Console.WriteLine("  [rt] " + s); }

    const string AppDir = @"C:\ferry-test\lifecycle";

    static Cfg MakeCfg()
    {
        var c = new Cfg();
        c.RemoteHost = "10.13.24.1";
        c.SshPort = 22;
        c.SshUser = "pcserver";
        c.AccountIp = "10.113.66.6";
        c.Token = "TEST-XT-0001";
        c.PortalUrl = "http://10.13.24.1:8080";
        c.HttpPort = 10821;
        c.UseTun = false;          // 测试机没有 mihomo，只测直连模式
        c.DirectSocks = true;
        c.ExpiresAt = 0;
        return c;
    }


    /// <summary>
    /// 经过**正在运行的**本地代理发一次 UDP（DNS 查询），返回是否拿到响应。
    /// 这是用户实际会走的路径：mihomo/Clash 连的就是这个端口。
    /// </summary>
    static bool UdpVia(int port)
    {
        try
        {
            var tcp = new System.Net.Sockets.TcpClient();
            tcp.Connect("127.0.0.1", port);
            var ns = tcp.GetStream();
            ns.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);
            var g = new byte[2];
            if (ReadN(ns, g, 2) != 2) return false;

            ns.Write(new byte[] { 0x05, 0x03, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, 0, 10);
            var h = new byte[10];
            if (ReadN(ns, h, 10) != 10 || h[1] != 0x00) return false;
            int bnd = (h[8] << 8) | h[9];

            var q = new byte[] { 0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                                 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
                                 3, (byte)'c', (byte)'o', (byte)'m', 0x00, 0x00, 0x01, 0x00, 0x01 };
            var pkt = new byte[10 + q.Length];
            pkt[3] = 0x01; pkt[4] = 223; pkt[5] = 5; pkt[6] = 5; pkt[7] = 5; pkt[9] = 53;
            Array.Copy(q, 0, pkt, 10, q.Length);

            var u = new System.Net.Sockets.UdpClient();
            u.Client.ReceiveTimeout = 8000;
            u.Send(pkt, pkt.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, bnd));
            var from = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
            var resp = u.Receive(ref from);
            bool ok = resp != null && resp.Length > 10;
            u.Close();
            tcp.Close();
            return ok;
        }
        catch (Exception ex) { Console.WriteLine("   udp err: " + ex.Message); return false; }
    }

    static int ReadN(Stream s, byte[] b, int n)
    {
        int off = 0;
        while (off < n)
        {
            int r = s.Read(b, off, n - off);
            if (r <= 0) break;
            off += r;
        }
        return off;
    }

    static void Main()
    {
        Directory.CreateDirectory(AppDir);
        var cfgPath = Path.Combine(AppDir, "config.json");
        var cfg = MakeCfg();
        cfg.Save(cfgPath);

        // ── ① 反复启停 3 轮 ──
        for (int round = 1; round <= 3; round++)
        {
            var rt = new FerryRuntime(cfg, AppDir, Log);
            var err = rt.Start();
            Ok(err.Length == 0, "round" + round + " start");
            if (err.Length > 0) Console.WriteLine("   err: " + err);
            Ok(rt.Running, "round" + round + " running");
            Ok(rt.ProxyAlive, "round" + round + " proxy alive");
            var ip = err.Length == 0 ? rt.ExitIpViaProxy(6000) : "";
            Console.WriteLine("   exit ip = " + (ip.Length > 0 ? ip : "(none)"));
            rt.Stop();
            Ok(!rt.Running, "round" + round + " stopped");
        }

        // ── ② 开着 Clash 时借网 / 停止 ──
        FerryCore.SetProxy(true, "127.0.0.1:7897");
        FerryCore.BackupProxyOnce(cfg, AppDir);
        Console.WriteLine("   proxy before = " + FerryCore.ProxyServer());

        var rt2 = new FerryRuntime(cfg, AppDir, Log);
        var e2 = rt2.Start();
        Ok(e2.Length == 0, "clash-on start");
        FerryCore.SetProxy(true, rt2.BorrowServer);
        Ok(FerryCore.ProxyServer() == rt2.BorrowServer, "clash-on borrowed proxy set");

        FerryCore.RestoreDirect(cfg, AppDir);
        rt2.Stop();
        var afterClash = FerryCore.ProxyServer();
        Console.WriteLine("   proxy after  = " + afterClash);
        Ok(afterClash.Contains("7897"), "clash-on: restored to 7897");

        // ── ③ 没有 Clash（代理关闭）时借网 / 停止 ──
        FerryCore.SetProxy(false, "");
        FerryCore.BackupProxyOnce(cfg, AppDir);

        var rt3 = new FerryRuntime(cfg, AppDir, Log);
        var e3 = rt3.Start();
        Ok(e3.Length == 0, "clash-off start");
        FerryCore.SetProxy(true, rt3.BorrowServer);
        FerryCore.RestoreDirect(cfg, AppDir);
        rt3.Stop();
        var afterOff = FerryCore.ProxyServer();
        bool enabled = FerryCore.ProxyEnabled();
        Console.WriteLine("   proxy after (no clash) = " + afterOff + " enabled=" + enabled);
        // 注意：Windows 上 ProxyEnable=0 时 ProxyServer 的旧值会保留（系统行为，
        // 不影响任何流量）。所以判据是「要么已禁用，要么不含我们的端口」。
        Ok(!enabled || (afterOff.IndexOf("10821") < 0 && afterOff.IndexOf("10820") < 0),
           "clash-off: no active dead pointer");

        // ── ③b 借网状态下，走真实运行时的本地代理测 UDP ──
        var rt4 = new FerryRuntime(cfg, AppDir, Log);
        var e4 = rt4.Start();
        Ok(e4.Length == 0, "borrow-for-udp start");
        if (e4.Length == 0)
        {
            Ok(UdpVia(rt4.HttpPort), "udp through running proxy (http port)");
            Ok(UdpVia(rt4.SocksPort), "udp through running proxy (socks port)");
        }
        rt4.Stop();

        // ── ④ 残局清理 ──
        FerryCore.SetProxy(true, "http://127.0.0.1:10821");   // 假装上次崩溃留的
        Ok(FerryCore.ProxyServer().Contains("10821"), "leftover simulated");
        TunMode.CleanupStale(AppDir, delegate (string s) { Console.WriteLine("   [stale] " + s); });
        FerryCore.RestoreDirect(cfg, AppDir);
        Ok(!FerryCore.ProxyEnabled() || FerryCore.ProxyServer().IndexOf("10821") < 0,
           "leftover cleaned (disabled counts as clean)");

        // 收尾：把系统代理恢复成 7897（这台机器上没意义，但保持干净）
        FerryCore.SetProxy(true, "127.0.0.1:7897");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

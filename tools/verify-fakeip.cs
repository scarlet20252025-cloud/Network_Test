using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Ferry;

// 验证 1.0.16 的两个修复：
//   ① fake-ip（198.18.x.x）不再被当成「境外」，也不会被传给上游
//   ② 节点模式（AllowTun=false）下不启用 TUN
static class FixCheck
{
    static int fails = 0;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static void Main()
    {
        // ── ① fake-ip ──
        Console.WriteLine("=== fake-ip 段 ===");
        foreach (var s in new string[] { "198.18.0.1", "198.18.255.255", "198.19.0.1" })
        {
            var ip = IPAddress.Parse(s);
            Ok(CnIpRanges.Contains(ip) == false, s + " not in CN table (baseline)");
        }
        // IsFakeIp 是私有静态方法，用反射验
        var t = typeof(ProxyServer);
        var isFake = t.GetMethod("IsFakeIp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Ok(isFake != null, "IsFakeIp exists");
        foreach (var s in new string[] { "198.18.0.1", "198.18.1.1", "198.19.255.255" })
            Ok((bool)isFake.Invoke(null, new object[] { IPAddress.Parse(s) }), s + " is fake-ip");
        foreach (var s in new string[] { "183.2.172.177", "104.16.132.229", "10.13.24.1", "198.17.1.1", "198.20.1.1" })
            Ok(!(bool)isFake.Invoke(null, new object[] { IPAddress.Parse(s) }), s + " is NOT fake-ip");

        // IsDomestic 对 fake-ip 要返回 true
        var inst = Activator.CreateInstance(t, new object[] { 18800, 1080, "10.13.24.1", (Action<string>)delegate (string s) { } });
        var isDom = t.GetMethod("IsDomestic", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Ok((bool)isDom.Invoke(inst, new object[] { "198.18.0.1" }), "IsDomestic(198.18.0.1) == true (fake-ip treated as domestic)");

        // PreferIp 对 fake-ip 要保留原值（不把假 IP 传下去）
        var prefer = t.GetMethod("PreferIp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var r = (string)prefer.Invoke(null, new object[] { "198.18.0.1" });
        Ok(r == "198.18.0.1", "PreferIp keeps fake-ip as-is (got " + r + ")");

        // ── ② AllowTun ──
        Console.WriteLine();
        Console.WriteLine("=== AllowTun ===");
        var f = typeof(FerryRuntime).GetField("AllowTun");
        Ok(f != null, "FerryRuntime.AllowTun exists");
        Ok(f.FieldType == typeof(bool), "AllowTun is bool");

        var appDir = @"C:\ferry-test\fix16";
        Directory.CreateDirectory(appDir);
        var cfg = new Cfg();
        cfg.RemoteHost = "10.13.24.1";
        cfg.RelayPort = 1080;
        cfg.AccountIp = "10.113.66.6";
        cfg.Token = "TEST-XT-0001";
        cfg.HttpPort = 18900;
        cfg.UseTun = true;          // 配置里开了 TUN
        cfg.DirectSocks = true;

        var rt = new FerryRuntime(cfg, appDir, delegate (string m) { Console.WriteLine("   [rt] " + m); });
        rt.AllowTun = false;        // 但节点模式下应该禁用
        var err = rt.Start();
        Ok(err.Length == 0, "runtime started with UseTun=true but AllowTun=false");
        if (err.Length == 0)
        {
            Ok(!rt.TunRunning, "TUN NOT running (AllowTun=false)");
            Console.WriteLine("   TunError = [" + rt.TunError + "]");
            rt.Stop();
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

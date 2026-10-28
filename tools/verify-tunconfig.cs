using System;
using System.IO;
using System.Reflection;
using Ferry;

// 1.0.31：TUN 配置生成的结构断言。
//
// 这一版修的是「防回环 PROXESS-NAME 规则曾经依赖启动瞬间检测到 Clash」——
// 所以最关键的断言是：**clashPort = 0 时也必须写出 PROCESS-NAME 规则**。
static class TunCfgCheck
{
    static int fails;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static int CountOf(string s, string needle)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    static void Main()
    {
        var dir = @"C:\Users\63526\AppData\Local\Temp\pkg\funcfg";
        Directory.CreateDirectory(dir);

        var cfg = new Cfg();
        var tun = new TunMode(cfg, dir, delegate (string m) { Console.WriteLine("   | " + m); });

        var mi = typeof(TunMode).GetMethod("BuildConfig", BindingFlags.NonPublic | BindingFlags.Instance);
        Ok(mi != null, "BuildConfig found");
        if (mi == null) { Environment.Exit(1); }

        var f = typeof(TunMode).GetField("_clashPort", BindingFlags.NonPublic | BindingFlags.Instance);

        // ── 场景 A：有本机 Clash ──
        if (f != null) f.SetValue(tun, 7897);
        var fb = typeof(TunMode).GetField("_bypassPort", BindingFlags.NonPublic | BindingFlags.Instance);
        Ok(fb != null, "_bypassPort field exists");
        if (fb != null) fb.SetValue(tun, 10805);
        var yaml = (string)mi.Invoke(tun, new object[] { 10801, "", 1080 });
        var path = Path.Combine(dir, "tun-config.yaml");
        File.WriteAllText(path, yaml);
        Console.WriteLine("   wrote " + path + " (" + yaml.Length + " bytes)");

        Ok(yaml.Contains("find-process-mode: strict"), "A: find-process-mode is strict (not always)");
        Ok(!yaml.Contains("find-process-mode: always"), "A: no always mode");
        Ok(CountOf(yaml, "- PROCESS-NAME,") == 26, "A: 13 procs x 2 spellings = 26 PROCESS-NAME rules");
        Ok(yaml.Contains("- PROCESS-NAME,clash-verge,ferry-clash"), "A: clash-verge goes through ferry-clash (not DIRECT)");
        Ok(yaml.Contains("- PROCESS-NAME,clash-verge.exe,ferry-clash"), "A: clash-verge.exe same");
        Ok(yaml.Contains("- PROCESS-NAME,verge-mihomo.exe,ferry-clash"), "A: verge-mihomo.exe same");
        Ok(yaml.Contains("- name: ferry-clash"), "A: ferry-clash proxy exists");
        Ok(yaml.Contains("port: 10805"), "A: ferry-clash points at bypass port");
        Ok(!yaml.Contains("- PROCESS-NAME,clash-verge,DIRECT"), "A: no DIRECT for clash (would be dead when network is down)");
        Ok(yaml.Contains("MATCH,ferry"), "A: MATCH,ferry fallback");
        Ok(yaml.Contains("- 198.18.0.2:53"), "A: dns-hijack narrowed to tun gateway");
        Ok(!yaml.Contains("- any:53"), "A: no blanket any:53 hijack (would break Clash DNS)");
        Ok(yaml.Contains("- NETWORK,udp,ferry-udp-group"), "A: UDP goes to ferry-udp-group");
        Ok(yaml.Contains("IP-CIDR,127.0.0.0/8,DIRECT,no-resolve"), "A: loopback direct");
        Ok(!yaml.Contains("clash-local"), "A: no removed clash-local proxy");
        Ok(!yaml.Contains("rule-providers"), "A: no removed rule-providers");
        Ok(!yaml.Contains("cn-cidr.txt"), "A: no cn-cidr file dependency");

        // 规则顺序：PROCESS-NAME 例外必须早于 MATCH
        int pn = yaml.IndexOf("- PROCESS-NAME,");
        int mt = yaml.LastIndexOf("MATCH,ferry");
        Ok(pn > 0 && pn < mt, "A: PROCESS-NAME before MATCH");
        int lanRule = yaml.IndexOf("IP-CIDR,10.0.0.0/8,DIRECT");
        int udpRule = yaml.IndexOf("NETWORK,udp,ferry-udp-group");
        Ok(lanRule > 0 && lanRule < pn, "A: LAN rules before PROCESS-NAME (clash must not tunnel LAN)");
        Ok(udpRule > 0 && udpRule < pn, "A: UDP rule before PROCESS-NAME (mihomo PROCESS-NAME ignores protocol)");
        Ok(yaml.TrimEnd().EndsWith("MATCH,ferry"), "A: MATCH is the last rule");

        // ── 场景 B：没有检测到本机 Clash（这就是用户机 23:44 的情形）──
        var tun2 = new TunMode(cfg, dir, delegate (string s) { });
        if (f != null) f.SetValue(tun2, 0);
        if (fb != null) fb.SetValue(tun2, 10805);
        var yaml2 = (string)mi.Invoke(tun2, new object[] { 10801, "", 1080 });
        File.WriteAllText(Path.Combine(dir, "tun-config-noclash.yaml"), yaml2);
        Ok(CountOf(yaml2, "- PROCESS-NAME,") == 26,
            "B: clashPort=0 STILL writes 26 PROCESS-NAME rules (the actual bug)");
        Ok(yaml2.Contains("- PROCESS-NAME,clash-verge.exe,ferry-clash"), "B: clash-verge.exe present without detection");
        Ok(yaml2.Contains("MATCH,ferry"), "B: still falls back to ferry");

        // 两个场景除 clashPort 外应当逐字相同（证明规则不再依赖检测结果）
        Ok(yaml == yaml2, "A == B: config identical regardless of Clash detection");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

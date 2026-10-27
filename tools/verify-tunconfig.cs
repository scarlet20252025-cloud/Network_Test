using System;
using System.IO;
using System.Reflection;
using Ferry;

// 1.0.21 的 TUN 分流：生成完整 mihomo 配置，交给 mihomo -t 校验，
// 再检查规则顺序（境外例外必须排在「国内走渡口」之前）。
static class TunCfgCheck
{
    static int fails;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static void Main()
    {
        var dir = @"C:\ferry-test\funcfg";
        Directory.CreateDirectory(dir);

        var cfg = new Cfg();
        var tun = new TunMode(cfg, dir, delegate (string m) { Console.WriteLine("   | " + m); });

        // 反射调 BuildConfig(socksPort, udpHost, udpPort)。
        // clashPort 走字段（Start 里赋值），这里直接写字段以绕过测启动。
        var f = typeof(TunMode).GetField("_clashPort", BindingFlags.NonPublic | BindingFlags.Instance);
        Ok(f != null, "_clashPort field exists");
        if (f != null) f.SetValue(tun, 7897);

        var mi = typeof(TunMode).GetMethod("BuildConfig", BindingFlags.NonPublic | BindingFlags.Instance);
        Ok(mi != null, "BuildConfig found");
        if (mi == null) { Environment.Exit(1); }

        var yaml = (string)mi.Invoke(tun, new object[] { 10801, "", 1080 });
        var path = Path.Combine(dir, "tun-config.yaml");
        File.WriteAllText(path, yaml);
        Console.WriteLine("   wrote " + path + " (" + yaml.Length + " bytes)");

        // ── 结构断言 ──
        Ok(yaml.Contains("clash-local"), "proxies has clash-local");
        Ok(yaml.Contains("127.0.0.1:7897") || yaml.Contains("server: 127.0.0.1"), "clash-local points to 127.0.0.1");
        Ok(yaml.Contains("overseas"), "proxy-group overseas exists");
        Ok(yaml.Contains("rule-providers"), "rule-providers section exists");
        Ok(yaml.Contains("path: ./cn-cidr.txt"), "rule-provider reads local file (no network)");
        Ok(!yaml.Contains("GEOIP,CN"), "does not use GEOIP (needs downloading a database)");
        Ok(yaml.Contains("PROCESS-NAME"), "PROCESS-NAME exceptions to avoid loops");

        // 规则顺序：1.1.1.0/24 例外必须早于 RULE-SET,cn
        int ex = yaml.IndexOf("IP-CIDR,1.1.1.0/24");
        int rs = yaml.IndexOf("RULE-SET,cn");
        Ok(ex > 0 && rs > 0 && ex < rs, "exception rule comes before RULE-SET,cn");

        // MATCH 必须在最后（mihomo 的兜底规则）
        int match = yaml.LastIndexOf("MATCH,");
        Ok(match > rs, "MATCH comes after RULE-SET");

        // 无 clash 端口时退回全走渡口
        var tun2 = new TunMode(cfg, dir, delegate (string s) { });
        if (f != null) f.SetValue(tun2, 0);
        var yaml2 = (string)mi.Invoke(tun2, new object[] { 10801, "", 1080 });
        Ok(!yaml2.Contains("clash-local"), "clashPort=0 falls back to ferry-only");
        Ok(yaml2.Contains("MATCH,ferry"), "clashPort=0 keeps MATCH,ferry");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

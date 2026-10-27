using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using Ferry;

// 1.0.22：验证「fake-ip 不再被当成国内」+「DNS 缓存生效」+「TUN 规则回到极简」。

static class SlowFixCheck
{
    static int fails;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static void Main()
    {
        var dir = @"C:\ferry-test\slowfix";
        Directory.CreateDirectory(dir);

        // ── 1) fake-ip 不再被当作国内 ──
        var cfg = new Cfg();
        var proxy = new ProxyServer(17951, 1080, "10.13.24.1", delegate (string s) { });

        var t = typeof(ProxyServer);
        var isDomestic = t.GetMethod("IsDomestic", BindingFlags.NonPublic | BindingFlags.Instance);
        var isFakeIp = t.GetMethod("IsFakeIp", BindingFlags.NonPublic | BindingFlags.Static);
        var resolveLocal = t.GetMethod("ResolveLocal", BindingFlags.NonPublic | BindingFlags.Static);
        Ok(isDomestic != null && isFakeIp != null && resolveLocal != null, "methods found");

        // 直接给 IP 字符串：这是 fake-ip 时不能再判国内
        Ok((bool)isDomestic.Invoke(proxy, new object[] { "198.18.0.5" }) == false,
           "fake-ip IP is NOT domestic");
        Ok((bool)isDomestic.Invoke(proxy, new object[] { "198.19.255.1" }) == false,
           "198.19.x fake-ip is NOT domestic");
        // 真实国内/境外 IP
        Ok((bool)isDomestic.Invoke(proxy, new object[] { "183.2.172.177" }) == true,
           "real CN ip is domestic");
        Ok((bool)isDomestic.Invoke(proxy, new object[] { "104.16.132.229" }) == false,
           "real foreign ip is not domestic");

        // ── 2) ResolveLocal 过滤 fake-ip ──
        var list = (List<IPAddress>)resolveLocal.Invoke(null, new object[] { "www.baidu.com" });
        Console.WriteLine("   baidu resolved: " + list.Count + " addr(s)");
        bool anyFake = false;
        foreach (var a in list) if ((bool)isFakeIp.Invoke(null, new object[] { a })) anyFake = true;
        Ok(!anyFake, "ResolveLocal never returns fake-ip");
        Ok(list.Count > 0, "baidu resolved to real address(es)");

        // ── 3) DNS 缓存：第二次必须是缓存命中（快）──
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var l2 = (List<IPAddress>)resolveLocal.Invoke(null, new object[] { "www.baidu.com" });
        sw.Stop();
        Console.WriteLine("   cached lookup: " + sw.ElapsedMilliseconds + "ms");
        Ok(sw.ElapsedMilliseconds < 5, "second lookup is cached (<5ms)");
        Ok(l2.Count == list.Count, "cache returns same result");

        // ── 4) TUN 规则回到极简 ──
        var tun = new TunMode(cfg, dir, delegate (string s) { });
        var fc = typeof(TunMode).GetField("_clashPort", BindingFlags.NonPublic | BindingFlags.Instance);
        var bm = typeof(TunMode).GetMethod("BuildConfig", BindingFlags.NonPublic | BindingFlags.Instance);
        if (fc != null) fc.SetValue(tun, 7897);
        var yaml = (string)bm.Invoke(tun, new object[] { 10801, "", 1080 });
        Console.WriteLine("   yaml " + yaml.Length + " bytes");

        Ok(yaml.Contains("MATCH,ferry"), "MATCH,ferry present");
        Ok(!yaml.Contains("RULE-SET"), "no RULE-SET (would need extra DNS per domain)");
        Ok(!yaml.Contains("rule-providers"), "no rule-providers (no file dependency)");
        Ok(!yaml.Contains("cn-cidr"), "does not reference cn-cidr.txt");
        Ok(!yaml.Contains("clash-local"), "no clash-local proxy (split happens in local proxy)");
        Ok(yaml.Contains("PROCESS-NAME,clash-verge.exe"), "keeps PROCESS-NAME loop guard");
        Ok(yaml.Contains("IP-CIDR,10.0.0.0/8,DIRECT"), "keeps LAN direct rules");

        File.WriteAllText(Path.Combine(dir, "tun-config.yaml"), yaml);
        Console.WriteLine("   wrote " + Path.Combine(dir, "tun-config.yaml"));

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

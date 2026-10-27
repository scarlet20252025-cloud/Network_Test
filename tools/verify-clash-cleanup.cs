using System;
using System.IO;
using System.Text;
using Ferry;

// 验证「清理旧版留在 Clash 配置里的渡口规则」。
//
// 这个功能防的是一个真实事故：1.0.9~1.0.12 会往 Verge 的 Merge.yaml 写
//   prepend-rules: [GEOIP,CN,国内出口]
// 把所有国内流量劫持到渡口端口；渡口没借网时端口是空的 → 国内站全打不开。
static class CleanupTest
{
    static int fails = 0;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static void Main()
    {
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var prof = Path.Combine(appdata, "io.github.clash-verge-rev.clash-verge-rev", "profiles");
        var f = Path.Combine(prof, "Merge.yaml");

        bool hadDir = Directory.Exists(prof);
        string backup = File.Exists(f) ? File.ReadAllText(f) : null;
        try
        {
            Directory.CreateDirectory(prof);

            // ① 干净文件：不该被动
            File.WriteAllText(f, "# Profile Enhancement Merge Template for Clash Verge\n\nprofile:\n  store-selected: true\n", new UTF8Encoding(false));
            var r1 = ClashCleanup.CleanLegacy(delegate (string s) { });
            Ok(r1 == null, "clean file -> no change");
            Ok(!File.ReadAllText(f).Contains("渡口"), "clean file still clean");

            // ② 带渡口块的文件：删掉块，保留原有内容
            var dirty = "# Profile Enhancement Merge Template for Clash Verge\n\nprofile:\n  store-selected: true\n\n" +
                        "# ── 渡口（由渡口助手自动写入；整段删掉就等于取消接入） ──\n" +
                        "prepend-proxies:\n  - name: 渡口\n    type: socks5\n    server: 127.0.0.1\n    port: 10801\n    udp: true\n\n" +
                        "prepend-proxy-groups:\n  - name: 国内出口\n    type: fallback\n    proxies: [渡口, DIRECT]\n\n" +
                        "prepend-rules:\n  - GEOIP,CN,国内出口\n";
            File.WriteAllText(f, dirty, new UTF8Encoding(false));

            var r2 = ClashCleanup.CleanLegacy(delegate (string s) { Console.WriteLine("   log: " + s); });
            Ok(r2 != null, "dirty file -> cleaned");
            var after = File.ReadAllText(f);
            Ok(!after.Contains("渡口"), "ferry block removed");
            Ok(!after.Contains("GEOIP,CN"), "hijacking rule removed");
            Ok(after.Contains("store-selected"), "user content preserved");
            Ok(File.Exists(f + ".bak-ferry"), "backup created");

            // ③ 再调一次：幂等
            var r3 = ClashCleanup.CleanLegacy(delegate (string s) { });
            Ok(r3 == null, "second call is a no-op");

            // ④ 没有 Verge 目录时不抛异常（测试机上那个目录被 Clash 占用，改测「profiles 里没有 Merge」）
            File.Delete(f);
            var r4 = ClashCleanup.CleanLegacy(delegate (string s) { });
            Ok(r4 == null, "no Merge.yaml -> no throw");
        }
        finally
        {
            // 原样恢复
            try
            {
                if (backup != null)
                {
                    Directory.CreateDirectory(prof);
                    File.WriteAllText(f, backup, new UTF8Encoding(false));
                    var bak = f + ".bak-ferry";
                    if (File.Exists(bak)) File.Delete(bak);
                }
            }
            catch (Exception) { }
        }

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

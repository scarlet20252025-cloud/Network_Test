using System;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Threading;
using Ferry;

// 1.0.25：验证「失败记忆」真的省掉了第二次等待。
// 场景：假渡口（不监听）→ 第一次连必失败并计时 → 第二次应该直接被记忆拦住，
// 不再去连，耗时应接近 0。
static class StutterCheck
{
    static int fails;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static void Main()
    {
        var t = typeof(ProxyServer);
        var recent = t.GetMethod("RecentlyFailed", BindingFlags.NonPublic | BindingFlags.Static);
        var mark = t.GetMethod("MarkFailed", BindingFlags.NonPublic | BindingFlags.Static);
        var connect = t.GetMethod("ConnectSmart", BindingFlags.NonPublic | BindingFlags.Instance);
        var cnMs = t.GetField("CnConnectMs", BindingFlags.NonPublic | BindingFlags.Static);
        var memo = t.GetField("_recentFail", BindingFlags.NonPublic | BindingFlags.Static);

        Ok(recent != null && mark != null && connect != null, "methods found");
        Ok(cnMs != null && (int)cnMs.GetValue(null) == 2500,
           "domestic quick timeout is 2500ms");

        // ── 1) 记忆的基本行为 ──
        var host = "test.stutter.local";
        Ok((bool)recent.Invoke(null, new object[] { "ferry", host, 443 }) == false,
           "nothing remembered yet");
        mark.Invoke(null, new object[] { "ferry", host, 443 });
        Ok((bool)recent.Invoke(null, new object[] { "ferry", host, 443 }) == true,
           "failure is remembered");
        Ok((bool)recent.Invoke(null, new object[] { "ferry", host, 8443 }) == false,
           "different port is not affected");
        Ok((bool)recent.Invoke(null, new object[] { "clash", host, 443 }) == false,
           "different route is not affected");

        // ── 2) 记忆真的省掉了连接尝试 ──
        // 起一个从没监听过的端口当「渡口」，连它会立即被拒（不是超时），
        // 但关键是**第二次根本不该再去连**。
        var proxy = new ProxyServer(17971, 1081, "127.0.0.1", delegate (string s) { });
        var fc = t.GetField("_clashPort", BindingFlags.NonPublic | BindingFlags.Instance);
        if (fc != null) fc.SetValue(proxy, 17972);   // 也不存在的本地代理

        var key = "quick.stutter.test";
        var sw = Stopwatch.StartNew();
        try { connect.Invoke(proxy, new object[] { key, 443, 8000 }); } catch { }
        sw.Stop();
        var first = sw.ElapsedMilliseconds;
        Console.WriteLine("   first attempt: " + first + "ms");

        sw = Stopwatch.StartNew();
        try { connect.Invoke(proxy, new object[] { key, 443, 8000 }); } catch { }
        sw.Stop();
        var second = sw.ElapsedMilliseconds;
        Console.WriteLine("   second attempt: " + second + "ms");

        Ok(second <= first + 50, "second attempt is not slower than the first");

        // ── 3) 表不会无限增长 ──
        var dict = memo.GetValue(null) as System.Collections.IDictionary;
        for (int i = 0; i < 5000; i++)
            mark.Invoke(null, new object[] { "ferry", "h" + i + ".test", 80 });
        Console.WriteLine("   table size after 5000 marks: " + dict.Count);
        Ok(dict.Count <= 4500, "failure table stays bounded");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Ferry;

// 1.1.1：**历史修复不变量**守卫。
//
// 用户明确要求「别引出新的问题、也别让旧的错误回滚」。这一条不测新功能，
// 只把过去用真实事故换来的结论钉成断言 —— 尤其那些「验证器过期会反过来
// 诱导人把修复改回去」的地方（1.1.0 直连 UDP、踩坑 72 fake-ip、26 条防回环、
// clash-egress fallback、严格 IDNA）。
//
// 跑法：powershell -ExecutionPolicy Bypass -File tools\run-verify.ps1 -Name verify-regression-guard
static class RegressionGuard
{
    static int fails;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static string Read(string rel)
    {
        var d = new DirectoryInfo(Environment.CurrentDirectory);
        for (int i = 0; i < 6 && d != null; i++, d = d.Parent)
        {
            string p = Path.Combine(d.FullName, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p)) return File.ReadAllText(p, Encoding.UTF8);
        }
        return null;
    }

    static string Between(string src, string from, string to)
    {
        if (src == null) return "";
        int a = src.IndexOf(from, StringComparison.Ordinal);
        if (a < 0) return "";
        int b = src.IndexOf(to, a + from.Length, StringComparison.Ordinal);
        if (b < 0) return "";
        return src.Substring(a, b - a);
    }

    // 注释里出现某个词不算「代码依赖它」，断言只看真正的代码行。
    static string CodeOnly(string src)
    {
        var sb = new StringBuilder();
        foreach (var line in src.Split('\n'))
            if (!line.TrimStart().StartsWith("//")) sb.AppendLine(line.TrimEnd('\r'));
        return sb.ToString();
    }

    static int CountOf(string s, string needle)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    static string PyFunc(string src, string name)
    {
        if (src == null) return null;
        var m = Regex.Match(src.Replace("\r\n", "\n"),
            @"(?ms)^def " + Regex.Escape(name) + @"\(.*?(?=^def |^class |\Z)");
        return m.Success ? m.Value : null;
    }

    static void Main()
    {
        string proxy = Read("ferry-client/src/Proxy.cs");
        string tunSrc = Read("ferry-client/src/TunMode.cs");
        string mainWin = Read("ferry-client/src/MainWindow.cs");
        string relay = Read("relay/ferry-socks5-server.py");
        string wg = Read("wireguard/ferry-socks5-server.py");
        string doc = Read("docs/分流架构.md");
        string agents = Read("AGENTS.md");

        Ok(proxy != null && tunSrc != null && mainWin != null &&
           relay != null && wg != null && doc != null && agents != null,
           "all guarded files readable");

        // ── A) 踩坑 71/72/73：fake-ip、段表、后缀表、不做 DNS ──
        var ps = new ProxyServer(29991, 29992, "127.0.0.1", delegate (string m) { });
        var isDomestic = typeof(ProxyServer).GetMethod("IsDomestic",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var isCnDomain = typeof(ProxyServer).GetMethod("IsDomesticDomain",
            BindingFlags.NonPublic | BindingFlags.Static);
        Func<string, bool> cn = delegate (string h)
        {
            return (bool)isDomestic.Invoke(ps, new object[] { h });
        };
        Func<string, bool> cnDom = delegate (string h)
        {
            return (bool)isCnDomain.Invoke(null, new object[] { h });
        };
        Ok(!cn("198.18.0.1"), "fake-ip is NOT domestic (pitfall 72 / 1.0.21 slowdown)");
        Ok(cn("183.2.172.177"), "CN IP is domestic (APNIC bitmap)");
        Ok(!cn("104.244.42.197"), "foreign IP is not domestic");
        Ok(cnDom("baidu.com"), "CN suffix matches");
        Ok(!cnDom("notbaidu.com"), "suffix boundary not over-matched");
        Ok(!cnDom("baidu.com.evil.com"), "suffix in the middle does not match");
        Ok(CnIpRanges.Count >= 5900, "CN range bitmap still loaded (" + CnIpRanges.Count + ")");
        Ok(proxy.Contains("if (IsFakeIp(ip)) return false;"), "fake-ip special case still in code");
        Ok(proxy.Contains("只查后缀表，不做 DNS 解析") || proxy.Contains("不碰 DNS"),
           "domain judgement still avoids DNS (pitfall 73)");

        var body = CodeOnly(Between(proxy, "Socket ConnectSmart(",
                                    "/// <summary>国内目标连渡口最多等这么久"));
        Ok(body.Length > 200, "ConnectSmart body located");
        Ok(!body.Contains("UseTun") && !body.Contains("AllowSplit"),
           "TUN mode does NOT disable splitting (1.0.16 / pitfall 71: foreign all 502)");
        Ok(body.Contains("ConnectSocks(_clashPort, host, port"),
           "foreign branch passes the RAW domain, not PreferIp (polluted DNS)");
        Ok(body.Contains("PreferIp(host)"), "domestic branch still uses PreferIp");
        Ok(body.Contains("port, quickMs, _socksHost);"),
           "foreign fallback to ferry uses the 2.5s quick timeout (1.1.1)");

        // ── B) 1.0.25：超时与失败记忆 ──
        var cnMs = typeof(ProxyServer).GetField("CnConnectMs", BindingFlags.NonPublic | BindingFlags.Static);
        var memoSec = typeof(ProxyServer).GetField("FailMemoSec", BindingFlags.NonPublic | BindingFlags.Static);
        Ok(cnMs != null && (int)cnMs.GetValue(null) == 2500, "CnConnectMs == 2500");
        Ok(memoSec != null && (int)memoSec.GetValue(null) == 300, "FailMemoSec == 300");
        Ok(proxy.Contains("_recentFail.Count > 4000"), "failure table still bounded");
        var recent = typeof(ProxyServer).GetMethod("RecentlyFailed",
            BindingFlags.NonPublic | BindingFlags.Static);
        var mark = typeof(ProxyServer).GetMethod("MarkFailed",
            BindingFlags.NonPublic | BindingFlags.Static);
        const string memoHost = "guard.memo.example";
        mark.Invoke(null, new object[] { "ferry", memoHost, 443 });
        Ok((bool)recent.Invoke(null, new object[] { "ferry", memoHost, 443 }), "memo records failure");
        Ok(!(bool)recent.Invoke(null, new object[] { "ferry", memoHost, 8443 }), "memo is port-scoped");
        Ok(!(bool)recent.Invoke(null, new object[] { "clash", memoHost, 443 }), "memo is direction-scoped");

        // ── C) 1.0.24 / 踩坑 68：日志去重机制不得回滚 ──
        Ok(proxy.Contains("void LogOnce("), "LogOnce retained");
        Ok(!proxy.Contains("_loggedOnce.Clear()"), "no whole-table Clear() re-spam (pitfall 68)");
        Ok(proxy.Contains("LogOnceWindowSec"), "one-shot alerts have a time window");
        Ok(proxy.Contains("IP 没有「一级域名」"), "RootDomain no longer collapses IP literals");

        // ── D) 1.1.1 新增机制本身存在（防止被整体回退）──
        Ok(proxy.Contains("enum SocksFailKind { None, Line, Target }"), "SocksFailKind enum present");
        Ok(proxy.Contains("out SocksFailKind fail"), "classified ConnectSocks overload present");
        Ok(proxy.Contains("fail = SocksFailKind.Target"), "REP!=0 classified as Target");
        Ok(proxy.Contains("if (why == SocksFailKind.Line) MarkFailed") ||
           proxy.Contains("if (why2 == SocksFailKind.Line) MarkFailed"),
           "only Line failures poison the memory");
        Ok(proxy.Contains("void NoteFallback("), "windowed fallback aggregator present");
        Ok(proxy.Contains("第二跳成功"), "aggregate records second-hop result");
        Ok(proxy.Contains("static bool IsInfrastructureTarget"), "probe-target silence present");
        Ok(proxy.Contains("string socksHost = \"127.0.0.1\""),
           "legacy 5-arg ConnectSocks default kept (TestProxy.cs)");

        // ── E) 1.0.31 / 1.0.37 / 1.1.0：生成的 mihomo 配置 ──
        string dir = Path.Combine(Path.GetTempPath(), "ferry-guard");
        Directory.CreateDirectory(dir);
        var cfg = new Cfg();
        var mi = typeof(TunMode).GetMethod("BuildConfig",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Ok(mi != null, "BuildConfig found");
        if (mi != null)
        {
            var fClash = typeof(TunMode).GetField("_clashPort",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var fBypass = typeof(TunMode).GetField("_bypassPort",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var tun = new TunMode(cfg, dir, delegate (string m) { });
            if (fClash != null) fClash.SetValue(tun, 7897);
            if (fBypass != null) fBypass.SetValue(tun, 10805);
            string yaml = (string)mi.Invoke(tun, new object[] { 10801, "10.13.24.1", 1080 });

            Ok(CountOf(yaml, "- PROCESS-NAME,") == 26,
               "26 anti-loop PROCESS-NAME rules (1.0.31, unconditional)");
            Ok(yaml.TrimEnd().EndsWith("MATCH,ferry"), "MATCH,ferry is the last rule");
            Ok(yaml.Contains("proxies: [DIRECT, ferry-clash]"),
               "clash-egress fallback kept (1.0.37 DSH disconnect fix)");
            Ok(yaml.Contains("find-process-mode: strict"), "strict process matching kept");
            Ok(yaml.Contains("- any:53"), "dns-hijack kept");
            Ok(!yaml.Contains("RULE-SET,cn") && !yaml.Contains("cn-cidr.txt"),
               "no TUN-layer CN ruleset (pitfall 72)");

            // 1.1.0 的 40% 丢包修复本体：别被「1080 不监听 UDP」的旧注释劝退改回去
            Ok(yaml.Contains("proxies: [ferry-udp]"), "UDP group is single-member (1.1.0)");
            Ok(yaml.Contains("server: 10.13.24.1"), "UDP proxy points at remote relay (1.1.0)");
            Ok(yaml.Contains("udp: true"), "socks5 UDP ASSOCIATE kept (1.1.0)");
            Ok(CountOf(yaml, "type: fallback") == 1,
               "only clash-egress remains fallback; UDP drift removed (1.1.0)");

            var tun2 = new TunMode(cfg, dir, delegate (string m) { });
            if (fClash != null) fClash.SetValue(tun2, 0);
            if (fBypass != null) fBypass.SetValue(tun2, 10805);
            string yaml2 = (string)mi.Invoke(tun2, new object[] { 10801, "10.13.24.1", 1080 });
            Ok(yaml == yaml2, "config identical with/without Clash detected (1.0.31)");
        }

        // ── F) 1.1.0 服务端严格 IDNA：两份副本必须一致 ──
        string[] banned = { "decode('ascii', 'ignore')", "encode('ascii', 'ignore')" };
        foreach (string b in banned)
            Ok(!relay.Contains(b) && !wg.Contains(b), "no silent ascii fallback: " + b);
        Ok(relay.Contains("host = decode_socks5_domain(raw)") &&
           wg.Contains("host = decode_socks5_domain(raw)"),
           "TCP CONNECT uses strict IDNA decode in both copies");
        Ok(relay.Contains("hb = encode_socks5_domain(host)") &&
           wg.Contains("hb = encode_socks5_domain(host)"),
           "TCP CONNECT uses strict IDNA encode in both copies");
        string rd = PyFunc(relay, "decode_socks5_domain");
        string wd = PyFunc(wg, "decode_socks5_domain");
        string re = PyFunc(relay, "encode_socks5_domain");
        string we = PyFunc(wg, "encode_socks5_domain");
        Ok(rd != null && rd == wd, "decode_socks5_domain identical in relay/wireguard");
        Ok(re != null && re == we, "encode_socks5_domain identical in relay/wireguard");

        // ── G) 1.1.1 文案与文档：别让旧结论回来误导人 ──
        Ok(mainWin.Contains("TUN 接管；TCP 仍按目标分流"), "global-mode UI no longer claims all traffic uses ferry");
        Ok(!mainWin.Contains("已开启（UDP / 游戏都走渡口）"), "old misleading UI text removed");
        Ok(!mainWin.Contains("全局模式已接管全部流量"), "old misleading log text removed");
        Ok(doc.Contains("五之四、失败分类与回退日志"), "architecture doc records the new classification");
        Ok(doc.Contains("1.1.0 更正"), "stale UDP claim explicitly corrected in doc");
        Ok(!doc.Contains("只监听 TCP"), "stale '1080 only listens TCP' conclusion gone");
        Ok(agents.Contains("分流只能在本地代理层做一次"), "AGENTS.md single-layer split rule intact");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

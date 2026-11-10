using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using Ferry;

// 1.1.1：失败分类 / 回退日志汇总 / 探测静默 的行为验证。
//
// 为什么单独一个验证器：旧代码把「上游回了 REP≠0」和「上游连不上」都当成线路
// 故障，任何一处改动都可能悄悄退化成「满屏刷日志 + 目标被硬绕开 5 分钟」。
// 这里用假的 SOCKS5 上游把三种结果分别造出来，逐条钉死。
//
// 跑法（verify.cmd 编不全 src，必须用 run-verify.ps1）：
//   powershell -ExecutionPolicy Bypass -File tools\run-verify.ps1 -Name verify-fallback
static class FallbackCheck
{
    static int fails;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    // Mode: 0 = 成功, 1 = 回 REP=0x05（目标级）, 2 = 握手后不回 REP（线路级）
    class FakeUpstream
    {
        readonly int _port;
        readonly int _mode;
        TcpListener _l;
        volatile bool _run;
        public int Hits;

        public FakeUpstream(int port, int mode) { _port = port; _mode = mode; }

        public void Start()
        {
            _l = new TcpListener(IPAddress.Loopback, _port);
            _l.Start();
            _run = true;
            var t = new Thread(delegate ()
            {
                while (_run)
                {
                    TcpClient c;
                    try { c = _l.AcceptTcpClient(); } catch (Exception) { break; }
                    var th = new Thread(delegate () { Serve(c); });
                    th.IsBackground = true;
                    th.Start();
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        public void Stop()
        {
            _run = false;
            try { _l.Stop(); } catch (Exception) { }
        }

        void Serve(TcpClient c)
        {
            try
            {
                var ns = c.GetStream();
                var greeting = new byte[3];
                ReadN(ns, greeting, 3);
                ns.Write(new byte[] { 5, 0 }, 0, 2);
                var head = new byte[4];
                ReadN(ns, head, 4);
                Interlocked.Increment(ref Hits);

                if (_mode == 2) { Thread.Sleep(1200); return; }   // 不给 REP，逼出读超时

                byte rep = _mode == 1 ? (byte)5 : (byte)0;
                ns.Write(new byte[] { 5, rep, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10);
                if (_mode == 0) Thread.Sleep(1200);                // 保持连接，让调用方拿到 socket
            }
            catch (Exception) { }
            finally { try { c.Close(); } catch (Exception) { } }
        }

        static void ReadN(NetworkStream ns, byte[] b, int n)
        {
            int off = 0;
            while (off < n) { int r = ns.Read(b, off, n - off); if (r <= 0) break; off += r; }
        }
    }

    static MethodInfo Priv(string name)
    {
        return typeof(ProxyServer).GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
    }

    static ProxyServer NewProxy(int listenPort, int socksPort, List<string> logs)
    {
        return new ProxyServer(listenPort, socksPort, "127.0.0.1",
            delegate (string m) { logs.Add(m); });
    }

    static void Main()
    {
        // ── 0) 接口存在性：旧入口不能被删掉 ──
        var enumType = typeof(ProxyServer).Assembly.GetType("Ferry.SocksFailKind");
        Ok(enumType != null, "SocksFailKind enum exists");
        if (enumType != null)
        {
            var names = string.Join(",", Enum.GetNames(enumType));
            Ok(names == "None,Line,Target", "enum has None/Line/Target (got " + names + ")");
        }

        int five = 0, six = 0;
        foreach (var m in typeof(ProxyServer).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "ConnectSocks") continue;
            if (m.GetParameters().Length == 5) five++;
            if (m.GetParameters().Length == 6) six++;
        }
        Ok(five == 1, "5-arg ConnectSocks kept (TestProxy / bypass / UDP callers)");
        Ok(six == 1, "6-arg ConnectSocks overload with out fail exists");

        Ok(Priv("LogOnce") != null, "LogOnce retained (1.0.24 anti-spam not rolled back)");
        Ok(Priv("NoteFallback") != null, "NoteFallback exists");
        Ok(Priv("IsInfrastructureTarget") != null, "IsInfrastructureTarget exists");
        Ok(Priv("RootDomain") != null, "RootDomain exists");

        var cnMs = typeof(ProxyServer).GetField("CnConnectMs", BindingFlags.NonPublic | BindingFlags.Static);
        var memoSec = typeof(ProxyServer).GetField("FailMemoSec", BindingFlags.NonPublic | BindingFlags.Static);
        var winField = typeof(ProxyServer).GetField("FallbackLogWindowSec", BindingFlags.NonPublic | BindingFlags.Static);
        var onceField = typeof(ProxyServer).GetField("LogOnceWindowSec", BindingFlags.NonPublic | BindingFlags.Static);
        Ok(cnMs != null && (int)cnMs.GetValue(null) == 2500, "CnConnectMs still 2500 (1.0.25)");
        Ok(memoSec != null && (int)memoSec.GetValue(null) == 300, "FailMemoSec still 300 (1.0.25)");
        Ok(winField != null && (int)winField.GetValue(null) == 60, "fallback log window = 60s");
        Ok(onceField != null && (int)onceField.GetValue(null) == 300, "LogOnce window = 300s");

        var loggedOnceField = typeof(ProxyServer).GetField("_loggedOnce",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Ok(loggedOnceField != null &&
           loggedOnceField.FieldType == typeof(Dictionary<string, DateTime>),
           "_loggedOnce is a time-window dictionary, not a HashSet that Clear()s");

        // ── 1) 三种失败性质 ──
        var pTarget = FreePort();
        var upTarget = new FakeUpstream(pTarget, 1);
        upTarget.Start();
        SocksFailKind kTarget;
        var sTarget = ProxyServer.ConnectSocks(pTarget, "1.2.3.4", 80, 2000, "127.0.0.1", out kTarget);
        Ok(sTarget == null && kTarget == SocksFailKind.Target,
           "REP!=0 -> Target (upstream alive, target unreachable)");
        upTarget.Stop();

        var pLine = FreePort();
        var upLine = new FakeUpstream(pLine, 2);
        upLine.Start();
        SocksFailKind kLine;
        var sLine = ProxyServer.ConnectSocks(pLine, "1.2.3.4", 80, 400, "127.0.0.1", out kLine);
        Ok(sLine == null && kLine == SocksFailKind.Line,
           "no REP / read timeout -> Line");
        upLine.Stop();

        SocksFailKind kDead;
        var sDead = ProxyServer.ConnectSocks(FreePort(), "1.2.3.4", 80, 400, "127.0.0.1", out kDead);
        Ok(sDead == null && kDead == SocksFailKind.Line, "upstream not listening -> Line");

        var pOk = FreePort();
        var upOk = new FakeUpstream(pOk, 0);
        upOk.Start();
        SocksFailKind kOk;
        var sOk = ProxyServer.ConnectSocks(pOk, "1.2.3.4", 80, 2000, "127.0.0.1", out kOk);
        Ok(sOk != null && kOk == SocksFailKind.None, "success -> None + socket");
        if (sOk != null) sOk.Close();
        upOk.Stop();

        // 4 参数兼容入口仍然可用（TestProxy.cs 就是这么调的）
        var pCompat = FreePort();
        var upCompat = new FakeUpstream(pCompat, 1);
        upCompat.Start();
        Ok(ProxyServer.ConnectSocks(pCompat, "1.2.3.4", 80, 1000) == null,
           "legacy 4-arg ConnectSocks still compiles and works");
        upCompat.Stop();

        // ── 2) 探测目标静默 / 域名去重 ──
        var infra = Priv("IsInfrastructureTarget");
        Func<string, int, bool> isInfra = delegate (string h, int p)
        {
            return (bool)infra.Invoke(null, new object[] { h, p });
        };
        Ok(isInfra("223.6.6.6", 53), "DNS IP :53 is infrastructure");
        Ok(isInfra("1.1.1.1", 443), "known DNS IP is infrastructure");
        Ok(isInfra("www.msftconnecttest.com", 80), "NCSI probe is infrastructure");
        Ok(!isInfra("www.workbuddy.cn", 443), "real business domain is NOT infrastructure");
        Ok(!isInfra("docs.qq.com", 443), "real business domain is NOT infrastructure");

        var root = Priv("RootDomain");
        Func<string, string> rootOf = delegate (string h)
        {
            return (string)root.Invoke(null, new object[] { h });
        };
        Ok(rootOf("223.6.6.6") == "223.6.6.6", "IP root domain keeps full IP");
        Ok(rootOf("223.5.5.5") == "223.5.5.5", "different DNS IPs are not merged");
        Ok(rootOf("img1.cdn.example.com") == "example.com", "domain root still collapses subdomains");

        // ── 3) 日志汇总：不再每条连接一行，且带第二跳结果 ──
        var nf = Priv("NoteFallback");
        var logs = new List<string>();
        var ps = NewProxy(FreePort(), FreePort(), logs);
        for (int i = 0; i < 10; i++)
            nf.Invoke(ps, new object[] { "渡口→本机代理", SocksFailKind.Target,
                                         i % 2 == 0, false, "h" + i + ".example.com", 443 });
        Ok(logs.Count == 1, "10 fallbacks -> exactly 1 aggregated line (no spam)");
        if (logs.Count > 0)
        {
            Ok(logs[0].Contains("第二跳成功"), "aggregate carries second-hop result");
            Ok(logs[0].Contains("目标级"), "aggregate carries failure classification");
            Ok(logs[0].Contains("汇总"), "aggregate is explicitly a window summary");
            Console.WriteLine("   " + logs[0]);
        }

        var logs2 = new List<string>();
        var ps2 = NewProxy(FreePort(), FreePort(), logs2);
        for (int i = 0; i < 5; i++)
            nf.Invoke(ps2, new object[] { "渡口→本机代理", SocksFailKind.Target,
                                          false, true, "223.6.6.6", 53 });
        Ok(logs2.Count == 0, "quiet probe traffic alone emits no line");
        nf.Invoke(ps2, new object[] { "渡口→本机代理", SocksFailKind.Target,
                                      true, false, "docs.qq.com", 443 });
        Ok(logs2.Count == 1 && logs2[0].Contains("探测流量 5 次"),
           "quiet traffic is counted in the summary (not silently lost)");

        // LogOnce 仍是「同一类只打一次」，但有时间窗
        var logs3 = new List<string>();
        var ps3 = NewProxy(FreePort(), FreePort(), logs3);
        var lo = Priv("LogOnce");
        lo.Invoke(ps3, new object[] { "测试告警", "a.example.com" });
        lo.Invoke(ps3, new object[] { "测试告警", "b.example.com" });
        Ok(logs3.Count == 1, "LogOnce still dedupes by root domain");

        // ── 4) 只有线路级失败才写失败记忆 ──
        var recent = Priv("RecentlyFailed");
        var connectSmart = Priv("ConnectSmart");
        var clashField = typeof(ProxyServer).GetField("_clashPort",
            BindingFlags.NonPublic | BindingFlags.Instance);

        const string cnHost = "183.2.172.177";   // baidu IP，CnIpRanges 里确定是国内
        int ferryTargetPort = FreePort(), clashTargetPort = FreePort();
        var ferryTarget = new FakeUpstream(ferryTargetPort, 1);
        var clashTarget = new FakeUpstream(clashTargetPort, 1);
        ferryTarget.Start(); clashTarget.Start();
        var logs4 = new List<string>();
        var ps4 = NewProxy(FreePort(), ferryTargetPort, logs4);
        clashField.SetValue(ps4, clashTargetPort);
        connectSmart.Invoke(ps4, new object[] { cnHost, 8081, 2000 });
        Ok(!(bool)recent.Invoke(null, new object[] { "ferry", cnHost, 8081 }),
           "Target failure does NOT poison 5-minute failure memory");
        Ok(ferryTarget.Hits > 0 || clashTarget.Hits > 0, "both upstreams were actually exercised");
        ferryTarget.Stop(); clashTarget.Stop();

        int deadFerryPort = FreePort(), liveClashPort = FreePort();
        var liveClash = new FakeUpstream(liveClashPort, 1);
        liveClash.Start();
        var logs5 = new List<string>();
        var ps5 = NewProxy(FreePort(), deadFerryPort, logs5);
        clashField.SetValue(ps5, liveClashPort);
        connectSmart.Invoke(ps5, new object[] { cnHost, 8082, 2000 });
        Ok((bool)recent.Invoke(null, new object[] { "ferry", cnHost, 8082 }),
           "Line failure IS remembered (1.0.25 behaviour preserved)");
        liveClash.Stop();

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

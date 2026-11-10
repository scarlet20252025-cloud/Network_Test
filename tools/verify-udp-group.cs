using System;
using System.Reflection;
using System.Text;

// 验证 TunMode.BuildConfig 生成的 YAML：UDP 出口固定单成员 select + 直连远端。
//
// 历史：1.0.26 曾把 DIRECT 放进 ferry-udp-group 做 fallback；1.1.0 实测发现
// fallback 每 10 秒重判会让会话在两条分支间漂移，断网时段约 40% 丢包，
// 改成固定 select [ferry-udp] 且直连远端 SOCKS5 后 0% 丢包。
// 「远端 1080 不监听 UDP」是早期把「裸发固定端口」和「socks5 UDP ASSOCIATE」
// 混为一谈的误判，见 docs/UDP丢包分层诊断-20261110.md §4.3。
// YAML 是拼字符串拼出来的，必须把真实产物拿出来断言 —— 别照旧注释把 fallback 加回来。
static class VerifyUdpGroup
{
    static int fails;
    static void Ok(bool c, string n) { Ok(c, n, null); }
    static void Ok(bool c, string n, string extra)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n + (extra == null ? "" : "   " + extra));
        if (!c) fails++;
    }

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        // BuildConfig 是 private 实例方法：BuildConfig(int socksPort, string udpHost, int udpPort)
        var t = typeof(Ferry.TunMode);
        var build = t.GetMethod("BuildConfig",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        Ok(build != null, "找到 BuildConfig", build == null ? "（找不到，后面无法断言）" : "");
        if (build == null)
        {
            Console.WriteLine("FAILED");
            Environment.Exit(1);
        }

        // TunMode 的构造签名：TunMode(Cfg cfg, string appDir, Action<string> log)
        object inst = null;
        var ctor = t.GetConstructor(new Type[] {
            typeof(Ferry.Cfg), typeof(string), typeof(Action<string>)
        });
        if (ctor != null)
        {
            try
            {
                var cfgObj = new Ferry.Cfg();
                Action<string> logger = delegate (string s) { };
                inst = ctor.Invoke(new object[] { cfgObj, "C:\\tmp\\ferry-test", logger });
            }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                Console.WriteLine("  构造异常: " + inner.GetType().Name + ": " + inner.Message);
            }
        }
        Ok(inst != null, "构造 TunMode 实例", inst == null ? "（签名不匹配）" : "");

        string yaml = null;
        try
        {
            yaml = (string)build.Invoke(inst, new object[] { 10801, "10.13.24.1", 1080 });
        }
        catch (Exception e)
        {
            var inner = e.InnerException ?? e;
            Console.WriteLine("  调用失败: " + inner.GetType().Name + ": " + inner.Message);
        }

        Ok(yaml != null && yaml.Length > 200, "拿到 YAML 文本", yaml == null ? "" : (yaml.Length + " 字节"));
        if (yaml == null) { Console.WriteLine("FAILED"); Environment.Exit(1); }

        // ── 核心断言（1.1.0 起）：UDP 组固定单成员 select，出口直连远端 ──
        int gi = yaml.IndexOf("name: ferry-udp-group");
        Ok(gi > 0, "存在 ferry-udp-group");
        if (gi > 0)
        {
            int next = yaml.IndexOf("- name:", gi + 10);
            string block = next > 0 ? yaml.Substring(gi, next - gi) : yaml.Substring(gi);
            Console.WriteLine("  --- ferry-udp-group 实际生成内容 ---");
            foreach (var line in block.Split('\n'))
                if (line.Trim().Length > 0) Console.WriteLine("    " + line.TrimEnd());

            Ok(block.Contains("type: select"), "组类型是 select（固定出口，不漂移）");
            Ok(!block.Contains("type: fallback"), "没有 fallback 漂移");
            Ok(block.Contains("proxies: [ferry-udp]"), "只列 ferry-udp 一个成员");
            Ok(!block.Contains("generate_204"), "没有会周期性重判的测活地址");
        }

        Ok(yaml.Contains("proxies: [ferry-udp]"), "整个配置里 UDP 组是单成员");
        int udpProxy = yaml.IndexOf("- name: ferry-udp");
        int udpNext = udpProxy > 0 ? yaml.IndexOf("- name:", udpProxy + 10) : -1;
        string udpSeg = udpProxy > 0
            ? yaml.Substring(udpProxy, (udpNext > 0 ? udpNext : yaml.Length) - udpProxy)
            : "";
        Ok(udpSeg.Contains("server: 10.13.24.1"), "UDP 出口直连远端中继");
        Ok(udpSeg.Contains("port: 1080"), "UDP 出口用中继控制端口");
        Ok(udpSeg.Contains("udp: true"), "保留 socks5 UDP ASSOCIATE");
        Ok(!udpSeg.Contains("127.0.0.1"), "UDP 出口不再绕本地代理");

        Console.WriteLine(fails == 0 ? "ALL PASS" : (fails + " FAILED"));
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

using System;
using System.Reflection;
using System.Text;

// 验证 TunMode.BuildConfig 生成的 YAML 里，UDP 组是 fallback 且 DIRECT 在第一位。
//
// 为什么要这个工具：这条改动是「游戏延迟回归」的修复核心 ——
// 1.0.26 把 DIRECT 从 ferry-udp-group 里拿掉，导致所有 UDP 都被强制绕远端，
// 游戏往返路径从「本机 → 目标」变成「本机 → pcs → 目标 → pcs → 本机」。
// YAML 是拼字符串拼出来的，光读代码容易改错，必须把真实产物拿出来断言。
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

        // ── 核心断言：ferry-udp-group ──
        int gi = yaml.IndexOf("name: ferry-udp-group");
        Ok(gi > 0, "存在 ferry-udp-group");
        if (gi > 0)
        {
            int next = yaml.IndexOf("- name:", gi + 10);
            string block = next > 0 ? yaml.Substring(gi, next - gi) : yaml.Substring(gi);
            Console.WriteLine("  --- ferry-udp-group 实际生成内容 ---");
            foreach (var line in block.Split('\n'))
                if (line.Trim().Length > 0) Console.WriteLine("    " + line.TrimEnd());

            Ok(block.Contains("type: fallback"), "组类型是 fallback（直连不通能自动降级）");
            // 断言必须只看 proxies: 那一行。
            // 之前写成在整个 block 里找 "DIRECT"，会先命中 rules 段里的
            // IP-CIDR,10.0.0.0/8,DIRECT —— 那是无关的 DIRECT，导致假失败。
            string proxiesLine = null;
            foreach (var line in block.Split('\n'))
                if (line.Trim().StartsWith("proxies:")) { proxiesLine = line.Trim(); break; }
            Ok(proxiesLine != null, "找到 proxies 行", proxiesLine);
            int di = proxiesLine == null ? -1 : proxiesLine.IndexOf("DIRECT");
            int ui = proxiesLine == null ? -1 : proxiesLine.IndexOf("ferry-udp");
            Ok(di > 0 && ui > 0, "DIRECT 与 ferry-udp 都在列表里");
            Ok(di > 0 && ui > 0 && di < ui, "DIRECT 排在第一位（= 默认走直连）");
            Ok(block.Contains("generate_204"), "有测活地址（禁网时才知道该切隧道）");
        }

        // ── 反回归：确认没有被写死成只走 ferry-udp ──
        Ok(!yaml.Contains("proxies: [ferry-udp]"),
           "没有「只列 ferry-udp」的写法（那是 1.0.26 的 bug）");

        Console.WriteLine(fails == 0 ? "ALL PASS" : (fails + " FAILED"));
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

// 验证 TunMode 生成的 mihomo 配置里 route-exclude-address 全是用 /CIDR
// 为什么单独一个验证器：2026-11-01 把裸 IP（223.5.5.5）写进 exclude 列表，
// mihomo 解析时直接 exit 1，TUN 起不来，用户当场不能用。
// 用：tools\verify.cmd verify-tun-exclude.cs
using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text;

class VerifyTunExclude
{
    static int ok = 0, fail = 0;

    static void Check(string name, bool cond, string extra = "")
    {
        if (cond) { ok++; Console.WriteLine("  [OK]   " + name + " " + extra); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + " " + extra); }
    }

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var asm = Assembly.LoadFrom("渡口助手.exe");
        var t = asm.GetType("Ferry.TunMode");
        if (t == null) { Console.WriteLine("找不到 Ferry.TunMode"); Environment.Exit(2); }
        var m = t.GetMethod("BuildConfig",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (m == null) { Console.WriteLine("找不到 BuildConfig"); Environment.Exit(2); }

        var inst = Activator.CreateInstance(t, true);
        string yaml = (string)m.Invoke(inst, new object[] { 10801, "10.13.24.1", 10802 });

        // 抽出 route-exclude-address 段
        int i = yaml.IndexOf("route-exclude-address:");
        int j = yaml.IndexOf("dns:", i);
        string seg = (i >= 0 && j > i) ? yaml.Substring(i, j - i) : "";

        Console.WriteLine("=== route-exclude-address 段 ===");
        Console.WriteLine(seg.Trim());
        Console.WriteLine();

        var addrs = new List<string>();
        foreach (var line in seg.Split('\n'))
        {
            var s = line.Trim();
            if (!s.StartsWith("- ")) continue;
            addrs.Add(s.Substring(2).Trim());
        }

        Check("段不为空", addrs.Count > 0, "(" + addrs.Count + " 条)");

        foreach (var a in addrs)
        {
            bool hasSlash = a.Contains("/");
            bool parses = false;
            if (hasSlash)
            {
                try
                {
                    var parts = a.Split('/');
                    IPAddress ip;
                    int len;
                    parses = IPAddress.TryParse(parts[0], out ip)
                             && int.TryParse(parts[1], out len);
                }
                catch { }
            }
            Check("CIDR 合法: " + a, hasSlash && parses);
        }

        // 关键项必须存在
        Check("含 10.0.0.0/8", addrs.Contains("10.0.0.0/8"));
        Check("含 172.16.0.0/12", addrs.Contains("172.16.0.0/12"));
        Check("含 192.168.0.0/16", addrs.Contains("192.168.0.0/16"));
        Check("含 223.5.5.5/32", addrs.Contains("223.5.5.5/32"));

        Console.WriteLine();
        Console.WriteLine("结果: " + ok + " 通过, " + fail + " 失败");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}

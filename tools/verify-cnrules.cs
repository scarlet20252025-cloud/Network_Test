using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Ferry;

// 验证 TUN 分流用的 CN 规则文件：生成的 CIDR 是否真的覆盖国内 IP、
// 且不误盖境外 IP。上一个版本用「字符串包含」判断，把 223.5 合并到
// 223.4.0.0/15 之后就误报失败了 —— 这个版本真解析 CIDR 做包含判断。
static class TunSplitCheck
{
    static int fails;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static List<KeyValuePair<uint, int>> Parse(string rules)
    {
        var list = new List<KeyValuePair<uint, int>>();
        foreach (var l in rules.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = l.Split(',');
            if (p.Length < 2 || p[0] != "IP-CIDR") continue;
            var parts = p[1].Split('/');
            var ip = IPAddress.Parse(parts[0]);
            var b = ip.GetAddressBytes();
            uint v = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            list.Add(new KeyValuePair<uint, int>(v, int.Parse(parts[1])));
        }
        return list;
    }

    static bool CoveredInFile(List<KeyValuePair<uint, int>> list, IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        uint v = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        foreach (var kv in list)
        {
            uint mask = kv.Value == 0 ? 0u : (0xFFFFFFFFu << (32 - kv.Value));
            if ((v & mask) == (kv.Key & mask)) return true;
        }
        return false;
    }

    static void Main()
    {
        var rules = CnIpRanges.AsCidrRules();
        var lines = rules.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Console.WriteLine("   rules generated: " + lines.Length);
        Ok(lines.Length > 100 && lines.Length < 4000, "rule count is sane (" + lines.Length + ")");

        int bad = 0;
        foreach (var l in lines)
            if (!System.Text.RegularExpressions.Regex.IsMatch(l, @"^IP-CIDR,\d+\.\d+\.\d+\.\d+/\d+$"))
            {
                if (bad < 3) Console.WriteLine("   bad line: " + l);
                bad++;
            }
        Ok(bad == 0, "all lines well-formed");

        var parsed = Parse(rules);

        // 国内必须覆盖
        foreach (var s in new[] { "223.5.5.5", "114.114.114.114", "183.2.172.177",
                                  "39.156.66.10", "110.242.68.66", "180.101.49.11", "202.108.22.5" })
            Ok(CoveredInFile(parsed, IPAddress.Parse(s)), s + " covered by rules");

        // 境外不能被盖住
        foreach (var s in new[] { "104.16.132.229", "8.8.8.8", "31.13.92.37",
                                  "140.82.121.4", "20.205.243.166", "1.1.1.1", "162.159.1.1" })
            Ok(!CoveredInFile(parsed, IPAddress.Parse(s)), s + " NOT covered");

        var dir = @"C:\ferry-test\tunsplit";
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cn-cidr.txt"), rules);
        Console.WriteLine("   wrote cn-cidr.txt (" + rules.Length + " bytes)");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

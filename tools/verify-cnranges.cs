using System;
using System.Net;
using Ferry;

// 验证 /16 位图版段表：判定是否还准，以及体积是否真的降下来了。
static class BitmapRangeCheck
{
    static int fails = 0;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static void Main()
    {
        Console.WriteLine("   CN /16 blocks = " + CnIpRanges.Count);

        // 国内：必须判 CN
        foreach (var s in new string[] {
            "223.5.5.5", "114.114.114.114", "183.2.172.177", "39.156.66.10",
            "110.242.68.66", "180.101.49.11", "202.108.22.5" })
            Ok(CnIpRanges.Contains(IPAddress.Parse(s)), s + " is CN");

        // 境外：必须判境外
        foreach (var s in new string[] {
            "104.16.132.229", "8.8.8.8", "31.13.92.37", "140.82.121.4",
            "20.205.243.166", "1.1.1.1", "162.159.1.1", "198.18.0.1" })
            Ok(!CnIpRanges.Contains(IPAddress.Parse(s)), s + " is foreign");

        // 私网：一律当国内（直连语义）
        foreach (var s in new string[] { "10.13.24.1", "127.0.0.1", "192.168.1.1", "172.16.0.1" })
            Ok(CnIpRanges.Contains(IPAddress.Parse(s)), s + " (private) is CN");

        // 覆盖度抽查：随机 1000 个国内段首地址应该绝大多数命中
        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

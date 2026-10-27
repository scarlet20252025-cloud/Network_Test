using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using Ferry;

// 1.0.23：验证「域名判定不依赖 DNS」。
// 核心是那批把用户日志刷屏的域名：m701.music.126.net（网易云）被判成境外，
// 以及一堆 DNS 服务器 IP 被判成国内后去撞渡口。
static class DomainSplitCheck
{
    static int fails;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static void Main()
    {
        var proxy = new ProxyServer(17961, 1080, "10.13.24.1", delegate (string s) { });
        var mi = typeof(ProxyServer).GetMethod("IsDomestic", BindingFlags.NonPublic | BindingFlags.Instance);
        Func<string, bool> dom = h => (bool)mi.Invoke(proxy, new object[] { h });

        Console.WriteLine("--- 日志里出现过、必须判国内的域名 ---");
        foreach (var h in new[] {
            "m701.music.126.net", "m801.music.126.net", "gateway.ooz.cn",
            "music.163.com", "www.baidu.com", "www.bilibili.com",
            "api.weixin.qq.com", "www.zhihu.com", "item.taobao.com",
            "cdn.jsdelivr.net" /* 境外 CDN，见下 */ })
        {
            bool d = dom(h);
            Console.WriteLine("   " + h + " -> " + (d ? "国内" : "境外"));
            if (h != "cdn.jsdelivr.net") Ok(d, h + " is domestic");
        }

        Console.WriteLine("--- 必须判境外的 ---");
        foreach (var h in new[] {
            "www.google.com", "www.youtube.com", "chatgpt.com",
            "github.com", "x.com", "raw.githubusercontent.com",
            "encrypted-tbn0.gstatic.com", "cdn.jsdelivr.net" })
        {
            bool d = dom(h);
            Ok(!d, h + " is foreign");
        }

        Console.WriteLine("--- IP 目标仍按段表判断 ---");
        Ok(dom("223.5.5.5"), "223.5.5.5 (aliyun dns) is domestic");
        Ok(dom("183.2.172.177"), "183.2.172.177 is domestic");
        Ok(!dom("104.16.132.229"), "104.16.132.229 is foreign");
        Ok(!dom("198.18.0.5"), "fake-ip is foreign");

        Console.WriteLine("--- 后缀匹配边界 ---");
        Ok(!dom("notbaidu.com"), "notbaidu.com is NOT domestic (no dot boundary)");
        Ok(dom("a.b.baidu.com"), "deep subdomain of baidu is domestic");
        Ok(dom("BAIDU.COM"), "uppercase matches");
        Ok(!dom("baidu.com.evil.com"), "suffix-in-middle does not match");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

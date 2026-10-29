using System;
using System.Text;
using Ferry;

// 直接调用 1.0.45 的 Hotspot.Start()，把返回的错误字符串打印出来。
//
// 为什么要单独跑：客户端日志只有一句「热点没起来（状态仍是 Off）」，
// 那是 1.0.44 的说法。1.0.45 改了共享源判据和卡死自愈，需要看它自己怎么报。
// 这个程序跑的是**和客户端完全相同的代码**（直接编译 Hotspot.cs）。
static class HotspotStartCheck
{
    static void Main(string[] args)
    {
        // ⚠ 这个程序会**真的启动热点**，在单网卡机器上会把当前 WiFi 顶掉。
        // 2026-10-29 已经在用户机上因此断过一次网，所以加硬闸：
        // 必须显式传参 I-ACCEPT-WIFI-DROP 才继续。
        if (args == null || args.Length == 0 || args[0] != "I-ACCEPT-WIFI-DROP")
        {
            Console.WriteLine("拒绝执行：这个程序会启停热点，可能顶掉本机 WiFi。");
            Console.WriteLine("只允许在 10.113.66.6 测试机上跑；确实要跑就传参 I-ACCEPT-WIFI-DROP");
            Environment.Exit(2);
            return;
        }
        Console.OutputEncoding = Encoding.UTF8;
        var err = Hotspot.EnsureLoaded();
        if (err != "")
        {
            Console.WriteLine("WinRT 不可用：" + err);
            Environment.Exit(1);
        }

        Console.WriteLine("=== 共享源选择 ===");
        var pick = typeof(Hotspot).GetMethod("PickShareProfile",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var chosen = pick.Invoke(null, null);
        if (chosen == null)
        {
            Console.WriteLine("  PickShareProfile 返回 null");
        }
        else
        {
            var pn = (string)chosen.GetType().GetProperty("ProfileName").GetValue(chosen, null);
            Console.WriteLine("  选中: " + pn);
        }

        Console.WriteLine("=== 调用 Hotspot.Start（等待 20 秒）===");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string r = Hotspot.Start("", "", 20000);
        sw.Stop();
        Console.WriteLine("  耗时 " + sw.ElapsedMilliseconds + " ms");
        Console.WriteLine("  返回: " + (r == "" ? "(空 = 成功)" : r));

        Console.WriteLine("=== 事后状态 ===");
        Console.WriteLine("  state = " + Hotspot.State());
        Console.WriteLine("  NicReady = " + Hotspot.NicReady());

        Environment.Exit(r == "" ? 0 : 1);
    }
}

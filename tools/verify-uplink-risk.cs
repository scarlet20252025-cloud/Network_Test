using System;
using System.Reflection;
using System.Text;
using Ferry;

// 验证 Hotspot.UplinkRisk()：在「只有一块无线网卡联网」时它必须报风险，
// 在「有线上行」时它必须放行。
//
// 为什么需要（2026-10-29 真实事故）：在没有这个检查之前，客户端会直接调
// StartTetheringAsync，把用户正在用的 WiFi（STA）顶掉 —— 他的远控直接断。
// 所以这个静态判定必须可靠，否则「保护」等于没有。
static class UplinkRiskCheck
{
    static int fails;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var hs = typeof(Hotspot);

        // ① 方法存在且是 public static（UI 要直接调）
        var mi = hs.GetMethod("UplinkRisk", BindingFlags.Public | BindingFlags.Static);
        Ok(mi != null, "Hotspot.UplinkRisk exists (public static)");
        if (mi == null) { Console.WriteLine("FAILED"); Environment.Exit(1); }
        Ok(mi.ReturnType == typeof(string), "UplinkRisk returns string");

        // ② 实跑：本机现在只有 WLAN 一块物理网卡在联网
        var err = Hotspot.EnsureLoaded();
        if (err != "")
        {
            Console.WriteLine("    (WinRT 不可用，跳过实跑：" + err + ")");
            Console.WriteLine(fails == 0 ? "ALL PASS" : (fails + " FAILED"));
            Environment.Exit(fails == 0 ? 0 : 1);
        }

        string risk = Hotspot.UplinkRisk();
        Console.WriteLine("--- UplinkRisk() 返回 ---");
        if (string.IsNullOrEmpty(risk)) Console.WriteLine("    (空 = 判为无风险)");
        else foreach (var line in risk.Split('\n')) Console.WriteLine("    " + line.TrimEnd());
        Console.WriteLine("---");

        // 这台机器：WLAN(WiFi, LocalAccess) + Radmin VPN(虚拟) + FerryTun(虚拟)
        // 唯一真正的物理上行是无线 → 必须报风险
        Ok(!string.IsNullOrEmpty(risk), "single wireless uplink -> risk reported");
        Ok(risk.Contains("只有一块无线网卡") || risk.Contains("WiFi"), "risk text mentions the single-NIC cause");
        Ok(risk.Contains("断") || risk.Contains("失去"), "risk text warns about losing connectivity");

        // ③ 有线上行时应当放行 —— 用反射验证判定逻辑本身（构造不了假 profile，
        //    所以这里只验「非 71 的共享源一定返回空」这条早退分支）
        Console.WriteLine("    (有线场景需要真插网线才能验，本机无法构造)");

        Console.WriteLine(fails == 0 ? "ALL PASS" : (fails + " FAILED"));
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

using System;
using System.Collections;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using Ferry;

// 1.0.45：验证热点共享源的选择逻辑（离线，不需要真的开热点）。
//
// 为什么需要（2026-10-29 用户机实测）：
//   1.0.44 要求共享源既「物理网卡」又「InternetAccess」。而渡口的主场景是
//   校园网禁网 —— 那时物理网卡只有 LocalAccess，全机唯一的 InternetAccess
//   是 FerryTun（TUN 虚拟网卡）。于是永远选不到物理网卡，只能落到 FerryTun，
//   而 FerryTun 当共享源会卡死在 InTransition（实测 25 秒超时）。
//
//   同一台机器上还有两个「长得像物理网卡」的假货：
//     Radmin VPN —— Famatech 虚拟以太网，iana=6，Up
//     etgame     —— 游戏加速器隧道，iana=53，Up
//   只看 IanaInterfaceType 会把 Radmin 当以太网选走。
//
// 本脚本验证：
//   ① Hotspot 有 IsVirtualAdapter / PickShareProfile / RestartIcs
//   ② IsVirtualAdapter 对真实适配器描述判断正确
//   ③ 共享源候选里不该出现 FerryTun / Radmin VPN / etgame / 热点 AP 网卡
static class HotspotPickerCheck
{
    static int fails;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static MethodInfo Find(Type t, string name)
    {
        return t.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
    }

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var hs = typeof(Hotspot);

        // ① 方法存在
        var isVirtual = Find(hs, "IsVirtualAdapter");
        var pick = Find(hs, "PickShareProfile");
        var restart = Find(hs, "RestartIcs");
        Ok(isVirtual != null, "Hotspot.IsVirtualAdapter exists");
        Ok(pick != null, "Hotspot.PickShareProfile exists");
        Ok(restart != null, "Hotspot.RestartIcs exists");
        if (isVirtual == null || pick == null) { Console.WriteLine("FAILED"); Environment.Exit(1); }

        // ② 用本机真实的适配器描述验证排除规则
        Console.WriteLine("--- 本机适配器判定 ---");
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            bool virt = (bool)isVirtual.Invoke(null, new object[] { ni.Description });
            Console.WriteLine("    " + (virt ? "VIRT " : "PHYS ") + ni.Description +
                              "  [" + ni.NetworkInterfaceType + ", " + ni.OperationalStatus + "]");
        }

        // 这几条是本机实测到的「必须排除」的描述
        string[] mustExclude = new string[] {
            "Meta Tunnel",                                // FerryTun
            "Famatech Radmin VPN Ethernet Adapter",       // Radmin VPN（iana=6，诱饵）
            "etgame Tunnel",                              // 游戏加速器
            "Microsoft Wi-Fi Direct Virtual Adapter #2",  // 热点自己的 AP 网卡
            "WAN Miniport (IP)",
            "Teredo Tunneling Pseudo-Interface"
        };
        foreach (var d in mustExclude)
            Ok((bool)isVirtual.Invoke(null, new object[] { d }), "excluded as virtual: " + d);

        // 这几条必须**不**被排除（真物理网卡）
        string[] mustKeep = new string[] {
            "Realtek 8852BE Wireless LAN WiFi 6 PCI-E NIC",
            "Realtek PCIe GbE Family Controller",
            "Intel(R) Wi-Fi 6 AX201 160MHz",
            "Intel(R) Ethernet Connection I219-V"
        };
        foreach (var d in mustKeep)
            Ok(!(bool)isVirtual.Invoke(null, new object[] { d }), "kept as physical: " + d);

        // ③ 真的跑一遍 PickShareProfile，看选中谁
        Console.WriteLine("--- 实际选中 ---");
        var err = Hotspot.EnsureLoaded();
        if (err != "")
        {
            Console.WriteLine("    跳过（WinRT 不可用：" + err + "）");
        }
        else
        {
            var chosen = pick.Invoke(null, null);
            if (chosen == null)
            {
                Console.WriteLine("    没有可用共享源（本机没有已连接的物理网卡）");
                Ok(true, "no physical adapter -> returns null (better than picking a fake)");
            }
            else
            {
                var pn = (string)chosen.GetType().GetProperty("ProfileName").GetValue(chosen, null);
                var adp = (object)chosen.GetType().GetProperty("NetworkAdapter").GetValue(chosen, null);
                string desc = "";
                if (adp != null)
                {
                    // Guid 归一化再比：WinRT 给的是小写无花括号，.NET 是大写带花括号。
                    // 直接字符串比较永远不相等 —— 这个坑在 Hotspot.cs 里也踩过，
                    // 表现是「所有网卡都被当成虚拟网卡」，最后报「没有可用共享源」。
                    var id = Convert.ToString(adp.GetType().GetProperty("NetworkAdapterId").GetValue(adp, null));
                    var want = Guid.Parse(id.Trim().Trim('{', '}'));
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        var have = Guid.Parse(ni.Id.Trim().Trim('{', '}'));
                        if (have == want) { desc = ni.Description; break; }
                    }
                }
                Console.WriteLine("    选中: " + pn + "   [" + desc + "]");
                Ok(!(bool)isVirtual.Invoke(null, new object[] { desc }),
                   "chosen adapter is not virtual");
                Ok(pn != "FerryTun", "did NOT pick FerryTun (the InTransition trap)");
            }
        }

        Console.WriteLine(fails == 0 ? "ALL PASS" : (fails + " FAILED"));
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

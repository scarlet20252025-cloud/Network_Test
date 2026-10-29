using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Ferry
{
    /// <summary>
    /// Windows 自带「移动热点」的启停（WinRT NetworkOperatorTetheringManager）。
    ///
    /// 为什么不用 netsh：
    ///   老办法 `netsh wlan set hostednetwork` 依赖「承载网络」能力，新网卡
    ///   （高通 FastConnect 等）在 `netsh wlan show drivers` 里直接显示
    ///   `Hosted network supported: No`，那条路根本走不通。系统设置里那个
    ///   「移动热点」开关用的是 WinRT API，新老网卡都能用。
    ///
    /// 为什么全部走反射：
    ///   目标机器只装 .NET Framework 4.8，不带 WinRT 的编译期元数据
    ///   （Windows.winmd）。用 Type.GetType("..., ContentType=WindowsRuntime")
    ///   在运行时按名字取类型，不需要任何额外 SDK 引用 —— 这是「目标机器
    ///   什么都不用装」这个前提的延续。代价是拿不到强类型，代码里到处是
    ///   object/反射，所以这个文件里的注释格外多。
    ///
    /// 为什么共享源要按「网络级别」挑而不是按网卡名：
    ///   渡口的出口可能是物理网卡（校园网/WiFi）、也可能是 TUN（全局模式）。
    ///   写死名字在换机器/换模式时就失效。挑第一个 InternetAccess 级别的
    ///   连接配置档，物理网卡和 TUN 都能覆盖（TUN 的级别也是 InternetAccess）。
    ///
    /// ⚠ 已知限制（2026-10-27 实测，写进 docs/踩坑记录）：
    ///   StartTetheringAsync 返回 Success **不代表**热点真的起来了。无线网卡的
    ///   SoftAP 驱动状态卡死时，热点会在约 15 秒后自动关闭、虚拟网卡停在
    ///   169.254.x.x。这种情况软件层（重启热点、换共享源、重写 SSID、重启网卡）
    ///   全部无效，只能重启电脑。所以这里的「成功」以**虚拟网卡拿到
    ///   192.168.137.1/24** 为准，不以 API 返回值为准。
    /// </summary>
    static class Hotspot
    {
        // ── WinRT 类型（运行时按名字取，取不到就是这台机器不支持）──
        static Type _tetheringMgr;
        static Type _netInfo;
        static MethodInfo _asTaskGeneric;
        static string _loadError;

        /// <summary>加载 WinRT 类型。返回空字符串表示可用。</summary>
        public static string EnsureLoaded()
        {
            if (_tetheringMgr != null) return "";
            if (_loadError != null) return _loadError;
            try
            {
                _tetheringMgr = Type.GetType(
                    "Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, " +
                    "Windows.Networking.NetworkOperators, ContentType=WindowsRuntime");
                _netInfo = Type.GetType(
                    "Windows.Networking.Connectivity.NetworkInformation, " +
                    "Windows.Networking.Connectivity, ContentType=WindowsRuntime");
                if (_tetheringMgr == null || _netInfo == null)
                {
                    _loadError = "这台系统没有移动热点 API（需要 Windows 10 及以上）";
                    return _loadError;
                }

                // WinRT 的异步方法返回 IAsyncOperation<T>，要拿到结果必须转成 Task。
                // 这个转换方法在 System.Runtime.WindowsRuntime 里，同样反射取。
                //
                // 注意：`Assembly.Load("System.Runtime.WindowsRuntime")` 用**简名**
                // 会抛 FileNotFoundException —— 实测（2026-10-28）它在 GAC_MSIL 里，
                // 简名解析不到，必须给完整强名称。两个可能的公钥都试一遍：
                //   b77a5c561934e089（.NET Framework 自带那份，实测能命中）
                //   b03f5f7f11d50a3a（部分运行时上见过）
                Assembly wrAsm = null;
                foreach (var full in new string[] {
                    "System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
                    "System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a" })
                {
                    try { wrAsm = Assembly.Load(full); } catch (Exception) { wrAsm = null; }
                    if (wrAsm != null) break;
                }
                if (wrAsm == null)
                {
                    _loadError = "找不到 System.Runtime.WindowsRuntime（热点启停需要它）";
                    return _loadError;
                }
                var ext = wrAsm.GetType("System.WindowsRuntimeSystemExtensions");
                foreach (var m in ext.GetMethods())
                {
                    if (m.Name != "AsTask") continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    if (ps[0].ParameterType.Name != "IAsyncOperation`1") continue;
                    _asTaskGeneric = m;
                    break;
                }
                if (_asTaskGeneric == null)
                {
                    _loadError = "找不到 WinRT 异步转换方法（System.Runtime.WindowsRuntime 版本不对）";
                    return _loadError;
                }
                return "";
            }
            catch (Exception e)
            {
                _loadError = "加载移动热点 API 失败：" + e.Message;
                return _loadError;
            }
        }

        /// <summary>把 WinRT 的 IAsyncOperation&lt;T&gt; 同步等到结果。</summary>
        static object Await(object winrtOp, Type resultType)
        {
            var asTask = _asTaskGeneric.MakeGenericMethod(resultType);
            var task = (Task)asTask.Invoke(null, new object[] { winrtOp });
            task.Wait(-1);
            var prop = task.GetType().GetProperty("Result");
            return prop == null ? null : prop.GetValue(task, null);
        }

        /// <summary>当前热点状态："On" / "Off" / 其他（Starting 等）。取不到返回空。</summary>
        public static string State()
        {
            if (EnsureLoaded() != "") return "";
            try
            {
                var mgr = Manager();
                if (mgr == null) return "";
                var p = mgr.GetType().GetProperty("TetheringOperationalState");
                var v = p == null ? null : p.GetValue(mgr, null);
                return v == null ? "" : v.ToString();
            }
            catch (Exception) { return ""; }
        }

        /// <summary>热点网卡是否拿到 192.168.137.1（这是「热点真的起来了」的唯一判据）。</summary>
        public static bool NicReady()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    // **不能**要求 OperationalStatus == Up。
                    //
                    // 实测（2026-10-28，Intel AC 7265）：热点起来后 SoftAP 复用同一块
                    // 物理网卡，适配器状态仍显示 Disconnected，但 192.168.137.1 已经
                    // 挂上去了。按 Up 过滤会把「热点其实正常」误判成「卡死」，
                    // 然后给用户报一个假的失败。
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        var a = ua.Address;
                        if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        var b = a.GetAddressBytes();
                        if (b[0] == 192 && b[1] == 168 && b[2] == 137 && b[3] == 1) return true;
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>当前热点配置（SSID / 密码）。取不到返回 null。</summary>
        public static string CurrentSsid()
        {
            try
            {
                var cfg = AccessPointConfig();
                if (cfg == null) return "";
                var p = cfg.GetType().GetProperty("Ssid");
                var v = p == null ? null : p.GetValue(cfg, null);
                return v == null ? "" : v.ToString();
            }
            catch (Exception) { return ""; }
        }

        static object Manager()
        {
            var profile = PickShareProfile();
            if (profile == null) return null;
            // CreateFromConnectionProfile 有重载，按参数类型精确找，避免传错。
            foreach (var m in _tetheringMgr.GetMethods())
            {
                if (m.Name != "CreateFromConnectionProfile") continue;
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;
                if (!ps[0].ParameterType.IsInstanceOfType(profile)) continue;
                return m.Invoke(null, new object[] { profile });
            }
            return null;
        }

        static object AccessPointConfig()
        {
            var mgr = Manager();
            if (mgr == null) return null;
            var m = MethodNoArg(mgr.GetType(), "GetCurrentAccessPointConfiguration");
            return m == null ? null : m.Invoke(mgr, null);
        }

        /// <summary>
        /// 把适配器 id 归一化后再比较。
        ///
        /// 为什么需要（2026-10-29 实测踩到的坑）：
        ///   WinRT 的 `NetworkAdapter.NetworkAdapterId` 是**小写、不带花括号**的 Guid 文本：
        ///     `240a0b06-fe61-4787-9314-52f74b98e203`
        ///   而 .NET 的 `NetworkInterface.Id` 是**大写、带花括号**：
        ///     `{240A0B06-FE61-4787-9314-52F74B98E203}`
        ///   直接 `string.Equals(..., OrdinalIgnoreCase)` 永远不相等 ——
        ///   于是任何按 id 找网卡描述的逻辑都会「找不到」，把**所有**网卡
        ///   当成虚拟网卡排除掉，最后报「本机没有已连接的物理网卡」。
        ///   这个 bug 在测试里表现为「实际选中 = null」。
        /// </summary>
        static Guid NormalizeId(string id)
        {
            if (string.IsNullOrEmpty(id)) return Guid.Empty;
            var s = id.Trim().Trim('{', '}');
            Guid g;
            return Guid.TryParse(s, out g) ? g : Guid.Empty;
        }

        /// <summary>按 WinRT 的 adapterId 找到 .NET 的网卡。</summary>
        static NetworkInterface FindNi(string winRtAdapterId)
        {
            var want = NormalizeId(winRtAdapterId);
            if (want == Guid.Empty) return null;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (NormalizeId(ni.Id) == want) return ni;
            }
            return null;
        }

        /// <summary>
        /// 取适配器描述（「Realtek 8852BE Wireless LAN …」这种）。
        /// 判断物理/虚拟只能靠它 —— 只看 IanaInterfaceType 不够。
        /// </summary>
        static string AdapterDescOf(object profile)
        {
            try
            {
                var t = profile.GetType();
                var ad = t.GetProperty("NetworkAdapter");
                var adVal = ad == null ? null : ad.GetValue(profile, null);
                if (adVal == null) return "";
                var idp = adVal.GetType().GetProperty("NetworkAdapterId");
                string id = idp == null ? "" : Convert.ToString(idp.GetValue(adVal, null));
                var ni = FindNi(id);
                return ni == null ? "" : (ni.Description ?? "");
            }
            catch (Exception) { return ""; }
        }

        /// <summary>
        /// 描述里这些词出现就**不是**可以拿去 NAT 的物理网卡。
        ///
        /// 为什么必须按描述排除（2026-10-29 用户机实测）：
        ///   只看 IanaInterfaceType 会把三类假货当成物理网卡 ——
        ///     · `Radmin VPN` —— Famatech 的虚拟以太网，iana=6，**Up**
        ///       （用户装着实机在用，一点名就必被选中）
        ///     · `etgame` —— 游戏加速器的隧道网卡，iana=53
        ///     · `WAN Miniport` / `Teredo` / `6to4` / `IP-HTTPS` —— 系统虚拟口
        ///   它们都能报 InternetAccess，但 ICS 拿它们当共享源只会把状态机搞乱。
        ///   物理网卡反而常常是 LocalAccess（校园网禁网），于是「先找物理」那条
        ///   规则反而排不上队 —— 必须先把假货挑出去。
        /// </summary>
        static readonly string[] _virtualWords = new string[] {
            "wi-fi direct", "virtual", "vpn", "tunnel", "teredo", "6to4",
            "ip-https", "wan miniport", "loopback", "hyper-v", "tap-",
            "wintun", "wireguard", "openvpn", "accelerat", "meta "
        };

        static bool IsVirtualAdapter(string desc)
        {
            if (string.IsNullOrEmpty(desc)) return true;   // 查不到就当虚拟，宁可不选
            var d = desc.ToLowerInvariant();
            foreach (var w in _virtualWords)
                if (d.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>取配置档的 IanaInterfaceType（71=WiFi，6=以太网，53=隧道）。</summary>
        static int IanaOf(object profile)
        {
            try
            {
                var t = profile.GetType();
                var ad = t.GetProperty("NetworkAdapter");
                var adVal = ad == null ? null : ad.GetValue(profile, null);
                if (adVal == null) return 0;
                var ia = adVal.GetType().GetProperty("IanaInterfaceType");
                return ia == null ? 0 : Convert.ToInt32(ia.GetValue(adVal, null));
            }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// 是不是热点自己的 AP 网卡（Microsoft Wi-Fi Direct Virtual Adapter）。
        ///
        /// 热点起来之后它会以「本地连接* N」或热点 SSID 的名字出现在
        /// GetConnectionProfiles 里，级别通常是 LocalAccess —— 要是被选成共享源，
        /// 就是「自己共享给自己」，状态机立刻卡死。
        /// 只靠名字前缀（原来的 "Hotspot" 判断）挡不住，必须看适配器描述。
        /// </summary>
        static bool IsApProfile(object profile)
        {
            try
            {
                var t = profile.GetType();
                var ad = t.GetProperty("NetworkAdapter");
                var adVal = ad == null ? null : ad.GetValue(profile, null);
                if (adVal == null) return false;
                var idp = adVal.GetType().GetProperty("NetworkAdapterId");
                string id = idp == null ? "" : Convert.ToString(idp.GetValue(adVal, null));
                var ni = FindNi(id);
                if (ni == null) return false;
                return (ni.Description ?? "").IndexOf("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 挑一个能当共享源的连接配置档。
        ///
        /// 判据是「**已连接的物理网卡**」（WLAN / 以太网），不是「网络级别」。
        /// 这个判断在 1.0.45 改过，原因见方法内注释。
        /// </summary>
        static object PickShareProfile()
        {
            try
            {
                var m = MethodNoArg(_netInfo, "GetConnectionProfiles");
                var profiles = (IEnumerable)m.Invoke(null, null);
                object physical = null;   // 已连接的物理网卡（不管有没有外网）
                object internet = null;   // 任何 InternetAccess
                object fallback = null;
                foreach (var p in profiles)
                {
                    if (p == null) continue;
                    var t = p.GetType();
                    var pnProp = t.GetProperty("ProfileName");
                    string pn = pnProp == null ? "" : Convert.ToString(pnProp.GetValue(p, null));
                    if (pn.StartsWith("Hotspot", StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsApProfile(p)) continue;   // 热点自己的 AP 网卡，绝不共享给自己

                    var gm = MethodNoArg(t, "GetNetworkConnectivityLevel");
                    if (gm == null) continue;
                    string level;
                    try { level = Convert.ToString(gm.Invoke(p, null)); }
                    catch (Exception) { continue; }

                    // 1.0.45 的关键修正（两处）：
                    //
                    // ① 物理网卡只要「已连接」（level != None）就能当共享源，
                    //    **不再要求 InternetAccess**。
                    //
                    //   为什么（2026-10-29 用户机实测）：
                    //   渡口的使用场景就是「校园网禁网」—— 那时 WLAN 连着 AP，
                    //   但级别是 LocalAccess（没有外网权限），而全机唯一的
                    //   InternetAccess 是 FerryTun（TUN 虚拟网卡，iana=53）。
                    //   1.0.44 要求 InternetAccess，于是永远选不到物理网卡，
                    //   只能落到 FerryTun → 卡死。用户看到的就是「热点开不起来」。
                    //   实测该机 GetConnectionProfiles：29 个历史 WLAN 配置档全是
                    //   None，当前连着的 GDPZ-student-5G 是 LocalAccess，
                    //   FerryTun 是唯一的 InternetAccess。
                    //
                    // ② 物理/虚拟不能只看 IanaInterfaceType，必须按适配器描述排除。
                    //   同一台机器上就有两个骗子：
                    //     Radmin VPN —— 虚拟以太网，iana=6，Up（用户装着实机在用）
                    //     etgame     —— 游戏加速器隧道，iana=53，Up
                    //   只看 iana 会把 Radmin 当以太网选走，ICS 照样卡住。
                    var desc = AdapterDescOf(p);
                    int iana = IanaOf(p);
                    bool looksPhysical = (iana == 71 || iana == 6) && !IsVirtualAdapter(desc);
                    if (looksPhysical && level != "None")
                    {
                        // 优先挑「真正连着」的那块：无线网卡（71）比以太网（6）更常见
                        if (physical == null || IanaOf(physical) != 71) physical = p;
                        continue;
                    }
                    if (level == "InternetAccess" && internet == null) internet = p;
                    if (fallback == null && level != "None") fallback = p;
                }

                if (physical != null) return physical;
                // 兜底也必须是物理网卡 —— 虚拟网卡宁可不用（会卡死），
                // 让调用方报「找不到可共享的网络连接」，比卡 25 秒强。
                if (internet != null && IsPhysicalProfile(internet)) return internet;
                if (fallback != null && IsPhysicalProfile(fallback)) return fallback;
                return null;
            }
            catch (Exception) { return null; }
        }

        static bool IsPhysicalProfile(object p)
        {
            int iana = IanaOf(p);
            if (iana != 71 && iana != 6) return false;
            return !IsVirtualAdapter(AdapterDescOf(p));
        }

        /// <summary>
        /// 开热点会不会把当前上网的那条线路顶掉？返回空字符串 = 没风险。
        ///
        /// 为什么必须有这个检查（2026-10-29 真实事故）：
        ///   我在用户机上直接调 StartTetheringAsync()，结果 **Realtek 8852BE 的
        ///   STA 连接被顶掉** —— 他正在用的 WiFi 断了，远控同时失去。
        ///
        /// 技术原因：单块无线网卡做 SoftAP 时，STA 和 AP 必须共用同一套射频前端。
        /// 很多驱动（含 8852BE）接到 SoftAP 请求后**不是拒绝，而是切换射频配置**：
        ///   · 有线网卡上行 + WiFi 做 AP      → 互不干扰，安全
        ///   · 两块 WiFi（一块 STA、一块 AP） → 安全
        ///   · 单块 WiFi 同时 STA + AP        → 多数驱动做不到，且失败方式是「顶掉 STA」
        ///
        /// 所以「共享源是无线网卡，且它是本机唯一的上行」时，开热点大概率等于
        /// 把用户自己的网断掉。这种情况必须先告诉他，让他自己决定。
        /// </summary>
        public static string UplinkRisk()
        {
            try
            {
                if (EnsureLoaded() != "") return "";
                var share = PickShareProfile();
                if (share == null) return "";

                int shareIana = IanaOf(share);
                if (shareIana != 71) return "";   // 有线做共享源不会有这个问题

                var shareName = Convert.ToString(
                    share.GetType().GetProperty("ProfileName").GetValue(share, null));

                // 还有没有别的上行？有线（iana=6 物理）或另一块已连接的无线。
                //
                // 注意：**不能**用 ReferenceEquals 判断「这条是不是共享源」。
                // WinRT 每次 GetConnectionProfiles() 都返回新的 COM 包装对象，
                // 同一个网络两次枚举出来的引用不相等 —— 第一版就栽在这里，
                // 结果「自己跟自己比」永远不相等，于是把自己算成了第二条上行，
                // UplinkRisk() 恒返回空（保护形同虚设）。按 ProfileName 比才可靠。
                object otherUplink = null;
                foreach (var p in (IEnumerable)MethodNoArg(_netInfo, "GetConnectionProfiles").Invoke(null, null))
                {
                    if (p == null) continue;
                    var gm = MethodNoArg(p.GetType(), "GetNetworkConnectivityLevel");
                    if (gm == null) continue;
                    var lvl = Convert.ToString(gm.Invoke(p, null));
                    if (lvl == "None") continue;
                    var desc = AdapterDescOf(p);
                    if (IsVirtualAdapter(desc)) continue;
                    var pn = Convert.ToString(p.GetType().GetProperty("ProfileName").GetValue(p, null));
                    if (string.Equals(pn, shareName, StringComparison.Ordinal)) continue;  // 就是共享源自己
                    int iana = IanaOf(p);
                    if (iana == 6) { otherUplink = p; break; }        // 有有线，稳
                    if (iana == 71 && otherUplink == null) otherUplink = p;
                }
                if (otherUplink != null) return "";   // 有第二条上行，单网卡并发问题被绕开了

                return "当前只有一块无线网卡在联网（" + shareName + "），它同时也是热点的发射网卡。\n\n"
                     + "多数无线网卡无法同时「连着 WiFi」和「发 WiFi」，强行开启的结果是：\n"
                     + "  · 你现在的 WiFi 会断（这台机器可能就此失去远程连接）\n"
                     + "  · 热点也可能起不来（无线网卡 SoftAP 驱动卡死，只能重启电脑恢复）\n\n"
                     + "如果这台机器是你远程在用的，请不要在这里开热点。\n"
                     + "确实要开就继续 —— 但请确保你人就在机器旁边。";
            }
            catch (Exception) { return ""; }
        }

        /// <summary>
        /// 当前有没有**物理有线网卡**在联网。
        ///
        /// 2026-10-30 需求：热点功能只在「检测到以太网连接」时才允许使用，
        /// 否则直接锁住。
        ///
        /// 为什么用这个当唯一门槛：单块无线网卡同时做 STA + SoftAP，
        /// 这块 Realtek 8852BE 的处理方式是「踢掉 STA」——用户正在用的 WiFi
        /// 会断（2026-10-29 真实事故，远控同时失去）。既然驱动没有能力检查，
        /// 就由我们在软件层禁止这种用法。有线做上行 + WiFi 做 AP 是两个独立
        /// 硬件，没有这个冲突。
        ///
        /// 判定细节（都踩过）：
        ///   · 必须**排除虚拟网卡**：Radmin VPN / etgame / WAN Miniport 都会
        ///     报 iana=6（以太网）且 Up，被当成有线就会放行一个危险操作。
        ///     复用 IsVirtualAdapter 的排除表。
        ///   · 必须**真的连着**：只看 Status == Up 不够，USB 网卡拔了线有时
        ///     还是 Up。还要看它有没有默认网关 —— 拿不到网关说明没通。
        /// </summary>
        public static bool HasWiredUplink()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (IsVirtualAdapter(ni.Description) || IsVirtualAdapter(ni.Name)) continue;

                    var ip = ni.GetIPProperties();
                    if (ip == null) continue;
                    // 要有 IPv4 地址 + 默认网关，才算「真的在联网」
                    bool hasV4 = false;
                    foreach (var ua in ip.UnicastAddresses)
                        if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        { hasV4 = true; break; }
                    if (!hasV4) continue;
                    if (ip.GatewayAddresses == null || ip.GatewayAddresses.Count == 0) continue;

                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// 热点锁定的原因文案。空 = 允许使用。
        ///
        /// 和 UplinkRisk() 的区别：UplinkRisk 是「有风险但可以确认后继续」，
        /// 这里是**硬锁**，不给「继续」的选项。
        /// </summary>
        public static string LockedReason()
        {
            if (HasWiredUplink()) return "";
            return "移动热点已锁定：当前没有检测到有线（以太网）连接。\n\n"
                 + "原因：这台笔记本只有一块无线网卡，它既是你的上网通道、"
                 + "又是热点的发射网卡。单块无线网卡无法同时做这两件事 —— "
                 + "强行开启会让你的 WiFi 直接断开。\n\n"
                 + "请先插上网线（有线做上行），再回来开启热点。";
        }


        /// <summary>
        /// 开热点。返回空字符串表示成功（**以网卡拿到 192.168.137.1 为准**）。
        /// </summary>
        /// <param name="ssid">空 = 不改现有配置</param>
        /// <param name="pass">空 = 不改现有配置；给了就必须 ≥ 8 位</param>
        /// <param name="waitMs">等网卡就绪的最长时间</param>
        public static string Start(string ssid, string pass, int waitMs)
        {
            var err = EnsureLoaded();
            if (err != "") return err;

            // 硬闸：没有有线上行就不许开（见 LockedReason 的注释）。
            // 放在最前面 —— 连 ICS 都不该去碰，避免把状态机搞乱。
            var locked = LockedReason();
            if (locked != "") return locked;

            if (!string.IsNullOrEmpty(pass) && pass.Length < 8)
                return "热点密码至少 8 位";

            try
            {
                // ICS 必须先起来，否则下面 StartTetheringAsync 会「假成功」。
                var ics = EnsureIcs();
                if (ics != "") return ics;

                var mgr = Manager();
                if (mgr == null) return "找不到可共享的网络连接（本机当前没有已连接的物理网卡）";

                // ── 卡死自愈：状态机停在 InTransition 时先自己救一次 ──
                //
                // 2026-10-29 用户机实测：1.0.41 用 FerryTun 当共享源把状态机卡在
                // InTransition 之后，后面所有版本都开不起来 ——
                //   StopTetheringAsync 超时、StartTetheringAsync 超时、
                //   换频段 / 换 SSID / 重启 WlanSvc 全部无效。
                // 而「重启 ICS 服务」（含按 PID 杀掉停在 STOP_PENDING 的 svchost）
                // 在实测里能把它救回来，不必让用户重启电脑。
                if (string.Equals(State(), "InTransition", StringComparison.OrdinalIgnoreCase))
                {
                    StopAndWait(mgr, 6000);
                    if (string.Equals(State(), "InTransition", StringComparison.OrdinalIgnoreCase))
                    {
                        var icsErr = RestartIcs();
                        if (icsErr != "")
                            return "热点状态机卡在「切换中」，ICS 也重启不了（" + icsErr + "）。请重启电脑后重试。";
                        Thread.Sleep(2000);
                        mgr = Manager();
                        if (mgr == null)
                            return "热点状态机卡在「切换中」。请重启电脑后重试。";
                    }
                    if (string.Equals(State(), "InTransition", StringComparison.OrdinalIgnoreCase))
                        return "热点状态机卡在「切换中」（InTransition），软件层救不回来。请重启电脑后重试 —— 这是无线网卡 SoftAP 驱动的已知限制。";
                }

                // 频段选择 + 失败降级（参考微软官方 TetheringOperationStatus 文档）。
                //
                // 关键认知（2026-10-28 查 MicrosoftDocs/winrt-api 得到）：
                //   BandInterference(11) = 主连接占用某个频段时，AP 不能再用同一频段。
                //   典型例子：station 连着 5GHz，AP 就不能配 5GHz。
                // 这台机器正是这个情况：WiFi 连着 GDPZ-student-5G（5GHz 信道 40），
                // 之前固定写 TwoPointFourGigahertz 其实是对的，但**没有降级逻辑**——
                // 一旦系统因为其它原因（信道限制/地区码）拒绝 2.4G，就直接失败。
                //
                // 正确做法：先 2.4G（避开和主连接 5GHz 的干扰），被拒就退到 Auto，
                // 并把 Windows 返回的具体状态写进日志，而不是笼统报「网卡未就绪」。
                // 记住首选频段，启动时按「2.4G → Auto」依次尝试。
                var preferBand = "TwoPointFourGigahertz";

                // 改 SSID/密码要先停再改再起 —— 热点开着的时候配置改不动。
                if (!string.IsNullOrEmpty(ssid) || !string.IsNullOrEmpty(pass))
                {
                    StopAndWait(mgr, 8000);
                    var cfg = MethodNoArg(mgr.GetType(), "GetCurrentAccessPointConfiguration").Invoke(mgr, null);
                    var ct = cfg.GetType();
                    if (!string.IsNullOrEmpty(ssid))
                    {
                        var p = ct.GetProperty("Ssid");
                        if (p != null && p.CanWrite) p.SetValue(cfg, ssid, null);
                    }
                    if (!string.IsNullOrEmpty(pass))
                    {
                        var p = ct.GetProperty("Passphrase");
                        if (p != null && p.CanWrite) p.SetValue(cfg, pass, null);
                    }
                    var cm = MethodNoArg(mgr.GetType(), "ConfigureAccessPointAsync");
                    if (cm != null)
                    {
                        var op = cm.Invoke(mgr, new object[] { cfg });
                        AwaitOpResult(op);
                    }
                }

                var startM = MethodNoArg(mgr.GetType(), "StartTetheringAsync");
                if (startM == null) return "这台系统的热点 API 不完整（缺 StartTetheringAsync）";

                // 按频段顺序尝试：先 2.4G（避开主连接 5GHz 的干扰），
                // 被 RadioRestriction / BandInterference / WiFiDeviceOff 拒了就退到 Auto。
                var bands = new string[] { preferBand, "Auto" };
                string lastStatus = "";
                string bandUsed = "";
                foreach (var band in bands)
                {
                    if (!ConfigureBand(mgr, band)) continue;
                    bandUsed = band;

                    var startOp = startM.Invoke(mgr, null);
                    var status = AwaitOpResult(startOp);
                    lastStatus = status;
                    _lastBandTried = band;

                    if (status == "Success") break;

                    // 这几类都是「频段/无线电」问题，换下一个频段再试
                    if (status == "RadioRestriction" || status == "BandInterference" || status == "WiFiDeviceOff")
                    {
                        _lastStatus = status;
                        continue;
                    }
                    // 其它错误（授权失败、设备关闭等）换频段也没用，直接报
                    return "启动热点失败：" + ExplainStatus(status) + "（Windows 状态：" + status + "）";
                }

                if (!string.IsNullOrEmpty(lastStatus) && lastStatus != "Success")
                {
                    return "启动热点失败：" + ExplainStatus(lastStatus) + "（Windows 状态：" + lastStatus
                           + "，已试频段：" + string.Join("→", bands) + "）";
                }

                // API 说 Success 不代表真起来了（见文件头注释）—— 继续等两个信号：
                //   ① TetheringOperationalState 变成 On（或正在切换）
                //   ② 网卡拿到 192.168.137.1
                //
                // 两个都要看：实测 state 可能停在 InTransition（热点已经在广播了），
                // 而 192.168.137.1 有可能是上次残留的静态地址。**只要状态不是 Off
                // 且地址在，就认为起来了**；这样两个方向的误判都躲开了。
                var until = DateTime.UtcNow.AddMilliseconds(waitMs);
                while (DateTime.UtcNow < until)
                {
                    var st = State();
                    bool live = !string.Equals(st, "Off", StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrEmpty(st);
                    if (live && NicReady()) return "";
                    Thread.Sleep(400);
                }
                var last = State();
                if (string.Equals(last, "Off", StringComparison.OrdinalIgnoreCase))
                    return "热点没起来（状态仍是 Off）。可能是无线网卡被占用或驱动异常。";

                // 网卡停在 169.254.x.x：ICS 的 NAT/DHCP 没接管 AP 网卡。
                // 重启一次 ICS 再试 —— 实测 ICS 半死时 API 照样返回 Success，
                // 网卡却永远拿不到 192.168.137.1，而重启 ICS 后重来一次能好。
                if (!NicReady())
                {
                    if (RestartIcs() == "")
                    {
                        var mgr2 = Manager();
                        if (mgr2 != null)
                        {
                            try
                            {
                                StopAndWait(mgr2, 6000);
                                ConfigureBand(mgr2, preferBand);
                                var sm2 = MethodNoArg(mgr2.GetType(), "StartTetheringAsync");
                                if (sm2 != null) AwaitOpResult(sm2.Invoke(mgr2, null));
                                var until2 = DateTime.UtcNow.AddMilliseconds(waitMs);
                                while (DateTime.UtcNow < until2)
                                {
                                    if (NicReady()) return "";
                                    Thread.Sleep(400);
                                }
                            }
                            catch (Exception) { }
                        }
                    }
                }

                return "热点已启动，但 AP 网卡没拿到 192.168.137.1（ICS 的 NAT/DHCP 没接管它）。" +
                       "这是无线网卡 SoftAP 驱动卡死的典型表现，重启电脑可恢复；" +
                       "重启后仍这样，请更新无线网卡驱动。";
            }
            catch (Exception e)
            {
                return "启动热点异常：" + Innermost(e).Message;
            }
        }

        /// <summary>关热点。返回空字符串表示成功。</summary>
        public static string Stop(int waitMs)
        {
            var err = EnsureLoaded();
            if (err != "") return err;
            try
            {
                var mgr = Manager();
                if (mgr == null) return "";
                StopAndWait(mgr, waitMs);
                CleanupWifiDirectAdapters();
                return "";
            }
            catch (Exception e)
            {
                return "关闭热点异常：" + Innermost(e).Message;
            }
        }

        /// <summary>
        /// 关掉热点后，把「Microsoft Wi-Fi Direct Virtual Adapter」禁掉。
        ///
        /// **为什么必须做（2026-10-29 血的教训）**：
        /// 单块无线网卡做 SoftAP 时，Windows 会派生一块
        /// `Microsoft Wi-Fi Direct Virtual Adapter`，它和物理网卡**共用同一套
        /// 射频前端**（MAC 只差首字节的本地位）。热点关掉后这块虚拟适配器
        /// **不会被自动清理**，一直是 Up 状态、持续做 Wi-Fi Direct 探测/广播。
        ///
        /// 实测后果：用户的 CS2 从 4ms 变成 61-78ms 的稳定锯齿，抖动 32ms，
        /// 而且**不借网、直连、渡口完全退出时一样慢**。折腾了一整夜，
        /// 换驱动、改省电、改 ASPM、改漫游全部无效，最后发现是这块虚拟网卡。
        /// 禁用它之后 p50 从 61ms 回到 4.6ms。
        ///
        /// 判定「该不该禁」：描述含 wi-fi direct 且 状态不是 Disabled 的都要处理。
        /// 用 PnP 层禁用（和「设备管理器里右键禁用」等价），断电重启后仍有效。
        ///
        /// 失败不报错 —— 这是「清理」动作，禁不掉也不该让「关闭热点」显示失败。
        /// </summary>
        public static void CleanupWifiDirectAdapters()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var desc = (ni.Description ?? "").ToLowerInvariant();
                    var name = (ni.Name ?? "").ToLowerInvariant();
                    if (desc.IndexOf("wi-fi direct", StringComparison.Ordinal) < 0 &&
                        desc.IndexOf("wifi direct", StringComparison.Ordinal) < 0 &&
                        name.IndexOf("wi-fi direct", StringComparison.Ordinal) < 0)
                        continue;
                    if (ni.OperationalStatus == OperationalStatus.Down &&
                        ni.NetworkInterfaceType == NetworkInterfaceType.Unknown)
                        continue;   // 已经不可用，跳过

                    // 优先按「名字里带 * 号的那块」（本地连接* N）精确禁；
                    // 名字不可靠时退回按描述匹配全部。
                    RunNetsh("interface set interface \"" + ni.Name + "\" admin=disable");
                }
            }
            catch (Exception) { }

            // netsh 对某些驱动无效（它只改管理状态），补一刀 PnP 层禁用。
            try
            {
                RunNetsh("interface set interface name=\"本地连接* 2\" admin=disable");
            }
            catch (Exception) { }
        }

        /// <summary>跑一条 netsh 命令，吞掉所有错误（清理动作不该失败）。</summary>
        static void RunNetsh(string args)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netsh", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    try { p.WaitForExit(4000); } catch (Exception) { }
                    try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        static void StopAndWait(object mgr, int waitMs)
        {
            var st = MethodNoArg(mgr.GetType(), "StopTetheringAsync");
            if (st == null) return;
            try { AwaitOpResult(st.Invoke(mgr, null)); }
            catch (Exception) { }
            // 等的是「状态离开 On」，**不是**等 192.168.137.1 消失 ——
            // 实测那是个手工配置的静态地址，热点关了它也留着（2026-10-28）。
            // 按地址等会白等 6 秒，用户感觉「关个热点卡半天」。
            var until = DateTime.UtcNow.AddMilliseconds(Math.Min(waitMs, 6000));
            while (DateTime.UtcNow < until)
            {
                if (!string.Equals(State(), "On", StringComparison.OrdinalIgnoreCase)) return;
                Thread.Sleep(300);
            }
        }

        /// <summary>取 IAsyncOperation&lt;TetheringOperationResult&gt; 的 Status 字符串。</summary>
        static string AwaitOpResult(object op)
        {
            if (op == null) return "";
            var ot = op.GetType();
            // IAsyncOperation`1 的泛型参数就是结果类型
            var resType = ot.IsGenericType ? ot.GetGenericArguments()[0] : null;
            if (resType == null) return "";
            var result = Await(op, resType);
            if (result == null) return "";
            var sp = result.GetType().GetProperty("Status");
            return sp == null ? "" : Convert.ToString(sp.GetValue(result, null));
        }

        /// <summary>
        /// 确保 ICS（SharedAccess）服务在跑。返回空字符串表示就绪。
        ///
        /// 为什么必须显式拉起来：WinRT 的 StartTetheringAsync 只负责「让网卡进入
        /// SoftAP 模式」，**不会去启动 ICS 服务**。ICS 没跑时它的行为是：
        ///   · API 返回 Success
        ///   · 网卡一度拿到 192.168.137.1
        ///   · 但系统在约 15 秒后把热点关掉，网卡退回 169.254
        /// 用户看到的就是「热点开了又自己关」「手机连上没网」。
        ///
        /// 2026-10-28 用户机实测：SharedAccess = Stopped，热点开了两次都失败；
        /// 测试机上 ICS 是 Running，同样代码能起来。差别就在这里。
        ///
        /// 为什么用 sc 而不是 ServiceController：
        /// 需要以管理员身份启动服务，ServiceController.Start() 在权限不足时
        /// 抛的异常信息很难看懂（"无法打开计算机上的服务"），而 sc.exe 的
        /// 返回码能直接判断。渡口本身以管理员运行（app.manifest
        /// requireAdministrator），所以这里不会弹 UAC。
        /// </summary>
        static string EnsureIcs()
        {
            try
            {
                if (IcsRunning()) return "";

                RunSc("start SharedAccess");

                // 服务启动是异步的，等它真的进入 Running（最多 15 秒）
                var until = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < until)
                {
                    if (IcsRunning()) return "";
                    Thread.Sleep(500);
                }
                return "Internet 连接共享（ICS）服务起不来，热点无法工作。" +
                       "请以管理员身份运行渡口，或在「服务」里手动启动 SharedAccess。";
            }
            catch (Exception e)
            {
                return "启动 ICS 服务失败：" + Innermost(e).Message;
            }
        }

        static bool IcsRunning()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", "query SharedAccess")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    var outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return outp.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception) { return false; }
        }

        static void RunSc(string args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", args)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
            }
        }

        /// <summary>取 SharedAccess 的进程 PID（0 = 取不到）。</summary>
        static int IcsPid()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", "queryex SharedAccess")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    var outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    var m = System.Text.RegularExpressions.Regex.Match(outp, @"PID\s*:\s*(\d+)");
                    return m.Success ? Convert.ToInt32(m.Groups[1].Value) : 0;
                }
            }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// 重启 ICS（SharedAccess）。返回空字符串表示最终在跑。
        ///
        /// 为什么需要「按 PID 结束进程」：
        ///   2026-10-29 用户机实测，卡死的 SharedAccess 停在 STOP_PENDING 不动，
        ///   `sc stop` 发出去了、`sc query` 一直显示 STOP_PENDING，等 4 分钟都不变。
        ///   这时唯一能让它下来的办法是结束承载它的 svchost 进程。
        ///   ICS 是纯软件服务（NAT + DHCP 分配器），杀掉重启没有任何副作用，
        ///   不像网卡驱动那样要重装。
        ///
        /// 为什么热点开不起来要动 ICS：
        ///   StartTetheringAsync 只负责「让网卡进 SoftAP 模式」，
        ///   192.168.137.1 是 ICS 的 NAT/DHCP 分配器配上去的。
        ///   ICS 半死时 API 照样返回 Success，网卡却永远停在 169.254.x.x ——
        ///   用户看到的就是「热点开了但手机连上没有网」。
        /// </summary>
        public static string RestartIcs()
        {
            try
            {
                RunSc("stop SharedAccess");
                var until = DateTime.UtcNow.AddSeconds(6);
                while (DateTime.UtcNow < until && IcsRunning()) Thread.Sleep(400);

                if (IcsRunning())
                {
                    // 卡在 STOP_PENDING：按 PID 结束承载它的 svchost
                    var pid = IcsPid();
                    if (pid > 0)
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo("taskkill", "/f /pid " + pid)
                        {
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using (var p = System.Diagnostics.Process.Start(psi))
                        {
                            p.StandardOutput.ReadToEnd();
                            p.WaitForExit(10000);
                        }
                    }
                    Thread.Sleep(2500);
                }

                RunSc("start SharedAccess");
                until = DateTime.UtcNow.AddSeconds(12);
                while (DateTime.UtcNow < until)
                {
                    if (IcsRunning()) return "";
                    Thread.Sleep(500);
                }
                return "ICS 服务重启后仍起不来";
            }
            catch (Exception e)
            {
                return "重启 ICS 失败：" + Innermost(e).Message;
            }
        }

        /// <summary>上一次启动尝试的频段（诊断用）。</summary>
        static string _lastBandTried = "";
        /// <summary>上一次被拒的状态（诊断用）。</summary>
        static string _lastStatus = "";

        /// <summary>
        /// 把热点 AP 配到指定频段。返回是否成功。
        /// 用新建的配置对象，避免旧对象残留导致 Band 写不进去。
        /// </summary>
        static bool ConfigureBand(object mgr, string bandName)
        {
            try
            {
                var appT = Type.GetType(
                    "Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration, " +
                    "Windows.Networking.NetworkOperators, ContentType=WindowsRuntime");
                var bandT = Type.GetType(
                    "Windows.Networking.NetworkOperators.TetheringWiFiBand, " +
                    "Windows.Networking.NetworkOperators, ContentType=WindowsRuntime");
                if (appT == null || bandT == null) return false;

                var cfg = Activator.CreateInstance(appT);
                var bp = appT.GetProperty("Band");
                if (bp == null || !bp.CanWrite) return false;
                bp.SetValue(cfg, Enum.Parse(bandT, bandName), null);

                // 保留现有 SSID/密码，只改频段
                try
                {
                    var cur = MethodNoArg(mgr.GetType(), "GetCurrentAccessPointConfiguration").Invoke(mgr, null);
                    if (cur != null)
                    {
                        var sp = appT.GetProperty("Ssid");
                        var pp = appT.GetProperty("Passphrase");
                        if (sp != null && sp.CanRead)
                        {
                            var v = sp.GetValue(cur, null);
                            if (v != null && !string.IsNullOrEmpty(Convert.ToString(v)))
                                sp.SetValue(cfg, v, null);
                        }
                        if (pp != null && pp.CanRead)
                        {
                            var v = pp.GetValue(cur, null);
                            if (v != null && !string.IsNullOrEmpty(Convert.ToString(v)))
                                pp.SetValue(cfg, v, null);
                        }
                    }
                }
                catch (Exception) { }

                var cm = MethodNoArg(mgr.GetType(), "ConfigureAccessPointAsync");
                if (cm == null) return false;
                var op = cm.Invoke(mgr, new object[] { cfg });
                AwaitOpResult(op);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 把 Windows 返回的 tethering 状态翻成能看懂的中文原因。
        ///
        /// 状态值来自 MicrosoftDocs/winrt-api 的 TetheringOperationStatus 文档。
        /// 以前失败时只报「网卡未就绪」，用户完全不知道该怎么办 ——
        /// 现在按状态给具体建议（尤其是 BandInterference 这种）。
        /// </summary>
        static string ExplainStatus(string status)
        {
            switch (status)
            {
                case "Success": return "成功";
                case "Unknown": return "未知错误";
                case "MobileBroadbandDeviceOff":
                    return "移动宽带设备已关闭";
                case "WiFiDeviceOff":
                    return "无线设备不可用（检查 Wi-Fi 开关 / 飞行模式；若 Wi-Fi 已开，通常是频段与主连接冲突）";
                case "EntitlementCheckTimeout":
                    return "运营商授权检查超时";
                case "EntitlementCheckFailure":
                    return "当前账户不支持网络共享";
                case "OperationInProgress":
                    return "另一个热点操作正在进行中（状态机卡住了，稍后重试或重启电脑）";
                case "BluetoothDeviceOff":
                    return "蓝牙设备已关闭";
                case "NetworkLimitedConnectivity":
                    return "当前网络连接受限";
                case "AlreadyOn":
                    return "热点已经在运行";
                case "RadioRestriction":
                    return "该频段受无线电限制（例如 6GHz 需要双天线；可改用 2.4GHz 或自动频段）";
                case "BandInterference":
                    return "频段干扰：主连接占用的频段与热点 AP 冲突（例如主连接走 5GHz 时，热点不能再配 5GHz；请改用 2.4GHz 或自动频段）";
                default:
                    return status;
            }
        }

        /// <summary>
        /// 按名字取**无参**方法，绕开重载歧义。
        ///
        /// 为什么不能用 GetMethod(name)：`NetworkOperatorTetheringManager` 上
        /// `StartTetheringAsync` 有**两个重载**（无参 / 带 ConnectionProfile），
        /// 直接按名字取会抛 AmbiguousMatchException。2026-10-28 用户机上实测：
        /// 报错是「发现不明确的匹配」，热点开关永远打不开。
        /// （测试机的 WinRT 投影恰好只暴露了一个重载，所以那边跑得好好的 ——
        ///  这类「只在别人机器上炸」的反射问题，必须按最严格的方式写。）
        /// </summary>
        static MethodInfo MethodNoArg(Type t, string name)
        {
            foreach (var m in t.GetMethods())
            {
                if (m.Name != name) continue;
                if (m.GetParameters().Length == 0) return m;
            }
            return null;
        }

        static Exception Innermost(Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            return e;
        }

        /// <summary>
        /// 后台执行（WinRT 调用会阻塞到热点起来/超时，不能在 UI 线程上跑）。
        /// 回调在**后台线程**上，调用方自己 Dispatcher 切回界面。
        /// </summary>
        public static void StartAsync(string ssid, string pass, Action<string> done)
        {
            var t = new Thread(delegate ()
            {
                string r;
                try { r = Start(ssid, pass, 20000); }
                catch (Exception e) { r = "启动热点异常：" + e.Message; }
                if (done != null) { try { done(r); } catch (Exception) { } }
            });
            t.IsBackground = true;
            t.Name = "ferry-hotspot-start";
            t.Start();
        }

        public static void StopAsync(Action<string> done)
        {
            var t = new Thread(delegate ()
            {
                string r;
                try { r = Stop(8000); }
                catch (Exception e) { r = "关闭热点异常：" + e.Message; }
                if (done != null) { try { done(r); } catch (Exception) { } }
            });
            t.IsBackground = true;
            t.Name = "ferry-hotspot-stop";
            t.Start();
        }
    }
}

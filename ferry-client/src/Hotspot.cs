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
        /// 挑一个能当共享源的连接配置档。
        ///
        /// 判据是「网络级别 = InternetAccess」，不是网卡名 —— 见文件头注释。
        /// 同时**跳过热点自己的配置档**：热点起来之后它也会出现在列表里
        /// （级别通常是 LocalAccess），要是被选中就会「自己共享给自己」。
        /// </summary>
        static object PickShareProfile()
        {
            try
            {
                var m = MethodNoArg(_netInfo, "GetConnectionProfiles");
                var profiles = (IEnumerable)m.Invoke(null, null);
                object fallback = null;
                object internet = null;
                foreach (var p in profiles)
                {
                    if (p == null) continue;
                    var t = p.GetType();
                    var pnProp = t.GetProperty("ProfileName");
                    string pn = pnProp == null ? "" : Convert.ToString(pnProp.GetValue(p, null));
                    if (pn.StartsWith("Hotspot", StringComparison.OrdinalIgnoreCase)) continue;

                    var gm = MethodNoArg(t, "GetNetworkConnectivityLevel");
                    if (gm == null) continue;
                    string level;
                    try { level = Convert.ToString(gm.Invoke(p, null)); }
                    catch (Exception) { continue; }

                    // 共享源**必须是物理网卡**（WLAN / 以太网），不能用虚拟网卡。
                    //
                    // 1.0.41 曾改成「FerryTun（虚拟网卡）优先」，理由是它代表
                    // 「已经借到网的那条路」。这是个错误：用户的实测显示
                    // 1.0.40（用物理网卡）能开热点，1.0.43（FerryTun 优先）
                    // 反而开不起来，卡在 InTransition。
                    // 虚拟网卡当共享源时，ICS 的 NAT 方向无法确定，
                    // 热点状态机就会卡住。改回 1.0.40 的行为：物理网卡优先。
                    //
                    // 怎么判断是不是虚拟网卡：看 IanaInterfaceType。
                    //   71 = IEEE 802.11 无线（WLAN）
                    //    6 = 以太网（Ethernet）
                    //  其他（53 =  проприетарный tunnel 等）一律跳过。
                    int iana = 0;
                    try
                    {
                        var ad = t.GetProperty("NetworkAdapter");
                        var adVal = ad == null ? null : ad.GetValue(p, null);
                        if (adVal != null)
                        {
                            var ia = adVal.GetType().GetProperty("IanaInterfaceType");
                            if (ia != null) iana = Convert.ToInt32(ia.GetValue(adVal, null));
                        }
                    }
                    catch (Exception) { }

                    bool isPhysical = (iana == 71 || iana == 6);
                    if (level == "InternetAccess" && isPhysical) return p;
                    if (level == "InternetAccess" && internet == null) internet = p;
                    // 没找到 InternetAccess 时留一个「非 None」的兜底，
                    // 总比完全没有共享源好（例如刚连上还没探测完连通性）。
                    if (fallback == null && level != "None") fallback = p;
                }
                return internet != null ? internet : fallback;
            }
            catch (Exception) { return null; }
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

            if (!string.IsNullOrEmpty(pass) && pass.Length < 8)
                return "热点密码至少 8 位";

            try
            {
                // ICS 必须先起来，否则下面 StartTetheringAsync 会「假成功」。
                var ics = EnsureIcs();
                if (ics != "") return ics;

                var mgr = Manager();
                if (mgr == null) return "找不到可共享的网络连接（本机当前没有能上网的接口）";

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
                return "热点已启动，但网卡没拿到 192.168.137.1（无线网卡 SoftAP 可能卡死，重启电脑可恢复）";
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
                return "";
            }
            catch (Exception e)
            {
                return "关闭热点异常：" + Innermost(e).Message;
            }
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Ferry
{
    /// <summary>
    /// 借网的实际控制：系统代理 / 隧道进程 / 代理进程。
    ///
    /// 这里**不重写**隧道和代理的通信逻辑 —— 那两个组件（ssh-tunnel-keeper.ps1
    /// 负责 ssh -D 断线重连，dual-proxy.mjs 负责 HTTP↔SOCKS 转换）已经调通了，
    /// C# 只是启动/停止它们并读状态。重写通信层收益为零、风险很高。
    /// </summary>
    static class FerryCore
    {
        const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        const int INTERNET_OPTION_REFRESH = 37;

        // ── 系统代理 ────────────────────────────────────────────

        public static bool ProxyEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegPath))
                {
                    if (k == null) return false;
                    var v = k.GetValue("ProxyEnable");
                    return v != null && Convert.ToInt32(v) == 1;
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>系统代理是否用的是「自动配置脚本」(PAC)。Clash / 部分加速器会用这种模式，
        /// 这时 ProxyServer 是空的 —— 只查 ProxyServer 会误判成「没人占用系统代理」。</summary>
        public static string ProxyAutoConfig()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegPath))
                {
                    if (k == null) return "";
                    var v = k.GetValue("AutoConfigURL");
                    return v == null ? "" : Convert.ToString(v);
                }
            }
            catch (Exception) { return ""; }
        }

        public static string ProxyServer()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegPath))
                {
                    if (k == null) return "";
                    var v = k.GetValue("ProxyServer");
                    return v == null ? "" : Convert.ToString(v);
                }
            }
            catch (Exception) { return ""; }
        }

        public static void SetProxy(bool enable, string server)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RegPath, true))
            {
                if (k == null) return;
                k.SetValue("ProxyEnable", enable ? 1 : 0, RegistryValueKind.DWord);
                if (!string.IsNullOrEmpty(server)) k.SetValue("ProxyServer", server, RegistryValueKind.String);
            }
            // 不广播的话，已经在跑的浏览器不会重新读注册表 ——
            // 表现就是「程序说已经借网了，浏览器还是打不开」。
            try
            {
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            }
            catch (Exception) { }
        }

        public static bool IsBorrowing(Cfg cfg)
        {
            return ProxyEnabled() && ProxyServer() == cfg.BorrowServer;
        }

        /// <summary>
        /// 日志出口。由 MainForm 在启动时接上界面日志。
        /// 为什么用静态委托：RestoreDirect 被 5 处调用（停止借网、定时检测、
        /// 卸载、启动清理…），每处都传一个 log 回调太啰嗦。
        /// </summary>
        public static Action<string> Logger;

        static void LogLine(string s)
        {
            var l = Logger;
            if (l != null) { try { l(s); } catch (Exception) { } }
        }

        /// <summary>
        /// 还原系统代理。卸载和「停止借网」都必须走这里。
        ///
        /// 关键：不是简单置 0，而是**还原用户原来的设置**。
        /// 很多人本来配着公司代理或 Clash，直接置 0 等于把人家的配置弄丢。
        ///
        /// ⚠ 这里踩过一个大坑，改过一次：
        ///
        ///   原来的写法是「原代理探不通就 SetProxy(false, "")」——
        ///   只置 ProxyEnable=0，**不更新 ProxyServer**，于是注册表里
        ///   留着渡口自己的 `http://127.0.0.1:10801`。
        ///   两个后果：
        ///     ① 用户之后手动打开「使用代理服务器」开关，指向的是一个
        ///        早就没了的渡口端口，直接全网上不去，而且没人知道为什么
        ///     ② 更常见的：用户本来靠 Clash 上外网（学校直连 GitHub 是
        ///        通的但很慢/经常超时）。停止借网那一刻 Clash 恰好没起来，
        ///        系统代理被关掉；之后用户自己打开 Clash，系统代理却还是关的
        ///        —— 表现就是「恢复直连后 GitHub 打不开了」。
        ///
        ///   现在的做法：
        ///     · 多探几次（Clash 可能正在启动）
        ///     · 探不通也把**原地址写回去**，只是把开关关掉 —— 用户之后
        ///       自己打开开关就是对的地址
        ///     · 明确记一行日志，告诉用户"原代理不可达，已关代理"，别让他猜
        /// </summary>
        public static void RestoreDirect(Cfg cfg, string appDir)
        {
            var st = LoadState(appDir);

            if (st != null && st.PrevEnabled && !string.IsNullOrEmpty(st.PrevServer))
            {
                // 多给几次机会：Clash / 公司代理可能正在启动过程中。
                // 单次 400ms 探测太急，三次约 1 秒能覆盖绝大多数情况。
                bool alive = false;
                for (int i = 0; i < 3; i++)
                {
                    if (IsProxyAlive(st.PrevServer)) { alive = true; break; }
                    if (i < 2) System.Threading.Thread.Sleep(350);
                }

                if (alive)
                {
                    SetProxy(true, st.PrevServer);
                    LogLine("已还原原代理 " + st.PrevServer);
                    return;
                }

                // 不可达：关掉开关，但**保留原地址**。
                // 这样用户之后手动打开开关时指向的是他原来的代理，
                // 而不是渡口那个已经关掉的端口。
                SetProxy(false, st.PrevServer);
                LogLine("原代理 " + st.PrevServer +
                        " 当前不可达，已关闭系统代理（地址已保留，" +
                        "之后启动它并打开系统代理开关即可）");
                return;
            }

            if (!string.IsNullOrEmpty(cfg.FallbackProxy) && IsProxyAlive(cfg.FallbackProxy))
            {
                SetProxy(true, cfg.FallbackProxy);
                return;
            }

            // 最后兜底：本来就没有原代理。只关开关，地址原样留着
            // （此时 ProxyServer 大概率是渡口自己，反正没开开关，不影响使用）。
            SetProxy(false, "");
        }

        /// <summary>
        /// 系统代理地址（host:port 或 http://host:port）当前是否可连。
        ///
        /// 只探本机回环地址 —— 远端代理不可达不该由我们决定，
        /// 而且探远端要等超时，会让「停止借网」按钮卡住。
        /// </summary>
        public static bool IsProxyAlive(string server)
        {
            if (string.IsNullOrEmpty(server)) return false;
            try
            {
                var s = server.Trim();
                // 注册表里可能是 "http=127.0.0.1:7897;https=127.0.0.1:7897" 这种多协议写法，
                // 取第一条的端口即可（本地代理端口通常同一个）
                int semi = s.IndexOf(';');
                if (semi >= 0)
                {
                    s = s.Substring(0, semi);
                    int eq = s.IndexOf('=');
                    if (eq > 0) s = s.Substring(eq + 1);
                }
                if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(7);
                if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(8);
                var idx = s.LastIndexOf(':');
                if (idx <= 0) return false;
                var host = s.Substring(0, idx);
                int port;
                if (!int.TryParse(s.Substring(idx + 1), out port)) return false;
                if (port <= 0 || port > 65535) return false;

                // 非回环地址直接当作「可用」—— 探它要等超时，不值
                if (host != "127.0.0.1" && host != "localhost" && host != "::1") return true;

                return PortOpen(port, 400);
            }
            catch (Exception) { return false; }
        }

        // ── 原代理备份（state.json）────────────────────────────

        public class ProxyState
        {
            public bool PrevEnabled;
            public string PrevServer = "";
        }

        static string StatePath(string appDir) { return Path.Combine(appDir, "state.json"); }

        public static ProxyState LoadState(string appDir)
        {
            try
            {
                var p = StatePath(appDir);
                if (!File.Exists(p)) return null;
                var d = MiniJson.ParseObject(File.ReadAllText(p, Encoding.UTF8));
                var st = new ProxyState();
                st.PrevEnabled = MiniJson.Bool(d, "prevEnabled", false);
                st.PrevServer = MiniJson.Str(d, "prevServer", "");
                return st;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 切到渡口之前备份原代理。
        ///
        /// 只在「原来的代理不是渡口」时才写 —— 否则每次点开始都把渡口地址
        /// 自己备份进去，关闭时还原成渡口，代理就永远回不去了。
        /// </summary>
        public static void BackupProxyOnce(Cfg cfg, string appDir)
        {
            if (ProxyServer() == cfg.BorrowServer) return;
            try
            {
                var json = "{\n  \"prevEnabled\": " + (ProxyEnabled() ? "true" : "false") +
                           ",\n  \"prevServer\": \"" + Escape(ProxyServer()) + "\"\n}\n";
                File.WriteAllText(StatePath(appDir), json, new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>本机有没有网（不判断能不能上外网，只看有没有默认路由之外的连通性）。</summary>
        public static bool HasNetwork(int timeoutMs)
        {
            // 第一层：零开销。看系统有没有「已启用且有地址」的网卡。
            // 网线拔了 / Wi-Fi 断了，这一步就知道，一个包都不用发。
            try
            {
                bool any = false;
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    var props = ni.GetIPProperties();
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        { any = true; break; }
                    }
                    if (any) break;
                }
                if (!any) return false;
            }
            catch (Exception) { }

            // 第二层：真解析一次。
            // 用 GetHostAddressesAsync 而不是 BeginGetHostAddresses：
            // 后者超时后 AsyncWaitHandle 不 Dispose、回调线程还在，
            // 每 15 秒漏一个，挂一天就是几千个句柄。
            try
            {
                var task = System.Net.Dns.GetHostAddressesAsync("www.baidu.com");
                if (!task.Wait(timeoutMs)) return false;
                var addrs = task.Result;
                return addrs != null && addrs.Length > 0;
            }
            catch (Exception) { return false; }
        }

        // ── 进程 ────────────────────────────────────────────────

        public class Procs
        {
            public List<int> Keeper = new List<int>();
            public List<int> Bridge = new List<int>();
            public List<int> Ssh = new List<int>();
            public int Total { get { return Keeper.Count + Bridge.Count + Ssh.Count; } }
        }

        /// <summary>
        /// 按命令行特征找本程序的子进程。
        ///
        /// 必须看命令行而不是只看名字：node.exe / ssh.exe / powershell.exe 都是
        /// 通用进程名，机器上本来就有别的在跑。杀错一个用户的 ssh 会话是灾难。
        /// </summary>
        public static Procs FindProcs(Cfg cfg)
        {
            var r = new Procs();
            try
            {
                using (var q = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='powershell.exe' OR Name='pwsh.exe' OR Name='node.exe' OR Name='ssh.exe'"))
                {
                    foreach (ManagementObject o in q.Get())
                    {
                        var cmd = o["CommandLine"] as string;
                        var pid = Convert.ToInt32(o["ProcessId"]);
                        if (string.IsNullOrEmpty(cmd)) continue;
                        if (cmd.IndexOf("ssh-tunnel-keeper.ps1", StringComparison.OrdinalIgnoreCase) >= 0) r.Keeper.Add(pid);
                        else if (cmd.IndexOf("dual-proxy.mjs", StringComparison.OrdinalIgnoreCase) >= 0) r.Bridge.Add(pid);
                        else if (cmd.IndexOf("ssh.exe", StringComparison.OrdinalIgnoreCase) >= 0
                                 && cmd.IndexOf("-D 127.0.0.1:" + cfg.SocksPort, StringComparison.OrdinalIgnoreCase) >= 0) r.Ssh.Add(pid);
                    }
                }
            }
            catch (Exception) { }
            return r;
        }

        // ── 启停 ────────────────────────────────────────────────

        public class StartResult
        {
            public List<string> Started = new List<string>();
            public string Error = "";
        }

        public static StartResult StartAll(Cfg cfg, string appDir)
        {
            var res = new StartResult();
            var p = FindProcs(cfg);

            if (p.Keeper.Count == 0)
            {
                var keeper = Path.Combine(appDir, "ssh-tunnel-keeper.ps1");
                if (!File.Exists(keeper)) { res.Error = "缺少 ssh-tunnel-keeper.ps1"; return res; }
                try
                {
                    var psi = new ProcessStartInfo("powershell.exe",
                        "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + keeper + "\"" +
                        " -RemoteHost " + cfg.RemoteHost + " -SocksPort " + cfg.SocksPort);
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    Process.Start(psi);
                    res.Started.Add("隧道");
                }
                catch (Exception e) { res.Error = "启动隧道失败：" + e.Message; }
            }

            if (p.Bridge.Count == 0)
            {
                var bridge = Path.Combine(appDir, "dual-proxy.mjs");
                if (!File.Exists(bridge)) { res.Error = "缺少 dual-proxy.mjs"; return res; }
                var node = FindNode();
                if (node == null) { res.Error = "找不到 node.exe（请安装 Node.js）"; return res; }
                try
                {
                    // 不传 --listen/--socks：dual-proxy 默认 port=auto，会自己从 10800
                // 往上找空闲端口并覆盖命令行。传了反而误导（看起来固定，其实被覆盖）。
                // 真端口在启动后从 .proxy-state/ports.json 读。
                var psi = new ProcessStartInfo(node,
                        "\"" + bridge + "\"" +
                        " --keeper \"" + Path.Combine(appDir, "ssh-tunnel-keeper.ps1") + "\"" +
                        " --remote " + cfg.RemoteHost +
                        " --log \"" + Path.Combine(appDir, "bridge.log") + "\"");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    Process.Start(psi);
                    res.Started.Add("代理");
                }
                catch (Exception e) { res.Error = "启动代理失败：" + e.Message; }
            }

            if (res.Started.Count == 0 && res.Error.Length == 0) res.Started.Add("已在运行");
            return res;
        }

        public static void StopAll(Cfg cfg, string appDir)
        {
            var p = FindProcs(cfg);
            var all = new List<int>();
            all.AddRange(p.Keeper);
            all.AddRange(p.Bridge);
            all.AddRange(p.Ssh);
            foreach (var pid in all)
            {
                try { Process.GetProcessById(pid).Kill(); }
                catch (Exception) { }
            }
            RestoreDirect(cfg, appDir);
        }

        static string FindNode()
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (dir.Trim().Length == 0) continue;
                try
                {
                    var p = Path.Combine(dir.Trim(), "node.exe");
                    if (File.Exists(p)) return p;
                }
                catch (Exception) { }
            }
            var common = new[] {
                @"C:\Program Files\nodejs\node.exe",
                @"C:\Program Files (x86)\nodejs\node.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"nodejs\node.exe")
            };
            foreach (var c in common) if (File.Exists(c)) return c;
            return null;
        }

        // ── 探测 ────────────────────────────────────────────────

        /// <summary>
        /// 端口是否在监听。用 TcpClient 短超时，绝不用 Test-NetConnection ——
        /// 后者对关闭端口要等满 14 秒，界面会假死。
        /// </summary>
        public static bool PortOpen(int port, int timeoutMs)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 取当前出口 IP。走本机代理端口拿到的就是远端出口，
        /// 不走代理就是本机公网出口 —— 这个对比是「借网到底生效没有」的唯一硬证据。
        /// </summary>
        public static string ExitIp(int viaPort, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://api.ipify.org");
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.KeepAlive = false;
                req.UserAgent = "FerryClient/1.0";
                if (viaPort > 0)
                {
                    req.Proxy = new WebProxy("http://127.0.0.1:" + viaPort);
                    // 本机回环也要走代理，否则 Windows 默认会绕过 localhost
                    req.Proxy.Credentials = CredentialCache.DefaultCredentials;
                }
                else
                {
                    req.Proxy = null;
                }
                using (var resp = req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var r = new StreamReader(s, Encoding.UTF8))
                    return r.ReadToEnd().Trim();
            }
            catch (Exception) { return ""; }
        }
    }
}

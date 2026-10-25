using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Ferry
{
    /// <summary>
    /// TUN 模式：在网络层接管全部流量，让不认系统代理的程序（多数游戏客户端、
    /// 部分桌面软件）也走渡口。
    ///
    /// 为什么不自己写 TUN：
    ///   TUN 需要一个完整的用户态 TCP/IP 栈（处理 TCP 三次握手、拥塞控制、
    ///   重传、分片…）。自己写是几千行的活，而且极难做对。
    ///   mihomo 自带 gvisor 栈，已经成熟，直接借用。
    ///
    /// 关键设计：**TUN 只是加一层，不替换任何现有逻辑**。
    ///   mihomo 的上游指向客户端自己已经跑起来的 SOCKS 端口：
    ///     TUN（截获全部流量）→ mihomo → 127.0.0.1:客户端SOCKS → ssh → 远端
    ///   TCP 走客户端原有的 ssh 隧道（不变），UDP 走远端 Python 中继
    ///   （ssh -D 在协议层面不支持 UDP ASSOCIATE）。
    ///   这样隧道、代理、认证、限速全部沿用现有实现，TUN 挂了也不影响 SOCKS 模式。
    ///
    /// 为什么必须管理员：
    ///   建虚拟网卡（Wintun）+ 改路由表都是内核级操作。
    ///   普通权限下 mihomo 会报 `configure tun interface: Access denied` 然后退出。
    ///   客户端的 app.manifest 已声明 requireAdministrator，正常启动就带权限。
    /// </summary>
    class TunMode
    {
        readonly Cfg _cfg;
        readonly string _appDir;
        readonly Action<string> _log;

        Process _proc;
        string _configPath;
        int _upstreamSocksPort;
        volatile bool _running;
        /// <summary>mihomo 日志里出现过的致命错误（建网卡失败等）。null = 没出现。</summary>
        volatile string _fatalError;

        /// <summary>
        /// 从 mihomo 的一行输出里识别致命错误。
        ///
        /// 为什么不能靠「进程还在」判断成功：
        ///   mihomo 建网卡失败时**只打一行 error 日志，进程继续活着**（它还在提
        ///   SOCKS 服务）。所以进程活着 + 网卡存在都可能是假象。
        /// </summary>
        void NoteFatal(string line)
        {
            if (_fatalError != null) return;
            if (line.IndexOf("Start TUN listening error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                int p = line.IndexOf("error:", StringComparison.OrdinalIgnoreCase);
                _fatalError = p >= 0 && p + 6 < line.Length
                    ? line.Substring(p + 6).Trim().Trim('"')
                    : line.Trim();
            }
        }

        /// <summary>
        /// 决定哪些 mihomo 日志进用户可见的日志窗口。
        ///
        /// 为什么要过滤：TUN 接管全局流量后，mihomo 每条连接失败都会打一行
        /// warning。实测启动那几秒能刷出上百行
        ///   `[TCP] dial ferry ... 198.18.0.1:xxxxx --> ...:443 error: ...`
        /// （TUN 比 SOCKS 端口先就绪，那几百毫秒内所有请求都连不上）。
        /// 这些对用户没有任何意义，却会把真正重要的「TUN 建网卡失败」
        /// 淹掉。用户看日志窗口只会觉得「一堆红字，坏了」。
        ///
        /// 保留什么：
        ///   · error / fatal 级别（真问题）
        ///   · 启动、监听、TUN 相关的关键行
        ///   · 不是 [TCP]/[UDP] 连接的其它 warning
        /// </summary>
        void LogTunLine(string line)
        {
            if (line == null || line.Length == 0) return;
            NoteFatal(line);

            // 连接级回溯一律不显示（数量太大、对用户无意义）
            bool noisy = line.IndexOf("[TCP] dial", StringComparison.OrdinalIgnoreCase) >= 0
                      || line.IndexOf("[UDP] dial", StringComparison.OrdinalIgnoreCase) >= 0
                      || line.IndexOf("connection refused", StringComparison.OrdinalIgnoreCase) >= 0;
            bool important = line.IndexOf("level=error", StringComparison.OrdinalIgnoreCase) >= 0
                          || line.IndexOf("level=fatal", StringComparison.OrdinalIgnoreCase) >= 0
                          || line.IndexOf("TUN listening", StringComparison.OrdinalIgnoreCase) >= 0
                          || line.IndexOf("Start initial configuration", StringComparison.OrdinalIgnoreCase) >= 0
                          || line.IndexOf("Initial configuration complete", StringComparison.OrdinalIgnoreCase) >= 0;

            if (important) { _log("[TUN] " + line); return; }
            if (noisy) return;   // 静默丢弃

            // 其它 warning（如「default interface changed」）保留，但压成一行短句
            _log("[TUN] " + line);
        }

        public TunMode(Cfg cfg, string appDir, Action<string> log)
        {
            _cfg = cfg;
            _appDir = appDir;
            _log = log ?? delegate { };
        }

        public bool Running { get { return _running; } }
        public string DeviceName { get { return "FerryTun"; } }

        /// <summary>mihomo 可执行文件路径。找不到返回 null。</summary>
        string FindMihomo()
        {
            // 优先用安装目录里自带的（安装包会附带），
            // 找不到再退回本机已有的 Clash Verge —— 开发环境常是这种情况。
            var bundled = Path.Combine(_appDir, "mihomo.exe");
            if (File.Exists(bundled)) return bundled;

            var cands = new List<string>();
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (dir.Trim().Length == 0) continue;
                try
                {
                    var p = Path.Combine(dir.Trim(), "mihomo.exe");
                    if (File.Exists(p)) cands.Add(p);
                }
                catch (Exception) { }
            }
            cands.Add(@"D:\Clash Verge\verge-mihomo.exe");
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Clash Verge\verge-mihomo.exe"));
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\Clash Verge\verge-mihomo.exe"));

            foreach (var c in cands) if (File.Exists(c)) return c;
            return null;
        }

        /// <summary>wintun.dll 是否就位。mihomo 靠它建网卡。</summary>
        bool HasWintun(string mihomoPath)
        {
            // 检查 mihomo 同目录和安装目录两处
            var dirs = new[] { Path.GetDirectoryName(mihomoPath), _appDir };
            foreach (var d in dirs)
            {
                if (string.IsNullOrEmpty(d)) continue;
                try { if (File.Exists(Path.Combine(d, "wintun.dll"))) return true; }
                catch (Exception) { }
            }
            return false;
        }

        /// <summary>
        /// 生成 mihomo 的 TUN 配置。
        ///
        /// 为什么每次启动都重新生成而不是预置一份：
        ///   SOCKS 端口是运行时分配的（可能因为被占用而从 10800 顺延），
        ///   预置配置里的端口一旦不对，TUN 就会把所有流量导进一个死端口 ——
        ///   表现是「开了 TUN 之后彻底断网」，比不开还糟。
        /// </summary>
        // ── Windows「已连接但无 Internet」检测 ──
        //
        // 现象：开了全局接管、实际能上网，但 Windows 网络图标还挂着「无法访问
        // Internet」，部分软件（自己的连通性检测跟的是系统状态 API）也跟着报断网。
        //
        // 为什么：系统的 NCSI（Network Connectivity Status Indicator）会去请求
        // www.msftconnecttest.com；但 NCSI 是 **NlaSvc 服务** 发的，它按物理网卡
        // 的 NLA 判定走，不一定进 TUN —— 校园网物理链路本来就没有外网，
        // 探测必然失败，于是系统判「无 Internet」（而流量其实都从 TUN 走了）。
        // 实测：经客户端代理访问同一个 URL 是 200「Microsoft Connect Test」。
        //
        // 做法：TUN 期间把主动探测关掉（EnableActiveProbing=0），停止时恢复原值。
        // 原值同时写进 ncsi.bak —— 客户端被强杀时下次启动还能恢复，不会永久关闭。
        // 副作用要说清楚：关掉之后，**任何**网络都不再显示「无 Internet」，
        // 所以客户端自己的状态行才是唯一线索。
        const string NcsiKey = @"SYSTEM\CurrentControlSet\Services\NlaSvc\Parameters\Internet";
        const string NcsiVal = "EnableActiveProbing";

        static string NcsiBakPath(string appDir)
        {
            return Path.Combine(appDir, "ncsi.bak");
        }

        public static void MuteNcsiProbing(string appDir)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(NcsiKey, true))
                {
                    if (k == null) return;
                    var cur = k.GetValue(NcsiVal);
                    // 已经是 0 说明被人改过（可能是上次没恢复）—— 不覆盖备份
                    if (cur != null && Convert.ToInt32(cur) == 0)
                    {
                        if (!File.Exists(NcsiBakPath(appDir))) return;
                    }
                    else
                    {
                        File.WriteAllText(NcsiBakPath(appDir),
                            cur == null ? "" : Convert.ToString(cur));
                    }
                    k.SetValue(NcsiVal, 0, RegistryValueKind.DWord);
                }
            }
            catch (Exception) { }
        }

        public static void RestoreNcsiProbing(string appDir)
        {
            var bak = NcsiBakPath(appDir);
            try
            {
                if (!File.Exists(bak)) return;
                var raw = File.ReadAllText(bak).Trim();
                using (var k = Registry.LocalMachine.OpenSubKey(NcsiKey, true))
                {
                    if (k != null)
                    {
                        if (raw.Length == 0) k.DeleteValue(NcsiVal, false);
                        else k.SetValue(NcsiVal, int.Parse(raw), RegistryValueKind.DWord);
                    }
                }
                File.Delete(bak);
            }
            catch (Exception) { }
        }

        string BuildConfig(int socksPort, string udpHost, int udpPort)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 渡口 TUN 配置（由客户端运行时生成，不要手改）");
            sb.AppendLine("# 上游指向客户端自己已经跑起来的 SOCKS 端口，TUN 只负责截流量。");
            sb.AppendLine();
            sb.AppendLine("mode: rule");
            sb.AppendLine("log-level: warning");
            sb.AppendLine("ipv6: false");
            sb.AppendLine("geodata-mode: false");
            sb.AppendLine("geo-auto-update: false");
            sb.AppendLine();
            // 不开 mixed-port：TUN 模式下不需要额外的 SOCKS 入口，
            // 开了反而多一个可能被占用的端口。
            sb.AppendLine("profile:");
            sb.AppendLine("  store-selected: false");
            sb.AppendLine("  store-fake-ip: false");
            sb.AppendLine();
            sb.AppendLine("tun:");
            sb.AppendLine("  enable: true");
            // gvisor 是纯用户态栈，不需要内核转发，兼容性最好。
            sb.AppendLine("  stack: gvisor");
            sb.AppendLine("  device: FerryTun");
            sb.AppendLine("  mtu: 1500");
            // auto-route 是「全局接管」的关键：自动把默认路由指到虚拟网卡。
            // mihomo 会把代理服务器自己的地址排除掉，不会造成路由回环。
            sb.AppendLine("  auto-route: true");
            sb.AppendLine("  auto-detect-interface: true");
            sb.AppendLine("  strict-route: false");
            sb.AppendLine("  dns-hijack:");
            sb.AppendLine("    - any:53");
            sb.AppendLine("  route-exclude-address:");
            sb.AppendLine("    - 127.0.0.0/8");
            sb.AppendLine("    - 169.254.0.0/16");
            sb.AppendLine();
            // TUN 截了 53 端口，系统 DNS 的响应回不来，必须让 mihomo 自己解析
            sb.AppendLine("dns:");
            sb.AppendLine("  enable: true");
            sb.AppendLine("  ipv6: false");
            sb.AppendLine("  enhanced-mode: fake-ip");
            sb.AppendLine("  fake-ip-range: 198.18.0.1/16");
            sb.AppendLine("  fake-ip-filter:");
            sb.AppendLine("    - '*.lan'");
            sb.AppendLine("    - '*.local'");
            sb.AppendLine("    - '+.msftconnecttest.com'");
            sb.AppendLine("    - '+.msftncsi.com'");
            sb.AppendLine("  nameserver:");
            sb.AppendLine("    - 223.5.5.5");
            sb.AppendLine("    - 119.29.29.29");
            sb.AppendLine("  proxy-server-nameserver:");
            sb.AppendLine("    - 223.5.5.5");
            sb.AppendLine("    - 119.29.29.29");
            sb.AppendLine();
            sb.AppendLine("proxies:");
            sb.AppendLine("  # TCP：客户端自己的 SOCKS（ssh -D，只支持 CONNECT）");
            sb.AppendLine("  - name: ferry-tcp");
            sb.AppendLine("    type: socks5");
            sb.AppendLine("    server: 127.0.0.1");
            sb.AppendLine("    port: " + socksPort.ToString());
            sb.AppendLine("    udp: false");
            sb.AppendLine();
            sb.AppendLine("  # UDP：远端 Python 中继（唯一支持 UDP ASSOCIATE 的线路）");
            sb.AppendLine("  - name: ferry-udp");
            sb.AppendLine("    type: socks5");
            sb.AppendLine("    server: " + udpHost);
            sb.AppendLine("    port: " + udpPort.ToString());
            sb.AppendLine("    udp: true");
            sb.AppendLine();
            sb.AppendLine("proxy-groups:");
            sb.AppendLine("  - name: ferry");
            sb.AppendLine("    type: select");
            sb.AppendLine("    proxies: [ferry-tcp]");
            sb.AppendLine("  - name: ferry-udp-group");
            sb.AppendLine("    type: select");
            sb.AppendLine("    proxies: [ferry-udp]");
            sb.AppendLine();
            sb.AppendLine("rules:");
            sb.AppendLine("  # 内网与回环永远直连 —— 否则局域网打印机/共享/远端 SSH 立刻失联");
            sb.AppendLine("  - IP-CIDR,127.0.0.0/8,DIRECT,no-resolve");
            sb.AppendLine("  - IP-CIDR,10.0.0.0/8,DIRECT,no-resolve");
            sb.AppendLine("  - IP-CIDR,172.16.0.0/12,DIRECT,no-resolve");
            sb.AppendLine("  - IP-CIDR,192.168.0.0/16,DIRECT,no-resolve");
            sb.AppendLine("  # UDP 单独分流：只有 ferry-udp 能承载");
            sb.AppendLine("  - NETWORK,udp,ferry-udp-group");
            sb.AppendLine("  - MATCH,ferry");
            return sb.ToString();
        }

        /// <summary>
        /// 启动 TUN。返回空字符串表示成功。
        /// </summary>
        /// <param name="socksPort">客户端当前 SOCKS 端口</param>
        /// <param name="udpHost">远端 UDP 中继地址</param>
        /// <param name="udpPort">远端 UDP 中继端口</param>
        public string Start(int socksPort, string udpHost, int udpPort)
        {
            if (_running) return "";

            if (!IsAdmin())
                return "TUN 需要管理员权限。请右键「以管理员身份运行」，或用安装包重新安装（已默认要求管理员）。";

            var mihomo = FindMihomo();
            if (mihomo == null)
                return "找不到 mihomo。安装包里应该自带；开发环境可以装 Clash Verge 后重试。";

            if (!HasWintun(mihomo))
                return "缺少 wintun.dll（mihomo 靠它创建虚拟网卡）。把它放到 mihomo 同目录或安装目录下。";

            _upstreamSocksPort = socksPort;
            _configPath = Path.Combine(_appDir, "tun-config.yaml");
            _fatalError = null;

            // 启动前先清掉残留的 FerryTun 网卡。
            //
            // 为什么必须做：客户端被强杀（任务管理器结束进程 / 蓝屏 / 注销）
            // 时 mihomo 子进程不会跟着死，网卡就留着了。下次启动 mihomo 建网卡
            // 会报 `Cannot create a file when that file already exists`，
            // 而那时网卡看着还在 —— 极容易误判成「启动成功」，实际流量全进死路由。
            // 主动清一次，代价只有几百毫秒，换来的是状态确定。
            CleanupStaleAdapter();

            try
            {
                // mihomo 要求 UTF-8 无 BOM，带 BOM 会报 yaml 解析错
                File.WriteAllText(_configPath,
                    BuildConfig(socksPort, udpHost, udpPort),
                    new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                return "写 TUN 配置失败：" + e.Message;
            }

            try
            {
                // ⚠ 必须去掉目录末尾的反斜杠。
                //
                // AppDomain.CurrentDomain.BaseDirectory 形如
                //   C:\Users\<你>\AppData\Local\Ferry\
                // 直接拼成 "-d \"C:\...\Ferry\"" 时，末尾的 `\"` 会被
                // Windows 命令行解析器当成**转义引号**（而不是「引号结束」），
                // 于是 -d 的值一路吃到后面的 -f，mihomo 收到的是：
                //   -d C:\...\Ferry" -f C:\...\tun-config.yaml
                // 报错 `open C:\...\Ferry" -f C:\...\tun-config.yaml\config.yaml`。
                // 实测就是这个错误，日志里那一长串路径是直接证据。
                var workDir = _appDir.TrimEnd('\\');
                var cfgArg = _configPath.TrimEnd('\\');

                var psi = new ProcessStartInfo(mihomo,
                    "-d \"" + workDir + "\" -f \"" + cfgArg + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                // 不设 WorkingDirectory 的话，mihomo 会在当前目录找数据文件
                psi.WorkingDirectory = workDir;

                var p = new Process();
                p.StartInfo = psi;
                p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    LogTunLine(e.Data);
                };
                p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    LogTunLine(e.Data);
                };
                p.Start();
                // 同 Tunnel：把 mihomo 也放进 Job。不这样的话客户端被强杀会留下
                // 残留的 mihomo + FerryTun 虚拟网卡，下次启动直接撞名
                // （Cannot create a file when that file already exists）。
                JobKeeper.Add(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                _proc = p;
                // 记下 PID —— 万一客户端被强杀，下次启动能靠它精确回收这个子进程。
                SavePidFile(p.Id);
            }
            catch (Exception e)
            {
                return "启动 mihomo 失败：" + e.Message;
            }

            // 等网卡真的建出来。
            //
            // ⚠ 只判「网卡存在」是不够的 —— 踩过一个很危险的坑：
            //   客户端被强杀后，mihomo 子进程会残留（没人杀它），虚拟网卡也就
            //   一直留着。下次启动时 mihomo 建网卡报
            //     Start TUN listening error: configure tun interface:
            //     Cannot create a file when that file already exists.
            //   但 AdapterExists 仍然返回 true（那是**上一次**的网卡），
            //   于是界面显示「全局接管已开启」，实际所有流量都进了死路由。
            //   用户看到的是「借网了但完全上不了网」，且毫无线索。
            //
            // 所以改成三条件同时满足：进程活着 + 网卡存在 + 没出现过 TUN 失败日志。
            for (int i = 0; i < 30; i++)
            {
                System.Threading.Thread.Sleep(400);
                if (_proc == null || _proc.HasExited)
                {
                    var code = _proc == null ? -1 : _proc.ExitCode;
                    Stop();
                    return "mihomo 启动后立即退出（exit " + code +
                           "）。常见原因：wintun.dll 版本不匹配、被杀软拦截、端口被占用。详见日志。";
                }
                if (_fatalError != null)
                {
                    Stop();
                    return "TUN 建网卡失败：" + _fatalError +
                           "。多半是上次残留的 FerryTun 网卡没清掉，重启一次电脑或手动删除网卡即可。";
                }
                if (AdapterExists("FerryTun"))
                {
                    _running = true;
                    MuteNcsiProbing(_appDir);
                    return "";
                }
            }

            Stop();
            return "TUN 网卡没能在 12 秒内创建。详见日志里的 [TUN] 行。";
        }

        public void Stop()
        {
            RestoreNcsiProbing(_appDir);
            _running = false;
            var p = _proc;
            _proc = null;
            if (p == null) return;
            try
            {
                if (!p.HasExited)
                {
                    p.Kill();
                    // 等它真的退出，否则网卡可能残留
                    if (!p.WaitForExit(3000))
                    {
                        _log("[TUN] 进程未能及时退出，尝试清理残留网卡");
                        CleanupStaleAdapter();
                    }
                }
            }
            catch (Exception) { }
            try { p.Dispose(); } catch (Exception) { }
            // 正常退出后 mihomo 会自己回收网卡，但 PID 文件要清掉，
            // 否则下次启动会去「杀一个已经不存在的 PID」（无害但日志噪音）。
            RemovePidFile();
            System.Threading.Thread.Sleep(600);
        }

        /// <summary>
        /// 清掉残留的 FerryTun 网卡。
        ///
        /// 残留从哪来：客户端被强杀（任务管理器结束进程 / 注销 / 崩溃）时，
        /// mihomo 子进程不会跟着死，它持有的虚拟网卡就一直占着 FerryTun 这个名字。
        /// 下次启动 mihomo 建网卡会报
        ///   Cannot create a file when that file already exists
        /// 而界面那时看着「网卡在」，极易误判成启动成功。
        ///
        /// 正确清理顺序：
        ///   ① 先杀残留的 mihomo —— 它退出时会自己回收网卡（实测有效）
        ///   ② 网卡还在，才用 Remove-NetAdapter 强删
        ///
        /// 为什么不用 `netsh interface set interface admin=disable`：
        ///   那只把网卡禁用，名字仍然被占用。实测禁用后再启动 mihomo
        ///   照样报 "file already exists"，等于没清。必须真删。
        /// </summary>
        void CleanupStaleAdapter()
        {
            // 先清理可能残留的 mihomo 子进程。
            // 用 PID 文件做凭据 —— 提权进程读不到 CommandLine，不能靠命令行特征匹配。
            KillStaleMihomo();

            if (!AdapterPresent("FerryTun")) { RemovePidFile(); return; }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                _log("[TUN] 发现残留网卡 FerryTun（尝试 " + (attempt + 1) + "），正在清理");
                RunHidden("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command " +
                    "\"Remove-NetAdapter -Name 'FerryTun' -Confirm:$false -ErrorAction SilentlyContinue\"", 20000);
                System.Threading.Thread.Sleep(1200);
                if (!AdapterPresent("FerryTun"))
                {
                    _log("[TUN] 残留网卡已清理");
                    RemovePidFile();
                    return;
                }
                // 网卡还在：可能是 mihomo 又把它建回来了，再杀一次
                KillStaleMihomo();
                System.Threading.Thread.Sleep(800);
            }

            _log("[TUN] 残留网卡清不掉（可能被别的进程占用），TUN 会启动失败；重启电脑可彻底恢复");
        }

        string PidFilePath { get { return Path.Combine(_appDir, "tun-mihomo.pid"); } }

        void SavePidFile(int pid)
        {
            try { File.WriteAllText(PidFilePath, pid.ToString(), new UTF8Encoding(false)); }
            catch (Exception) { }
        }

        void RemovePidFile()
        {
            try { if (File.Exists(PidFilePath)) File.Delete(PidFilePath); }
            catch (Exception) { }
        }

        /// <summary>按 PID 文件杀掉上次残留的 mihomo（只在它确实是 mihomo 时才杀）。</summary>
        void KillStaleMihomo()
        {
            try
            {
                if (!File.Exists(PidFilePath)) return;
                var txt = File.ReadAllText(PidFilePath).Trim();
                int pid;
                if (!int.TryParse(txt, out pid) || pid <= 0) { RemovePidFile(); return; }

                var p = Process.GetProcessById(pid);   // 不存在会抛，直接跳过
                if (p == null || p.HasExited) { RemovePidFile(); return; }

                // 校验：必须真的是 mihomo，避免 PID 复用后误杀无关进程
                var name = "";
                try { name = p.ProcessName; } catch (Exception) { }
                if (name.IndexOf("mihomo", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    _log("[TUN] PID " + pid + " 现在不是 mihomo（" + name + "），不杀");
                    RemovePidFile();
                    return;
                }

                _log("[TUN] 杀掉上次残留的 mihomo（PID " + pid + "）");
                try { p.Kill(); p.WaitForExit(4000); } catch (Exception) { }
                System.Threading.Thread.Sleep(1000);
            }
            catch (Exception) { /* 进程不存在，正常 */ }
            finally { RemovePidFile(); }
        }

        static void RunHidden(string file, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(timeoutMs);
                }
            }
            catch (Exception) { }
        }

        // ── 系统探测 ────────────────────────────────────────────

        public static bool IsAdmin()
        {
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var pr = new System.Security.Principal.WindowsPrincipal(id);
                return pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 虚拟网卡是否存在**且处于 Up**。
        ///
        /// 为什么必须判 Up 而不是「存在」：
        ///   踩过一个很隐蔽的坑 —— 曾经用 `netsh interface set interface admin=disable`
        ///   清残留网卡，结果那个「已禁用」状态被系统记住：mihomo 重新建同名的
        ///   网卡时，网卡直接以 Disabled 状态出现，而 mihomo 自己不会报错、
        ///   进程照常运行。此时 AdapterExists 返回 true，界面显示「全局接管已开启」，
        ///   但所有流量都进不了隧道 —— 表现为「借网了但完全上不了网」，零线索。
        ///   判 Up 之后这个问题立刻暴露。
        /// </summary>
        public static bool AdapterExists(string name)
        {
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    bool match = string.Equals(ni.Name, name, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(ni.Description, name, StringComparison.OrdinalIgnoreCase);
                    if (!match) continue;
                    // Up 才是真的能用；Disabled / NotPresent / Down 都算失败
                    if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                        return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>网卡是否以任何形式存在（含 Disabled）。清理残留时用这个判据。</summary>
        public static bool AdapterPresent(string name)
        {
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (string.Equals(ni.Name, name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(ni.Description, name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch (Exception) { }
            return false;
        }
    }
}

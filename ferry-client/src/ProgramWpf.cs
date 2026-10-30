using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;

namespace Ferry
{
    /// <summary>
    /// 程序入口（WPF 版）。
    ///
    /// 命令行参数与 WinForms 版保持一致，安装器 / 快捷方式 / 自动化脚本
    /// 都不需要改：
    ///   /start            启动即借网
    ///   /stop             静默停止借网（还原系统代理后退出）
    ///   /update           跑一遍完整更新链路并写自检日志（无 UI）
    ///   /auto-update      启动后等借网就绪，自动触发一次真实更新（测试用）
    ///   /allow-wait       新实例启动时等待旧实例释放单实例锁
    ///   /uninstall        静默卸载（给 Inno 的 [UninstallRun] 用）
    ///   /install-autostart, /remove-autostart
    /// </summary>
    static class Program
    {
        static Mutex _mutex;
        const string WndTitle = "渡口 Ferry";

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr FindWindow(string cls, string title);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr h, int cmd);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr h);

        const int SW_RESTORE = 9;

        [STAThread]
        static void Main(string[] args)
        {
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var cfgPath = Path.Combine(appDir, "config.json");

            // 异常处理要**最先**注册：以前它挂在「拿到单实例锁之后」，
            // 于是启动早期（读配置、恢复备份、WPF 初始化）出的问题
            // 一律表现为「双击没反应，日志里一个字都没有」。
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                try
                {
                    var ex = e.ExceptionObject as Exception;
                    File.AppendAllText(Path.Combine(appDir, "assistant.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [崩溃] " +
                        (ex == null ? "未知" : ex.ToString()) + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch (Exception) { }
            };
            AppDomain.CurrentDomain.ProcessExit += delegate
            {
                Trace2(appDir, "进程退出");
            };

            Trace2(appDir, "启动：参数 [" + string.Join(" ", args) + "] dir=" + appDir);

            // 兜底：当前目录没有 config.json，但标准安装目录里有 —— 用安装目录那份。
            // （用户可能从桌面直接双击了一个拷出来的 exe）
            if (!File.Exists(cfgPath))
            {
                try
                {
                    var installed = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ferry");
                    var icfg = Path.Combine(installed, "config.json");
                    if (File.Exists(icfg))
                    {
                        appDir = installed.TrimEnd('\\') + "\\";
                        cfgPath = icfg;
                    }
                }
                catch (Exception) { }
            }

            // 清理上次更新留下的中间文件。
            //
            // .old 是「换掉的那个自己」，.new 是「还没启用的新版本」——
            // 只在更新失败回滚/中断时才会留下。能启动到这个位置，说明
            // 主 exe 是好的，这两个都是垃圾，清掉免得用户以为程序装重了。
            try
            {
                var oldExe = Path.Combine(appDir, "渡口助手.exe.old");
                if (File.Exists(oldExe)) File.Delete(oldExe);
                var newExe = Path.Combine(appDir, "渡口助手.exe.new");
                if (File.Exists(newExe)) File.Delete(newExe);
            }
            catch (Exception) { }

            // .old 是「刚被替换掉的自己」，更新完立刻启动时它可能还被系统
            // 短暂锁着（实测过一次没删掉，目录里留着 133 KB 的 .old）。
            // 3 秒后再试一次，失败就算了 —— 下次启动还会清。
            try
            {
                var oldPath = Path.Combine(appDir, "渡口助手.exe.old");
                if (File.Exists(oldPath))
                {
                    new System.Threading.Timer(delegate
                    {
                        try { if (File.Exists(oldPath)) File.Delete(oldPath); }
                        catch (Exception) { }
                    }, null, 3000, System.Threading.Timeout.Infinite);
                }
            }
            catch (Exception) { }

            // ── 静默卸载 ──
            if (HasArg(args, "/uninstall") || HasArg(args, "-uninstall"))
            {
                DoSilentUninstall(cfgPath, appDir);
                return;
            }

            // ── 只做「删 HKLM 遗留卸载项」这一步（提权后由 /uninstall 拉起）──
            //
            // 为什么单独一个分支：/uninstall 是在普通权限下跑的（客户端是
            // asInvoker，[UninstallRun] 直接起它不会提权），那一步删不掉 HKLM。
            // 于是 /uninstall 末尾会用一个提权的 powershell 再起本程序，
            // 只带这个参数 —— 那时进程是管理员，reg delete 才生效。
            // 不做任何其它事，也不碰网络（用户的网络在 /uninstall 里已经恢复了）。
            if (HasArg(args, "/elevated-regclean") || HasArg(args, "-elevated-regclean"))
            {
                RunRegDelete(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\"
                    + "{8F3A2B1C-4D5E-4F60-9A7B-1C2D3E4F5A6B}_is1",
                    appDir);
                return;
            }

            // ── 激活自愈：卸载 / 重装 / 换目录之后把 token 找回来 ──
            //
            // 用户反馈过：「卸载重启，或者更新，有时候还是会丢密钥，要重新填」。
            // 根因有三条，这里补最后一条：
            //   ① 安装器的 [UninstallDelete] 删掉整个 {app} 目录 → config.json 和
            //      同目录的 .bak 一起没（这一条靠 %APPDATA% 的那份备份兜底）；
            //   ② 老版本是 admin 安装，配置落在**另一个管理员账户**的 AppData 下，
            //      换到 per-user 后读不到（已经在下面 cfgPath 的兜底里处理）；
            //   ③ 装到新目录时安装器写进来的是空 token 的新配置 → 界面「未激活」。
            //
            // 只要用户曾经保存过一次配置，%APPDATA%\Ferry\config.backup.json 里
            // 就有完整令牌；这里自动补回去，用户不需要重新要密钥。
            try
            {
                if (Cfg.RecoverFromRoamingBackup(cfgPath))
                    Trace("已从备份恢复激活信息（账号 " + Cfg.Load(cfgPath).AccountIp + "）");
            }
            catch (Exception) { }

            // ── 激活自愈（第二层）：本地三份都没有 token，就按 IP 问服务端要 ──
            //
            // 服务端 clients 表以 IP 为主键，/api/config 只允许查自己的 IP。
            // 「卸载重装、配置文件被清、换目录」这些情况下，只要这台机器在服务端
            // 还有记录，就能直接恢复激活 —— 用户不用再找管理员要密钥。
            //
            // ⚠ 只在**没有** token 时才补；已有的配置一个字都不动。
            try
            {
                if (Cfg.RecoverFromServer(cfgPath))
                    Trace2(appDir, "已按 IP 从服务端恢复激活（账号 " + Cfg.Load(cfgPath).AccountIp + "）");
            }
            catch (Exception) { }

            // ── 自启开关 ──
            if (HasArg(args, "/install-autostart") || HasArg(args, "-install-autostart"))
            {
                SettingsWindow.SetAutoStart(true);
                return;
            }
            if (HasArg(args, "/remove-autostart") || HasArg(args, "-remove-autostart"))
            {
                SettingsWindow.SetAutoStart(false);
                return;
            }

            // ── 静默停止借网 ──
            if (HasArg(args, "/stop") || HasArg(args, "-stop"))
            {
                var cfgS = Cfg.Load(cfgPath);
                try { FerryCore.RestoreDirect(cfgS, appDir); } catch (Exception) { }
                foreach (var pr in System.Diagnostics.Process.GetProcessesByName("渡口助手"))
                {
                    if (pr.Id == System.Diagnostics.Process.GetCurrentProcess().Id) continue;
                    try { pr.Kill(); pr.WaitForExit(4000); } catch (Exception) { }
                }
                return;
            }

            // ── 更新自检（无 UI）──
            if (HasArg(args, "/update") || HasArg(args, "-update"))
            {
                UpdateSelfTest.Run(cfgPath, appDir);
                return;
            }

            // ── 单实例锁 ──
            bool created;
            _mutex = new Mutex(true, @"Local\FerryClient", out created);

            if (!created && HasArg(args, "/allow-wait"))
            {
                for (int i = 0; i < 40 && !created; i++)
                {
                    Thread.Sleep(250);
                    try { _mutex.Dispose(); } catch (Exception) { }
                    _mutex = new Mutex(true, @"Local\FerryClient", out created);
                }
            }

            if (!created)
            {
                Trace2(appDir, "已有实例在运行，本次启动退出");
                var h = FindWindow(null, WndTitle);
                if (h != IntPtr.Zero)
                {
                    ShowWindow(h, SW_RESTORE);
                    SetForegroundWindow(h);
                }
                else
                {
                    MessageBox.Show("渡口已经在运行了。\n\n它可能收在右下角的托盘里，双击那个图标就能打开。",
                        "渡口", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

            Trace2(appDir, "拿到单实例锁，开始建窗口");

            // Job Object：本进程一退出就回收 ssh / mihomo
            try { JobKeeper.Ensure(System.Diagnostics.Process.GetCurrentProcess()); }
            catch (Exception) { }

            var win = new MainWindow(cfgPath, appDir);
            win.StartBorrowOnLoad = HasArg(args, "/start") || HasArg(args, "-start");
            win.AutoUpdateOnLoad = HasArg(args, "/auto-update") || HasArg(args, "-auto-update");

            // ShutdownMode=OnExplicitShutdown 时，窗口关闭不会结束 Application —— 
            // 我们要的正是「关窗收进托盘」。
            Trace2(appDir, "进入消息循环（借网=" + win.StartBorrowOnLoad + " 自动更新=" + win.AutoUpdateOnLoad + "）");
            app.Run(win);
            Trace2(appDir, "消息循环结束，正常退出");
        }

        /// <summary>启动/退出日志（比 Trace 多一个 appDir 参数，供 Main 早期使用）。</summary>
        static void Trace2(string appDir, string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(appDir, "assistant.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        static bool HasArg(string[] args, string key)
        {
            foreach (var a in args)
                if (string.Equals(a, key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>启动早期写日志（此时 MainWindow 还没建好）。</summary>
        static void Trace(string msg)
        {
            try
            {
                var p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assistant.log");
                File.AppendAllText(p,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 用 reg.exe 删掉一条 HKLM 卸载项。**调用者必须已提权。**
        ///
        /// 为什么用 reg.exe 而不是 Registry.LocalMachine.DeleteSubKeyTree：
        ///   后者无权限时抛 UnauthorizedAccessException，而这个方法在
        ///   DoSilentUninstall 里被 try/catch 包着 —— 抛了等于没做，日志里
        ///   也看不出是"没权限"还是"没有这条键"。reg.exe 的退出码能区分。
        /// </summary>
        static void RunRegDelete(string key, string appDir)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "reg.exe",
                    Arguments = "delete \"HKLM\\" + key + "\" /f",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var pr = System.Diagnostics.Process.Start(psi))
                {
                    if (pr == null) return;
                    pr.WaitForExit(5000);
                    Trace2(appDir, "reg delete 遗留卸载项 exit=" + pr.ExitCode);
                }
            }
            catch (Exception) { }
        }

        static void DoSilentUninstall(string cfgPath, string appDir)
        {
            // ① 先把网络恢复原状：还原系统代理 → 杀 mihomo → 删 FerryTun 网卡。
            //    顺序不能反 —— 先删网卡再还原代理，中间这段时间机器是断网的。
            try
            {
                var cfg = Cfg.Load(cfgPath);
                FerryCore.RestoreDirect(cfg, appDir);
                FerryCore.StopAll(cfg, appDir);
                TunMode.CleanupStale(appDir, delegate (string s) { });
            }
            catch (Exception) { }

            // ② 自启（计划任务 + 启动目录快捷方式）
            try { SettingsWindow.SetAutoStart(false); } catch (Exception) { }

            // ②-b 清掉「管理员安装时代」残留的 HKLM 卸载项。
            //
            //     为什么会有这条僵尸项（2026-10-30 用户报「卸载后什么都没变」）：
            //       0.9.5 及更早是 admin 安装 → Inno 把卸载信息写 HKLM；
            //       0.9.6 起改 per-user（lowest）→ 写 HKCU。AppId 没变，两条并存，
            //       且 UninstallString 都指向同一个 unins000.exe。
            //       用户从「应用和功能」点中旧那条（没有"(当前用户)"后缀，
            //       排序靠前，看着更像正式版）→ 跑的是新版卸载器
            //       → 它只维护 HKCU → HKLM 那条留着
            //       → 控制面板还有、快捷方式还在、程序还能打开。
            //
            //     为什么不由安装器清：
            //       安装器是 PrivilegesRequired=lowest，对 HKLM 没写权限。
            //       实测 RegDeleteKeyIncludingSubkeys 编译能过但运行时静默失败
            //       （装完 1.0.52 后 HKLM 那条 0.9.1 依然在）。
            //
            //     为什么这里要自提权：
            //       客户端 manifest 是 asInvoker，[UninstallRun] 起它也是普通权限，
            //       reg delete HKLM 一样会失败（exit 5）。所以先判断是不是管理员：
            //       不是就用 WMI + runas 起一个提权的自己，只做这一件事然后退出。
            //
            //     为什么用 WMI 而不是 Process.Start(Verb=runas)：
            //       见 Elevator.cs 的长注释 —— 本进程在带 KILL_ON_JOB_CLOSE 的
            //       Job Object 里，ShellExecute 的提权请求会静默失败并回退成
            //       普通权限。必须让 WmiPrvSE 起一个不在我们 Job 里的中转进程。
            //
            //     失败不抛：删不掉只是控制面板多一条，不能因此让卸载半途而废。
            try
            {
                const string legacy =
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\"
                    + "{8F3A2B1C-4D5E-4F60-9A7B-1C2D3E4F5A6B}_is1";

                bool stillThere;
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(legacy))
                        stillThere = (k != null);
                }
                catch (Exception) { stillThere = false; }

                if (stillThere)
                {
                    if (TunMode.IsAdmin())
                    {
                        RunRegDelete(legacy, appDir);
                    }
                    else
                    {
                        // 自提权：让 WmiPrvSE 起一个 powershell，再由它 runas 起来。
                        // --elevated-regclean 是我们的隐藏分支（见 main 的参数处理）。
                        var self = System.Reflection.Assembly.GetEntryAssembly().Location;
                        var ps =
                            "Start-Sleep -Milliseconds 600; " +
                            "Start-Process -FilePath '" + self.Replace("'", "''") + "' " +
                            "-ArgumentList '/elevated-regclean' -Verb RunAs -WindowStyle Hidden";
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + ps + "\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        var pr = System.Diagnostics.Process.Start(psi);
                        if (pr != null) pr.WaitForExit(8000);
                        Trace2(appDir, "已请求提权清理 HKLM 遗留卸载项");
                    }
                }
            }
            catch (Exception) { }

            // ③ 卸载**不删**用户的激活备份（%APPDATA%\Ferry\config.backup.json）。
            //
            //    硬规则：程序任何自动流程都不许删用户的配置文件 ——
            //    有就默认不动，没有才从服务器补。我为了「验证服务端恢复」手动删过
            //    一次用户的配置，直接把他弄断网；代价远大于「少一个 500 字节的文件」。
            //    安装目录里的 config.json 由安装器 [UninstallDelete] 管理（那是用户
            //    主动卸载的语义），漫游目录这份是「重装后自愈」的唯一保险。

            // ④ 更新/安装过程留在 %TEMP% 的中间文件
            try
            {
                var tmp = Path.GetTempPath();
                foreach (var name in new string[] {
                    "ferry-relaunch.cmd", "ferry-client-new.exe", "ferry-self-config.json" })
                {
                    try
                    {
                        var p = Path.Combine(tmp, name);
                        if (File.Exists(p)) File.Delete(p);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
    }
}

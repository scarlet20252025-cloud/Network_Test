using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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

            // 清理上次更新留下的 .old
            try
            {
                var oldExe = Path.Combine(appDir, "渡口助手.exe.old");
                if (File.Exists(oldExe)) File.Delete(oldExe);
            }
            catch (Exception) { }

            // ── 静默卸载 ──
            if (HasArg(args, "/uninstall") || HasArg(args, "-uninstall"))
            {
                DoSilentUninstall(cfgPath, appDir);
                return;
            }

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

            // 未捕获异常统一记日志（静默崩溃时用户只看到「双击没反应」）
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                try
                {
                    var ex = e.ExceptionObject as Exception;
                    File.AppendAllText(Path.Combine(appDir, "assistant.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [崩溃] " +
                        (ex == null ? "未知" : ex.ToString()) + Environment.NewLine);
                }
                catch (Exception) { }
            };

            // Job Object：本进程一退出就回收 ssh / mihomo
            try { JobKeeper.Ensure(System.Diagnostics.Process.GetCurrentProcess()); }
            catch (Exception) { }

            var win = new MainWindow(cfgPath, appDir);
            win.StartBorrowOnLoad = HasArg(args, "/start") || HasArg(args, "-start");
            win.AutoUpdateOnLoad = HasArg(args, "/auto-update") || HasArg(args, "-auto-update");

            // ShutdownMode=OnExplicitShutdown 时，窗口关闭不会结束 Application —— 
            // 我们要的正是「关窗收进托盘」。
            app.Run(win);
        }

        static bool HasArg(string[] args, string key)
        {
            foreach (var a in args)
                if (string.Equals(a, key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void DoSilentUninstall(string cfgPath, string appDir)
        {
            try
            {
                var cfg = Cfg.Load(cfgPath);
                FerryCore.RestoreDirect(cfg, appDir);
                FerryCore.StopAll(cfg, appDir);
            }
            catch (Exception) { }
            try { SettingsWindow.SetAutoStart(false); } catch (Exception) { }
        }
    }
}

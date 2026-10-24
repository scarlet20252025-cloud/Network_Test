using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Ferry
{
    static class Program
    {
        // 单实例锁。Local\ 前缀让它只在当前登录会话内互斥 ——
        // 同一个人开两个没意义，但管理员用另一个账号登录后仍能启动一份来排障。
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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var cfgPath = Path.Combine(appDir, "config.json");

            // 静默卸载：给 Inno Setup 的 [UninstallRun] 用。
            // 必须走本程序而不是让安装器直接删文件 —— 卸载器不懂得
            // 要把系统代理还原、要杀掉还活着的 ssh/node。
            if (HasArg(args, "/uninstall") || HasArg(args, "-uninstall"))
            {
                DoSilentUninstall(cfgPath, appDir);
                return;
            }

            // 静默装自启：给 Inno Setup 的 [Run] 用（勾了「开机自动启动」时）。
            // 用计划任务而不是启动文件夹快捷方式 —— 后者每次开机弹 UAC。
            if (HasArg(args, "/install-autostart") || HasArg(args, "-install-autostart"))
            {
                SettingsDialog.SetAutoStart(true);
                return;
            }
            if (HasArg(args, "/remove-autostart") || HasArg(args, "-remove-autostart"))
            {
                SettingsDialog.SetAutoStart(false);
                return;
            }

            bool created;
            _mutex = new Mutex(true, @"Local\FerryClient", out created);

            if (!created)
            {
                // 已经在跑：把那个窗口顶到最前，而不是再开一个。
                // 用户双击第二次的本意是「我要看到它」，不是「再开一份」。
                var h = FindWindow(null, WndTitle);
                if (h != IntPtr.Zero)
                {
                    ShowWindow(h, SW_RESTORE);
                    SetForegroundWindow(h);
                }
                else
                {
                    MessageBox.Show("渡口已经在运行了。\n\n它可能收在右下角的托盘里，双击那个图标就能打开。",
                        "渡口", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }

            // 兜底：任何未捕获异常都要弹出来并记日志。
            // 静默崩溃的话，用户看到的就是「双击没反应」，完全无法排查。
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { Crash(e.Exception, appDir); };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e) { Crash(e.ExceptionObject as Exception, appDir); };

            try
            {
                var form = new MainForm(cfgPath, appDir);
                form.StartBorrowOnLoad = HasArg(args, "/start") || HasArg(args, "-start");
                Application.Run(form);
            }
            finally
            {
                _mutex.ReleaseMutex();
            }
        }

        static bool HasArg(string[] args, string name)
        {
            if (args == null) return false;
            foreach (var a in args)
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 卸载清理。顺序和 DoStop 一致：先还原代理，再杀进程 ——
        /// 反了的话，代理会短暂指向一个已经死掉的端口。
        /// </summary>
        static void DoSilentUninstall(string cfgPath, string appDir)
        {
            try
            {
                var cfg = Cfg.Load(cfgPath);
                FerryCore.RestoreDirect(cfg, appDir);
                FerryCore.StopAll(cfg, appDir);

                // 清自启。两种形式都要清：
                //   ① 计划任务（新方式，免 UAC）
                //   ② 启动文件夹快捷方式（旧版本留下的，或计划任务失败时的回退）
                try { SettingsDialog.SetAutoStart(false); } catch (Exception) { }
                try
                {
                    var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                    foreach (var pat in new[] { "*渡口*", "*Ferry*" })
                        foreach (var f in Directory.GetFiles(startup, pat))
                            try { File.Delete(f); } catch (Exception) { }
                }
                catch (Exception) { }
            }
            catch (Exception) { }
        }

        static void Crash(Exception ex, string appDir)
        {
            try
            {
                var log = Path.Combine(appDir, "crash.log");
                File.AppendAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " +
                    (ex == null ? "(null)" : ex.ToString()) + Environment.NewLine);
            }
            catch (Exception) { }
            MessageBox.Show("渡口遇到错误：\n\n" + (ex == null ? "" : ex.Message) +
                "\n\n详细信息已写入安装目录的 crash.log。", "渡口", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

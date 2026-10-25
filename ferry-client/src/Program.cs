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

            // 兜底：当前目录没有 config.json，但标准安装目录里有 —— 用安装目录那份。
            //
            // 为什么需要：分发过「单个 exe」让人拷到桌面覆盖更新，用户很可能
            // 直接双击了桌面上那个 exe。那时 BaseDirectory 是桌面，配置一个都
            // 读不到，界面显示「未激活」+ 弹「配置里没有门户地址」——
            // 看起来像激活信息被更新搞丢了（实际安装目录里的 config.json 完好）。
            // 与其解释，不如让它自己找到正确的配置。
            if (!File.Exists(cfgPath))
            {
                try
                {
                    var installed = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Ferry");
                    var icfg = Path.Combine(installed, "config.json");
                    if (File.Exists(icfg))
                    {
                        appDir = installed.TrimEnd('\\') + "\\";
                        cfgPath = icfg;
                    }
                }
                catch (Exception) { }
            }

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

            // 静默停止借网：`渡口助手.exe /stop`
            //
            // 顺序很重要：
            //   ① 先还原系统代理 —— 否则杀掉客户端后代理还指着 10801，
            //      那个端口已经没了，用户会「整个网都上不去」
            //   ② 再杀正在跑的客户端 —— 它退出时 Job Object 会连带回收
            //      mihomo 和 FerryTun 网卡
            //
            // 这个参数对自动化测试和快捷方式都有用（不然只能用鼠标点）。
            if (HasArg(args, "/stop") || HasArg(args, "-stop"))
            {
                var cfgS = Cfg.Load(cfgPath);
                FerryCore.RestoreDirect(cfgS, appDir);
                foreach (var pr in System.Diagnostics.Process.GetProcessesByName("渡口助手"))
                {
                    if (pr.Id == System.Diagnostics.Process.GetCurrentProcess().Id) continue;
                    try { pr.Kill(); pr.WaitForExit(4000); } catch (Exception) { }
                }
                return;
            }

            bool created;
            _mutex = new Mutex(true, @"Local\FerryClient", out created);

            // 提权重启时带 /allow-wait：旧实例可能还没退干净（它要先 Environment.Exit），
            // 直接判定「已在运行」就会激活一个正在销毁的窗口然后自杀 —— 表现就是
            // 「点了以管理员重启，结果什么都没了」。这里给它最多 10 秒。
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
                // 初始化 Job Object：之后启动的所有子进程（ssh、mihomo）都放进去，
                // 本进程一退出（包括被强杀）Windows 自动回收它们。
                // 失败不致命 —— 只是失去自动清理，记一行日志继续。
                if (!JobKeeper.Ensure(System.Diagnostics.Process.GetCurrentProcess()))
                {
                    try
                    {
                        File.AppendAllText(Path.Combine(appDir, "assistant.log"),
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                            " [警告] Job Object 创建失败，客户端被强杀时可能残留子进程" +
                            Environment.NewLine);
                    }
                    catch (Exception) { }
                }

                // 安装器留的「想要开机自启」标记。
                //
                // 为什么用标记文件而不是 /install-autostart 传参：
                //   安装器调 exe 时它自己已经提权，而本 exe 的 manifest 也是
                //   requireAdministrator —— 从高完整性进程用 CreateProcess 启动
                //   同样要求提权的 exe 会失败，报
                //     CreateProcess 失败；错误代码 740。请求的操作需要提升。
                //   用户装的时候会看到一个报错框（但程序还能启动，因为另一条
                //   [Run] 走 ShellExecute）。改成标记文件就彻底避开了这个坑。
                //
                // 本进程此刻已经是管理员，建 RunLevel=HighestAvailable 的计划任务
                // 不会有任何问题。
                try
                {
                    var mark = Path.Combine(appDir, ".want-autostart");
                    if (File.Exists(mark))
                    {
                        File.Delete(mark);
                        SettingsDialog.SetAutoStart(true);
                    }
                }
                catch (Exception) { }

                // 上次可能因为崩溃没来得及恢复 NCSI 探测开关（TUN 期间被静音）。
                // 启动时先恢复原状：真要开 TUN，Start() 会重新静音。
                try { TunMode.RestoreNcsiProbing(appDir); } catch (Exception) { }

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

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Ferry
{
    /// <summary>
    /// `/update` 命令行自检：跑一遍完整更新链路，每一步写日志，无 UI。
    ///
    /// 为什么需要这个入口：
    ///   自动更新曾经连着失败三次，而我三次都用「手工复制 exe 后版本号变了」
    ///   当验收 —— 那不是测试。有了这个入口，说「更新能用」才有据可查：
    ///   跑一次，看 update-selftest.log，每一步都在。
    ///
    /// 它和 DoUpdate 的区别：不持有单实例锁（所以测不出抢锁竞态），
    /// 那种场景要用 `/auto-update`（等借网就绪后走真实 DoUpdate）。
    /// </summary>
    static class UpdateSelfTest
    {
        static string _logPath;

        public static void Run(string cfgPath, string appDir)
        {
            _logPath = Path.Combine(appDir, "update-selftest.log");
            try { File.Delete(_logPath); } catch (Exception) { }

            var cfg = Cfg.Load(cfgPath);
            var mine = typeof(UpdateSelfTest).Assembly.GetName().Version.ToString();
            L("=== /update 开始，当前 " + mine + " ===");

            if (string.IsNullOrEmpty(cfg.PortalUrl))
            {
                L("配置里没有门户地址，放弃");
                return;
            }

            // 1) 查版本
            var v = Api.Version(cfg.PortalUrl);
            if (!v.Ok) { L("拿不到版本信息，放弃"); return; }
            L("服务端版本 = " + v.Latest);

            if (Api.CompareVer(v.Latest, mine) <= 0)
            {
                L("已是最新（" + mine + "），无需更新");
                L("=== 结束 ===");
                return;
            }

            var exe = Process.GetCurrentProcess().MainModule.FileName;
            var old = exe + ".old";
            var tmp = Path.Combine(Path.GetTempPath(), "ferry-client-new.exe");
            var url = cfg.PortalUrl.TrimEnd('/') + "/download-client";

            try
            {
                // 2) 下载
                L("下载 " + url);
                if (File.Exists(tmp)) { try { File.Delete(tmp); } catch (Exception) { } }
                using (var wc = new System.Net.WebClient())
                {
                    wc.Proxy = null;
                    wc.DownloadFile(url, tmp);
                }
                var len = new FileInfo(tmp).Length;
                L("下载完成 " + len + " 字节");
                if (len < 50 * 1024) { L("文件太小，放弃"); return; }

                // 3) 落新文件 → 改名 → 启用
                //
                // 这个顺序必须和 MainWindow.DoUpdate 完全一致，否则「自检通过」
                // 不代表真实更新能过 —— 之前就是这里用 rename+copy（中间有窗口，
                // 目录里短暂没有 exe），而 DoUpdate 已经改成 copy-new → move。
                var staged = exe + ".new";
                if (File.Exists(old)) { try { File.Delete(old); } catch (Exception) { } }
                if (File.Exists(staged)) { try { File.Delete(staged); } catch (Exception) { } }

                L("暂存新版本 " + staged);
                File.Copy(tmp, staged, true);
                L("暂存成功 " + new FileInfo(staged).Length + " 字节");

                L("改名 " + exe + " → " + old);
                File.Move(exe, old);
                L("改名成功");

                try
                {
                    L("启用新版本");
                    File.Move(staged, exe);
                    L("启用成功 " + new FileInfo(exe).Length + " 字节");
                }
                catch (Exception)
                {
                    try { File.Move(old, exe); L("已回滚"); } catch (Exception) { }
                    throw;
                }

                try { File.Delete(tmp); } catch (Exception) { }

                // 4) 起新进程（WMI，脱离本进程的 Job）
                L("启动新进程");
                bool ok = Elevator.StartDetached("cmd.exe /c start \"\" \"" + exe + "\"");
                L("WMI 结果 = " + ok);

                L("=== 成功，退出旧进程 ===");
                Thread.Sleep(300);
                Environment.Exit(0);
            }
            catch (Exception e)
            {
                L("失败：" + e.GetType().Name + " - " + e.Message);
                try
                {
                    if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe);
                }
                catch (Exception) { }
                Environment.Exit(1);
            }
        }

        static void L(string s)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s;
            try { File.AppendAllText(_logPath, line + "\r\n", new UTF8Encoding(false)); }
            catch (Exception) { }
        }
    }
}

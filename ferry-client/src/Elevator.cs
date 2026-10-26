using System;
using System.Diagnostics;
using System.Text;
using System.Windows.Forms;

namespace Ferry
{
    /// <summary>
    /// 「按需提权」。
    ///
    /// 从 0.9.6 起客户端 manifest 是 asInvoker：装的时候不弹 UAC、纯代理模式
    /// 也不需要管理员，只有真正要用 TUN（全局接管）时才值得打扰用户。
    ///
    /// 为什么之前是 requireAdministrator：TUN 要建虚拟网卡 + 改路由表，必须提权。
    /// 但代价太大 —— 每次启动弹 UAC、安装器还得处理 740、标准用户干脆用不了。
    /// 现在把「要不要提权」交还给用户：不勾 TUN 就一直是普通进程。
    /// </summary>
    static class Elevator
    {
        public static bool IsAdmin()
        {
            return TunMode.IsAdmin();
        }

        /// <summary>
        /// 以管理员身份重启本程序，然后退出当前进程。
        ///
        /// 为什么要绕一层 powershell：
        ///   单实例互斥锁（Local\...）在新进程启动时还握在旧进程手里，
        ///   直接 Start-Process 的话新进程会判定「已经有实例」→ 激活旧窗口 → 自杀，
        ///   表现就是「点了没有任何反应」。
        ///   让 powershell 等 1.2 秒再起，旧进程那时已经退干净。
        ///   不用 cmd /c start，因为它没法带 UAC 提权。
        /// </summary>
        /// <summary>
        /// 以管理员身份重启本程序，然后退出当前进程。
        ///
        /// 为什么不能直接 Process.Start(Verb = runas)：
        ///   本进程在一个 Job Object 里（JobKeeper 建的，带
        ///   JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE 且没有 BREAKAWAY_OK）。
        ///   在 Job 上下文里，ShellExecute 的提权请求会**静默失败并回退成
        ///   普通权限启动** —— 新实例确实起来了，但 IsAdmin() 仍是 false，
        ///   报「TUN 需要管理员权限」。（实测日志：提权重启后新实例仍无权限）
        ///
        /// 所以这里改用 WMI 的 Win32_Process.Create 启动一个 powershell：
        ///   · 它的父进程是 WmiPrvSE.exe，**完全不在我们的 Job 里**
        ///   · 再由它 Start-Process -Verb RunAs，提权才真的生效
        ///   · 整个过程中转进程和 GUI 无关，加 -WindowStyle Hidden 不闪窗
        /// </summary>
        /// <summary>
        /// 用 WMI 起一个与本进程**完全无关**的进程（父是 WmiPrvSE，不在我们的 Job 里）。
        /// 返回 false 表示 WMI 不可用，调用方自己决定怎么退化。
        /// </summary>
        public static bool StartDetached(string commandLine)
        {
            try
            {
                using (var mc = new System.Management.ManagementClass("Win32_Process"))
                {
                    var p = mc.GetMethodParameters("Create");
                    p["CommandLine"] = commandLine;
                    var o = mc.InvokeMethod("Create", p, null);
                    return o != null && Convert.ToInt32(o["ReturnValue"]) == 0;
                }
            }
            catch (Exception) { return false; }
        }

        public static void RestartElevated(string extraArgs)
        {
            string exe = Application.ExecutablePath;
            string ps = "Start-Sleep -Milliseconds 700; " +
                        "Start-Process -FilePath '" + exe + "'" +
                        " -ArgumentList '" + extraArgs + "' -Verb RunAs";
            string cmdLine = "powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + ps + "\"";

            bool ok = false;
            try
            {
                using (var mc = new System.Management.ManagementClass("Win32_Process"))
                {
                    var inParams = mc.GetMethodParameters("Create");
                    inParams["CommandLine"] = cmdLine;
                    var outParams = mc.InvokeMethod("Create", inParams, null);
                    var rc = outParams != null ? Convert.ToInt32(outParams["ReturnValue"]) : -1;
                    ok = (rc == 0);
                }
            }
            catch (Exception) { ok = false; }

            if (!ok)
            {
                // WMI 被禁用（精简版系统偶见）：退回 explorer 启动一个 .cmd，
                // explorer 同样不在我们的 Job 里。
                try
                {
                    var bat = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                     "ferry-elevate.cmd");
                    System.IO.File.WriteAllText(bat,
                        "@echo off\r\ntimeout /t 1 /nobreak >nul\r\n" +
                        "powershell -NoProfile -WindowStyle Hidden -Command \"" +
                        ps.Replace("'", "''") + "\"\r\n", Encoding.Default);
                    Process.Start("explorer.exe", "\"" + bat + "\"");
                    ok = true;
                }
                catch (Exception) { }
            }

            if (!ok)
            {
                MessageBox.Show(
                    "没法自动以管理员身份重启（系统不允许创建外部进程）。\r\n\r\n" +
                    "请手动：关闭程序 → 右键「以管理员身份运行」。",
                    "渡口", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Environment.Exit(0);
        }
    }
}

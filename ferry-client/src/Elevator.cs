using System;
using System.Diagnostics;
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
        public static void RestartElevated(string extraArgs)
        {
            string exe = Application.ExecutablePath;
            var psi = new ProcessStartInfo(exe, extraArgs);
            // ⚠ UseShellExecute = true 是这里的**关键**，不是随手写的：
            //   · false 时 CreateProcess 启动的 powershell/子进程会进本进程的
            //     Job Object（JobKeeper 把 ssh / mihomo 都放进去），而我们一退出
            //     Windows 就把整个 Job 杀掉 —— 那个"延迟 1.2 秒再启动"的
            //     powershell 也被一起毙了，结果就是「点了重启，程序只是退出了」。
            //     （真实踩到：用户反馈"重启还是只是退出了没重启成功"）
            //   · true 走 ShellExecute，新进程的父是 explorer，完全独立；
            //     同时 Verb=runas 才能触发 UAC 提权。
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            try { psi.WorkingDirectory = System.IO.Path.GetDirectoryName(exe); }
            catch (Exception) { }

            try
            {
                Process.Start(psi);
            }
            catch (Exception e)
            {
                // 用户在 UAC 上点了"否"：别退出，继续用普通模式，
                // 并且把原因说清楚（不然又是一次"点了没反应"）。
                MessageBox.Show(
                    "以管理员身份重启失败（多半是 UAC 上点了「否」）：\r\n" + e.Message +
                    "\r\n\r\n可以手动右键程序 →「以管理员身份运行」。",
                    "渡口", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // 已经拉起新进程，旧的必须立刻走 —— 否则单实例锁会把它挡在门外。
            Environment.Exit(0);
        }
    }
}

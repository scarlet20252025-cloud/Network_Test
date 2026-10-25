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
        public static void RestartElevated()
        {
            string exe = Application.ExecutablePath;
            string cmd = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" +
                "Start-Sleep -Milliseconds 1200; Start-Process -FilePath '" + exe +
                "' -Verb RunAs\"";
            try
            {
                var psi = new ProcessStartInfo("powershell.exe", cmd);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch (Exception) { }
            Application.Exit();
        }
    }
}

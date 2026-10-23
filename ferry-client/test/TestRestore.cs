// 验证 RestoreDirect 的保护：原代理死了就别还原，直接关掉系统代理。
//
// 背景：用户原本用 Clash（127.0.0.1:7897）。借网期间如果把 Clash 关了，
// 停止借网时还原成 Clash 的地址 = 指向一个没人监听的端口 = 彻底上不了网。
// 这比借网失败还糟，所以还原前必须探一下。
//
// 用法：TestRestore.exe <state.json 里的 prevServer> [appDir]
using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Ferry
{
    static class TestRestore
    {
        [STAThread]
        static int Main(string[] args)
        {
            try { return Run(args); }
            catch (Exception e)
            {
                Console.WriteLine("!! 异常: " + e.GetType().FullName);
                Console.WriteLine("   Message: " + (e.Message ?? "(null)"));
                var t = e.InnerException;
                while (t != null) { Console.WriteLine("   Inner: " + t.Message); t = t.InnerException; }
                return 9;
            }
        }

        static int Run(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== IsProxyAlive 判定验证 ===");
            Console.WriteLine("（注册表写入在 DSH 沙箱里被拦，所以这里只测纯 TCP 探测）");
            Console.WriteLine();

            // 活着的本地代理
            var alive = FerryCore.IsProxyAlive("127.0.0.1:7897");
            Console.WriteLine("[1] 127.0.0.1:7897  -> " + alive + "   （期望 True：本机 Clash 在监听）");

            // 明确没人监听的端口
            var dead = FerryCore.IsProxyAlive("127.0.0.1:19997");
            Console.WriteLine("[2] 127.0.0.1:19997 -> " + dead + "  （期望 False：没人监听）");

            // 带协议前缀 + 多协议写法
            var prefixed = FerryCore.IsProxyAlive("http://127.0.0.1:7897");
            Console.WriteLine("[3] http://127.0.0.1:7897 -> " + prefixed + "  （期望 True）");

            var multi = FerryCore.IsProxyAlive("http=127.0.0.1:7897;https=127.0.0.1:7897");
            Console.WriteLine("[4] http=...;https=... -> " + multi + "  （期望 True）");

            // 非回环地址：保守当作可用（探远端要等超时）
            var remote = FerryCore.IsProxyAlive("10.0.0.1:8080");
            Console.WriteLine("[5] 10.0.0.1:8080  -> " + remote + "   （期望 True：非回环不做探测）");

            // 空值
            Console.WriteLine("[6] (空)            -> " + FerryCore.IsProxyAlive("") + "  （期望 False）");

            int fail = 0;
            if (!alive) fail++;
            if (dead) fail++;
            if (!prefixed) fail++;
            if (!multi) fail++;
            if (!remote) fail++;
            if (FerryCore.IsProxyAlive("")) fail++;

            Console.WriteLine();
            Console.WriteLine(fail == 0 ? "=== 通过 ===" : "=== 失败 ===");
            return fail;
        }
    }
}

// 验证直连探测（ProxyServer.ProbeDirect）不会把测速线程炸掉。
//
// 背景：1.2.4 及以前，直连状态点「一键测试」会崩 —— MainWindow 的按钮处理器
// 写的是 `!_rt.Running`，而直连时 _rt 是 null。修掉空引用之后，直连要走
// ProbeDirect 真测裸链路，所以这段新代码必须自证：能通就返回 true + 正数耗时，
// 不通就返回 false，**任何情况下都不许抛异常**（线程池里抛异常 = 进程退出）。
//
// 用法：powershell -ExecutionPolicy Bypass -File tools\run-verify.ps1 -Name verify-speedtest

using System;
using System.Diagnostics;
using Ferry;

class VerifySpeedTest
{
    static int _fail;

    static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + "   " + detail);
        if (!ok) _fail++;
    }

    static int Main()
    {
        Console.WriteLine("== verify-speedtest ==");
        int ms;

        // 1) 国内站：直连应当可达，且耗时必须是正数（0 说明又退回「只测握手」那种假数据）
        bool ok = ProxyServer.ProbeDirect("www.baidu.com", 80, 3000, out ms);
        Check("直连 baidu 可达", ok, "ok=" + ok + " ms=" + ms);
        Check("直连 baidu 耗时 > 0", !ok || ms > 0, "ms=" + ms);

        // 2) 境外站：能通就通，不通也必须安全返回（大陆直连打不开是产品边界）
        bool ok2 = ProxyServer.ProbeDirect("github.com", 80, 1500, out ms);
        Check("直连 github 安全返回", true, "ok=" + ok2 + " ms=" + ms);

        // 3) 不存在的域名：DNS 解析失败也必须被吃掉，不能抛
        bool ok3 = ProxyServer.ProbeDirect("no-such-host.ferry.invalid", 80, 800, out ms);
        Check("坏域名安全返回 false", !ok3, "ok=" + ok3 + " ms=" + ms);

        // 4) 本机不可达端口：连接被拒也必须被吃掉
        bool ok4 = ProxyServer.ProbeDirect("127.0.0.1", 9, 800, out ms);
        Check("不可达端口安全返回 false", !ok4, "ok=" + ok4 + " ms=" + ms);

        // 5) 超时必须生效：黑洞地址不能把线程拖满整个 timeout 之外
        var sw = Stopwatch.StartNew();
        ProxyServer.ProbeDirect("10.255.255.1", 80, 700, out ms);
        sw.Stop();
        Check("超时受控（<= 3s）", sw.ElapsedMilliseconds <= 3000, "elapsed=" + sw.ElapsedMilliseconds + "ms");

        Console.WriteLine(_fail == 0 ? "ALL OK" : (_fail + " FAILED"));
        return _fail == 0 ? 0 : 1;
    }
}

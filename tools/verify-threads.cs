using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using Ferry;

// 1.0.27：验证「每连接只占 1 个线程」这次改造。
// 起一个本地代理，跑 N 个并发 HTTP 请求，数进程线程数。
static class ThreadCheck
{
    static int fails;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static void Main()
    {
        var proxy = new ProxyServer(17991, 1080, "10.13.24.1", delegate (string s) { });
        Ok(proxy.Start(), "proxy started on 17991");

        int before = Process.GetCurrentProcess().Threads.Count;
        Console.WriteLine("   threads before: " + before);

        // 开 60 条长连接（模拟浏览器的 keep-alive），让它们挂着不关
        var clients = new System.Collections.Generic.List<System.Net.Sockets.TcpClient>();
        try
        {
            for (int i = 0; i < 60; i++)
            {
                try
                {
                    var c = new System.Net.Sockets.TcpClient();
                    c.Connect("127.0.0.1", 17991);
                    clients.Add(c);
                }
                catch { }
            }
            Thread.Sleep(2500);
            int during = Process.GetCurrentProcess().Threads.Count;
            Console.WriteLine("   threads with " + clients.Count + " idle conns: " + during);
            Console.WriteLine("   delta = " + (during - before));

            // 改造前：每个连接 3 线程（60 × 3 = 180）。
            // 改造后：每个连接 1 线程（60），所以 delta 应该明显小于 180。
            Ok(during - before < 120,
               "thread growth is well below 3 per connection (was 3, now ~1)");
            Ok(during - before > 0, "connections do consume some threads (sanity)");
        }
        finally
        {
            foreach (var c in clients) { try { c.Close(); } catch { } }
            Thread.Sleep(1500);
            int after = Process.GetCurrentProcess().Threads.Count;
            Console.WriteLine("   threads after closing: " + after);
            Ok(after <= before + 40, "threads are released after the connections close");
            try { proxy.Stop(); } catch { }
        }

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

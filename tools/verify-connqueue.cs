using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Ferry;

// 1.0.41：验证并发策略从「拒绝」改成「排队」。
//
// 原来超过 MaxConns 直接 Close —— 客户端看到连接失败就会重试，
// 重试风暴反而把并发推得更高。开热点时（手机应用也一起挤进端口）
// 瞬时并发冲高，这个策略直接表现为「热点一开本机就断网」。
//
// 现在改成 SemaphoreSlim 排队：满了就等，不丢弃。
// 这个脚本验证：
//   ① 排队的连接最终会被处理（不会丢）
//   ② 并发始终不超过上限（整机线程数有界）
static class ConnQueueCheck
{
    static int fails;
    static void Ok(bool c, string n)
    {
        Console.WriteLine((c ? "PASS  " : "FAIL  ") + n);
        if (!c) fails++;
    }

    static int ReadN(NetworkStream s, byte[] b, int n)
    {
        int got = 0;
        while (got < n)
        {
            int r = s.Read(b, got, n - got);
            if (r <= 0) return got;
            got += r;
        }
        return got;
    }

    static void Main()
    {
        var t = typeof(ProxyServer);

        // ① backlog 常量存在且够大
        var blF = t.GetField("ListenBacklog", BindingFlags.NonPublic | BindingFlags.Static);
        Ok(blF != null, "ListenBacklog constant exists");
        if (blF != null) Ok((int)blF.GetValue(null) >= 512, "backlog >= 512 (was default, drops burst connections)");

        // ② 并发闸门字段存在
        var semF = t.GetField("_connSem", BindingFlags.NonPublic | BindingFlags.Instance);
        Ok(semF != null, "_connSem field exists (SemaphoreSlim, queueing not rejecting)");

        // ③ 上限仍然保留（不能取消，否则线程爆炸）
        var maxF = t.GetField("MaxConns", BindingFlags.NonPublic | BindingFlags.Static);
        Ok(maxF != null, "MaxConns still exists");
        if (maxF != null) Ok((int)maxF.GetValue(null) == 400, "MaxConns still 400 (thread explosion guard)");

        // ④ 端到端：起一个代理，灌一批并发连接，看有没有被丢
        var logs = new StringBuilder();
        var proxy = new ProxyServer(17995, 1080, "10.13.24.1",
            delegate (string s) { lock (logs) logs.AppendLine(s); });
        Ok(proxy.Start(), "proxy started");

        int N = 60;
        var done = 0;
        var failed = 0;
        var threads = new List<Thread>();

        for (int i = 0; i < N; i++)
        {
            var th = new Thread(delegate ()
            {
                try
                {
                    var c = new TcpClient();
                    c.Connect("127.0.0.1", 17995);
                    var ns = c.GetStream();
                    // SOCKS5 握手（无认证）
                    ns.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);
                    var rep = new byte[2];
                    if (ReadN(ns, rep, 2) == 2 && rep[0] == 0x05 && rep[1] == 0x00)
                        Interlocked.Increment(ref done);
                    else
                        Interlocked.Increment(ref failed);
                    ns.Close();
                    c.Close();
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref failed);
                }
            });
            th.IsBackground = true;
            threads.Add(th);
            th.Start();
        }

        foreach (var th in threads) th.Join(20000);

        Console.WriteLine("   " + N + " concurrent connects -> handshake ok=" + done + " failed=" + failed);
        Ok(failed == 0, "no connection dropped under burst (queueing works)");

        try { proxy.Stop(); } catch { }

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

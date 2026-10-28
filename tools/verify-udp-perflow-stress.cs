using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Reflection;
using System.Threading;
using Ferry;

// 1.0.43 压测：每流独立上游时，远端 relay（Python async 单进程）扛不扛得住。
//
// 1.0.28 的独立版为什么会失败：没有空闲回收，流永远不释放，
// 远端同时挂几十条控制连接就超时（日志「上游不支持或连不上」）。
// 1.0.29 因此改成共享上游 —— 但那是把多路复用做成了广播（见 UdpRelay 注释）。
// 1.0.43 回到独立上游，配套要解决的两件事：
//   ① 100 条并发关联能不能都建起来（远端扛得住吗）
//   ② 客户端断开后，名额会不会立刻回收（否则又会打爆远端）
//
// ② 这条在第一次压测时是 FAIL 的：客户端全断开后 6 秒，100 条一条都没释放。
//   根因是主循环阻塞在 UDP ReceiveFrom 上，感知不到 TCP 控制连接的断开。
//   修法：加一条监视线程阻塞读 ns，对端一断就关掉 clientSock，
//   主循环随即退出，finally 回收名额并关闭专用上游。
static class UdpStressCheck
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

    static int OpenAssoc(int port, out TcpClient keep)
    {
        keep = new TcpClient();
        try
        {
            keep.Connect("127.0.0.1", port);
            var ns = keep.GetStream();
            ns.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);
            var h = new byte[2];
            if (ReadN(ns, h, 2) != 2) return -1;
            ns.Write(new byte[] { 0x05, 0x03, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, 0, 10);
            var rep = new byte[10];
            if (ReadN(ns, rep, 10) != 10 || rep[1] != 0x00) return -1;
            return (rep[8] << 8) | rep[9];
        }
        catch (Exception) { return -1; }
    }

    static void Main()
    {
        var logs = new StringBuilder();
        var proxy = new ProxyServer(17992, 1080, "10.13.24.1",
            delegate (string s) { lock (logs) logs.AppendLine(s); });
        Ok(proxy.Start(), "proxy started");

        var t = typeof(ProxyServer);
        var nowF = t.GetField("_udpAssocNow", BindingFlags.NonPublic | BindingFlags.Static);

        int N = 100;
        int ok = 0, bad = 0;
        var keeps = new List<TcpClient>();

        Console.WriteLine("   opening " + N + " per-flow associations...");
        for (int i = 0; i < N; i++)
        {
            TcpClient k;
            int bnd = OpenAssoc(17992, out k);
            if (bnd > 0) { ok++; keeps.Add(k); }
            else { bad++; try { k.Close(); } catch { } }
            if ((i + 1) % 25 == 0)
                Console.WriteLine("     " + (i + 1) + " tried, ok=" + ok + " failed=" + bad);
        }
        Console.WriteLine("   result: ok=" + ok + " failed=" + bad);
        Ok(bad == 0, "remote relay handled " + N + " per-flow upstreams (no ASSOCIATE timeout)");

        string all;
        lock (logs) all = logs.ToString();
        bool upstreamFail = all.Contains("上游不支持或连不上");
        Ok(!upstreamFail, "no 'upstream not supported' error (that was the 1.0.28 symptom)");

        int live = (int)nowF.GetValue(null);
        Console.WriteLine("   live associations: " + live);
        Ok(live >= N, "counter reflects the live flows");

        // 全部关掉，看名额是否回收
        foreach (var k in keeps) { try { k.Close(); } catch { } }
        Console.WriteLine("   waiting for reclaim...");
        Thread.Sleep(6000);
        int after = (int)nowF.GetValue(null);
        Console.WriteLine("   live after close: " + after);
        Ok(after <= 2, "associations reclaimed after control conns closed");

        try { proxy.Stop(); } catch { }

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Ferry;

// 1.0.29：验证「一个上游关联服务所有本地关联」。
//
// 关键指标：开 6 个客户端 UDP 关联时，**上游的 TCP 连接数应该只有 1 条**，
// 而不是 6 条（那是 1.0.28 的行为，也是远端偶发拒连的原因）。
static class SharedUpCheck
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

    static byte[] DnsQuery(string host)
    {
        var ms = new System.IO.MemoryStream();
        ms.Write(new byte[] { 0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, 0, 12);
        foreach (var part in host.Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(part);
            ms.WriteByte((byte)b.Length);
            ms.Write(b, 0, b.Length);
        }
        ms.WriteByte(0);
        ms.Write(new byte[] { 0x00, 0x01, 0x00, 0x01 }, 0, 4);
        return ms.ToArray();
    }

    static bool SendDns(int relayPort, string name, out int respLen)
    {
        respLen = 0;
        try
        {
            var q = DnsQuery(name);
            var pkt = new byte[10 + q.Length];
            pkt[2] = 0; pkt[3] = 1;
            pkt[4] = 223; pkt[5] = 5; pkt[6] = 5; pkt[7] = 5;
            pkt[8] = 0; pkt[9] = 53;
            Array.Copy(q, 0, pkt, 10, q.Length);

            using (var u = new UdpClient())
            {
                u.Client.ReceiveTimeout = 6000;
                u.Send(pkt, pkt.Length, new IPEndPoint(IPAddress.Loopback, relayPort));
                var from = new IPEndPoint(IPAddress.Any, 0);
                var resp = u.Receive(ref from);
                respLen = resp.Length;
                return resp.Length > 10;
            }
        }
        catch (Exception) { return false; }
    }

    static void Main()
    {
        var logs = new StringBuilder();
        var proxy = new ProxyServer(17999, 1080, "10.13.24.1",
            delegate (string s) { lock (logs) logs.AppendLine(s); });
        Ok(proxy.Start(), "proxy started");

        // ── 1) 6 个关联 + 每个都发一次 DNS ──
        var keeps = new List<TcpClient>();
        var ports = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            TcpClient k;
            int bnd = OpenAssoc(17999, out k);
            if (bnd > 0) { keeps.Add(k); ports.Add(bnd); }
            else { try { k.Close(); } catch { } }
        }
        Console.WriteLine("   opened " + ports.Count + " client associations");
        Ok(ports.Count >= 5, "most associations opened");

        int okCount = 0;
        foreach (var p in ports)
        {
            int n;
            if (SendDns(p, "example.com", out n)) okCount++;
        }
        Console.WriteLine("   associations that got a DNS answer: " + okCount + "/" + ports.Count);
        Ok(okCount >= Math.Min(3, ports.Count), "UDP actually flows through the shared upstream");

        // ── 2) 上游只该有 1 条控制连接 ──
        var t = typeof(ProxyServer);
        var ctrl = t.GetField("_upCtrl", BindingFlags.NonPublic | BindingFlags.Static);
        var upEp = t.GetField("_upEp", BindingFlags.NonPublic | BindingFlags.Static);
        Ok(ctrl != null && ctrl.GetValue(null) != null, "shared upstream control conn exists");
        Console.WriteLine("   shared upstream endpoint: " + upEp.GetValue(null));

        string all;
        lock (logs) all = logs.ToString();
        int readyLines = 0;
        foreach (var line in all.Split('\n'))
            if (line.Contains("UDP 上游已就绪")) readyLines++;
        Console.WriteLine("   'upstream ready' lines: " + readyLines);
        Ok(readyLines == 1, "upstream was established exactly once for all 6 streams");

        // ── 3) 全部关掉后，上游回收 ──
        foreach (var k in keeps) { try { k.Close(); } catch { } }
        Thread.Sleep(23000);
        int alive = 0;
        var relayList = t.GetField("_relays", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null)
                        as System.Collections.IList;
        if (relayList != null) alive = relayList.Count;
        Console.WriteLine("   relays after close: " + alive);
        Ok(alive == 0, "all client relays cleaned up");

        try { proxy.Stop(); } catch { }

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

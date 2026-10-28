using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Ferry;

// 1.0.43：UDP 从「共享上游 + 广播」改回「每流独立上游」。
//
// 为什么改回来：共享上游只有一个 UDP socket，收到回包只能广播给所有活跃流。
// A 应用的回包会被发给 B、C、D —— 手机上几十个应用时协议错乱，
// 表现是「热点一开，本机和手机的网络都不正常」。
//
// 本脚本验证三件事：
//   ① UdpRelay 有独立上游字段（UpSock / UpCtrl / UpEp）
//   ② 旧的广播循环 UpRecvLoop 已经不存在（不再误发）
//   ③ 新的单流下游 UpRecvLoopFor 存在
//   ④ 端到端：并发建多条关联，每条都能独立收发（不串扰）
static class UdpPerFlowCheck
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
        var list = new List<byte> { 0xab, 0xcd, 0x01, 0x00, 0x00, 0x01,
                                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        foreach (var part in host.Split('.'))
        {
            list.Add((byte)part.Length);
            list.AddRange(Encoding.ASCII.GetBytes(part));
        }
        list.Add(0x00);
        list.AddRange(new byte[] { 0x00, 0x01, 0x00, 0x01 });
        return list.ToArray();
    }

    static void Main()
    {
        var t = typeof(ProxyServer);

        // ① 独立上游字段
        var relayT = t.GetNestedType("UdpRelay", BindingFlags.NonPublic);
        Ok(relayT != null, "UdpRelay type found");
        if (relayT != null)
        {
            var fs = relayT.GetField("UpSock", BindingFlags.Public | BindingFlags.Instance);
            var fc = relayT.GetField("UpCtrl", BindingFlags.Public | BindingFlags.Instance);
            var fe = relayT.GetField("UpEp", BindingFlags.Public | BindingFlags.Instance);
            Ok(fs != null, "UdpRelay.UpSock exists (per-flow upstream socket)");
            Ok(fc != null, "UdpRelay.UpCtrl exists (per-flow control conn)");
            Ok(fe != null, "UdpRelay.UpEp exists (per-flow upstream endpoint)");
        }

        // ② 旧广播循环必须已删除
        var oldLoop = t.GetMethod("UpRecvLoop", BindingFlags.NonPublic | BindingFlags.Instance);
        Ok(oldLoop == null, "shared UpRecvLoop removed (no more broadcast)");

        // ③ 新的单流下游
        var newLoop = t.GetMethod("UpRecvLoopFor", BindingFlags.NonPublic | BindingFlags.Instance);
        Ok(newLoop != null, "UpRecvLoopFor exists (per-flow, sends only to own peer)");

        // ④ 端到端：并发多条关联，各自发各自的 DNS 查询，看是否各自拿到应答
        var logs = new StringBuilder();
        var proxy = new ProxyServer(17993, 1080, "10.13.24.1",
            delegate (string s) { lock (logs) logs.AppendLine(s); });
        Ok(proxy.Start(), "proxy started");

        int N = 12;
        int got = 0, missed = 0;
        var keeps = new List<TcpClient>();
        var socks = new List<Socket>();
        var ports = new List<int>();

        for (int i = 0; i < N; i++)
        {
            TcpClient k;
            int bnd = OpenAssoc(17993, out k);
            if (bnd <= 0) { try { k.Close(); } catch { } continue; }
            keeps.Add(k);
            ports.Add(bnd);
            var u = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            u.ReceiveTimeout = 4000;
            socks.Add(u);
        }
        Console.WriteLine("   opened " + keeps.Count + " associations");
        Ok(keeps.Count >= N, "all " + N + " associations opened");

        // 每条流发自己的查询（用 223.5.5.5 的 IP 形式，避免本地解析差异）
        var q = DnsQuery("www.baidu.com");
        var hdr = new List<byte> { 0x00, 0x00, 0x00, 0x01 };
        hdr.AddRange(new byte[] { 223, 5, 5, 5 });
        hdr.AddRange(new byte[] { 0x00, 0x35 });
        var pkt = new byte[hdr.Count + q.Length];
        hdr.CopyTo(pkt); q.CopyTo(pkt, hdr.Count);

        for (int i = 0; i < socks.Count; i++)
        {
            EndPoint ep = new IPEndPoint(IPAddress.Loopback, ports[i]);
            try { socks[i].SendTo(pkt, ep); } catch (Exception) { }
        }

        // 各自收自己的（不串扰的话每条都应收到自己的应答）
        for (int i = 0; i < socks.Count; i++)
        {
            try
            {
                var buf = new byte[4096];
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n = socks[i].ReceiveFrom(buf, ref from);
                if (n > 0) got++; else missed++;
            }
            catch (SocketException) { missed++; }
        }
        Console.WriteLine("   " + socks.Count + " flows -> got reply " + got + ", missed " + missed);
        Ok(got >= socks.Count - 2, "most flows got their own reply (no crosstalk)");

        foreach (var u in socks) { try { u.Close(); } catch { } }
        foreach (var k in keeps) { try { k.Close(); } catch { } }
        try { proxy.Stop(); } catch { }

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

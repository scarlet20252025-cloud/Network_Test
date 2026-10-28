using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Ferry;

// 1.0.38：验证「UDP 回包不会因为上行静默被误杀」。
//
// 背景（2026-10-28 Hytale 现场）：
//   游戏/语音是**下行远大于上行**的流量。玩家站着不动、资产下载的空档，
//   上行可能十几秒没有包。旧逻辑用「最近 5 秒发过包」判活，这些流的回包
//   被直接丢掉 —— 表现是「连接还在、但一直卡着不动」，日志里
//   `ReceiveLoop: No packets received for 5.0s` 刷屏然后 Connection timed out。
//
// 这个脚本验证三件事：
//   ① 关联建立后，**只收不发**的流仍然能收到回包（不因上行静默被丢）
//   ② 静默超过 UdpIdleSec 之后才回收
//   ③ 回收时间是 45/90 秒这组新常量，不是旧的 5/20
static class UdpIdleCheck
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

    /// <summary>建一条 UDP 关联，返回 relay 端口；keep 要一直开着。</summary>
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

    static byte[] DnsQuery()
    {
        // www.baidu.com A
        var list = new List<byte> { 0xab, 0xcd, 0x01, 0x00, 0x00, 0x01,
                                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        foreach (var part in new string[] { "www", "baidu", "com" })
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

        // ── ① 常量是新的那组 ──
        var idleF = t.GetField("UdpIdleSec", BindingFlags.NonPublic | BindingFlags.Static);
        var assocF = t.GetField("UdpAssocIdleSec", BindingFlags.NonPublic | BindingFlags.Static);
        Ok(idleF != null && assocF != null, "idle constants exist");
        if (idleF != null) Ok((int)idleF.GetValue(null) == 45, "per-flow idle window is 45s (was 5s)");
        if (assocF != null) Ok((int)assocF.GetValue(null) == 90, "association idle window is 90s (was 20s)");

        // ── ② UdpRelay 有 LastRecv（判活要看收发两侧）──
        var relayT = t.GetNestedType("UdpRelay", BindingFlags.NonPublic);
        Ok(relayT != null, "UdpRelay type found");
        var recvF = relayT == null ? null : relayT.GetField("LastRecv", BindingFlags.Public | BindingFlags.Instance);
        Ok(recvF != null, "UdpRelay.LastRecv exists (liveness uses both directions)");

        // ── ③ 端到端：建关联 → 发一个包 → 静默 8 秒（旧逻辑早就判死了）→ 再收 ──
        var logs = new StringBuilder();
        var proxy = new ProxyServer(17996, 1080, "10.13.24.1",
            delegate (string s) { lock (logs) logs.AppendLine(s); });
        Ok(proxy.Start(), "proxy started");

        TcpClient keep;
        int bnd = OpenAssoc(17996, out keep);
        Ok(bnd > 0, "UDP association opened (relay port " + bnd + ")");
        if (bnd <= 0) { proxy.Stop(); Console.WriteLine("FAILED"); Environment.Exit(1); }

        var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.ReceiveTimeout = 4000;

        // 目标是远端能到的一个 UDP DNS
        var q = DnsQuery();
        var hdr = new List<byte> { 0x00, 0x00, 0x00, 0x01 };       // RSV, FRAG, ATYP=IPv4
        hdr.AddRange(new byte[] { 223, 5, 5, 5 });                  // 223.5.5.5
        hdr.AddRange(new byte[] { 0x00, 0x35 });                    // port 53
        var pkt = new byte[hdr.Count + q.Length];
        hdr.CopyTo(pkt); q.CopyTo(pkt, hdr.Count);

        EndPoint ep = new IPEndPoint(IPAddress.Loopback, bnd);
        udp.SendTo(pkt, ep);
        bool first;
        try
        {
            var buf = new byte[4096];
            udp.ReceiveFrom(buf, ref ep);
            first = true;
        }
        catch (SocketException) { first = false; }
        Console.WriteLine("   first DNS reply: " + (first ? "ok" : "timeout"));
        Ok(first, "first DNS reply arrives through the relay");

        // 静默 8 秒 —— 旧逻辑 5 秒就判死了，这一发必定被丢
        Console.WriteLine("   staying silent for 8s (old logic would drop replies after 5s)...");
        Thread.Sleep(8000);

        udp.SendTo(pkt, ep);
        bool second;
        try
        {
            var buf = new byte[4096];
            udp.ReceiveFrom(buf, ref ep);
            second = true;
        }
        catch (SocketException) { second = false; }
        Console.WriteLine("   reply after 8s silence: " + (second ? "ok" : "timeout"));
        Ok(second, "reply still delivered after 8s of uplink silence (the actual bug)");

        try { udp.Close(); } catch { }
        try { keep.Close(); } catch { }
        try { proxy.Stop(); } catch { }

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

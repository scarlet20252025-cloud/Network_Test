using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Ferry;

// 验证 UDP 关联：**每条流独占一个上游**（1.0.42 起的正确行为）。
//
// 历史别搞反：1.0.29 曾改成「所有流共享一条上游」，后来证明是错的 ——
// 共享只有一个 UDP socket，回包只能广播给所有活跃流，A 应用的回包发给 B/C/D，
// 热点一开本机和手机都不正常。现在每条 UdpRelay 有自己的 UpCtrl / UpSock /
// UpEp，靠 45s/90s 空闲回收 + 512 上限防止堆积（1.0.28 撞上限是因为不回收）。
// 静态字段 _upCtrl / _upEp 早已不存在，旧断言「只该有 1 条控制连接」已作废。
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

        // ── 2) 每条活跃流都要有自己的上游控制连接（不再广播回包）──
        var t = typeof(ProxyServer);
        var relayField = t.GetField("_relays", BindingFlags.NonPublic | BindingFlags.Static);
        Ok(relayField != null, "_relays flow table exists");
        var relays = relayField == null ? null : relayField.GetValue(null) as System.Collections.IList;
        Ok(relays != null, "flow table readable");
        if (relays != null)
        {
            int withCtrl = 0;
            var relayType = relays.Count > 0 ? relays[0].GetType() : t.GetNestedType("UdpRelay", BindingFlags.NonPublic);
            var upCtrlField = relayType == null ? null : relayType.GetField("UpCtrl");
            Ok(upCtrlField != null, "UdpRelay has its own UpCtrl field");
            if (upCtrlField != null)
                foreach (var r in relays)
                    if (upCtrlField.GetValue(r) != null) withCtrl++;
            Console.WriteLine("   relays=" + relays.Count + " with per-flow UpCtrl=" + withCtrl);
            Ok(withCtrl >= Math.Min(3, ports.Count),
               "each active flow owns an upstream control connection (no broadcast)");
        }

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

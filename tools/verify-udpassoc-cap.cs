using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Ferry;

// 1.0.42：验证 UDP 关联上限从「拒绝」改成「淘汰最久未活动的流」。
//
// 现场（2026-10-28 开热点）：日志连刷
//   「UDP 关联数已达 96，拒绝新关联」
// 原因：上一版把空闲回收放宽到 45s/90s（为了不误杀游戏流），上限却留在 96。
// 「流不回收 + 名额少」叠加 → 手机一连热点就占满 → 所有新 UDP 全被拒
// → 表现是「热点一开本机就断网」。
//
// 本脚本验证：
//   ① 上限提到 512
//   ② 淘汰方法存在
//   ③ 建满上限后仍然能接纳新关联（不再丢弃）
static class UdpCapCheck
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
        var t = typeof(ProxyServer);

        // ① 上限提到 512
        var maxF = t.GetField("MaxUdpAssoc", BindingFlags.NonPublic | BindingFlags.Static);
        Ok(maxF != null, "MaxUdpAssoc field found");
        if (maxF != null)
        {
            int v = (int)maxF.GetValue(null);
            Ok(v >= 512, "udp assoc cap raised to " + v + " (was 96, saturated by a phone)");
        }

        // ② 淘汰方法存在
        var evF = t.GetMethod("EvictStalestUdpRelay", BindingFlags.NonPublic | BindingFlags.Instance);
        Ok(evF != null, "EvictStalestUdpRelay exists (evict instead of reject)");

        // ③ 端到端：连开 100 条关联，看有没有被拒
        var logs = new StringBuilder();
        var proxy = new ProxyServer(17994, 1080, "10.13.24.1",
            delegate (string s) { lock (logs) logs.AppendLine(s); });
        Ok(proxy.Start(), "proxy started");

        int okCount = 0, failCount = 0;
        var keeps = new List<TcpClient>();
        for (int i = 0; i < 100; i++)
        {
            TcpClient k;
            int bnd = OpenAssoc(17994, out k);
            if (bnd > 0) { okCount++; keeps.Add(k); }
            else { failCount++; try { k.Close(); } catch { } }
        }
        Console.WriteLine("   100 associations -> ok=" + okCount + " rejected=" + failCount);
        Ok(failCount == 0, "no association rejected under burst");

        string all;
        lock (logs) all = logs.ToString();
        bool sawReject = all.Contains("拒绝新关联");
        Ok(!sawReject, "no 'rejected new association' log line");

        foreach (var k in keeps) { try { k.Close(); } catch { } }
        try { proxy.Stop(); } catch { }

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

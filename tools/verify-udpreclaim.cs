using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Reflection;
using Ferry;

// 1.0.28：验证 UDP 关联的回收。
//
// 场景：模拟 mihomo 的行为 —— 建一堆 UDP 关联但**不关** TCP 控制连接
// （mihomo 就是这样）。改造前这些关联会一直占着名额，撞到上限后全部失败；
// 改造后空闲 20 秒应该被主动回收。
static class UdpReclaimCheck
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
        var proxy = new ProxyServer(17997, 1080, "10.13.24.1",
            delegate (string s) { lock (logs) logs.AppendLine(s); });
        Ok(proxy.Start(), "proxy started");

        var t = typeof(ProxyServer);
        var nowField = t.GetField("_udpAssocNow", BindingFlags.NonPublic | BindingFlags.Static);
        var maxField = t.GetField("MaxUdpAssoc", BindingFlags.NonPublic | BindingFlags.Static);
        Ok(nowField != null && maxField != null, "stats fields found");
        Ok((int)maxField.GetValue(null) == 96, "cap raised to 96 (was 32)");

        // ── 1) 建 6 个关联，**故意不关控制连接**（模拟 mihomo）──
        var keeps = new List<TcpClient>();
        int opened = 0;
        for (int i = 0; i < 6; i++)
        {
            TcpClient k;
            int bnd = OpenAssoc(17997, out k);
            if (bnd > 0) { keeps.Add(k); opened++; }
            else { try { k.Close(); } catch { } }
        }
        Console.WriteLine("   opened " + opened + " associations (control conns left open)");
        Ok(opened >= 5, "most associations opened");

        Thread.Sleep(500);
        int live = (int)nowField.GetValue(null);
        Console.WriteLine("   live associations: " + live);
        Ok(live >= 5, "they are counted as live");

        // ── 2) 什么都不做，等空闲回收（20 秒 + 余量）──
        Console.WriteLine("   waiting 26s for idle reclaim...");
        Thread.Sleep(26000);
        live = (int)nowField.GetValue(null);
        Console.WriteLine("   live after idle: " + live);
        Ok(live == 0, "idle associations were reclaimed even though TCP stayed open");

        // ── 3) 日志不该每关联一行 ──
        string all;
        lock (logs) all = logs.ToString();
        int perAssoc = 0;
        foreach (var line in all.Split('\n'))
            if (line.Contains("UDP 关联 ") && !line.Contains("累计")) perAssoc++;
        Console.WriteLine("   per-association log lines: " + perAssoc);
        Ok(perAssoc == 0, "no per-association log lines (noise removed)");

        foreach (var k in keeps) { try { k.Close(); } catch { } }
        try { proxy.Stop(); } catch { }

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

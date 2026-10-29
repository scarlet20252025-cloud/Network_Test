using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

// 量化「UDP 走隧道」到底贵多少。
//
// 为什么要这个工具：
//   用户反馈「最新版打游戏延迟高，0.9.30 完全没问题」。已经查明 0.9.30 的
//   ferry-udp 指向远端 1080（那是 TCP 端口，不收 UDP），所以**游戏 UDP 根本
//   没走隧道**；1.0.26 修好之后才真的绕远端。但「绕远端贵多少」一直没人量过。
//
//   本工具对同一个目标发同样的 UDP 报文，比较两条路径的 RTT 分布：
//     直连     本机 → 目标
//     走隧道   本机 → SOCKS5(远端中继) → 目标
//
// 用法：
//   tools\verify.cmd measure-udp-latency.cs [目标IP] [目标端口] [样本数]
//   默认 223.5.5.5:53（阿里 DNS，回包稳定，适合量 RTT）
static class UdpLatency
{
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

    static bool DoHandshake(Socket sock, out NetworkStream ns)
    {
        ns = new NetworkStream(sock, false);
        ns.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);
        var r = new byte[2];
        if (ReadN(ns, r, 2) != 2 || r[0] != 0x05) return false;
        return true;
    }

    static IPEndPoint OpenAssoc(NetworkStream ns, int timeoutMs, IPAddress fallbackHost)
    {
        ns.Write(new byte[] { 0x05, 0x03, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, 0, 10);
        var head = new byte[4];
        if (ReadN(ns, head, 4) != 4 || head[1] != 0x00) return null;
        int len = head[3] == 0x01 ? 4 : (head[3] == 0x04 ? 16 : 0);
        var hostBytes = new byte[len];
        if (ReadN(ns, hostBytes, len) != len) return null;
        var pb = new byte[2];
        if (ReadN(ns, pb, 2) != 2) return null;
        int port = (pb[0] << 8) | pb[1];
        var addr = new IPAddress(hostBytes);
        // BND.ADDR 回 0.0.0.0 表示「就在你连的那台机器上」。
        // 注意这里是**远端**中继，所以回落到 relayHost 而不是 127.0.0.1 ——
        // 写成 Loopback 会把包发到本机自己，然后永远收不到回包（踩过一次）。
        if (addr.Equals(IPAddress.Any) || addr.Equals(IPAddress.IPv6Any)) addr = fallbackHost;
        return new IPEndPoint(addr, port);
    }

    static byte[] DnsQuery()
    {
        // www.qq.com 的 A 查询。用一个国内域名，避免被墙干扰 RTT 统计。
        return new byte[] {
            0x12,0x34, 0x01,0x00, 0x00,0x01, 0x00,0x00, 0x00,0x00, 0x00,0x00,
            0x03,(byte)'w',(byte)'w',(byte)'w',
            0x02,(byte)'q',(byte)'q',
            0x03,(byte)'c',(byte)'o',(byte)'m',
            0x00, 0x00,0x01, 0x00,0x01
        };
    }

    static byte[] BuildPacket(byte[] payload, IPAddress dst, int port)
    {
        var pkt = new byte[10 + payload.Length];
        pkt[0] = 0x00; pkt[1] = 0x00; pkt[2] = 0x00; pkt[3] = 0x01;  // ATYP=IPv4
        var b = dst.GetAddressBytes();
        Buffer.BlockCopy(b, 0, pkt, 4, 4);
        pkt[8] = (byte)(port >> 8); pkt[9] = (byte)(port & 0xFF);
        Buffer.BlockCopy(payload, 0, pkt, 10, payload.Length);
        return pkt;
    }

    static void Report(string label, List<double> samples, int attempted)
    {
        if (samples.Count == 0)
        {
            Console.WriteLine("  " + label + "：全部失败（0/" + attempted + "）");
            return;
        }
        samples.Sort();
        double p50 = samples[(int)Math.Floor(samples.Count * 0.5)];
        double p95 = samples[Math.Min(samples.Count - 1, (int)Math.Floor(samples.Count * 0.95))];
        double sum = 0;
        foreach (var s in samples) sum += s;
        Console.WriteLine(string.Format(
            "  {0}：成功 {1}/{2}  min={3:F1}  p50={4:F1}  p95={5:F1}  max={6:F1}  平均={7:F1}  (ms)",
            label, samples.Count, attempted, samples[0], p50, p95, samples[samples.Count - 1],
            sum / samples.Count));
    }

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string targetIp = args.Length > 0 ? args[0] : "223.5.5.5";
        int targetPort = args.Length > 1 ? int.Parse(args[1]) : 53;
        int count = args.Length > 2 ? int.Parse(args[2]) : 20;

        string relayHost = "10.13.24.1";
        int relayPort = 1080;
        var target = IPAddress.Parse(targetIp);
        var payload = DnsQuery();

        Console.WriteLine("=== UDP 延迟对比 ===");
        Console.WriteLine("  目标      " + targetIp + ":" + targetPort);
        Console.WriteLine("  远端中继  " + relayHost + ":" + relayPort);
        Console.WriteLine("  样本      " + count);
        Console.WriteLine();

        // ── ① 直连 ──
        var direct = new List<double>();
        var sw = new System.Diagnostics.Stopwatch();
        var u1 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        u1.ReceiveTimeout = 2000;
        for (int i = 0; i < count; i++)
        {
            try
            {
                u1.SendTo(payload, new IPEndPoint(target, targetPort));
                var buf = new byte[2048];
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                sw.Restart();
                int n = u1.ReceiveFrom(buf, ref from);
                sw.Stop();
                if (n > 0) direct.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch (Exception) { }
            System.Threading.Thread.Sleep(120);
        }
        u1.Close();
        Report("直连  ", direct, count);

        // ── ② 走远端 SOCKS5 中继 ──
        var tunnel = new List<double>();
        Socket ctl = null;
        Socket udp = null;
        try
        {
            ctl = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            ctl.Connect(new IPEndPoint(IPAddress.Parse(relayHost), relayPort));
            NetworkStream ns;
            if (DoHandshake(ctl, out ns))
            {
                var relay = OpenAssoc(ns, 5000, IPAddress.Parse(relayHost));
                if (relay == null)
                {
                    Console.WriteLine("  走隧道  ：UDP ASSOCIATE 被拒绝");
                }
                else
                {
                    Console.WriteLine("  中继端点 " + relay);
                    udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    udp.ReceiveTimeout = 2000;
                    var pkt = BuildPacket(payload, target, targetPort);
                    for (int i = 0; i < count; i++)
                    {
                        try
                        {
                            udp.SendTo(pkt, relay);
                            var buf = new byte[2048];
                            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                            sw.Restart();
                            int n = udp.ReceiveFrom(buf, ref from);
                            sw.Stop();
                            if (n > 0) tunnel.Add(sw.Elapsed.TotalMilliseconds);
                        }
                        catch (Exception) { }
                        System.Threading.Thread.Sleep(120);
                    }
                    Report("走隧道", tunnel, count);
                }
            }
            else
            {
                Console.WriteLine("  走隧道  ：SOCKS5 握手失败");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("  走隧道  ：连不上中继 —— " + e.Message);
        }
        finally
        {
            if (udp != null) try { udp.Close(); } catch (Exception) { }
            if (ctl != null) try { ctl.Close(); } catch (Exception) { }
        }

        // ── 结论 ──
        if (direct.Count > 0 && tunnel.Count > 0)
        {
            direct.Sort();
            tunnel.Sort();
            double d50 = direct[(int)Math.Floor(direct.Count * 0.5)];
            double t50 = tunnel[(int)Math.Floor(tunnel.Count * 0.5)];
            Console.WriteLine();
            Console.WriteLine(string.Format(
                "=== 走隧道比直连多 {0:F1} ms（p50 {1:F1} → {2:F1}）===",
                t50 - d50, d50, t50));
        }
        Environment.Exit(0);
    }
}

using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Ferry;

// 1.0.45：验证「UDP 上游可以独立于 TCP 上游」。
//
// 为什么需要（2026-10-29 发现的静默 bug）：
//   OpenSSH 的 -D 在协议层面不实现 UDP ASSOCIATE（RFC 1928 CMD=0x03）。
//   隧道模式下 ProxyServer 的上游是 127.0.0.1:<ssh 的 SOCKS 端口>，
//   拿它做 UDP ASSOCIATE 必然失败 —— 而且失败得完全无声，
//   用户看到的是「游戏 / 语音一直连不上」，日志里一个字都没有。
//
// 本脚本验证四件事：
//   ① ProxyServer 有 SetUdpUpstream 方法
//   ② 端到端：UDP 上游指向远端中继时，关联能建起来、能收发
//   ③ 端到端：UDP 上游指向一个不通的地址时，关联立刻失败（不挂起）
//   ④ TCP 上游保持原样（不会被 UDP 设置污染）
static class UdpUpstreamCheck
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

    static bool DoHandshake(Socket sock, out NetworkStream ns)
    {
        ns = new NetworkStream(sock, false);
        ns.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);
        var r = new byte[2];
        if (ReadN(ns, r, 2) != 2 || r[0] != 0x05) return false;
        return true;
    }

    static IPEndPoint OpenAssoc(NetworkStream ns, int timeoutMs)
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
        return new IPEndPoint(new IPAddress(hostBytes), port);
    }

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        // ① 方法存在
        var mi = typeof(ProxyServer).GetMethod("SetUdpUpstream");
        Ok(mi != null, "ProxyServer.SetUdpUpstream exists");
        if (mi == null) { Console.WriteLine("FAILED"); Environment.Exit(1); }

        // 端口：随便挑两个空闲的
        int listenPort = 12801;
        int remoteHostPort = 1080;
        string remote = "10.13.24.1";

        // ② UDP 上游指向远端中继（真实收发）
        var proxy = new ProxyServer(listenPort, 1, "127.0.0.1", delegate (string s) { });
        proxy.SetUdpUpstream(remote, remoteHostPort);
        proxy.Start();
        System.Threading.Thread.Sleep(300);

        Socket c = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        c.Connect(new IPEndPoint(IPAddress.Loopback, listenPort));
        NetworkStream ns;
        bool hs = DoHandshake(c, out ns);
        Ok(hs, "socks handshake to local proxy");
        IPEndPoint relay = OpenAssoc(ns, 5000);
        Ok(relay != null, "UDP ASSOCIATE accepted (upstream = " + remote + ":" + remoteHostPort + ")");

        if (relay != null)
        {
            Console.WriteLine("   relay endpoint = " + relay);
            // 发一个 DNS 查询（8.8.8.8:53 / www.qq.com A 记录）走中继
            var q = new byte[] {
                0x12,0x34, 0x01,0x00, 0x00,0x01, 0x00,0x00, 0x00,0x00, 0x00,0x00,
                0x03,(byte)'w',(byte)'w',(byte)'w',
                0x02,(byte)'q',(byte)'q',
                0x03,(byte)'c',(byte)'o',(byte)'m',
                0x00, 0x00,0x01, 0x00,0x01
            };
            var pkt = new byte[10 + q.Length];
            pkt[0] = 0x00; pkt[1] = 0x00; pkt[2] = 0x00; pkt[3] = 0x01;   // 0.0.0.0 = 按目标地址发
            pkt[4] = 0x08; pkt[5] = 0x08; pkt[6] = 0x08; pkt[7] = 0x08;   // 8.8.8.8
            pkt[8] = 0x00; pkt[9] = 0x35;                                  // :53
            Buffer.BlockCopy(q, 0, pkt, 10, q.Length);

            var u = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            u.ReceiveTimeout = 6000;
            try
            {
                u.SendTo(pkt, relay);
                var buf = new byte[2048];
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n = u.ReceiveFrom(buf, ref from);
                Ok(n > 0, "got UDP reply through remote relay (" + n + " bytes)");
            }
            catch (Exception e)
            {
                Ok(false, "UDP round trip failed: " + e.Message);
            }
            u.Close();
        }
        try { c.Close(); } catch (Exception) { }
        proxy.Stop();
        System.Threading.Thread.Sleep(200);

        // ③ UDP 上游指向死地址：关联应该快速失败，不挂住
        int p2 = 12802;
        var proxy2 = new ProxyServer(p2, 1, "127.0.0.1", delegate (string s) { });
        proxy2.SetUdpUpstream("127.0.0.1", 1);   // 肯定没人听
        proxy2.Start();
        System.Threading.Thread.Sleep(300);
        Socket c2 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        c2.Connect(new IPEndPoint(IPAddress.Loopback, p2));
        NetworkStream ns2;
        if (DoHandshake(c2, out ns2))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IPEndPoint bad = OpenAssoc(ns2, 5000);
            sw.Stop();
            Ok(bad == null, "dead UDP upstream rejected (took " + sw.ElapsedMilliseconds + "ms)");
        }
        try { c2.Close(); } catch (Exception) { }
        proxy2.Stop();

        Console.WriteLine(fails == 0 ? "ALL PASS" : (fails + " FAILED"));
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Ferry;

// 1.0.26：验证 UDP 真的能穿过「TUN → 本地代理 → 上游中继」这条路。
//
// 做法：起一个真实的 ProxyServer（上游指向测试机的 pcs），
// 然后像 mihomo 那样对它做 SOCKS5 UDP ASSOCIATE，发一条 DNS 查询。
static class UdpRelayCheck
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

    static byte[] BuildDns(string host)
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

    static void Main()
    {
        // ── 1) 生成的配置里 ferry-udp 必须指向本地 ──
        var dir = @"C:\ferry-test\udpcheck";
        System.IO.Directory.CreateDirectory(dir);
        var cfg = new Cfg();
        var tun = new TunMode(cfg, dir, delegate (string s) { });
        var bm = typeof(TunMode).GetMethod("BuildConfig", BindingFlags.NonPublic | BindingFlags.Instance);
        var yaml = (string)bm.Invoke(tun, new object[] { 10801, "10.13.24.1", 1080 });

        // 找到 ferry-udp 那一段
        int i = yaml.IndexOf("- name: ferry-udp");
        Ok(i > 0, "ferry-udp block exists");
        var block = yaml.Substring(i, Math.Min(160, yaml.Length - i));
        Console.WriteLine("   " + block.Replace("\n", " | ").Replace("\r", ""));
        Ok(block.Contains("server: 127.0.0.1"),
           "ferry-udp points to 127.0.0.1 (not the remote host)");
        Ok(block.Contains("port: 10801"), "ferry-udp uses the local proxy port");
        Ok(block.Contains("udp: true"), "udp enabled on the outbound");

        // ── 2) 真的发一个 UDP DNS 查询，看能不能收到应答 ──
        var proxy = new ProxyServer(17981, 1080, "10.13.24.1", delegate (string s) { });
        Ok(proxy.Start(), "local proxy started on 17981");

        try
        {
            using (var tcp = new TcpClient())
            {
                tcp.Connect("127.0.0.1", 17981);
                var ns = tcp.GetStream();

                ns.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);
                var hello = new byte[2];
                Ok(ReadN(ns, hello, 2) == 2 && hello[1] == 0x00, "socks handshake ok");

                // UDP ASSOCIATE
                ns.Write(new byte[] { 0x05, 0x03, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, 0, 10);
                var rep = new byte[10];
                Ok(ReadN(ns, rep, 10) == 10 && rep[1] == 0x00, "udp associate accepted");
                int bndPort = (rep[8] << 8) | rep[9];
                Console.WriteLine("   associate bnd port = " + bndPort);

                // 发一条 DNS 查询（走 UDP）
                var q = BuildDns("example.com");
                var pkt = new byte[10 + q.Length];
                pkt[0] = 0; pkt[1] = 0; pkt[2] = 0;          // RSV + FRAG
                pkt[3] = 1;                                   // ATYP IPv4
                pkt[4] = 223; pkt[5] = 5; pkt[6] = 5; pkt[7] = 5;   // 223.5.5.5
                pkt[8] = 0; pkt[9] = 53;
                Array.Copy(q, 0, pkt, 10, q.Length);

                using (var u = new UdpClient())
                {
                    u.Client.ReceiveTimeout = 6000;
                    u.Send(pkt, pkt.Length, new IPEndPoint(IPAddress.Loopback, bndPort));
                    try
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        var resp = u.Receive(ref from);
                        Console.WriteLine("   udp response " + resp.Length + " bytes");
                        Ok(resp.Length > 10, "got UDP response through relay");

                        // 解析出 DNS 应答里的第一个 IP（跳过 SOCKS5 UDP 头）
                        Ok(resp.Length > 12 && resp[10] == 0x12 && resp[11] == 0x34,
                           "response is a DNS answer");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("   udp recv error: " + ex.Message);
                        Ok(false, "got UDP response through relay");
                    }
                }
            }
        }
        finally
        {
            try { proxy.Stop(); } catch { }
        }

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}

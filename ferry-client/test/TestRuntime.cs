// 端到端运行时自测：真实连 SSH 远端、真的建隧道、真的走代理。
//
// 为什么不是点 GUI：GUI 点击依赖窗口拿到前台，在自动化环境里不可靠；
// 而且这一段要验证的是字节与端口，不是像素。
//
// 用法：TestRuntime.exe <remoteHost> [sshUser] [sshPort]
//   例：TestRuntime.exe 10.113.66.6 Administrator 22
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Ferry
{
    static class TestRuntime
    {
        [STAThread]
        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string host = args.Length > 0 ? args[0] : "10.113.66.6";
            string user = args.Length > 1 ? args[1] : "Administrator";
            int sshPort = args.Length > 2 ? int.Parse(args[2]) : 22;

            Console.WriteLine("=== 渡口运行时端到端自测 ===");
            Console.WriteLine("远端 " + user + "@" + host + ":" + sshPort);

            // 直连出口（作为对照）
            var directIp = DirectIp();
            Console.WriteLine("[0] 直连出口 IP: " + (directIp.Length > 0 ? directIp : "（取不到）"));

            var cfg = new Cfg();
            cfg.RemoteHost = host;
            cfg.SshUser = user;
            cfg.SshPort = sshPort;
            cfg.HttpPort = 10801;

            var logLines = new StringBuilder();
            Action<string> log = delegate (string m)
            {
                lock (logLines) logLines.AppendLine("    > " + m);
                Console.WriteLine("    > " + m);
            };

            var rt = new FerryRuntime(cfg, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rt"), log);
            var err = rt.Start();
            if (err.Length > 0)
            {
                Console.WriteLine("[1] ❌ 启动失败：" + err);
                rt.Stop();
                return 2;
            }
            Console.WriteLine("[1] ✅ 隧道 + 代理就绪：SOCKS " + rt.SocksPort + " / HTTP " + rt.HttpPort);
            Console.WriteLine("    隧道进程存活: " + rt.TunnelAlive + "，代理监听: " + rt.ProxyAlive);

            int fail = 0;

            // ② 通过代理访问网站
            var body = HttpVia(rt.HttpPort, "http://www.baidu.com/");
            if (body.Length == 0) { Console.WriteLine("[2] ❌ 经代理访问 baidu 失败"); fail++; }
            else Console.WriteLine("[2] ✅ 经代理访问 baidu 成功，" + body.Length + " 字节");

            // ③ 出口 IP 对照 —— 这是「流量到底有没有走远端」的唯一硬证据
            var viaIp = rt.ExitIpViaProxy(12000);
            Console.WriteLine("[3] 经代理出口 IP: " + (viaIp.Length > 0 ? viaIp : "（取不到）"));
            if (viaIp.Length > 0 && directIp.Length > 0 && viaIp != directIp)
                Console.WriteLine("    ✅ 出口已改变 → 流量确实经过远端");
            else if (viaIp.Length > 0 && viaIp == directIp)
                Console.WriteLine("    ⚠️ 出口与直连相同 —— 可能远端和本机走同一出口，或隧道没生效");
            else
                Console.WriteLine("    ⚠️ 出口探测失败（api.ipify 在国内可能不可达，不算失败）");

            // ④ HTTPS CONNECT
            var https = HttpVia(rt.HttpPort, "https://www.baidu.com/");
            if (https.Length == 0) { Console.WriteLine("[4] ❌ HTTPS CONNECT 失败"); fail++; }
            else Console.WriteLine("[4] ✅ HTTPS CONNECT 成功，" + https.Length + " 字节");

            Console.WriteLine("统计：连接 " + rt.ConnTotal + "，活跃 " + rt.ConnActive);

            rt.Stop();
            Console.WriteLine("隧道已停，代理已停");
            Console.WriteLine(fail == 0 ? "=== 端到端通过 ===" : "=== 有 " + fail + " 项失败 ===");
            return fail;
        }

        static string DirectIp()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://myip.ipip.net");
                req.Proxy = null;
                req.Timeout = 8000;
                req.KeepAlive = false;
                using (var r = req.GetResponse())
                using (var s = r.GetResponseStream())
                using (var sr = new StreamReader(s, Encoding.UTF8))
                    return GrabIp(sr.ReadToEnd());
            }
            catch (Exception) { return ""; }
        }

        /// <summary>从任意文本里抠第一个 IPv4。myip.ipip.net 返回「当前 IP：1.2.3.4 来自于：…」。</summary>
        public static string GrabIp(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(text, @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})");
            return m.Success ? m.Groups[1].Value : text.Trim();
        }

        static string HttpVia(int proxyPort, string url)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Proxy = new WebProxy("http://127.0.0.1:" + proxyPort);
                req.Timeout = 20000;
                req.ReadWriteTimeout = 20000;
                req.KeepAlive = false;
                req.UserAgent = "FerryTest/1.0";
                using (var resp = req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var r = new StreamReader(s, Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch (Exception e)
            {
                Console.WriteLine("    异常：" + e.Message);
                return "";
            }
        }
    }
}

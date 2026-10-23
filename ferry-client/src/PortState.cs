// 端口状态：dual-proxy 自动探测的结果，C# 侧读它而不是信 config
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Ferry
{
    static class PortState
    {
        /// <summary>
        /// dual-proxy 实际监听的端口。
        ///
        /// 为什么不能直接用 config.json 里的 httpPort：
        /// dual-proxy.mjs 默认 `port: 'auto'`，会从 10800 往上找空闲端口并
        /// **覆盖**命令行传的 --listen/--socks。客户机上 Clash / v2rayN 常占
        /// 10808/10809，写死端口就是「软件起不来」。所以真端口以 ports.json 为准。
        ///
        /// 文件位置：dual-proxy.mjs 在 &lt;appDir&gt;，它算的是
        /// dirname(mjs)/../.proxy-state/ports.json = &lt;appDir 的父目录&gt;/.proxy-state
        /// </summary>
        public class Ports
        {
            public int Socks;
            public int Http;
            public int Panel;
            public string Mode = "";
            public DateTime UpdatedAt = DateTime.MinValue;
            public bool Valid { get { return Http > 0 && Socks > 0; } }
        }

        public static string FilePath(string appDir)
        {
            var parent = Path.GetDirectoryName(appDir.TrimEnd('\\'));
            if (string.IsNullOrEmpty(parent)) parent = appDir;
            return Path.Combine(Path.Combine(parent, ".proxy-state"), "ports.json");
        }

        public static Ports Read(string appDir)
        {
            var r = new Ports();
            try
            {
                var p = FilePath(appDir);
                if (!File.Exists(p)) return r;
                var d = MiniJson.ParseObject(File.ReadAllText(p, Encoding.UTF8));
                r.Socks = MiniJson.Int(d, "socks", 0);
                r.Http = MiniJson.Int(d, "http", 0);
                r.Panel = MiniJson.Int(d, "panel", 0);
                r.Mode = MiniJson.Str(d, "mode", "");
                var ts = MiniJson.Str(d, "updatedAt", "");
                DateTime t;
                if (DateTime.TryParse(ts, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out t))
                    r.UpdatedAt = t.ToLocalTime();
            }
            catch (Exception) { }
            return r;
        }

        /// <summary>
        /// 借网时用的端口：优先 ports.json（代理真的在监听那个），
        /// 还没起来就退回 config 的期望值，至少界面有数可显示。
        /// </summary>
        public static Ports Effective(Cfg cfg, string appDir)
        {
            var p = Read(appDir);
            if (p.Valid) return p;
            p.Socks = cfg.SocksPort;
            p.Http = cfg.HttpPort;
            return p;
        }
    }
}

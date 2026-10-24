using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Ferry
{
    /// <summary>
    /// config.json 的读写。字段少且扁平，所以不引第三方 JSON 库 ——
    /// .NET Framework 里能用的只有 JavaScriptSerializer（System.Web.Extensions），
    /// 它把数字反序列化成 int/decimal 二选一，处理起来比手写更难缠。
    ///
    /// 约定：写盘永远是 UTF-8 无 BOM + 两空格缩进，方便用户自己打开看。
    /// </summary>
    class Cfg
    {
        public string RemoteHost = "";
        public string AccountIp = "";
        public string Token = "";
        public string PortalUrl = "";
        public string FallbackProxy = "";
        public int SshPort = 22;
        /// <summary>远端 SSH 登录名。不同主机不一样，写死会在换机器时莫名连不上。</summary>
        public string SshUser = "pcserver";
        public int HttpPort = 10801;
        /// <summary>授权到期时间（unix 秒）。0 = 永久或未激活。</summary>
        public long ExpiresAt = 0;

        // ── 本地设置（门户不下发，用户自己在设置面板里改）──
        /// <summary>检测到断网时自动借网。</summary>
        public bool AutoBorrow = false;
        /// <summary>启动后直接收进托盘，不显示主窗口。</summary>
        public bool StartMinimized = false;
        /// <summary>隧道断开自动重连。</summary>
        public bool AutoReconnect = true;
        /// <summary>断网检测间隔（秒）。</summary>
        public int NetCheckSec = 15;

        // ── TUN 模式（让不认代理的程序也走渡口）──
        /// <summary>
        /// 借网时同时开启 TUN（全局接管）。需要管理员权限。
        ///
        /// 为什么做成可选而不是默认开：
        ///   TUN 会接管**所有**流量并改路由表。对只想让浏览器借网的用户，
        ///   这是多余的风险（比如 TUN 起不来会短暂断网）。
        ///   默认关，用户明确需要（打游戏、用不认代理的软件）时再开。
        /// </summary>
        public bool UseTun = false;
        /// <summary>远端 UDP 中继地址（TUN 模式下 UDP 走它）。</summary>
        public string UdpRelayHost = "";
        /// <summary>远端 UDP 中继端口。0 = 用默认 1080。</summary>
        public int UdpRelayPort = 1080;

        public bool Activated { get { return !string.IsNullOrEmpty(Token); } }

        public int SocksPort { get { return HttpPort - 1; } }

        public string BorrowServer
        {
            get { return "http://127.0.0.1:" + HttpPort.ToString(CultureInfo.InvariantCulture); }
        }

        public static Cfg Load(string path)
        {
            var c = new Cfg();
            if (!File.Exists(path)) return c;
            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                var d = MiniJson.ParseObject(text);
                c.RemoteHost = MiniJson.Str(d, "remoteHost", "10.13.24.1");
                c.AccountIp = MiniJson.Str(d, "accountIp", "");
                c.Token = MiniJson.Str(d, "token", "");
                c.PortalUrl = MiniJson.Str(d, "portalUrl", "").TrimEnd('/');
                c.FallbackProxy = MiniJson.Str(d, "fallbackProxy", "");
                c.SshPort = MiniJson.Int(d, "sshPort", 22);
                c.SshUser = MiniJson.Str(d, "sshUser", "pcserver");
                if (string.IsNullOrEmpty(c.SshUser)) c.SshUser = "pcserver";
                c.HttpPort = MiniJson.Int(d, "httpPort", 10801);
                c.ExpiresAt = MiniJson.Long(d, "expiresAt", 0);
                c.AutoBorrow = MiniJson.Bool(d, "autoBorrow", false);
                c.StartMinimized = MiniJson.Bool(d, "startMinimized", false);
                c.AutoReconnect = MiniJson.Bool(d, "autoReconnect", true);
                c.NetCheckSec = MiniJson.Int(d, "netCheckSec", 15);
                c.UseTun = MiniJson.Bool(d, "useTun", false);
                c.UdpRelayHost = MiniJson.Str(d, "udpRelayHost", "");
                c.UdpRelayPort = MiniJson.Int(d, "udpRelayPort", 1080);
                // UDP 中继默认跟远端同一台机（pcs 上的 Python 中继）
                if (string.IsNullOrEmpty(c.UdpRelayHost)) c.UdpRelayHost = c.RemoteHost;
            }
            catch (Exception)
            {
                // 配置坏了不能让程序起不来 —— 用默认值继续，用户重装一次即可。
            }
            if (c.HttpPort < 1024 || c.HttpPort > 65534) c.HttpPort = 10801;
            if (c.SshPort < 1 || c.SshPort > 65535) c.SshPort = 22;
            if (c.NetCheckSec < 10 || c.NetCheckSec > 300) c.NetCheckSec = 15;
            if (string.IsNullOrEmpty(c.UdpRelayHost)) c.UdpRelayHost = c.RemoteHost;
            if (c.UdpRelayPort < 1 || c.UdpRelayPort > 65535) c.UdpRelayPort = 1080;
            return c;
        }

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"remoteHost\": ").Append(Quote(RemoteHost)).Append(",\n");
            sb.Append("  \"sshPort\": ").Append(SshPort).Append(",\n");
            sb.Append("  \"sshUser\": ").Append(Quote(SshUser)).Append(",\n");
            sb.Append("  \"httpPort\": ").Append(HttpPort).Append(",\n");
            sb.Append("  \"accountIp\": ").Append(Quote(AccountIp)).Append(",\n");
            sb.Append("  \"token\": ").Append(Quote(Token)).Append(",\n");
            sb.Append("  \"portalUrl\": ").Append(Quote(PortalUrl)).Append(",\n");
            sb.Append("  \"fallbackProxy\": ").Append(Quote(FallbackProxy)).Append(",\n");
            sb.Append("  \"expiresAt\": ").Append(ExpiresAt).Append(",\n");
            sb.Append("  \"autoBorrow\": ").Append(AutoBorrow ? "true" : "false").Append(",\n");
            sb.Append("  \"startMinimized\": ").Append(StartMinimized ? "true" : "false").Append(",\n");
            sb.Append("  \"autoReconnect\": ").Append(AutoReconnect ? "true" : "false").Append(",\n");
            sb.Append("  \"netCheckSec\": ").Append(NetCheckSec).Append(",\n");
            sb.Append("  \"useTun\": ").Append(UseTun ? "true" : "false").Append(",\n");
            sb.Append("  \"udpRelayHost\": ").Append(Quote(UdpRelayHost)).Append(",\n");
            sb.Append("  \"udpRelayPort\": ").Append(UdpRelayPort).Append("\n");
            sb.Append("}\n");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            // 先写临时文件再换名：写到一半被关机，旧配置还在。
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        static string Quote(string s)
        {
            if (s == null) s = "";
            var sb = new StringBuilder("\"");
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }

    /// <summary>
    /// 够用就好的 JSON 读取器：只处理对象、字符串、数字、布尔、null。
    /// 不处理嵌套对象/数组的写入（config.json 用不到）。
    /// </summary>
    static class MiniJson
    {
        public static Dictionary<string, object> ParseObject(string s)
        {
            int i = 0;
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '{') throw new FormatException("not an object");
            return ParseObjBody(s, ref i);
        }

        static Dictionary<string, object> ParseObjBody(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '{') i++;
            for (; ; )
            {
                SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == '}') { i++; break; }
                if (s[i] == ',') { i++; continue; }
                var key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                SkipWs(s, ref i);
                d[key] = ParseValue(s, ref i);
            }
            return d;
        }

        static object ParseValue(string s, ref int i)
        {
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '"') return ParseString(s, ref i);
            if (c == '{') { i++; return ParseObjBody(s, ref i); }
            if (c == '[')
            {
                // 数组：只跳过，存成字符串，config 里用不到
                int depth = 0;
                int start = i;
                while (i < s.Length)
                {
                    if (s[i] == '[') depth++;
                    else if (s[i] == ']') { depth--; i++; if (depth == 0) break; continue; }
                    i++;
                }
                return s.Substring(start, Math.Min(i - start, s.Length - start));
            }
            if (i + 4 <= s.Length && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (i + 5 <= s.Length && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (i + 4 <= s.Length && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int n0 = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;
            var num = s.Substring(n0, i - n0);
            double dv;
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) return dv;
            return 0.0;
        }

        static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            if (i < s.Length && s[i] == '"') i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 <= s.Length)
                        {
                            int cp;
                            if (int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp))
                                sb.Append((char)cp);
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        public static string Str(Dictionary<string, object> d, string k, string def)
        {
            object v = Lookup(d, k);
            return v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : def;
        }

        public static int Int(Dictionary<string, object> d, string k, int def)
        {
            object v = Lookup(d, k);
            if (v == null) return def;
            try { return (int)Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture)); }
            catch (Exception) { return def; }
        }

        public static long Long(Dictionary<string, object> d, string k, long def)
        {
            object v = Lookup(d, k);
            if (v == null) return def;
            try { return (long)Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture)); }
            catch (Exception) { return def; }
        }

        public static bool Bool(Dictionary<string, object> d, string k, bool def)
        {
            object v = Lookup(d, k);
            if (v == null) return def;
            if (v is bool) return (bool)v;
            var s = Convert.ToString(v, CultureInfo.InvariantCulture);
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";
        }

        /// <summary>
        /// 单独抽出来，避免每个取字段的方法都写一遍 out 变量声明。
        /// （C# 5 的编译器不允许 out 实参在短路的 &amp;&amp; 之后使用，会报未赋值。）
        /// </summary>
        static object Lookup(Dictionary<string, object> d, string k)
        {
            if (d == null) return null;
            object v = null;
            d.TryGetValue(k, out v);
            return v;
        }
    }
}

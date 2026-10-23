using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace Ferry
{
    /// <summary>激活结果。失败时 Error 非空，其余字段无意义。</summary>
    class ActivateResult
    {
        public bool Ok;
        public string Error = "";
        public string Token = "";
        public string RemoteHost = "";
        public string PortalUrl = "";
        public int SshPort;
        public int HttpPort;
        public long ExpiresAt;
        public int DaysLeft;
    }

    /// <summary>授权状态。到期/停用都靠这里传达，客户端不自己猜。</summary>
    class LicenseState
    {
        public bool Ok;
        public string Error = "";
        public bool Enabled;
        public bool Expired;
        public bool Suspended;
        public long ExpiresAt;
        public int DaysLeft;
        public int HoursLeft;
    }

    /// <summary>新版本信息。</summary>
    class VersionInfo
    {
        public bool Ok;
        public string Latest = "";
        public string Url = "";
        public string Notes = "";
    }

    static class Api
    {
        const int TimeoutMs = 10000;

        /// <summary>
        /// 所有请求都显式关掉代理。
        ///
        /// 不这么做会踩两个坑：①本机装了 Clash/加速器时，去 10.x 内网的请求
        /// 会被代理接管然后失败；②本程序自己会把系统代理指向本地端口，
        /// 一旦「借网」开着再调 API，请求会绕进自己的代理形成回环。
        /// </summary>
        static HttpWebRequest Make(string url, string method)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Proxy = null;
            req.Timeout = TimeoutMs;
            req.ReadWriteTimeout = TimeoutMs;
            req.KeepAlive = false;
            req.UserAgent = "FerryClient/1.0";
            return req;
        }

        static string ReadBody(WebResponse resp)
        {
            using (var s = resp.GetResponseStream())
            using (var r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        /// <summary>
        /// 把 WebException 的响应体取出来 —— 服务端 400/401 时会把
        /// {"ok":false,"error":"密钥无效"} 放在 body 里，不读就只剩一句
        /// 「远程服务器返回错误: (401) 未经授权」，用户完全不知道错在哪。
        /// </summary>
        static string ErrorBody(WebException e)
        {
            var r = e.Response as HttpWebResponse;
            if (r == null) return "";
            try { using (var s = r.GetResponseStream()) using (var sr = new StreamReader(s, Encoding.UTF8)) return sr.ReadToEnd(); }
            catch (Exception) { return ""; }
        }

        public static ActivateResult Activate(string portalUrl, string code)
        {
            var res = new ActivateResult();
            var url = portalUrl.TrimEnd('/') + "/api/activate";
            var payload = "{\"code\":\"" + Esc(code) + "\"}";
            try
            {
                var req = Make(url, "POST");
                req.ContentType = "application/json; charset=utf-8";
                var bytes = Encoding.UTF8.GetBytes(payload);
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                string body;
                using (var resp = req.GetResponse()) body = ReadBody(resp);
                Fill(res, body);
            }
            catch (WebException e)
            {
                var code401 = 0;
                try { if (e.Response is HttpWebResponse) code401 = (int)((HttpWebResponse)e.Response).StatusCode; }
                catch (Exception) { }

                var body = ErrorBody(e);
                if (body.Length > 0) Fill(res, body);

                // 服务端 500 时返回的是一整页 HTML / traceback，
                // 直接显示给用户就是「一坨看不懂的内容」。这里统一换成可读文案，
                // 原始内容只在没拿到任何 JSON 字段时兜底。
                if (code401 >= 500)
                    res.Error = "服务端出错了（HTTP " + code401 + "）。请稍后重试，或联系管理员看服务端日志。";
                else if (string.IsNullOrEmpty(res.Error))
                    res.Error = body.Length > 0 && body.TrimStart().StartsWith("<")
                        ? "服务端返回了网页而不是数据（HTTP " + code401 + "）。请确认门户地址正确。"
                        : "服务端返回异常（HTTP " + code401 + "）";
            }
            catch (Exception e)
            {
                res.Error = "请求失败：" + e.Message;
            }
            return res;
        }

        static void Fill(ActivateResult res, string body)
        {
            try
            {
                var d = MiniJson.ParseObject(body);
                res.Ok = MiniJson.Bool(d, "ok", false);
                res.Error = MiniJson.Str(d, "error", "");
                res.Token = MiniJson.Str(d, "token", "");
                res.RemoteHost = MiniJson.Str(d, "remoteHost", "");
                res.PortalUrl = MiniJson.Str(d, "portalUrl", "");
                res.SshPort = MiniJson.Int(d, "sshPort", 22);
                res.HttpPort = MiniJson.Int(d, "httpPort", 10801);
                res.ExpiresAt = MiniJson.Long(d, "expiresAt", 0);
                res.DaysLeft = MiniJson.Int(d, "daysLeft", 0);
            }
            catch (Exception)
            {
                res.Ok = false;
                res.Error = "服务端返回了看不懂的内容";
            }
        }

        public static LicenseState License(Cfg cfg)
        {
            var st = new LicenseState();
            if (!cfg.Activated) { st.Error = "未激活"; return st; }
            if (string.IsNullOrEmpty(cfg.PortalUrl)) { st.Ok = true; st.Enabled = true; st.Error = "未配置门户，跳过校验"; return st; }
            var url = cfg.PortalUrl.TrimEnd('/') + "/api/license?ip=" + Uri.EscapeDataString(cfg.AccountIp)
                    + "&token=" + Uri.EscapeDataString(cfg.Token);
            try
            {
                var req = Make(url, "GET");
                string body;
                using (var resp = req.GetResponse()) body = ReadBody(resp);
                var d = MiniJson.ParseObject(body);
                st.Ok = MiniJson.Bool(d, "ok", false);
                st.Error = MiniJson.Str(d, "error", "");
                st.Enabled = MiniJson.Bool(d, "enabled", false);
                st.Expired = MiniJson.Bool(d, "expired", false);
                st.Suspended = MiniJson.Bool(d, "suspended", false);
                st.ExpiresAt = MiniJson.Long(d, "expiresAt", 0);
                st.DaysLeft = MiniJson.Int(d, "daysLeft", 0);
                st.HoursLeft = MiniJson.Int(d, "hoursLeft", 0);
            }
            catch (WebException e)
            {
                // 查不到授权时**不能**直接判死 —— 用户在断网环境下本来就连不上门户，
                // 这时候把本地的到期时间当依据，比「一律拒绝」合理。
                st.Ok = false;
                st.Error = e.Status == WebExceptionStatus.Timeout ? "连接门户超时" : "连不上门户";
                st.Enabled = LocalStillValid(cfg);
                st.ExpiresAt = cfg.ExpiresAt;
                st.DaysLeft = DaysLeftOf(cfg.ExpiresAt);
                st.Expired = cfg.ExpiresAt > 0 && cfg.ExpiresAt <= NowUnix();
            }
            catch (Exception e)
            {
                st.Ok = false;
                st.Error = e.Message;
                st.Enabled = LocalStillValid(cfg);
                st.ExpiresAt = cfg.ExpiresAt;
                st.DaysLeft = DaysLeftOf(cfg.ExpiresAt);
            }
            return st;
        }

        /// <summary>
        /// 上报心跳：累计上下行字节 + 活跃连接数。
        ///
        /// 服务端要显示「每个用户当前多快」，而 Windows 拿不到 per-connection
        /// 的网络字节数 —— 只有客户端自己知道。5 秒一次，失败静默忽略
        /// （断网时本来就连不上门户，不该刷错误）。
        /// </summary>
        public static void Heartbeat(Cfg cfg, long rxBytes, long txBytes, int conns, bool running)
        {
            if (!cfg.Activated || string.IsNullOrEmpty(cfg.PortalUrl)) return;
            try
            {
                var url = cfg.PortalUrl.TrimEnd('/') + "/api/heartbeat";
                var payload = "{\"ip\":\"" + Esc(cfg.AccountIp) + "\",\"token\":\"" + Esc(cfg.Token) +
                              "\",\"rxBytes\":" + rxBytes + ",\"txBytes\":" + txBytes +
                              ",\"conns\":" + conns + ",\"running\":" + (running ? "true" : "false") + "}";
                var req = Make(url, "POST");
                req.Timeout = 5000;
                req.ContentType = "application/json; charset=utf-8";
                var bytes = Encoding.UTF8.GetBytes(payload);
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (req.GetResponse()) { }
            }
            catch (Exception) { }
        }

        public static VersionInfo Version(string portalUrl)
        {
            var v = new VersionInfo();
            if (string.IsNullOrEmpty(portalUrl)) return v;
            try
            {
                var req = Make(portalUrl.TrimEnd('/') + "/version.json", "GET");
                string body;
                using (var resp = req.GetResponse()) body = ReadBody(resp);
                var d = MiniJson.ParseObject(body);
                v.Latest = MiniJson.Str(d, "latest", "");
                v.Url = MiniJson.Str(d, "url", "");
                v.Notes = MiniJson.Str(d, "notes", "");
                v.Ok = v.Latest.Length > 0;
            }
            catch (Exception) { }
            return v;
        }

        public static bool LocalStillValid(Cfg cfg)
        {
            return cfg.ExpiresAt <= 0 || cfg.ExpiresAt > NowUnix();
        }

        public static long NowUnix()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        public static int DaysLeftOf(long expiresAt)
        {
            if (expiresAt <= 0) return 0;
            var left = expiresAt - NowUnix();
            if (left <= 0) return 0;
            return (int)(left / 86400);
        }

        static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}

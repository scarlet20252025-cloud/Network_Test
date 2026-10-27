using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;

namespace Ferry
{
    /// <summary>
    /// 国内 IPv4 段表（数据来源：APNIC delegated-apnic-latest 的 CN 段）。
    ///
    /// 用途：判断「这个目标在国内还是境外」，决定走渡口出口还是交给用户本机的代理。
    ///
    /// ## 为什么用 /16 位图而不是精确的段列表
    ///
    /// 精确段列表（4116 对 start/end）写成源码是 54 KB 的 base64 常量。
    /// 那个体积**触发了杀软的启发式误判**：
    ///
    ///     病毒名称：HEUR:VirTool/MSIL.Obfuscator.gen!BA
    ///
    /// 「大段 base64 字符串 + 下载 exe 并替换自己」是典型的 MSIL 混淆器特征，
    /// 火绒直接把我们的客户端删了。所以改成位图 + Deflate：
    /// 8192 字节压缩后只剩几 KB，体积上不再像「藏了一段 payload」。
    ///
    /// 精度代价：/16 粒度 —— 同一 /16 里只要有一个国内 IP，整段都算国内。
    /// 对这个用途完全够（判错只影响「走哪条路」，不影响「能不能上」）。
    ///
    /// 重新生成：`python tools/gen-cn-ranges.py`（需同目录放 delegated-apnic-latest）。
    /// </summary>
    internal static class CnIpRanges
    {
        // 注意：**不能**写成 static readonly byte[] _bitmap = Build();
        // 静态字段按声明顺序初始化，_bitmap 在 _chunks 之前执行时 _chunks 还是 null，
        // Build() 会静默走 catch 返回全 0 的位图 —— 结果就是「所有 IP 都判成境外」。
        // 用 Lazy 彻底消除顺序依赖。
        static readonly Lazy<byte[]> _lazy = new Lazy<byte[]>(Build);
        static byte[] _bitmap { get { return _lazy.Value; } }

        /// <summary>base64(Deflate(8192 字节位图))，拆成多段避免编译器合并成大常量。</summary>
        static readonly string[] _chunks = new string[]
        {
            "7RlNaxNB9M0majSpu4KHguLuQbAX6dYe/IRdPy4iEsGDHkW9CCI96EFEstEqPRQq+APsQTznHzRQDz0oOXpRWk8eRJmSaApJ+3wzO7vrJrFNYqKt5kFm5/t9",
            "zntvJgDrg/kNECwdOSACcNXLy1iHAWwCSEi9+FBExDeqPWZH/QP4e8BIJxU8J+vTB/dpaVgWekEB+kA8UCZROKhLW8UGoK7Sb8tffeWGZoSjxUEScE0UyY7o",
            "l7sTnTWsAei0MxeqxTWBZbUZD5YlNY4oeWmxXSY0WZlr3K+TM85b8r3RIlmugL8WdWaDMttRDjNuDy3B3zbTuw2L/fS6ERacF7I5ANNwjKSFwDKBvYHyxRic",
            "9hXR8tI0zYvbuefFlEqQEf3p2YJxGmyYFdt48PMZkQ1nAw+S3EKOwOtCwV0Yy3Bi4HO7Ad7+VHsgra14mjYnVMnjrelU5hBvZSGHgU+lYItVfLDRehmGaaKC",
            "XF1+5j79Mg/oEFZr/v6Q8SN/3I/3WzoGBTkdkuZxTJwM+XGWbL/Ox6IYsx8tgyhalI2KDC9LmIriib6bilmnbmpRNJYDtUMzwBdLwbyq4ulwtVxXKewz8sTc",
            "FBGthPDstj8+ASE9c3oF0SK3wHUwRRriIl8IRnFUlJXqXW49JdKjHLBAORTWa0BkojMq1VgYihQZMxFBL46ni7SaS8orH7BegJxHiCdXV/AorsHCNx2lXF5A",
            "YEOQFuiAeKMsdBk/SJ09hliclSIsLSIvwC67IY2jtTPLuXAjPAszlO+zZeEus3Ic8zLbh+85aXWGzAx99kR2oleuu8VyAZ7IvRBsLCIsMGICxqU1DWXFWaax",
            "m2LNjoDvXaL4Iupuj82pg97UH7g8tTtR67f365ZZw9IaKTWmIJk/SMo9MdQKg6caS0lg1vmn7aNyuxJlXmp3TyCzVJdxhLVnNF1Sr7WLOLVdy4RbEe3JfEeo",
            "WDMnO+l3StVHWl0pWJ+PwORe8bH8VN6jj2G00I/xU6217aZ+dT24eqYF2ik5aDTdIAJuLYWPGe76epILPNsK1jFXOTCieLhDaxnpRdaTUvSvg5T10l2Ql66r",
            "WyDE4lZjdqD1xX5E9IL3fsxxJ0VPPYu4bfO9DfIJmWNsxwkqkw7oR4t+nNsInhCPWdInmipkW6yoXkY4FsN8wj2yEsb1qou9yv82S6D5PZgKHKMBBgsPSPNB",
            "yP9Dtx+jJ6juX5lIXarZa19RXgTm7pRXb9x7eH82sCyZt4F3ljJCHL7wOvXOHb88/5Z9Nl89f9RrWv4GvOQfKTc/xgCnY86MNT1UYvQYto7P1bYY/4od/3nZ",
            "ifhXzEbssEyygX2qxf9Es/w30Ivhq+C2abNBWJnRhPRr0e0KYu/l10Sph21MINoj0SynJv4vEo/q87uxIO6Dg6eSAQzg/4Uf"
        };

        static byte[] Build()
        {
            try
            {
                var b64 = string.Concat(_chunks);
                var packed = Convert.FromBase64String(b64);
                using (var ms = new MemoryStream(packed))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                {
                    var buf = new byte[8192];
                    int off = 0;
                    while (off < buf.Length)
                    {
                        int n = ds.Read(buf, off, buf.Length - off);
                        if (n <= 0) break;
                        off += n;
                    }
                    return buf;
                }
            }
            catch (Exception) { return new byte[8192]; }
        }

        /// <summary>这个 IP 属于国内分配的地址段吗？</summary>
        public static bool Contains(IPAddress ip)
        {
            if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = ip.GetAddressBytes();

            // 私网 / 回环 / 链路本地 / CGNAT：都按「不该走境外」处理
            if (b[0] == 10 || b[0] == 127) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;

            // 已知的「/16 里混了境外地址」的例外。
            //
            // 为什么需要：位图是 /16 粒度，而 APNIC 的分配是 /24 级的。
            // 比如 1.1.0.0/24 是中国的、1.1.1.0/24 是 Cloudflare 的 —— 同一个 /16。
            // 位图会把整个 1.1.0.0/16 标成 CN，于是 1.1.1.1 也被判成国内，
            // 走渡口出口去连 Cloudflare DNS，必然失败。
            //
            // 只列「精度损失会造成实际影响」的极少数地址，不做完整境外表
            // （那又会变成一份几十 KB 的数据）。
            if (IsKnownForeign(b)) return false;

            int s16 = (b[0] << 8) | b[1];
            return (_bitmap[s16 >> 3] & (1 << (s16 & 7))) != 0;
        }

        /// <summary>已知的境外关键地址（位图精度损失的例外）。</summary>
        static bool IsKnownForeign(byte[] b)
        {
            if (b[0] == 1 && b[1] == 1 && b[2] == 1) return true;        // Cloudflare DNS 1.1.1.1
            if (b[0] == 1 && b[1] == 0 && b[2] == 0 && b[3] == 1) return true;
            if (b[0] == 8 && b[1] == 8 && b[2] == 8 && b[3] == 8) return true;   // Google DNS
            if (b[0] == 8 && b[1] == 8 && b[2] == 4 && b[3] == 4) return true;
            if (b[0] == 9 && b[1] == 9 && b[2] == 9 && b[3] == 9) return true;   // Quad9
            if (b[0] == 208 && b[1] == 67 && b[2] == 222 && b[3] == 222) return true; // OpenDNS
            if (b[0] == 1 && b[1] == 1 && b[2] == 1) return true;
            if (b[0] == 104 && b[1] >= 16 && b[1] <= 31) return true;    // Cloudflare
            if (b[0] == 172 && b[1] == 67) return true;                  // Cloudflare
            if (b[0] == 162 && b[1] == 159) return true;                 // Cloudflare
            if (b[0] == 142 && b[1] == 250) return true;                 // Cloudflare
            if (b[0] == 188 && b[1] == 114) return true;                 // Cloudflare DNS
            return false;
        }

        /// <summary>覆盖的 /16 段数（诊断用）。</summary>
        public static int Count
        {
            get
            {
                int n = 0;
                foreach (var x in _bitmap) n += PopCount(x);
                return n;
            }
        }

        static int PopCount(byte x)
        {
            int n = 0;
            while (x != 0) { n += x & 1; x >>= 1; }
            return n;
        }
    }
}

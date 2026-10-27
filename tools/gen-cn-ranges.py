# -*- coding: utf-8 -*-
"""从 APNIC 分配数据生成 C# 用的国内 IPv4 段表（二进制搜索）。

为什么要这个表：之前用「猜大厂 IP 段」的方式判断国内外，非常不可靠
（google.com 被污染成 31.13.92.37，判成境外；但很多正常国内站会被判错）。
APNIC 的 delegated 文件是权威分配数据，4116 个段覆盖 3.4 亿 IPv4。

为什么用 base64 而不是十六进制字面量：4116 段写成 "0x...,0x...," 是 102 KB
源码（还会让 exe 明显变大）；打包成 big-endian uint32 序列再 base64 只有 ~44 KB。
"""
import base64
import io
import struct

rows = []
for line in io.open(r'C:\Users\63526\AppData\Local\Temp\apnic.txt', encoding='utf-8', errors='ignore'):
    p = line.strip().split('|')
    if len(p) < 7 or p[2] != 'ipv4' or p[1] != 'CN':
        continue
    if p[6] not in ('allocated', 'assigned'):
        continue
    a, b, c, d = [int(x) for x in p[3].split('.')]
    start = (a << 24) | (b << 16) | (c << 8) | d
    rows.append((start, start + int(p[4]) - 1))

rows.sort()
merged = []
for s, e in rows:
    if merged and s <= merged[-1][1] + 1:
        merged[-1] = (merged[-1][0], max(merged[-1][1], e))
    else:
        merged.append((s, e))

blob = b''.join(struct.pack('>II', s, e) for s, e in merged)
b64 = base64.b64encode(blob).decode('ascii')

# 每行 100 字符，拼成 C# 字符串常量
chunks = [b64[i:i + 100] for i in range(0, len(b64), 100)]
literal = '\n'.join('            "%s" +' % c for c in chunks)
literal = literal.rstrip(' +') + ';'

cs = '''using System;
using System.Net;

namespace Ferry
{
    /// <summary>
    /// 国内 IPv4 段表（数据来源：APNIC delegated-apnic-latest 的 CN 段）。
    ///
    /// 为什么需要它：客户端要判断「这个目标在国内还是境外」，才能决定走渡口
    /// 还是交给用户本机的代理。之前的实现是「猜几个大厂 IP 段」，非常不可靠
    /// —— 比如 google.com 在国内 DNS 上被污染成 31.13.92.37（Facebook 的段）。
    ///
    /// 为什么用「段表 + 二分」而不是 GeoIP 库：GeoIP 要带几十 MB 数据文件，
    /// 而我们要的判断只有一个布尔值，用分配数据就够了。表以 base64 形式编译进
    /// exe（约 44 KB 文本），首次使用时解析一次。
    ///
    /// 更新方式：`python tools/gen-cn-ranges.py`（需要同目录放一份
    /// delegated-apnic-latest，见脚本头注释）。
    /// </summary>
    internal static class CnIpRanges
    {
        static readonly uint[] _ranges = Build();

        const string Raw =
''' + literal + '''

        static uint[] Build()
        {
            var bytes = Convert.FromBase64String(Raw);
            var arr = new uint[bytes.Length / 4];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = ((uint)bytes[i * 4] << 24) | ((uint)bytes[i * 4 + 1] << 16) |
                         ((uint)bytes[i * 4 + 2] << 8) | bytes[i * 4 + 3];
            return arr;
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

            uint v = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

            // _ranges 是 [start0, end0, start1, end1, ...]，按 start 升序
            int lo = 0, hi = _ranges.Length / 2 - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                uint s = _ranges[mid * 2], e = _ranges[mid * 2 + 1];
                if (v < s) hi = mid - 1;
                else if (v > e) lo = mid + 1;
                else return true;
            }
            return false;
        }

        public static int Count { get { return _ranges.Length / 2; } }
    }
}
'''

io.open(r'E:\肥鱼不乐园\borrow-net\ferry-client\src\CnIpRanges.cs', 'w',
        encoding='utf-8', newline='\n').write(cs)
print('generated: %d ranges, base64 %d chars' % (len(merged), len(b64)))

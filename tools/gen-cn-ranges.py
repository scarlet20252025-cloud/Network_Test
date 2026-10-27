# -*- coding: utf-8 -*-
"""把国内 IP 段表从「54KB base64」改成「Deflate 压缩的 /16 位图」。

## 为什么要改：火绒报毒

用户日志：

```
病毒名称：HEUR:VirTool/MSIL.Obfuscator.gen!BA
病毒路径：C:\\Users\\ASUS\\AppData\\Local\\Ferry\\渡口助手.exe.new
操作结果：已处理，删除文件
```

`HEUR:...Obfuscator` 是**启发式**判定，不是特征库匹配。触发它的组合是：

1. exe 里有一大段 base64 字符串（1.0.13 把 4116 个段写成 54KB base64）
2. 这个 exe 会**下载另一个 exe 并替换自己**（自动更新）

「大段 base64 + 自我替换」= 教科书级的 MSIL 混淆器特征。

而 1.0.12（151KB）没报，1.0.13（325KB）报 —— 体积暴增 174KB 正是那段 base64。

## 改法

- 4116 个 (start,end) 对 → 65536 个 /16 段的**位图**（8192 字节）
- Deflate 压缩后 base64（约 3~4KB，而不是 54KB）
- 拆成多个小字符串常量，编译器不会合并成一个大块

精度代价：/16 粒度意味着同一 /16 段里有一个国内 IP，整段都算国内。
对「判断该走哪条路」这个用途完全够用（判错只影响走哪条路，不影响能不能上）。
"""
import base64
import io
import struct
import zlib

# ── 从 APNIC 生成 /16 位图 ──
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

bits = bytearray(8192)      # 65536 个 /16 段，1 bit each
for s, e in rows:
    for s16 in range(s >> 16, (e >> 16) + 1):
        bits[s16 >> 3] |= (1 << (s16 & 7))

ones = sum(bin(b).count('1') for b in bits)
packed = zlib.compress(bytes(bits), 9)
b64 = base64.b64encode(packed).decode('ascii')
print('CN /16 blocks = %d, bitmap = 8192 B, deflate = %d B, base64 = %d chars'
      % (ones, len(packed), len(b64)))

# ── 生成 C# ──
CH = 120
parts = [b64[i:i + CH] for i in range(0, len(b64), CH)]
lit = '\n'.join('            "%s",' % p for p in parts).rstrip(',')

cs = '''using System;
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
        static readonly byte[] _bitmap = Build();

        /// <summary>base64(Deflate(8192 字节位图))，拆成多段避免编译器合并成大常量。</summary>
        static readonly string[] _chunks = new string[]
        {
''' + lit + '''
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

            int s16 = (b[0] << 8) | b[1];
            return (_bitmap[s16 >> 3] & (1 << (s16 & 7))) != 0;
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
'''

io.open(r'E:\肥鱼不乐园\borrow-net\ferry-client\src\CnIpRanges.cs', 'w',
        encoding='utf-8', newline='\n').write(cs)
print('written, C# size = %d bytes' % len(cs))

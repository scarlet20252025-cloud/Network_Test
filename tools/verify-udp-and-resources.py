"""修正 UDP 测试：BND.ADDR 返回 0.0.0.0 时要用代理服务器地址。

RFC 1928：UDP ASSOCIATE 的 BND.ADDR/BND.PORT 是「客户端应该发往的地址」。
服务端如果不知道自己的对外地址，可以返回 0.0.0.0（全零），
此时客户端应改用**它连接的 SOCKS 服务器地址**。

我们的服务端就是这样（bind 0.0.0.0，返回的 getsockname 是 0.0.0.0）。
所以测试脚本必须替换 —— 这是标准行为，不是 bug。

顺带修远端资源查询（PowerShell 引号在 ssh 里被吃掉了）。
"""
import base64
import socket
import struct
import subprocess
import time

RELAY = ('10.13.24.1', 1080)


def run_remote(ps, timeout=90):
    enc = base64.b64encode(ps.encode('utf-16-le')).decode()
    r = subprocess.run(
        ['ssh', '-o', 'ConnectTimeout=8', '-o', 'BatchMode=yes',
         '-o', 'LogLevel=ERROR', 'pcs',
         'powershell -NoProfile -EncodedCommand ' + enc],
        capture_output=True, text=True, timeout=timeout)
    out = (r.stdout or '') + (r.stderr or '')
    if '#< CLIXML' in out:
        out = out.split('#< CLIXML')[0]
    return out


RES_PS = r'''
$p = Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
     Where-Object { $_.CommandLine -and $_.CommandLine -like '*ferry-socks5*' }
if ($p) {
  $pr = Get-Process -Id $p.ProcessId
  Write-Output ($pr.ProcessId.ToString() + '|' + $pr.Threads.Count + '|' +
                $pr.HandleCount + '|' + [math]::Round($pr.WorkingSet64/1MB, 1))
} else {
  Write-Output 'NOT RUNNING'
}
'''


def resources():
    out = run_remote(RES_PS).strip()
    # 可能有多行，取最后一行含 | 的
    for line in reversed(out.splitlines()):
        if '|' in line:
            p = line.strip().split('|')
            try:
                return {'pid': int(p[0]), 'threads': int(p[1]),
                        'handles': int(p[2]), 'memMB': float(p[3])}
            except (ValueError, IndexError):
                continue
    return None


def udp_dns_test():
    """完整的 UDP ASSOCIATE + DNS 查询。"""
    s = socket.create_connection(RELAY, 15)
    s.settimeout(15)
    s.sendall(b'\x05\x01\x00')
    if s.recv(2) != b'\x05\x00':
        raise RuntimeError('handshake')

    s.sendall(b'\x05\x03\x00\x01' + b'\x00' * 6)
    head = s.recv(4)
    if len(head) < 4 or head[1] != 0:
        raise RuntimeError('associate failed: %r' % head)

    atyp = head[3]
    if atyp == 1:
        rest = s.recv(6)
        relay_ip = '.'.join(str(x) for x in rest[:4])
        relay_port = int.from_bytes(rest[4:6], 'big')
    elif atyp == 3:
        n = s.recv(1)[0]
        hostb = s.recv(n)
        pb = s.recv(2)
        relay_ip = hostb.decode('ascii')
        relay_port = int.from_bytes(pb, 'big')
    elif atyp == 4:
        rest = s.recv(18)
        relay_ip = socket.inet_ntop(socket.AF_INET6, rest[:16])
        relay_port = int.from_bytes(rest[16:18], 'big')
    else:
        raise RuntimeError('bad atyp %d' % atyp)

    # 关键修正：0.0.0.0 / :: 时改用代理服务器地址（RFC 1928 允许）
    if relay_ip in ('0.0.0.0', '::', ''):
        relay_ip = RELAY[0]

    # DNS 查询
    q = struct.pack('>HHHHHH', 0x1234, 0x0100, 1, 0, 0, 0)
    q += b'\x06github\x03com\x00'
    q += struct.pack('>HH', 1, 1)
    pkt = b'\x00\x00\x00\x01' + socket.inet_aton('223.5.5.5') + \
          (53).to_bytes(2, 'big') + q

    u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    u.settimeout(10)
    try:
        t0 = time.time()
        u.sendto(pkt, (relay_ip, relay_port))
        data, _ = u.recvfrom(4096)
        dt = round((time.time() - t0) * 1000, 1)
        # 解析：跳过 SOCKS5 UDP 头
        atyp2 = data[3]
        off = 4 + (4 if atyp2 == 1 else (1 + data[4] if atyp2 == 3 else 16)) + 2
        dns = data[off:]
        txid = struct.unpack('>H', dns[:2])[0] if len(dns) >= 2 else -1
        rcode = dns[3] & 0x0F if len(dns) >= 4 else -1
        ancount = struct.unpack('>H', dns[6:8])[0] if len(dns) >= 8 else -1
        return {'relay': '%s:%d' % (relay_ip, relay_port), 'ms': dt,
                'total': len(data), 'txid': txid, 'rcode': rcode,
                'answers': ancount}
    finally:
        u.close()
        s.close()


print('=== 0. 基线 ===')
b = resources()
print('  %s' % b)

print()
print('=== 1. UDP ASSOCIATE + 真实 DNS 查询 ===')
try:
    r = udp_dns_test()
    ok = (r['txid'] == 0x1234 and r['rcode'] == 0 and r['answers'] > 0)
    print('  relay=%s  %sms  reply=%dB' % (r['relay'], r['ms'], r['total']))
    print('  txid=0x%04x rcode=%d answers=%d' % (r['txid'], r['rcode'], r['answers']))
    print('  [%s] UDP 链路正常' % ('OK' if ok else 'FAIL'))
except Exception as e:
    print('  ERR %s' % str(e)[:100])

print()
print('=== 2. 连续 30 次 UDP 关联（看是否回收）===')
okc = 0
for i in range(30):
    try:
        r = udp_dns_test()
        if r['txid'] == 0x1234 and r['answers'] > 0:
            okc += 1
    except Exception:
        pass
print('  %d/30 成功' % okc)
time.sleep(5)
a = resources()
print('  资源: %s' % a)
if b and a:
    print('  delta threads=%+d handles=%+d mem=%+.1fMB'
          % (a['threads'] - b['threads'], a['handles'] - b['handles'],
             a['memMB'] - b['memMB']))

print()
print('=== 3. 混合负载后资源 ===')
import concurrent.futures as cf


def one_tcp(i):
    try:
        s = socket.create_connection(RELAY, 15)
        s.settimeout(15)
        s.sendall(b'\x05\x01\x00')
        s.recv(2)
        hb = b'www.baidu.com'
        s.sendall(bytes([5, 1, 0, 3, len(hb)]) + hb + (443).to_bytes(2, 'big'))
        h = s.recv(4)
        ok = len(h) == 4 and h[1] == 0
        s.close()
        return ok
    except Exception:
        return False


t0 = time.time()
with cf.ThreadPoolExecutor(max_workers=30) as ex:
    results = list(ex.map(one_tcp, range(100)))
print('  TCP 100 并发: %d ok in %.2fs' % (sum(results), time.time() - t0))
time.sleep(4)
c = resources()
print('  资源: %s' % c)
if a and c:
    print('  delta threads=%+d handles=%+d mem=%+.1fMB'
          % (c['threads'] - a['threads'], c['handles'] - a['handles'],
             c['memMB'] - a['memMB']))

"""长期稳定性 + 泄漏检测（修正资源查询）。

前面所有资源查询都返回 None —— 因为 `powershell -Command "..."` 里
内层单引号被 ssh 吃了。改用 -EncodedCommand + 空格分隔输出。
"""
import base64
import concurrent.futures as cf
import socket
import struct
import subprocess
import time

RELAY = ('10.13.24.1', 1080)

RES_PS = r'''
$p = Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
     Where-Object { $_.CommandLine -and $_.CommandLine -like '*ferry-socks5*' }
if ($p) {
  # Get-Process 返回的 Process 对象属性是 Id，不是 ProcessId
  # （ProcessId 是 CimInstance 上的）。写错会得到空字符串，
  # 输出变成 'R,,7,317,38.6' —— 解析端就会错位。
  $pr = Get-Process -Id $p.ProcessId
  Write-Output ('R,' + $pr.Id + ',' + $pr.Threads.Count + ',' +
                $pr.HandleCount + ',' + [math]::Round($pr.WorkingSet64/1MB, 1))
} else {
  Write-Output 'R,NONE'
}
'''
_RES_ENC = base64.b64encode(RES_PS.encode('utf-16-le')).decode()


def resources():
    try:
        r = subprocess.run(
            ['ssh', '-o', 'ConnectTimeout=8', '-o', 'BatchMode=yes',
             '-o', 'LogLevel=ERROR', 'pcs',
             'powershell -NoProfile -EncodedCommand ' + _RES_ENC],
            capture_output=True, text=True, timeout=60)
    except subprocess.TimeoutExpired:
        return None
    for line in (r.stdout or '').splitlines():
        line = line.strip()
        if line.startswith('R,'):
            # 逗号分隔：空格分隔时 'R ' + pid 会拼出 'R  7 ...'，
            # split() 段数不定，按索引取容易错位。
            p = [x.strip() for x in line.split(',')[1:]]
            if len(p) >= 4 and p[0] != 'NONE':
                try:
                    return {'pid': int(p[0]), 'threads': int(p[1]),
                            'handles': int(p[2]), 'memMB': float(p[3])}
                except (ValueError, IndexError):
                    continue
    return None


def socks_connect(host, port, timeout=15):
    s = socket.create_connection(RELAY, timeout)
    s.settimeout(timeout)
    s.sendall(b'\x05\x01\x00')
    if s.recv(2) != b'\x05\x00':
        s.close()
        raise RuntimeError('hs')
    hb = host.encode('ascii')
    s.sendall(bytes([5, 1, 0, 3, len(hb)]) + hb + port.to_bytes(2, 'big'))
    h = s.recv(4)
    if len(h) < 4 or h[1] != 0:
        s.close()
        raise RuntimeError('conn')
    atyp = h[3]
    if atyp == 1:
        s.recv(6)
    elif atyp == 3:
        n = s.recv(1)[0]
        s.recv(n + 2)
    elif atyp == 4:
        s.recv(18)
    return s


def udp_dns():
    s = socket.create_connection(RELAY, 15)
    s.settimeout(15)
    s.sendall(b'\x05\x01\x00')
    s.recv(2)
    s.sendall(b'\x05\x03\x00\x01' + b'\x00' * 6)
    h = s.recv(4)
    if h[1] != 0:
        s.close()
        raise RuntimeError('assoc')
    atyp = h[3]
    if atyp == 1:
        rest = s.recv(6)
        ip = '.'.join(str(x) for x in rest[:4])
        port = int.from_bytes(rest[4:6], 'big')
    else:
        s.close()
        raise RuntimeError('atyp %d' % atyp)
    if ip in ('0.0.0.0', ''):
        ip = RELAY[0]

    q = struct.pack('>HHHHHH', 0x1234, 0x0100, 1, 0, 0, 0)
    q += b'\x06github\x03com\x00' + struct.pack('>HH', 1, 1)
    pkt = b'\x00\x00\x00\x01' + socket.inet_aton('223.5.5.5') + \
          (53).to_bytes(2, 'big') + q
    u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    u.settimeout(10)
    try:
        u.sendto(pkt, (ip, port))
        data, _ = u.recvfrom(4096)
        return len(data) > 20
    finally:
        u.close()
        s.close()


print('=== 基线 ===')
b = resources()
print('  %s' % b)
if not b:
    print('  [X] 读不到资源，后续对比无意义')
    raise SystemExit(1)

print()
print('=== 阶段 1：300 次 TCP 建连 ===')
t0 = time.time()


def one_tcp(_):
    try:
        s = socks_connect('www.baidu.com', 443)
        s.close()
        return True
    except Exception:
        return False


with cf.ThreadPoolExecutor(max_workers=40) as ex:
    res = list(ex.map(one_tcp, range(300)))
print('  %d/300 ok in %.2fs' % (sum(res), time.time() - t0))
time.sleep(4)
p1 = resources()
print('  %s' % p1)

print()
print('=== 阶段 2：200 次 UDP 关联 ===')
okc = 0
t0 = time.time()
for _ in range(200):
    try:
        if udp_dns():
            okc += 1
    except Exception:
        pass
print('  %d/200 ok in %.2fs' % (okc, time.time() - t0))
time.sleep(6)
p2 = resources()
print('  %s' % p2)

print()
print('=== 阶段 3：静置 20 秒（等回收）===')
time.sleep(20)
p3 = resources()
print('  %s' % p3)

print()
print('=== 泄漏分析 ===')
def delta(a, c, k):
    if not a or not c:
        return '?'
    return '%+d' % (c[k] - a[k])

print('  threads:  base=%s -> tcp=%s -> udp=%s -> idle=%s'
      % (b['threads'], p1['threads'] if p1 else '?',
         p2['threads'] if p2 else '?', p3['threads'] if p3 else '?'))
print('  handles:  base=%s -> tcp=%s -> udp=%s -> idle=%s'
      % (b['handles'], p1['handles'] if p1 else '?',
         p2['handles'] if p2 else '?', p3['handles'] if p3 else '?'))
print('  memMB:    base=%.1f -> tcp=%.1f -> udp=%.1f -> idle=%.1f'
      % (b['memMB'], p1['memMB'] if p1 else 0,
         p2['memMB'] if p2 else 0, p3['memMB'] if p3 else 0))

verdict = []
if p3:
    if p3['threads'] - b['threads'] > 20:
        verdict.append('threads 增长 %d，可能有泄漏' % (p3['threads'] - b['threads']))
    if p3['handles'] - b['handles'] > 100:
        verdict.append('handles 增长 %d，可能有泄漏' % (p3['handles'] - b['handles']))
    if p3['memMB'] - b['memMB'] > 30:
        verdict.append('内存增长 %.1fMB，可能有泄漏' % (p3['memMB'] - b['memMB']))
print()
print('  结论: %s' % ('; '.join(verdict) if verdict else '静置后资源回到基线附近，无泄漏迹象'))

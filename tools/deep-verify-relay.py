"""深度验证：数据传输 / 限速 / UDP / 长时间稳定性 / task 泄漏。

前面的压力测试只「建连不发数据」，那测不到：
  · 竞速赢家之后的数据管道是否正常（pipe 有没有正确接上）
  · 限速是否仍然生效（大流量下 GCRA 有没有被绕过）
  · 输掉的那条连接有没有被真正关闭（不关会泄漏 socket）
  · UDP 路径有没有被影响
"""
import base64
import socket
import ssl
import subprocess
import time

RELAY = ('10.13.24.1', 1080)
PY = r'C:\Users\PCserver\AppData\Local\Python\pythoncore-3.14-64\python.exe'


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
  Write-Host ($pr.ProcessId.ToString() + '|' + $pr.Threads.Count + '|' +
              $pr.HandleCount + '|' + [math]::Round($pr.WorkingSet64/1MB, 1))
} else {
  Write-Host 'NOT RUNNING'
}
'''


def resources():
    out = run_remote(RES_PS).strip()
    if '|' not in out:
        return None
    parts = out.split('|')
    return {'pid': int(parts[0]), 'threads': int(parts[1]),
            'handles': int(parts[2]), 'memMB': float(parts[3])}


def socks5_open(host, port, timeout=20):
    s = socket.create_connection(RELAY, timeout)
    s.settimeout(timeout)
    s.sendall(b'\x05\x01\x00')
    if s.recv(2) != b'\x05\x00':
        s.close()
        raise RuntimeError('handshake')
    hb = host.encode('ascii')
    s.sendall(bytes([5, 1, 0, 3, len(hb)]) + hb + port.to_bytes(2, 'big'))
    head = s.recv(4)
    if len(head) < 4 or head[1] != 0:
        s.close()
        raise RuntimeError('connect: %r' % head)
    atyp = head[3]
    if atyp == 1:
        s.recv(6)
    elif atyp == 3:
        n = s.recv(1)[0]
        s.recv(n + 2)
    elif atyp == 4:
        s.recv(18)
    return s


def https_get(host, path='/', limit=200000, timeout=25):
    """经中继做一次真实 HTTPS GET，返回 (status, bytes, seconds)。"""
    t0 = time.time()
    s = socks5_open(host, 443, timeout)
    try:
        ctx = ssl.create_default_context()
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE
        ss = ctx.wrap_socket(s, server_hostname=host)
        req = ('GET %s HTTP/1.1\r\nHost: %s\r\nUser-Agent: ferry-test\r\n'
               'Connection: close\r\n\r\n' % (path, host))
        ss.sendall(req.encode())
        buf = b''
        while len(buf) < limit:
            chunk = ss.recv(65536)
            if not chunk:
                break
            buf += chunk
        ss.close()
        status = 0
        first = buf.split(b'\r\n', 1)[0].decode('latin1', 'ignore')
        if first.startswith('HTTP/'):
            try:
                status = int(first.split()[1])
            except (IndexError, ValueError):
                pass
        return status, len(buf), round(time.time() - t0, 3)
    finally:
        try:
            s.close()
        except Exception:
            pass


print('=== 0. 基线资源 ===')
b = resources()
print('  %s' % b)

print()
print('=== 1. 真实 HTTPS 数据传输（不是只建连）===')
for host in ('www.baidu.com', 'www.google.com', 'github.com', 'www.youtube.com'):
    try:
        st, n, sec = https_get(host)
        print('  %-20s HTTP %-4s %6d bytes  %.2fs' % (host, st, n, sec))
    except Exception as e:
        print('  %-20s ERR %s' % (host, str(e)[:50]))

print()
print('=== 2. 大流量下的限速（下载门户安装包 16.9MB）===')
try:
    t0 = time.time()
    s = socks5_open('10.13.24.1', 8080, timeout=60)
    s.sendall(b'GET /download HTTP/1.1\r\nHost: 10.13.24.1\r\nConnection: close\r\n\r\n')
    total = 0
    while True:
        chunk = s.recv(65536)
        if not chunk:
            break
        total += len(chunk)
        if time.time() - t0 > 60:
            break
    s.close()
    sec = time.time() - t0
    mb = total / 1048576.0
    print('  %d bytes (%.2f MB) in %.2fs = %.2f MB/s' % (total, mb, sec, mb / sec))
    print('  （限速设定 10 MB/s，应 <= 11）')
except Exception as e:
    print('  ERR %s' % str(e)[:80])

print()
print('=== 3. UDP 路径是否还正常 ===')
try:
    # 用 Python 直接发一次 UDP ASSOCIATE + DNS 查询
    s = socket.create_connection(RELAY, 15)
    s.settimeout(15)
    s.sendall(b'\x05\x01\x00')
    s.recv(2)
    s.sendall(b'\x05\x03\x00\x01' + b'\x00' * 6)
    head = s.recv(4)
    if len(head) >= 10:
        pass
    # 读 BND
    atyp = head[3]
    if atyp == 1:
        addr = s.recv(6)
        relay_ip = '.'.join(str(x) for x in addr[:4])
        relay_port = int.from_bytes(addr[4:6], 'big')
    else:
        relay_ip, relay_port = None, None
    print('  ASSOCIATE ok, relay=%s:%s' % (relay_ip, relay_port))

    # 发一个 DNS 查询
    if relay_ip and relay_port:
        import struct
        q = struct.pack('>HHHHHH', 0x1234, 0x0100, 1, 0, 0, 0)
        q += b'\x07aliyun\x03com\x00'
        q += struct.pack('>HH', 1, 1)
        # SOCKS5 UDP 头
        pkt = b'\x00\x00\x00\x01' + socket.inet_aton('223.5.5.5') + \
              (53).to_bytes(2, 'big') + q
        u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        u.settimeout(8)
        u.sendto(pkt, (relay_ip, relay_port))
        data, _ = u.recvfrom(2048)
        print('  UDP DNS reply %d bytes (header %d)' % (len(data), data[3] if len(data) > 3 else -1))
        u.close()
    s.close()
except Exception as e:
    print('  ERR %s' % str(e)[:80])

print()
print('=== 4. 资源复查（看有没有泄漏）===')
time.sleep(4)
a = resources()
print('  %s' % a)
if b and a:
    print('  delta threads: %+d' % (a['threads'] - b['threads']))
    print('  delta handles: %+d' % (a['handles'] - b['handles']))
    print('  delta memMB  : %+.1f' % (a['memMB'] - b['memMB']))

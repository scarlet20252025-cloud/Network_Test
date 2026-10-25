"""并发压力测试：竞速逻辑在大量并发下是否泄漏 task / socket。

要观察的：
  1. 200 个并发连接能否全部成功（不崩、不超时）
  2. 中继进程的线程/句柄数是否稳定（泄漏会一直涨）
  3. 结束后 1080 端口仍可用（没被耗尽）
  4. 混合国内外目标时路由是否正确

用法：先确保远端中继在跑（10.13.24.1:1080）。
"""
import concurrent.futures as cf
import http.client
import socket
import ssl
import time
import urllib.request

PROXY = ('10.13.24.1', 1080)

TARGETS = [
    ('www.baidu.com', 443, 'direct'),
    ('www.bilibili.com', 443, 'direct'),
    ('www.google.com', 443, 'clash'),
    ('www.youtube.com', 443, 'clash'),
    ('github.com', 443, 'clash'),
]


def socks5_connect(host, port, timeout=15):
    """完整走一遍 SOCKS5 CONNECT，返回 socket。"""
    s = socket.create_connection(PROXY, timeout)
    s.settimeout(timeout)
    # 握手
    s.sendall(b'\x05\x01\x00')
    resp = s.recv(2)
    if resp != b'\x05\x00':
        s.close()
        raise RuntimeError('handshake failed: %r' % resp)
    # CONNECT
    hb = host.encode('ascii')
    req = bytes([5, 1, 0, 3, len(hb)]) + hb + port.to_bytes(2, 'big')
    s.sendall(req)
    head = s.recv(4)
    if len(head) < 4 or head[1] != 0:
        s.close()
        raise RuntimeError('connect failed: %r' % head)
    atyp = head[3]
    if atyp == 1:
        s.recv(6)
    elif atyp == 3:
        n = s.recv(1)[0]
        s.recv(n + 2)
    elif atyp == 4:
        s.recv(18)
    return s


def one(args):
    host, port, expect = args
    t0 = time.time()
    try:
        s = socks5_connect(host, port)
        # 只建连不发数据 —— 这正好测「竞速建连」这一层
        s.close()
        return (host, 'ok', round(time.time() - t0, 3))
    except Exception as e:
        return (host, 'ERR:%s' % str(e)[:40], round(time.time() - t0, 3))


def measure_remote():
    """读远端中继进程的线程/句柄数。"""
    import subprocess
    ps = (
        "$p = Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" | "
        "Where-Object { $_.CommandLine -and $_.CommandLine -like '*ferry-socks5*' }; "
        "if ($p) { $pr = Get-Process -Id $p.ProcessId; "
        "$pr.ProcessId.ToString() + '|' + $pr.Threads.Count + '|' + $pr.HandleCount + '|' + "
        "[math]::Round($pr.WorkingSet64/1MB,1) } else { 'none' }"
    )
    r = subprocess.run(
        ['ssh', '-o', 'ConnectTimeout=8', '-o', 'BatchMode=yes',
         '-o', 'LogLevel=ERROR', 'pcs',
         'powershell -NoProfile -Command "' + ps + '"'],
        capture_output=True, text=True, timeout=60)
    return (r.stdout or '').strip()


print('=== 基线 ===')
base = measure_remote()
print('  remote relay (pid|threads|handles|memMB): %s' % base)

# ── 单连接正确性 ──
print()
print('=== 单连接（5 个目标各 3 次）===')
for host, port, expect in TARGETS:
    times = []
    okc = 0
    for _ in range(3):
        h, st, t = one((host, port, expect))
        if st == 'ok':
            okc += 1
            times.append(t)
    avg = round(sum(times) / len(times), 3) if times else '-'
    print('  %-20s %d/3 ok  avg=%ss  (expect %s)' % (host, okc, avg, expect))

# ── 并发 200 ──
print()
print('=== 并发 200（混合目标）===')
jobs = []
for i in range(200):
    jobs.append(TARGETS[i % len(TARGETS)])

t0 = time.time()
ok = err = 0
by_host = {}
with cf.ThreadPoolExecutor(max_workers=50) as ex:
    for host, st, t in ex.map(one, jobs):
        if st == 'ok':
            ok += 1
        else:
            err += 1
            by_host.setdefault(host, []).append(st)
elapsed = round(time.time() - t0, 2)
print('  ok=%d err=%d  elapsed=%ss' % (ok, err, elapsed))
if by_host:
    for h, errs in by_host.items():
        print('    %-20s %d err, e.g. %s' % (h, len(errs), errs[0]))

# ── 压力后资源 ──
print()
print('=== 压力后资源 ===')
time.sleep(3)
after = measure_remote()
print('  remote relay: %s' % after)

# ── 压力后仍可用 ──
print()
print('=== 压力后复查 ===')
for host, port, expect in TARGETS:
    h, st, t = one((host, port, expect))
    print('  %-20s %s %ss' % (host, st, t))

print()
print('=== 结论 ===')
if err == 0:
    print('  200 并发全部成功，无失败')
else:
    print('  有 %d 个失败，需要看上面的错误分布' % err)

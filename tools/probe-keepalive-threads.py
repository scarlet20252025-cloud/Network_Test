"""探针：HTTP/1.1 keep-alive 连接是否被服务端及时回收（2026-10-31 新增）。

为什么需要它：
    门户在 2026-10-30 把 `protocol_version` 从默认的 HTTP/1.0 改成了 1.1
    （手机浏览器对 1.0 响应会白屏 / 一直转圈）。但 1.1 默认**开启 keep-alive**：
    一个请求处理完后，`ThreadingMixIn` 起的线程会阻塞在 `rfile.readline()`
    等下一个请求，直到客户端断开或 socket 超时。

    `BaseHTTPRequestHandler` 的 `timeout` 默认是 `None` —— 也就是**永不超时**。
    于是每个「连上、发一个请求、然后不说话」的客户端都会永久占住一个线程。
    手机浏览器切后台、局域网扫描器、任何代理探测都会造成这种连接。

    这个脚本用「开 N 条连接、各发一个请求、然后静默保持」来量：
      · 基线线程数 → 保持 N 条 → 线程数增量
      · 等待超过 socket 超时后，线程数是否回落

用法：
    # 先起一个隔离实例
    cd portal && python -u portal-server.py --port 18091 --db test-stab.db
    # 再跑探针（默认 40 条连接，保持 25 秒）
    python tools/probe-keepalive-threads.py --port 18091
    python tools/probe-keepalive-threads.py --port 18091 --conns 80 --hold 40

退出码：0 = 线程被回收（正常）；1 = 线程没有回收（有泄漏）。
"""
import argparse
import socket
import subprocess
import sys
import time

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass


def server_pid(port):
    """按监听端口找服务端 PID。"""
    out = subprocess.run(['netstat', '-ano'], capture_output=True, text=True,
                         errors='ignore').stdout
    for line in out.splitlines():
        if ':%d ' % port in line and 'LISTENING' in line:
            return int(line.split()[-1])
    return None


def thread_count(pid):
    """读进程线程数。用 PowerShell 的 ThreadCount，避免依赖 psutil。"""
    ps = ('(Get-Process -Id %d -ErrorAction Stop).Threads.Count' % pid)
    r = subprocess.run(['powershell', '-NoProfile', '-Command', ps],
                       capture_output=True, text=True, errors='ignore')
    try:
        return int(r.stdout.strip())
    except ValueError:
        return None


def open_idle_conns(host, port, n, path='/version.json'):
    """开 n 条 keep-alive 连接，各发一个完整请求并读完响应，然后**不关闭**。"""
    conns = []
    for _ in range(n):
        try:
            s = socket.create_connection((host, port), timeout=8)
            s.sendall(('GET %s HTTP/1.1\r\nHost: %s:%d\r\n\r\n'
                       % (path, host, port)).encode())
            # 读满一个响应（读到 Content-Length 指定的长度即可，保持连接打开）
            buf = b''
            while b'\r\n\r\n' not in buf:
                chunk = s.recv(4096)
                if not chunk:
                    break
                buf += chunk
            head, _, rest = buf.partition(b'\r\n\r\n')
            length = 0
            for line in head.split(b'\r\n'):
                if line.lower().startswith(b'content-length:'):
                    length = int(line.split(b':', 1)[1].strip())
            while len(rest) < length:
                chunk = s.recv(4096)
                if not chunk:
                    break
                rest += chunk
            conns.append(s)          # 故意不 close，也不 shutdown
        except Exception as e:
            print('  [warn] 第 %d 条连接失败: %s' % (len(conns) + 1, e))
    return conns


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--host', default='127.0.0.1')
    ap.add_argument('--port', type=int, default=18091)
    ap.add_argument('--conns', type=int, default=40)
    ap.add_argument('--hold', type=float, default=25.0,
                    help='保持静默的秒数；应大于服务端 socket 超时')
    args = ap.parse_args()

    pid = server_pid(args.port)
    if not pid:
        print('找不到 %d 端口的监听进程，先起实例' % args.port)
        return 2
    print('服务端 PID = %d' % pid)

    base = thread_count(pid)
    print('基线线程数 : %s' % base)

    print('打开 %d 条 keep-alive 连接（各发一个请求后静默保持）...' % args.conns)
    conns = open_idle_conns(args.host, args.port, args.conns)
    print('已建立 : %d 条' % len(conns))

    time.sleep(2)
    after = thread_count(pid)
    print('连接后线程数: %s  (增量 %s)' % (after, (after or 0) - (base or 0)))

    print('静默保持 %.0f 秒，观察是否回收...' % args.hold)
    time.sleep(args.hold)
    later = thread_count(pid)
    print('保持后线程数: %s  (相对基线增量 %s)' % (later, (later or 0) - (base or 0)))

    # 还活着的连接数：能 sendall 成功且 recv 不立刻 EOF 说明服务端仍持有
    alive = 0
    for s in conns:
        try:
            s.settimeout(1.0)
            s.sendall(b'GET /version.json HTTP/1.1\r\nHost: x\r\n\r\n')
            alive += 1
        except Exception:
            pass
    for s in conns:
        try:
            s.close()
        except Exception:
            pass
    print('仍可复用（服务端未关）: %d / %d' % (alive, len(conns)))

    leak = (later or 0) - (base or 0)
    print()
    if leak <= max(2, args.conns // 4):
        print('[OK] 线程已回收，增量 %d 在噪声范围内' % leak)
        return 0
    print('[FAIL] 线程未回收，增量 %d（%d 条静默连接被永久占用）' % (leak, len(conns)))
    return 1


if __name__ == '__main__':
    sys.exit(main())

"""门户 HTTP 层健壮性回归：畸形请求头 + 空闲连接回收 + 并发上限。

为什么单独一个脚本：
    2026-10-31 把门户从 HTTP/1.0 改成 1.1（手机浏览器打不开的真正原因）时，
    引出了三个**只在真机/真网络下才暴露**的问题，curl 全绿也看不出来：

    1. HTTP/1.1 默认开 keep-alive，而 `BaseHTTPRequestHandler.timeout` 是 None，
       于是「连上、发一个请求、然后不说话」的连接会**永久占住一个线程**
       （ThreadingMixIn 是一连接一线程）。实测 40 条静默连接 → 线程 7→47，
       20 秒后仍 44。手机切后台、扫描器、curl 探测都会制造这种连接。

    2. `read_form` 里 `int(self.headers.get('Content-Length'))` 在 try 之外，
       `Content-Length: abc` 直接 500 + 打整段 traceback。

    3. 加了 timeout 之后，`log_message` 里的 `'/api/' in args[0]` 遇到
       `log_error("Request timed out: %r", e)` 传进来的异常对象会抛
       TypeError（"not a container or iterable"），每条超时连接刷一段 traceback。

    这三个都不是功能问题，是**服务端可用性**问题，普通页面验证看不出来，
    所以单独一个脚本盯它们。

前提：本地测试实例已在跑（用独立库，别指向线上）：
    cd portal
    python -u portal-server.py --port 18091 --db test-stab.db

用法：
    python tools/verify-portal-http.py
    python tools/verify-portal-http.py --port 18091 --conns 40 --hold 30

退出码：0 = 全过；1 = 有失败。
"""
import argparse
import re
import socket
import subprocess
import sys
import time

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

ok = fail = 0


def check(name, cond, extra=''):
    global ok, fail
    if cond:
        ok += 1
        print('  [OK]   %s %s' % (name, extra))
    else:
        fail += 1
        print('  [FAIL] %s %s' % (name, extra))


# ── 底层收发 ──

def raw(host, port, req, wait=8, maxbytes=8192):
    """发一段原始请求，返回 (状态行, 头部文本, 是否含 Content-Length, 原始字节数)。"""
    s = socket.create_connection((host, port), timeout=wait)
    try:
        s.sendall(req)
        buf = b''
        try:
            while len(buf) < maxbytes:
                d = s.recv(4096)
                if not d:
                    break
                buf += d
        except (socket.timeout, ConnectionResetError):
            pass
    finally:
        try:
            s.close()
        except Exception:
            pass
    head = buf.split(b'\r\n\r\n', 1)[0].decode('utf-8', 'replace')
    status = head.split('\r\n')[0] if head else ''
    return status, head, ('content-length' in head.lower()), len(buf)


def server_pid(port):
    out = subprocess.run(['netstat', '-ano'], capture_output=True, text=True,
                         errors='ignore').stdout
    for line in out.splitlines():
        if ':%d ' % port in line and 'LISTENING' in line:
            return int(line.split()[-1])
    return None


def thread_count(pid):
    r = subprocess.run(
        ['powershell', '-NoProfile', '-Command',
         '(Get-Process -Id %d -ErrorAction Stop).Threads.Count' % pid],
        capture_output=True, text=True, errors='ignore')
    try:
        return int(r.stdout.strip())
    except ValueError:
        return None


def open_idle_conns(host, port, n, path='/version.json'):
    """开 n 条 keep-alive 连接，各发一个请求读完响应，然后**故意不关闭**。"""
    conns = []
    for _ in range(n):
        try:
            s = socket.create_connection((host, port), timeout=8)
            s.sendall(('GET %s HTTP/1.1\r\nHost: %s:%d\r\n\r\n'
                       % (path, host, port)).encode())
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
                    try:
                        length = int(line.split(b':', 1)[1].strip())
                    except ValueError:
                        length = 0
            while len(rest) < length:
                chunk = s.recv(4096)
                if not chunk:
                    break
                rest += chunk
            conns.append(s)
        except Exception:
            pass
    return conns


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--host', default='127.0.0.1')
    ap.add_argument('--port', type=int, default=18091)
    ap.add_argument('--conns', type=int, default=40)
    ap.add_argument('--hold', type=float, default=30.0,
                    help='保持静默的秒数，必须大于 Handler.timeout（20s）')
    ap.add_argument('--skip-threads', action='store_true',
                    help='跳过线程回收检查（跨机器跑时拿不到 PID 就用它）')
    args = ap.parse_args()
    H, P = args.host, args.port

    print('=== 1. 畸形 Content-Length 不该 500 ===')
    for tag, hdr in [('非数字 abc', b'abc'),
                     ('负数 -5', b'-5'),
                     ('超大 99999999', b'99999999'),
                     # 纯空格会被 strip 成空串 → 当零长度处理，不该挂住。
                     # 注意别测 " 12 " 这种「声明了 body 却不发」的用例 ——
                     # 服务端在 read(12) 上阻塞是**正确**行为，不是 bug。
                     ('纯空格', b'   ')]:
        st, head, has_cl, n = raw(
            H, P,
            b'POST /feedback HTTP/1.1\r\nHost: x\r\nContent-Length: ' + hdr +
            b'\r\nContent-Type: application/x-www-form-urlencoded\r\n\r\n')
        code = st.split(' ')[1] if ' ' in st else '???'
        check('%s 不是 5xx' % tag, not code.startswith('5'),
              '(%s)' % (st or '无响应'))
        check('%s 带 Content-Length' % tag, has_cl)

    print()
    print('=== 2. 正常请求不受影响 ===')
    st, head, has_cl, n = raw(H, P, b'GET /version.json HTTP/1.1\r\nHost: x\r\n\r\n')
    check('GET /version.json 200', ' 200 ' in st, '(%s)' % st)
    check('响应是 HTTP/1.1', st.startswith('HTTP/1.1'), '(%s)' % st)
    check('带 Content-Length', has_cl)

    print()
    print('=== 3. 204 / 302 必须显式声明零长度 ===')
    st, head, has_cl, n = raw(H, P, b'GET /favicon.ico HTTP/1.1\r\nHost: x\r\n\r\n')
    check('favicon 204', ' 204 ' in st, '(%s)' % st)
    check('204 带 Content-Length', has_cl)
    st, head, has_cl, n = raw(H, P, b'GET /portal HTTP/1.1\r\nHost: x\r\n\r\n')
    check('/portal 302 回首页', ' 302 ' in st and 'Location: /' in head, '(%s)' % st)
    check('302 带 Content-Length', has_cl)

    if args.skip_threads:
        print()
        print('=== 4. 空闲连接回收（已跳过）===')
    else:
        pid = server_pid(P)
        if not pid:
            print()
            print('  [skip] 拿不到 %d 端口的 PID，跳过线程回收检查' % P)
        else:
            print()
            print('=== 4. 空闲 keep-alive 连接必须被回收 ===')
            base = thread_count(pid)
            conns = open_idle_conns(H, P, args.conns)
            time.sleep(2)
            peak = thread_count(pid)
            print('      基线线程 %s → 建连后 %s（%d 条连接）'
                  % (base, peak, len(conns)))
            print('      静默保持 %.0f 秒（Handler.timeout=20s）...' % args.hold)
            time.sleep(args.hold)
            after = thread_count(pid)
            print('      保持后线程 %s' % after)
            for s in conns:
                try:
                    s.close()
                except Exception:
                    pass
            grew = (peak or 0) - (base or 0)
            leaked = (after or 0) - (base or 0)
            check('连接确实占用了线程', grew >= max(2, args.conns // 2),
                  '(+%d)' % grew)
            check('超时后线程回收', leaked <= max(3, args.conns // 4),
                  '(残留 +%d)' % leaked)

    print()
    print('=' * 50)
    print('结果: %d 通过, %d 失败' % (ok, fail))
    return 0 if fail == 0 else 1


if __name__ == '__main__':
    sys.exit(main())

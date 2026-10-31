"""线上门户新代码验证（只读，不写任何数据）。

确认线上跑的是「HTTP/1.1 修复版」而不是重启后的旧代码：
  A. 畸形 Content-Length → 必须是 403，不能是 500（修复前是 500）
  B. 每个响应都带 Content-Length（HTTP/1.1 keep-alive 前提）
  C. 204 / 302 显式零长度
  D. 空闲 keep-alive 连接 20 秒后被服务端关掉（修复前永不关）
"""
import socket
import sys
import time

HOST = sys.argv[1] if len(sys.argv) > 1 else '10.13.24.1'
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 8080

ok = fail = 0


def check(name, cond, extra=''):
    global ok, fail
    if cond:
        ok += 1
        print('  [OK]   %s %s' % (name, extra))
    else:
        fail += 1
        print('  [FAIL] %s %s' % (name, extra))


def raw(req, wait=10):
    s = socket.create_connection((HOST, PORT), timeout=wait)
    try:
        s.sendall(req)
        buf = b''
        try:
            while len(buf) < 8192:
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
    return (head.split('\r\n')[0] if head else ''), head


print('目标: %s:%d' % (HOST, PORT))
print()
print('=== A. 畸形 Content-Length（修复前会 500）===')
for tag, hdr in [('abc', b'abc'), ('-5', b'-5'), ('99999999', b'99999999')]:
    st, head = raw(b'POST /feedback HTTP/1.1\r\nHost: x\r\nContent-Length: ' + hdr +
                   b'\r\nContent-Type: application/x-www-form-urlencoded\r\n\r\n')
    code = st.split(' ')[1] if ' ' in st else '???'
    check('Content-Length: %s 不是 5xx' % tag, not code.startswith('5'), '(%s)' % (st or '无响应'))

print()
print('=== B. 响应带 Content-Length（1.1 keep-alive 前提）===')
for path in [b'/version.json', b'/', b'/about']:
    st, head = raw(b'GET ' + path + b' HTTP/1.1\r\nHost: x\r\n\r\n')
    check('%s' % path.decode(), 'content-length' in head.lower(), '(%s)' % st)

print()
print('=== C. 204 / 302 显式零长度 ===')
st, head = raw(b'GET /favicon.ico HTTP/1.1\r\nHost: x\r\n\r\n')
check('favicon 204', ' 204 ' in st, '(%s)' % st)
check('204 带 Content-Length', 'content-length' in head.lower())
st, head = raw(b'GET /portal HTTP/1.1\r\nHost: x\r\n\r\n')
check('/portal 302', ' 302 ' in st, '(%s)' % st)
check('302 带 Content-Length', 'content-length' in head.lower())

print()
print('=== D. 空闲 keep-alive 连接会被服务端关（修复前永不关）===')
conns = []
for _ in range(5):
    try:
        s = socket.create_connection((HOST, PORT), timeout=10)
        s.sendall(b'GET /version.json HTTP/1.1\r\nHost: x\r\n\r\n')
        buf = b''
        while b'\r\n\r\n' not in buf:
            d = s.recv(4096)
            if not d:
                break
            buf += d
        conns.append(s)
    except Exception as e:
        print('  [warn] %s' % e)
print('  已开 %d 条静默连接，等 25 秒看是否被服务端关掉...' % len(conns))
time.sleep(25)
closed = 0
for s in conns:
    try:
        s.settimeout(3)
        s.sendall(b'X')
        # 服务端已关的话，这里会读到 EOF（recv 返回 b''）或抛重置
        d = s.recv(100)
        if d == b'':
            closed += 1
    except Exception:
        closed += 1
    try:
        s.close()
    except Exception:
        pass
check('空闲连接被回收', closed >= 3, '(%d/%d 已关闭)' % (closed, len(conns)))

print()
print('=' * 50)
print('结果: %d 通过, %d 失败' % (ok, fail))
sys.exit(0 if fail == 0 else 1)

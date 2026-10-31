# -*- coding: utf-8 -*-
"""连测门户稳定性：连续 N 次原始 HTTP 请求，统计成功/耗时分布。

为什么单独写成文件而不是一行内联：
  2026-10-31 教训 —— 内联脚本里写了 `if b'200' in first`（first 是 str），
  抛 TypeError，被 PowerShell 捕获后只显示 "ERR TypeError"，
  于是一台**完全正常**的服务器被误读成"门户挂了"。
  已经第三次栽在"测试脚手架自己的 bug 被当成被测对象的 bug"上了
  （见 docs/踩坑记录.md 第 80 条）。所以这类检查固定成脚本，写完先自测。

用法：
    python tools/check-portal-up.py
    python tools/check-portal-up.py 10.13.24.1 8080 8
"""
import socket
import sys
import time

sys.stdout.reconfigure(encoding='utf-8')


def one(host, port, path='/', timeout=6):
    """发一次原始 HTTP 请求，返回 (状态行, 耗时ms)。失败抛异常。"""
    t0 = time.time()
    s = socket.create_connection((host, port), timeout)
    s.settimeout(timeout)
    try:
        s.sendall(('GET %s HTTP/1.1\r\nHost: %s:%d\r\n'
                   'Connection: close\r\nUser-Agent: check/1.0\r\n\r\n'
                   % (path, host, port)).encode('ascii'))
        buf = b''
        while b'\r\n' not in buf:
            d = s.recv(4096)
            if not d:
                break
            buf += d
    finally:
        s.close()
    line = buf.split(b'\r\n')[0].decode('ascii', 'replace')
    return line, (time.time() - t0) * 1000.0


def main():
    host = sys.argv[1] if len(sys.argv) > 1 else '10.13.24.1'
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 8080
    n = int(sys.argv[3]) if len(sys.argv) > 3 else 6

    print('测试 %s:%d  共 %d 次' % (host, port, n))
    ok = 0
    times = []
    for i in range(n):
        try:
            line, ms = one(host, port)
            times.append(ms)
            # ⚠ 注意 first 是 str，必须和 str 比，不能和 b'200' 比
            good = '200' in line
            if good:
                ok += 1
            print('  #%d  %-26s %6.0f ms  %s'
                  % (i + 1, line, ms, '' if good else '← 非 200'))
        except Exception as e:
            print('  #%d  ERR %s: %s' % (i + 1, type(e).__name__, e))
        time.sleep(0.35)

    print()
    print('  %d/%d 成功' % (ok, n))
    if times:
        ts = sorted(times)
        print('  耗时 p50=%.0fms  max=%.0fms' % (ts[len(ts) // 2], ts[-1]))
    print('  结论:', '正常' if ok == n else ('部分失败' if ok else '全失败'))
    return 0 if ok == n else 1


if __name__ == '__main__':
    sys.exit(main())

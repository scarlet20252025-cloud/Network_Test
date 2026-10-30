# -*- coding: utf-8 -*-
"""抓一个 URL 的原始响应头，确认 HTTP 协议版本与关键头。

为什么需要它：
  2026-10-30 用户报「手机浏览器打不开门户，但同地址 curl 能通」，
  根因之一是 Python BaseHTTPRequestHandler 默认回 HTTP/1.0。
  用 curl -I 看的是 curl 自己的解读，这里直接读 socket 原始字节，
  看服务器到底吐了什么。

用法：
    python tools/peek-headers.py http://10.13.24.1:8080/
    python tools/peek-headers.py http://10.13.24.1:8080/ /get-android
"""
import sys
import urllib.parse
import socket

sys.stdout.reconfigure(encoding='utf-8')


def peek(url):
    u = urllib.parse.urlparse(url)
    host = u.hostname or '127.0.0.1'
    port = u.port or 80
    path = u.path or '/'
    if u.query:
        path += '?' + u.query
    req = ('GET %s HTTP/1.1\r\nHost: %s:%d\r\n'
           'Connection: close\r\nUser-Agent: peek/1.0\r\n\r\n'
           % (path, host, port)).encode('ascii')
    s = socket.create_connection((host, port), 8)
    s.settimeout(8)
    s.sendall(req)
    buf = b''
    try:
        while b'\r\n\r\n' not in buf and len(buf) < 65536:
            d = s.recv(4096)
            if not d:
                break
            buf += d
    finally:
        s.close()
    head = buf.split(b'\r\n\r\n')[0].decode('utf-8', 'replace')
    return head


def main():
    urls = sys.argv[1:] or ['http://10.13.24.1:8080/']
    for url in urls:
        print('=' * 60)
        print('GET', url)
        print('=' * 60)
        try:
            print(peek(url))
        except Exception as e:
            print('ERR:', type(e).__name__, e)
        print()

        # 关键判据
        try:
            head = peek(url)
        except Exception:
            continue
        first = head.split('\r\n')[0]
        print('  协议行     :', first)
        print('  HTTP/1.1 ? :', 'YES' if first.startswith('HTTP/1.1') else 'NO')
        print('  Content-Len:', 'YES' if 'Content-Length' in head else 'NO')
        print('  Transfer-Enc:', 'YES（chunked，也算合法）' if 'Transfer-Encoding' in head else 'NO')
        print()

    return 0


if __name__ == '__main__':
    sys.exit(main())

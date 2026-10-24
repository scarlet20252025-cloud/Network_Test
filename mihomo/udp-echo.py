#!/usr/bin/env python3
"""UDP echo 服务端 —— 用来验证「本机能否直接 UDP 到达远端」。

为什么需要它：SOCKS5 的 UDP ASSOCIATE 要求客户端直接向服务端返回的
中继地址发 UDP 包。如果中间有防火墙只放行 TCP（校园网很常见），
整条 UDP 链路就是死的，再怎么改 mihomo 配置都没用。
所以先起一个最小的 echo，从另一端打一发，30 秒内就知道行不行。

用法（服务端，远端跑）：
    python udp-echo.py --port 10820

它会把收到的每个包原样回发，并打印来源。Ctrl+C 退出。
"""
import argparse
import socket
import sys
import time


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--port', type=int, default=10820)
    ap.add_argument('--host', default='0.0.0.0')
    ap.add_argument('--seconds', type=float, default=0,
                    help='自动退出秒数，0 = 一直跑')
    args = ap.parse_args()

    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind((args.host, args.port))
    print(f'UDP echo 已监听 {args.host}:{args.port}', flush=True)

    deadline = time.time() + args.seconds if args.seconds else None
    s.settimeout(1.0)
    n = 0
    while True:
        if deadline and time.time() > deadline:
            print(f'到时间退出，共处理 {n} 个包', flush=True)
            break
        try:
            data, peer = s.recvfrom(65535)
        except socket.timeout:
            continue
        except KeyboardInterrupt:
            break
        n += 1
        print(f'  #{n} 收到 {len(data)}B 来自 {peer[0]}:{peer[1]} → 回发', flush=True)
        try:
            s.sendto(data, peer)
        except Exception as e:
            print(f'  回发失败: {e}', flush=True)
    s.close()
    return 0


if __name__ == '__main__':
    sys.exit(main())

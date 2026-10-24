#!/usr/bin/env python3
"""UDP 探针 —— 从本机向远端 UDP 端口打一发，看能不能收到回包。

配合 udp-echo.py 用：远端起 echo，本机跑这个。

为什么不用 ping：ping 是 ICMP，很多网络对 ICMP 和 UDP 的策略完全不同
（实测有环境 ICMP 全通但 UDP 全断）。这里要测的是 UDP 数据报。

用法：
    python udp-probe.py --host 10.13.24.1 --port 10820
"""
import argparse
import socket
import sys
import time


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--host', required=True)
    ap.add_argument('--port', type=int, default=10820)
    ap.add_argument('--count', type=int, default=5)
    ap.add_argument('--timeout', type=float, default=3.0)
    ap.add_argument('--payload', default='FERRY-UDP-PROBE')
    args = ap.parse_args()

    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.settimeout(args.timeout)

    print(f'向 {args.host}:{args.port} 发 {args.count} 个 UDP 包（超时 {args.timeout}s）')
    ok = 0
    rtts = []
    for i in range(args.count):
        token = f'{args.payload}-{i}'.encode()
        t0 = time.time()
        try:
            s.sendto(token, (args.host, args.port))
            data, peer = s.recvfrom(65535)
            dt = (time.time() - t0) * 1000
            rtts.append(dt)
            if data == token:
                ok += 1
                print(f'  #{i+1} 通  {dt:.1f}ms  {len(data)}B 来自 {peer[0]}:{peer[1]}')
            else:
                print(f'  #{i+1} 回包内容不匹配: {data[:40]!r}')
        except socket.timeout:
            print(f'  #{i+1} 超时')
        except Exception as e:
            print(f'  #{i+1} 错误: {type(e).__name__}: {e}')
        time.sleep(0.3)
    s.close()

    print('-' * 50)
    if ok:
        avg = sum(rtts) / len(rtts)
        print(f'结论：UDP 可达 {ok}/{args.count}，平均 {avg:.1f}ms —— UDP 中继可行')
        return 0
    print(f'结论：UDP 不可达 0/{args.count} —— 这条路走不通')
    return 1


if __name__ == '__main__':
    sys.exit(main())

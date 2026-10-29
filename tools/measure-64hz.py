# -*- coding: utf-8 -*-
"""按 CS2 的节奏（64Hz）精确测量链路往返 —— 这是唯一能复现游戏卡顿的测法。

为什么必须这样测：
    之前用 ping 测（1 秒 1 包）得到 20ms，看起来正常；
    但 CS2 是 64 tick，每秒 64 包，实测 p50=61ms、max=88ms —— 和游戏里
    显示的 89ms 完全对上。低频测不出问题，高频才暴露。

原理：
    在 pcs(10.13.24.1) 上跑一个 TCP echo（端口 19100），本机建立**一次**连接，
    然后每 1/64 秒发一个 32 字节包并等回包。全程只有一个连接，所以测到的
    是纯网络往返，不含 TCP 握手开销。

前置：
    1. pcs 上的 echo 服务要起着：
         scp tools/start-echo.cmd pcs:C:/Windows/Temp/start-echo.cmd
         ssh pcs 'C:\Windows\Temp\start-echo.cmd'
       （或者用计划任务 FerriEcho，见 docs/无线延迟排查-20261029.md）
    2. 本机能 ping 通 10.13.24.1

用法：
    python tools\measure-64hz.py             # 默认 128 包
    python tools\measure-64hz.py 256         # 更多样本
    python tools\measure-64hz.py 128 100     # 指定频率 100Hz
"""
import socket
import statistics
import sys
import time

sys.stdout.reconfigure(encoding='utf-8')

HOST = '10.13.24.1'
PORT = 19100
COUNT = int(sys.argv[1]) if len(sys.argv) > 1 else 128
HZ = int(sys.argv[2]) if len(sys.argv) > 2 else 64


def measure():
    s = socket.socket()
    s.settimeout(3)
    try:
        s.connect((HOST, PORT))
    except Exception as e:
        print('连不上 %s:%d —— echo 服务起了吗？(%s)' % (HOST, PORT, e))
        return None
    vals = []
    lost = 0
    for _ in range(COUNT):
        t0 = time.perf_counter()
        try:
            s.sendall(b'X' * 32)
            s.recv(64)
            vals.append((time.perf_counter() - t0) * 1000)
        except Exception:
            lost += 1
            vals.append(-1)
        time.sleep(1.0 / HZ)
    s.close()
    return vals, lost


def main():
    print('=== %dHz × %d 包（模拟 CS2 的 %d tick）===' % (HZ, COUNT, HZ))
    r = measure()
    if not r:
        return 1
    vals, lost = r
    ok = [v for v in vals if v >= 0]
    if not ok:
        print('全部丢失')
        return 1

    print()
    for i in range(0, len(vals), 16):
        print('  ' + ' '.join('%5.1f' % v if v >= 0 else '    L' for v in vals[i:i + 16]))

    v = sorted(ok)
    print()
    print('p50=%.1fms  p90=%.1fms  p99=%.1fms  max=%.1fms'
          % (v[len(v) // 2], v[int(len(v) * 0.9)], v[int(len(v) * 0.99)], v[-1]))
    print('平均=%.1fms  标准差=%.1fms  丢失=%d/%d'
          % (statistics.mean(ok), statistics.stdev(ok) if len(ok) > 1 else 0, lost, COUNT))

    # 分类：慢包（>40ms）占比，以及它们是否落在固定区间
    slow = [x for x in ok if x > 40]
    if slow:
        slow_sorted = sorted(slow)
        print('慢包(>40ms): %d/%d = %.0f%%   区间 %.0f-%.0fms'
              % (len(slow), len(ok), len(slow) * 100.0 / len(ok),
                 slow_sorted[0], slow_sorted[-1]))

    print()
    print('=== 判读（CS2 在 64tick 下的体感）===')
    p50 = v[len(v) // 2]
    if p50 <= 20:
        print('  PASS：p50 ≤ 20ms，游戏应流畅。' )
    elif p50 <= 35:
        print('  可玩：p50 %.0fms，会有轻微顿挫。' % p50)
    else:
        print('  FAIL：p50 %.0fms，这就是游戏里卡的原因。' % p50)
    jitter = statistics.stdev(ok) if len(ok) > 1 else 0
    if jitter > 15:
        print('  抖动 %.0fms 偏大（>15），即使平均不差也会觉得卡。' % jitter)
    return 0


if __name__ == '__main__':
    sys.exit(main())

"""量「逐行写日志」到底会不会卡住 asyncio 事件循环 —— 用循环自身的唤醒延迟当尺子。

为什么需要这个脚本（2026-10-30）：
  `ferry-socks5-server.py` 的 UDP 关联日志曾经是「每建一条关联就 print(flush=True)」，
  交接文档和 CHANGELOG 都把「延迟从 10ms 爬到 50-70ms 再骤降」归因于它，
  理由是「刷盘期间整个事件循环停摆」。

  但 `print(flush=True)` **不等于** fsync —— 它只把用户态缓冲交给 OS，
  数据进的是页缓存。实测（pcs，Python 3.14）：

      write + flush + os.fsync : 2.967 ms/行   <- 这才是"刷盘"
      write + flush            : 0.0033 ms/行  <- 中继实际做的事，差 900 倍
      write（缓冲）            : 0.0005 ms/行

  所以「逐行 flush 拖垮事件循环」这个结论必须先验证再照抄到 TCP 日志上。
  本脚本不问「写一行花多久」，而是问真正重要的问题：
  **在持续写日志的情况下，事件循环里一个 10ms 定时器的实际唤醒延迟是多少。**

  这才是「停摆」的可观测定义：如果写日志会阻塞循环，
  tick 的 lag 分布就会跟着日志速率一起变差；如果不会，lag 与速率无关。

用法（在 pcs 或本机都行）：
    python tools/bench-log-flush.py                    # 默认 1/20/100 行每秒
    python tools/bench-log-flush.py --rates 1 20 200 --secs 6
    python tools/bench-log-flush.py --mode fsync       # 对照：真刷盘

输出：每种 (速率, 模式) 组合的 tick 延迟 p50/p99/max，单位毫秒。
判据：p99 与 max 不随日志速率上升 = 写日志不是瓶颈。
"""
import argparse
import asyncio
import os
import statistics
import sys
import tempfile
import time

LINE = '[12:00:00] 10.140.83.29 -> api.ymeng.cc:443 done, 392770 bytes (direct)\n'
TICK = 0.010          # 10ms，和中继里各种 await 的超时量级接近


async def measure(rate, secs, mode, out_path):
    """以 rate 行/秒写日志，同时量 10ms ticker 的唤醒延迟。"""
    f = open(out_path, 'a', encoding='utf-8')
    lags = []
    stop = False

    async def ticker():
        # 关键：用 loop.time() 而不是 time.time()，避免被系统时钟调整干扰
        loop = asyncio.get_running_loop()
        prev = loop.time()
        while not stop:
            await asyncio.sleep(TICK)
            now = loop.time()
            lags.append((now - prev - TICK) * 1000.0)
            prev = now

    async def writer():
        loop = asyncio.get_running_loop()
        if rate <= 0:
            return
        interval = 1.0 / rate
        nextt = loop.time()
        while not stop:
            f.write(LINE)
            if mode in ('flush', 'fsync'):
                f.flush()
            if mode == 'fsync':
                os.fsync(f.fileno())
            nextt += interval
            delay = nextt - loop.time()
            if delay > 0:
                await asyncio.sleep(delay)

    t_tick = asyncio.ensure_future(ticker())
    t_write = asyncio.ensure_future(writer())
    await asyncio.sleep(secs)
    stop = True
    await asyncio.gather(t_tick, t_write, return_exceptions=True)
    f.close()
    # 丢掉前几个样本（启动抖动）
    lags = lags[3:]
    if not lags:
        return None
    lags.sort()
    return {
        'n': len(lags),
        'p50': lags[len(lags) // 2],
        'p99': lags[min(len(lags) - 1, int(len(lags) * 0.99))],
        'max': lags[-1],
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--rates', type=float, nargs='+', default=[0, 1, 20, 100])
    ap.add_argument('--secs', type=float, default=6.0)
    ap.add_argument('--mode', default='flush', choices=['buffered', 'flush', 'fsync'])
    args = ap.parse_args()

    out = os.path.join(tempfile.gettempdir(), 'ferry-bench-logflush.log')
    if os.path.exists(out):
        os.remove(out)

    print('tick=%dms  mode=%s  secs=%.1f  python=%s'
          % (TICK * 1000, args.mode, args.secs, sys.version.split()[0]))
    print('%-10s %-8s %8s %8s %8s' % ('行/秒', '样本', 'p50(ms)', 'p99(ms)', 'max(ms)'))
    base = None
    for r in args.rates:
        res = asyncio.run(measure(r, args.secs, args.mode, out))
        if res is None:
            print('%-10.0f %-8s (无样本)' % (r, '-'))
            continue
        if base is None:
            base = res
        print('%-10.0f %-8d %8.2f %8.2f %8.2f'
              % (r, res['n'], res['p50'], res['p99'], res['max']))
    print()
    print('判据：p99/max 不随「行/秒」上升 → 写日志不是事件循环的瓶颈。')
    try:
        print('日志体积：%d 字节' % os.path.getsize(out))
        os.remove(out)
    except OSError:
        pass


if __name__ == '__main__':
    main()

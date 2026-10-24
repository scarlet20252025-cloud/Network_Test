"""验证 UDP 关联的登记 / 上限 / 回收。

不起真服务，直接 import 中继模块测类行为 —— 这样能精确控制时间。
"""
import asyncio
import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
MOD = os.path.join(ROOT, 'wireguard', 'ferry-socks5-server.py')

spec = importlib.util.spec_from_file_location('relay', MOD)
relay = importlib.util.module_from_spec(spec)
spec.loader.exec_module(relay)

ok = fail = 0


def check(name, cond, extra=''):
    global ok, fail
    if cond:
        ok += 1
        print('  [OK]   %s %s' % (name, extra))
    else:
        fail += 1
        print('  [FAIL] %s %s' % (name, extra))


async def main():
    print('=== 1. 关联登记与回调 ===')
    closed = []

    def on_close(r):
        closed.append(r)
        relay._relays.discard(r)

    r1 = relay.UdpRelay('10.1.1.1', 1234, on_close=on_close)
    await r1.start()
    relay._relays.add(r1)
    check('登记后计数 1', len(relay._relays) == 1, 'n=%d' % len(relay._relays))
    check('中继端口已分配', r1.port > 0, 'port=%d' % r1.port)

    # 两个 socket 各占一个端口
    p1 = r1.client_sock.getsockname()[1]
    p2 = r1.target_sock.getsockname()[1]
    check('占用 2 个不同端口', p1 != p2, '%d / %d' % (p1, p2))

    print('\n=== 2. close 会回调并退登记 ===')
    r1.close()
    await asyncio.sleep(0.1)
    check('回调被调用', len(closed) == 1)
    check('计数归零', len(relay._relays) == 0, 'n=%d' % len(relay._relays))

    print('\n=== 3. 重复 close 不会重复回调 ===')
    r1.close()
    await asyncio.sleep(0.05)
    check('仍然只回调 1 次', len(closed) == 1, 'n=%d' % len(closed))

    print('\n=== 4. 硬上限常量合理 ===')
    check('上限 > 0', relay.RELAY_HARD_LIMIT > 0, str(relay.RELAY_HARD_LIMIT))
    # Windows 动态端口 16384 个，每个关联 2 个 → 理论上限约 8192
    check('上限不超过端口容量的一半',
          relay.RELAY_HARD_LIMIT <= 8000,
          '%d（端口上限约 8192）' % relay.RELAY_HARD_LIMIT)

    print('\n=== 5. 回收逻辑：过期关联会被挑出来 ===')
    # 造 3 个关联，其中 2 个时间戳很老
    import time
    r2 = relay.UdpRelay('10.1.1.2', 0, on_close=on_close)
    await r2.start()
    r3 = relay.UdpRelay('10.1.1.3', 0, on_close=on_close)
    await r3.start()
    r4 = relay.UdpRelay('10.1.1.4', 0, on_close=on_close)
    await r4.start()
    for r in (r2, r3, r4):
        relay._relays.add(r)
    r2.last_active = time.time() - 3600
    r3.last_active = time.time() - 3600
    r4.last_active = time.time()          # 刚活动过

    now_t = time.time()
    dead = [r for r in list(relay._relays)
            if not r.closed and now_t - r.last_active > relay.UDP_IDLE_TIMEOUT]
    check('挑出 2 个过期关联', len(dead) == 2, 'dead=%d' % len(dead))
    check('刚活动的没被挑中', r4 not in dead)

    for r in dead:
        r.close()
    await asyncio.sleep(0.1)
    check('回收后只剩活跃的', len(relay._relays) == 1, 'n=%d' % len(relay._relays))

    r4.close()
    await asyncio.sleep(0.1)
    check('全部关闭后归零', len(relay._relays) == 0, 'n=%d' % len(relay._relays))

    print('\n=== 6. 限速对象仍在（别把之前的改坏）===')
    check('RateTable 存在', hasattr(relay, 'RateTable'))
    check('GLOBAL 存在', hasattr(relay, 'GLOBAL'))
    check('RATES 存在', hasattr(relay, 'RATES'))

    print('\n' + '=' * 50)
    print('结果: %d 通过, %d 失败' % (ok, fail))
    return 0 if fail == 0 else 1


sys.exit(asyncio.run(main()))

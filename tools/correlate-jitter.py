# -*- coding: utf-8 -*-
"""抖动来源定位：对比「到网关」与「到局域网内另一台机器」的抖动是否同步。

为什么这个能定位：
  · 到网关(10.141.56.1) 和 到 pcs(10.13.24.1) 都要经过**同一个无线链路**，
    但网关是第一跳（AP 自己），pcs 在 AP 的另一侧。
  · 如果两边**同步抖动** → 抖动发生在「本机的无线发送路径」上（网卡/驱动）。
  · 如果只有网关抖、pcs 不抖 → 抖动发生在 AP 自身的处理上（与本机无关）。
  · 如果只有 pcs 抖、网关不抖 → 校园网内部问题。

用法： python tools\correlate-jitter.py
"""
import re
import statistics
import subprocess
import sys
import time

sys.stdout.reconfigure(encoding='utf-8')

TARGETS = [
    ('网关(AP)', '10.141.56.1'),
    ('pcs(局域网)', '10.13.24.1'),
]


def ping_once(host, size=32, timeout=2000):
    try:
        p = subprocess.run(['ping', '-n', '1', '-w', str(timeout), '-l', str(size), host],
                           capture_output=True, timeout=6)
        out = (p.stdout or b'').decode('gbk', 'replace')
        m = re.search(r'(?:time|时间)[=<>]\s*(\d+)\s*ms', out)
        if m:
            return int(m.group(1))
        if 'TTL' in out or 'TTL=' in out:
            return 0
        return None
    except Exception:
        return None


N = 20
print('=== 交替采样 %d 轮（每个目标各一次，背靠背）===' % N)
print('  这样两个目标落在同一个时间窗口内，抖动才可比。\n')

rows = []
for i in range(N):
    a = ping_once(TARGETS[0][1])
    b = ping_once(TARGETS[1][1])
    rows.append((a, b))
    mark = ''
    if a is not None and b is not None:
        if a >= 50 and b >= 50:
            mark = '  <<< 两边一起慢'
        elif a >= 50 and b < 50:
            mark = '  <<< 只有网关慢'
        elif b >= 50 and a < 50:
            mark = '  <<< 只有 pcs 慢'
    print('  %2d  网关 %-5s  pcs %-5s%s' % (i + 1, a, b, mark))
    time.sleep(0.05)

g = [r[0] for r in rows if r[0] is not None]
p = [r[1] for r in rows if r[1] is not None]

print()
print('=== 统计 ===')
for name, vals in ((TARGETS[0][0], g), (TARGETS[1][0], p)):
    if vals:
        s = sorted(vals)
        spikes = sum(1 for v in vals if v >= 50)
        print('  %-12s n=%d  p50=%d  min=%d  max=%d  尖峰(>=50)=%d/%d'
              % (name, len(vals), s[len(s) // 2], s[0], s[-1], spikes, len(vals)))

print()
print('=== 同步性分析 ===')
both_slow = only_g = only_p = both_fast = 0
for a, b in rows:
    if a is None or b is None:
        continue
    if a >= 50 and b >= 50:
        both_slow += 1
    elif a >= 50:
        only_g += 1
    elif b >= 50:
        only_p += 1
    else:
        both_fast += 1
total = both_slow + only_g + only_p + both_fast
if total:
    print('  两边一起慢: %d/%d (%.0f%%)' % (both_slow, total, both_slow * 100.0 / total))
    print('  只有网关慢: %d/%d' % (only_g, total))
    print('  只有 pcs 慢: %d/%d' % (only_p, total))
    print('  两边都快  : %d/%d' % (both_fast, total))

print()
print('=== 判读 ===')
if total == 0:
    print('  样本不足')
elif both_slow >= total * 0.5:
    print('  → 两边同步抖动：抖动在本机的无线发送路径（网卡驱动/固件）。')
    print('    这解释了为什么换 AP、换信道都没用。')
elif only_g >= total * 0.4:
    print('  → 主要是到网关这一段：AP 自身处理慢，与本机无关。')
elif only_p >= total * 0.4:
    print('  → 主要是到 pcs 这一段：校园网内部问题。')
else:
    print('  → 抖动分散，没有明显归属。可能是随机拥塞。')

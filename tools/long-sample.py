# -*- coding: utf-8 -*-
"""长时间采样：用足够的样本量判断「本机 vs 手机」到底有没有差异。

为什么需要它：之前十几次判断失败，根子都是**单次 15-20 个包的样本太小**。
同一个位置、同一分钟内，手机自己就会「抖一阵稳一阵」（实测 max 从 61ms
掉到 8.7ms）。用这种样本做对比，每次结论都不一样。

方法：每轮同时测「本机→网关」和「手机→网关」，各 20 包，跑 N 轮，
最后比较**中位数**和**尖峰比例**，而不是看某一次的 max。

用法：
    python tools\long-sample.py            # 默认 5 轮
    python tools\long-sample.py 10         # 10 轮
"""
import re
import statistics
import subprocess
import sys
import time

sys.stdout.reconfigure(encoding='utf-8')

ADB = r'C:\tmp\ptools\platform-tools\adb.exe'
GW = '10.141.56.1'
ROUNDS = int(sys.argv[1]) if len(sys.argv) > 1 else 5
PER_ROUND = 20


def pc_ping(n):
    vals = []
    for _ in range(n):
        try:
            p = subprocess.run(['ping', '-n', '1', '-w', '2000', '-l', '32', GW],
                               capture_output=True, timeout=6)
            out = (p.stdout or b'').decode('gbk', 'replace')
            m = re.search(r'(?:time|时间)[=<>]\s*(\d+)\s*ms', out)
            if m:
                vals.append(int(m.group(1)))
        except Exception:
            pass
        time.sleep(0.05)
    return vals


def phone_ping(n):
    """手机一次性发 n 包，解析 min/avg/max/mdev。"""
    try:
        p = subprocess.run([ADB, 'shell', 'ping -c %d -i 0.2 %s' % (n, GW)],
                           capture_output=True, timeout=90,
                           env={'MSYS_NO_PATHCONV': '1', 'PATH': ''})
        out = (p.stdout or b'').decode('utf-8', 'replace')
        m = re.search(r'min/avg/max/mdev\s*=\s*([\d.]+)/([\d.]+)/([\d.]+)/([\d.]+)', out)
        if m:
            return [float(x) for x in m.groups()]
    except Exception:
        pass
    return None


print('=== 长时间采样：%d 轮 × 每轮 %d 包 ===' % (ROUNDS, PER_ROUND))
print('  目的：用足量样本判断「本机 vs 手机」的差异是否真实存在\n')

pc_all = []
phone_avgs = []
phone_maxs = []

for i in range(ROUNDS):
    print('--- 第 %d/%d 轮 ---' % (i + 1, ROUNDS))
    pv = pc_ping(PER_ROUND)
    if pv:
        s = sorted(pv)
        p50 = s[len(s) // 2]
        spikes = sum(1 for v in pv if v >= 50)
        print('  本机: n=%d  p50=%d  min=%d  max=%d  尖峰=%d/%d'
              % (len(pv), p50, s[0], s[-1], spikes, len(pv)))
        pc_all.extend(pv)

    ph = phone_ping(PER_ROUND)
    if ph:
        mn, av, mx, md = ph
        print('  手机: avg=%.1f  min=%.1f  max=%.1f  mdev=%.1f' % (av, mn, mx, md))
        phone_avgs.append(av)
        phone_maxs.append(mx)
    else:
        print('  手机: 无响应')
    time.sleep(1)

print()
print('=== 汇总 ===')
if pc_all:
    s = sorted(pc_all)
    print('  本机 合计 n=%d  p50=%d  p90=%d  max=%d  尖峰比例=%.0f%%'
          % (len(pc_all), s[len(s) // 2], s[int(len(s) * 0.9)], s[-1],
             sum(1 for v in pc_all if v >= 50) * 100.0 / len(pc_all)))
if phone_avgs:
    print('  手机 avg 的中位数=%.1f  各轮 avg: %s'
          % (statistics.median(phone_avgs), ['%.1f' % x for x in phone_avgs]))
    print('  手机 max 各轮: %s' % ['%.1f' % x for x in phone_maxs])

print()
print('=== 判读 ===')
if pc_all and phone_avgs:
    pc_med = statistics.median(pc_all)
    ph_med = statistics.median(phone_avgs)
    print('  本机 p50 = %d ms' % pc_med)
    print('  手机 avg 中位数 = %.1f ms' % ph_med)
    ratio = pc_med / ph_med if ph_med else 0
    print('  比值 = %.1f 倍' % ratio)
    if ratio >= 3:
        print('  → 本机确实明显更差，差异是真实的。')
    elif ratio >= 1.5:
        print('  → 本机略差，但差距不大 —— 之前的单次对比夸大了差异。')
    else:
        print('  → 两者接近：问题不在本机，之前的结论被小样本误导了。')
elif pc_all:
    print('  只有本机数据，无法对比。')

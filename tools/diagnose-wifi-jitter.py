# -*- coding: utf-8 -*-
"""无线链路抖动诊断：判断是「网卡休眠」还是「信道干扰/上游拥塞」。

背景：这台机器（Realtek 8852BE，SSID GDPZ-student-5G）出现交替的
3ms / 70ms 延迟。已经把 IpsEn 从 1 改成 0（关省电），但复测没改善，
所以需要更细的判据来区分原因。

三种典型模式：
  A. 连续发包快、稀疏发包慢      → 网卡休眠窗口（信标周期 102.4ms）
  B. 两种都慢且抖                → 信道干扰 / 拥塞 / 信号问题
  C. 局域网快、公网慢            → 上游（运营商/校园网出口）问题

用法： python tools\diagnose-wifi-jitter.py
"""
import re
import statistics
import subprocess
import sys
import time

sys.stdout.reconfigure(encoding='utf-8')

TARGETS = {
    '网关 10.141.56.1': '10.141.56.1',
    'pcs 10.13.24.1': '10.13.24.1',
    '公网DNS 223.5.5.5': '223.5.5.5',
}


def ping_once(host, timeout=1500):
    """发一个包，返回 RTT 毫秒；失败返回 None。"""
    try:
        p = subprocess.run(['ping', '-n', '1', '-w', str(timeout), host],
                           capture_output=True, timeout=5)
        out = (p.stdout or b'').decode('gbk', 'replace')
        m = re.search(r'(?:time|时间)[=<]\s*(\d+)\s*ms', out)
        if m:
            return int(m.group(1))
        if re.search(r'(?:TTL|TTL=)', out):
            return 1
        return None
    except Exception:
        return None


def sample(host, n=15, gap_ms=0):
    vals = []
    for _ in range(n):
        v = ping_once(host)
        if v is not None:
            vals.append(v)
        if gap_ms:
            time.sleep(gap_ms / 1000.0)
    return vals


def stat(vals, label):
    if not vals:
        print('  %-22s 无响应' % label)
        return None
    p50 = statistics.median(vals)
    print('  %-22s n=%-3d p50=%-4.0f min=%-4d max=%-4d 抖动(σ)=%.1f'
          % (label, len(vals), p50, min(vals), max(vals),
             statistics.pstdev(vals) if len(vals) > 1 else 0))
    return p50


def main():
    print('=== 1. 连续发包（0ms 间隔，网卡保持唤醒）===')
    burst = {}
    for name, host in TARGETS.items():
        burst[name] = stat(sample(host, 15, 0), name)

    print()
    print('=== 2. 稀疏发包（120ms 间隔，撞信标周期）===')
    paced = {}
    for name, host in TARGETS.items():
        paced[name] = stat(sample(host, 15, 120), name)

    print()
    print('=== 3. 无线信号与链路速率 ===')
    try:
        p = subprocess.run(['netsh', 'wlan', 'show', 'interfaces'],
                           capture_output=True, timeout=15)
        out = (p.stdout or b'').decode('gbk', 'replace')
        for key in ('SSID', '信号', '接收速率', '发送速率', '通道'):
            m = re.search(key + r'\s*:\s*(.+)', out)
            if m:
                print('  %-10s %s' % (key, m.group(1).strip()))
    except Exception as e:
        print('  netsh 失败: %s' % e)

    print()
    print('=== 判读 ===')
    g = burst.get('网关 10.141.56.1')
    gp = paced.get('网关 10.141.56.1')
    l = burst.get('pcs 10.13.24.1')
    w = burst.get('公网DNS 223.5.5.5')

    if g is None:
        print('  网关无响应，无法判读')
        return

    if gp is not None and gp - g > 30:
        print('  → 模式 A：连续 %.0fms vs 稀疏 %.0fms，差 %.0fms' % (g, gp, gp - g))
        print('    典型的网卡休眠窗口。但 IpsEn 已经改成 0 了 ——')
        print('    如果仍是这个模式，说明驱动还有别的节能开关（PowerSaveMode）。')
    elif g > 30:
        print('  → 模式 B：连续发包本身就 %.0fms，不是休眠问题。' % g)
        print('    方向：信道干扰、信号弱、或者这块网卡/驱动本身有问题。')
    else:
        print('  → 网关延迟正常（%.0fms），无线侧没问题。' % g)

    if l is not None and w is not None:
        if w - l > 40:
            print('  → 局域网 %.0fms、公网 %.0fms：差距在**上游**（校园网出口/运营商）。' % (l, w))
        else:
            print('  → 局域网与公网延迟接近，瓶颈不在上游。')

    print()
    print('  提示：跑这组数据时如果正在打游戏/下载，结果会失真。')


if __name__ == '__main__':
    main()

"""分析 UI 卡顿的时间结构：是「周期性整块占用」还是「随机被抢占」。

为什么需要：probe-ui-spikes.py 只报「尖峰间隔中位数」，但如果一次阻塞
持续 200ms，20Hz 采样会连续测到 4-5 个慢样本，中位间隔就成了采样间隔
（0.05s），看起来像「持续卡」—— 分不清是周期性的还是随机的。

这个脚本做两件事：
  1. **成簇分析**：把连续慢样本合并成「阻塞事件」，看每次持续多久、多久一次
  2. **相位分析**：把每个阻塞事件的起点对 1 秒取模。如果全部聚集在
     某个固定相位，就是 DispatcherTimer（1 秒）里的工作；如果散开，
     就是外部抢占（其它进程抢 CPU）

用法：
    python tools/probe-ui-phase.py 渡口助手 40
"""
import ctypes
import ctypes.wintypes as wt
import statistics
import subprocess
import sys
import time

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

user32 = ctypes.WinDLL('user32', use_last_error=True)
SMTO_BLOCK = 0x0001
SMTO_ABORTIFHUNG = 0x0002
WM_NULL = 0x0000

SLOW_MS = 25.0          # 超过算「这个时刻 UI 忙」
SAMPLE_GAP = 0.02       # 20ms 采样


def find_pid(name):
    out = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq %s.exe' % name, '/FO', 'CSV'],
                         capture_output=True, text=True, errors='ignore').stdout
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) >= 2 and parts[0].lower() == (name + '.exe').lower():
            try:
                return int(parts[1])
            except ValueError:
                pass
    return None


def find_window_by_pid(pid):
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, lparam):
        p = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(hwnd):
            found.append(hwnd)
        return True

    user32.EnumWindows(cb, 0)
    return found


def main():
    name = sys.argv[1] if len(sys.argv) > 1 else '渡口助手'
    secs = float(sys.argv[2]) if len(sys.argv) > 2 else 40.0

    pid = find_pid(name)
    if not pid:
        print('找不到进程:', name)
        return 2
    wins = find_window_by_pid(pid)
    if not wins:
        print('没有可见窗口')
        return 2
    hwnd = wins[0]

    print('PID %d / hwnd 0x%X，采样 %.0f 秒 @ %.0fms'
          % (pid, hwnd, secs, SAMPLE_GAP * 1000))
    print('阈值 %.0f ms' % SLOW_MS)
    print()

    res = wt.DWORD()
    samples = []
    t_start = time.perf_counter()
    while time.perf_counter() - t_start < secs:
        t0 = time.perf_counter()
        ret = user32.SendMessageTimeoutW(hwnd, WM_NULL, 0, 0,
                                         SMTO_BLOCK | SMTO_ABORTIFHUNG, 2000,
                                         ctypes.byref(res))
        dt = (time.perf_counter() - t0) * 1000
        samples.append((t0 - t_start, dt if ret else 9999))
        time.sleep(SAMPLE_GAP)

    # ── 成簇：连续慢样本合并成一次「阻塞事件」──
    events = []
    cur = None
    for t, d in samples:
        if d > SLOW_MS:
            if cur is None:
                cur = [t, t, d, 1]
            else:
                cur[1] = t
                cur[2] = max(cur[2], d)
                cur[3] += 1
        else:
            if cur is not None:
                events.append(cur)
                cur = None
    if cur is not None:
        events.append(cur)

    n = len(samples)
    slow_n = sum(1 for _, d in samples if d > SLOW_MS)
    dur = samples[-1][0] - samples[0][0]
    print('样本 %d 个 / %.1f 秒，慢样本 %d（%.1f%%）'
          % (n, dur, slow_n, 100.0 * slow_n / max(1, n)))
    print('阻塞事件 %d 次' % len(events))
    print()

    if not events:
        print('没有阻塞事件 —— 现在不卡。请在「卡的时候」重跑。')
        return 0

    print('每次事件：起点 → 持续时长 → 峰值延迟')
    for t0, t1, peak, cnt in events[:30]:
        # 持续时长 ≈ 最后一次慢样本 - 第一次慢样本 + 一个采样间隔
        span = (t1 - t0) + SAMPLE_GAP
        print('  %7.2fs  %5.0f ms  (峰值 %6.1f, %d 个样本)'
              % (t0, span * 1000, peak, cnt))
    if len(events) > 30:
        print('  ... 还有 %d 次' % (len(events) - 30))

    print()
    spans = [(e[1] - e[0] + SAMPLE_GAP) * 1000 for e in events]
    print('事件持续时长：中位 %.0f ms，最长 %.0f ms'
          % (statistics.median(spans), max(spans)))

    if len(events) >= 3:
        gaps = [events[i + 1][0] - events[i][0] for i in range(len(events) - 1)]
        print('事件间隔：中位 %.2f s，最小 %.2f s，最大 %.2f s'
              % (statistics.median(gaps), min(gaps), max(gaps)))
        print()

        # ── 相位分析：对 1 秒取模，看是否聚集 ──
        phases = [e[0] % 1.0 for e in events]
        if len(phases) >= 3:
            mean = sum(phases) / len(phases)
            # 用圆周集中度（circular concentration）而不是普通标准差
            import math
            sx = sum(math.cos(2 * math.pi * p) for p in phases) / len(phases)
            sy = sum(math.sin(2 * math.pi * p) for p in phases) / len(phases)
            R = math.sqrt(sx * sx + sy * sy)
            print('相位聚集度 R = %.3f（1.0 = 全部同一相位，0 = 完全散开）' % R)
            if R > 0.5:
                mean_phase = (math.atan2(sy, sx) / (2 * math.pi)) % 1.0
                print('  ★ 强烈聚集在每秒的 %.2f 秒处 —— 是 1 秒 DispatcherTimer' % mean_phase)
                print('     （即 _fast.Tick → RefreshUi + GuardSystemProxy）')
            elif R > 0.3:
                print('  · 有一定聚集（%.2f 秒附近），可能是定时器 + 其它因素叠加' % mean_phase)
            else:
                print('  ★ 相位散开 —— 不是 1 秒定时器，更像外部抢占'
                      '（其它进程抢 CPU）或不定期的后台任务')

    print()
    if slow_n > n * 0.15:
        print('[FAIL] 慢样本超过 15%%，UI 基本不可用')
    else:
        print('[WARN] 有 %d 次阻塞' % len(events))
    return 0


if __name__ == '__main__':
    sys.exit(main())

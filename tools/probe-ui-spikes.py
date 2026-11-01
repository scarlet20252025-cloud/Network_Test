"""抓 UI 尖峰的时间分布，看它们是否周期性。

为什么需要：probe-ui-latency.py 只给出分位数，看不出「尖峰多久一次」。
如果尖峰是固定间隔（1 秒 / 5 秒 / 15 秒…），就能直接对上某个定时器，
不用再猜。

用法：
    python tools/probe-ui-spikes.py 渡口助手 30
"""
import ctypes
import ctypes.wintypes as wt
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
    secs = float(sys.argv[2]) if len(sys.argv) > 2 else 30.0
    THRESH = 25.0   # 超过这个算一次「可感知的顿挫」

    pid = find_pid(name)
    if not pid:
        print('找不到进程:', name)
        return 2
    wins = find_window_by_pid(pid)
    if not wins:
        print('没有可见窗口')
        return 2
    hwnd = wins[0]

    print('PID %d / hwnd 0x%X，采样 %.0f 秒，阈值 %.0f ms'
          % (pid, hwnd, secs, THRESH))
    print()

    res = wt.DWORD()
    samples = []      # (相对秒, 延迟ms)
    t_start = time.time()
    while time.time() - t_start < secs:
        t0 = time.perf_counter()
        r = user32.SendMessageTimeoutW(hwnd, WM_NULL, 0, 0,
                                       SMTO_BLOCK | SMTO_ABORTIFHUNG, 2000,
                                       ctypes.byref(res))
        dt = (time.perf_counter() - t0) * 1000
        samples.append((time.time() - t_start, dt if r else 9999))
        time.sleep(0.05)      # 20Hz 采样，比 UI 刷新率高

    spikes = [(t, d) for t, d in samples if d > THRESH]
    print('样本 %d，尖峰(>%.0fms) %d 个（%.1f%%）'
          % (len(samples), THRESH, len(spikes),
             100.0 * len(spikes) / max(1, len(samples))))
    print()

    if not spikes:
        print('没抓到尖峰 —— 换个时间点再试，或者窗口当时没被拖动')
        return 0

    print('尖峰明细（相对时间 → 延迟）：')
    for t, d in spikes[:40]:
        print('  %7.2fs  %6.1f ms' % (t, d))

    print()
    if len(spikes) >= 3:
        gaps = [spikes[i + 1][0] - spikes[i][0] for i in range(len(spikes) - 1)]
        gaps_sorted = sorted(gaps)
        med = gaps_sorted[len(gaps_sorted) // 2]
        print('尖峰间隔：中位数 %.2fs，最小 %.2fs，最大 %.2fs'
              % (med, min(gaps), max(gaps)))
        print()
        # 找最接近的定时器周期
        for period, label in [(1.0, '1 秒定时器 _fast (RefreshUi+GuardSystemProxy)'),
                              (5.0, '5 秒定时器 _hbTimer (SendHeartbeat)'),
                              (10.0, '10 秒 _slow (ProbeNet)'),
                              (15.0, '15 秒 有线探测 RefreshWiredCacheAsync'),
                              (3.0, '3 秒定时器'),
                              (0.8, '0.8 秒 restore 定时器')]:
            if abs(med - period) < period * 0.18:
                print('  ★ 间隔 %.2fs 最接近 %s' % (med, label))
        if med < 0.5:
            print('  ★ 间隔很短（%.2fs）—— 不是周期定时器，'
                  '更像是持续性的高频操作（渲染/GC/日志）' % med)

    print()
    slow = [d for _, d in samples if d > THRESH]
    if len(slow) >= len(samples) * 0.15:
        print('[FAIL] 超过 15%% 的采样都顿挫，UI 基本不可用')
    elif len(slow) >= 3:
        print('[WARN] 有 %d 次明显顿挫' % len(slow))
    else:
        print('[OK] 偶尔顿一下，可能只是启动/GC')
    return 0


if __name__ == '__main__':
    sys.exit(main())

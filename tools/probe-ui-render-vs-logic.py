"""判断 UI 卡顿是在「渲染层」还是「逻辑层」。

原理：
    把窗口最小化 → WPF 停止渲染和布局（但 DispatcherTimer 照常跑）。
      如果最小化后消息延迟恢复正常  → 卡在渲染/布局
      如果最小化后仍然卡            → 卡在定时器里的代码逻辑

用法：
    python tools/probe-ui-render-vs-logic.py 渡口助手
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
SW_MINIMIZE = 6
SW_RESTORE = 9


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


def sample(hwnd, secs, label):
    res = wt.DWORD()
    lat = []
    t_end = time.time() + secs
    while time.time() < t_end:
        t0 = time.perf_counter()
        ret = user32.SendMessageTimeoutW(hwnd, WM_NULL, 0, 0,
                                         SMTO_BLOCK | SMTO_ABORTIFHUNG, 2000,
                                         ctypes.byref(res))
        dt = (time.perf_counter() - t0) * 1000
        lat.append(dt if ret else 9999)
        time.sleep(0.03)

    lat.sort()
    n = len(lat)
    slow = sum(1 for x in lat if x > 25)
    print('%-22s p50=%5.1f  p90=%6.1f  max=%6.1f  slow=%d/%d'
          % (label + ':', lat[n // 2], lat[int(n * 0.9)], lat[-1], slow, n))
    return lat[n // 2], lat[int(n * 0.9)], slow, n


def main():
    name = sys.argv[1] if len(sys.argv) > 1 else '渡口助手'
    pid = find_pid(name)
    if not pid:
        print('找不到进程:', name)
        return 2
    wins = find_window_by_pid(pid)
    if not wins:
        print('没有可见窗口')
        return 2
    hwnd = wins[0]
    print('PID %d / hwnd 0x%X\n' % (pid, hwnd))

    print('=== 阶段 1：正常显示状态 ===')
    a = sample(hwnd, 12, '正常显示')

    print()
    print('=== 阶段 2：最小化（停渲染，定时器照跑）===')
    user32.ShowWindow(hwnd, SW_MINIMIZE)
    time.sleep(2)
    b = sample(hwnd, 12, '最小化')

    print()
    print('=== 阶段 3：恢复显示 ===')
    user32.ShowWindow(hwnd, SW_RESTORE)
    time.sleep(2)
    c = sample(hwnd, 12, '恢复显示')

    print()
    print('--- 结论 ---')
    # 用 slow 计数（>25ms 的样本数）判断，比 p90 稳
    sa, sb, sc = a[2] / a[3], b[2] / b[3], c[2] / c[3]
    print('顿挫比例：正常 %.0f%% / 最小化 %.0f%% / 恢复 %.0f%%'
          % (sa * 100, sb * 100, sc * 100))
    if sa > 0.1 and sb < sa * 0.4:
        print('★ 最小化后明显好转 → 卡在**渲染/布局**（WPF 绘制，不是定时器逻辑）')
    elif sa > 0.1 and sb > sa * 0.7:
        print('★ 最小化后一样卡 → 卡在**定时器里的代码**（RefreshUi 等逻辑）')
    elif sa <= 0.1:
        print('· 当前不卡，测不出差异。请在卡的时候重跑。')
    return 0


if __name__ == '__main__':
    sys.exit(main())

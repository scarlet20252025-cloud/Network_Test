"""量 WPF 窗口的消息循环延迟（UI 线程响应性）。

为什么用 SendMessageTimeout：
    拖动窗口卡顿 = UI 线程无法及时处理消息。往窗口发一个 WM_NULL
    并等它被处理，等的时间就是「UI 线程此刻有多忙」。
    这比读 CPU 占用准 —— CPU 60% 但有 Yield 也不卡，CPU 20% 一直阻塞才卡。

用法：
    python tools/probe-ui-latency.py 渡口助手 20
"""
import ctypes
import ctypes.wintypes as wt
import statistics
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


def find_window_by_pid(pid):
    """枚举顶层窗口，找出属于该 PID 的可见窗口。"""
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


def find_pid(name):
    import subprocess
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


def main():
    name = sys.argv[1] if len(sys.argv) > 1 else '渡口助手'
    secs = float(sys.argv[2]) if len(sys.argv) > 2 else 20.0

    pid = find_pid(name)
    if not pid:
        print('找不到进程:', name)
        return 2
    wins = find_window_by_pid(pid)
    print('PID %d，找到 %d 个可见窗口' % (pid, len(wins)))
    if not wins:
        print('没有可见窗口（可能最小化到托盘了）')
        return 2

    hwnd = wins[0]
    print('探测窗口句柄 0x%X，采样 %.0f 秒（每 100ms 一次）' % (hwnd, secs))
    print()

    res = wt.DWORD()
    lat = []
    win_rect = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(win_rect))
    print('窗口位置: (%d,%d) %dx%d' % (
        win_rect.left, win_rect.top,
        win_rect.right - win_rect.left, win_rect.bottom - win_rect.top))
    print()

    end = time.time() + secs
    while time.time() < end:
        t0 = time.perf_counter()
        r = user32.SendMessageTimeoutW(hwnd, WM_NULL, 0, 0,
                                       SMTO_BLOCK | SMTO_ABORTIFHUNG, 2000,
                                       ctypes.byref(res))
        dt = (time.perf_counter() - t0) * 1000
        if not r:
            lat.append(9999)   # 超时 = 完全卡死
        else:
            lat.append(dt)
        time.sleep(0.1)

    if not lat:
        print('没有样本')
        return 1

    ok = [x for x in lat if x < 9000]
    lat.sort()
    n = len(lat)
    print('UI 消息处理延迟（%d 个样本）' % n)
    if ok:
        ok.sort()
        m = len(ok)
        print('  p50  = %6.1f ms' % statistics.median(ok))
        print('  p90  = %6.1f ms' % ok[min(m - 1, int(m * 0.9))])
        print('  p99  = %6.1f ms' % ok[min(m - 1, int(m * 0.99))])
        print('  max  = %6.1f ms' % max(ok))
    print('  超时(>2s) = %d' % (n - len(ok)))
    print()

    if n - len(ok) > 0:
        print('[FAIL] UI 线程出现 %d 次完全无响应' % (n - len(ok)))
    elif ok and max(ok) > 200:
        print('[FAIL] 有 %d 次延迟 >200ms（拖动窗口会明显顿挫）'
              % sum(1 for x in ok if x > 200))
    elif ok and max(ok) > 80:
        print('[WARN] 最大延迟 %.0f ms，偏卡但不至于明显顿挫' % max(ok))
    else:
        print('[OK] UI 线程响应正常')
    return 0


if __name__ == '__main__':
    sys.exit(main())

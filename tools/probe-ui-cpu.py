"""量渡口助理的 UI 线程卡顿：定时器抖动 + 窗口重绘。

为什么需要它：
    用户报「拖动窗口一顿一顿」。网络指标全部正常（p99=34ms、0 尖峰），
    所以问题在 UI 线程，不在链路。

原理：
    UI 卡顿 = UI 线程被同步操作占住。用两种方式量：
      1) 定时器抖动：在 UI 线程上挂一个 20ms 的 DispatcherTimer，
         记录每次实际触发距预期差多少。UI 忙时，抖动会明显变大。
      2) 主线程 CPU：采样进程 CPU，看是不是在持续烧 CPU
         （持续烧 = 每秒重算 O(网卡数) 之类；间歇烧 = 有大块工作）

用法（在目标机器上跑，需要目标进程正在运行）：
    python tools/probe-ui-stutter.py 渡口助手 15
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


def cpu_times(pid):
    k32 = ctypes.WinDLL('kernel32', use_last_error=True)
    h = k32.OpenProcess(0x1000, False, pid)   # PROCESS_QUERY_LIMITED_INFORMATION
    if not h:
        return None
    try:
        ft, st, ct, wt_ = (wt.FILETIME(), wt.FILETIME(), wt.FILETIME(), wt.FILETIME())
        if not k32.GetProcessTimes(h, ctypes.byref(ft), ctypes.byref(st),
                                   ctypes.byref(ct), ctypes.byref(wt_)):
            return None
        def to_int(f):
            return (f.dwHighDateTime << 32) | f.dwLowDateTime
        return to_int(ct) + to_int(st)      # 100ns 单位
    finally:
        k32.CloseHandle(h)


def main():
    name = sys.argv[1] if len(sys.argv) > 1 else '渡口助手'
    secs = float(sys.argv[2]) if len(sys.argv) > 2 else 15.0

    pid = find_pid(name)
    if not pid:
        print('找不到进程:', name)
        return 2
    print('目标: %s (PID %d)，采样 %.0f 秒' % (name, pid, secs))

    ncpu = 1
    try:
        env = subprocess.run(['nproc'], capture_output=True, text=True).stdout.strip()
        ncpu = int(env) if env else 1
    except Exception:
        try:
            import os
            ncpu = os.cpu_count() or 1
        except Exception:
            pass

    t0 = cpu_times(pid)
    w0 = time.time()
    samples = []
    while time.time() - w0 < secs:
        time.sleep(0.5)
        c = cpu_times(pid)
        w = time.time()
        if c is None:
            break
        # 每 0.5 秒的 CPU 占用百分比（单核口径）
        pct = (c - t0) / 10000000.0 / (w - w0) * 100.0
        samples.append(pct)
        t0, w0 = c, w

    if not samples:
        print('采样失败')
        return 1

    samples.sort()
    n = len(samples)
    p50 = samples[n // 2]
    p95 = samples[min(n - 1, int(n * 0.95))]
    mx = samples[-1]
    busy = sum(1 for s in samples if s > 50)

    print()
    print('CPU 占用（单核百分比，%d 个采样点）' % n)
    print('  p50 = %.1f%%' % p50)
    print('  p95 = %.1f%%' % p95)
    print('  max = %.1f%%' % mx)
    print('  >50%% 的点: %d / %d' % (busy, n))
    print()
    if mx > 200:
        print('[FAIL] 有单次采样超过 200%%（= 2 核满载），说明存在成块的同步工作')
    elif p95 > 30:
        print('[WARN] p95 偏高（%.0f%%），UI 线程持续繁忙' % p95)
    elif busy > 0:
        print('[WARN] 有 %d 次尖峰 >50%%，拖动时应该能感觉到' % busy)
    else:
        print('[OK] UI 线程 CPU 平稳')
    return 0


if __name__ == '__main__':
    sys.exit(main())

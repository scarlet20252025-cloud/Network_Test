# -*- coding: utf-8 -*-
"""验证「App 本体内到底能不能读 /proc/net/dev」—— 决定自检 ④ 该怎么修。

背景：交接文档 P2 写着「读 /proc/net/dev 的 tun0 收发字节增量判断隧道是否有流量
（不需要 root，Android 10+ 可读）」。这句话需要验证：**readable by whom?**

2026-10-30 第一次尝试用 `adb shell run-as` 以 App 的 uid 读，得到
Permission denied —— 但 `run-as` 跑在 `runas_app` 域，而 App 本体跑在
`untrusted_app` 域，**两者是不同的 SELinux 域**，不能用 run-as 的结果代替。

本脚本用「让 App 自己读」的方式验证：编译一个最小的探针入口，
在 App 进程内读 /proc/net/dev 并把结果写到 files/ 下，再用 adb 取回。

由于加探针要重新出 APK，这个脚本只负责「取结果 + 判定」，探针入口
本身在 MainActivity 的 `--ez probeifs true` 里（见那里的注释）。

用法：
    python tools/probe-proc-net-dev.py
"""
import os
import re
import subprocess
import sys
import time

sys.stdout.reconfigure(encoding='utf-8')

ADB = r'C:\tmp\ptools\platform-tools\adb.exe'
PHONE = '6e870b85'
PKG = 'com.ferry.app.debug'


def adb(*args, timeout=60):
    env = dict(os.environ)
    env['ADB_LIBUSB'] = '1'
    env['MSYS_NO_PATHCONV'] = '1'
    return subprocess.run([ADB, '-s', PHONE] + list(args),
                          capture_output=True, text=True,
                          encoding='utf-8', errors='replace',
                          env=env, timeout=timeout)


def main():
    print('=== 触发 App 内探针 ===')
    adb('shell', 'am force-stop ' + PKG)
    time.sleep(2)
    adb('shell', 'am start -n %s/com.ferry.app.MainActivity --ez probeifs true' % PKG)
    time.sleep(6)

    print('=== 取回探针结果 ===')
    r = adb('shell', 'run-as %s cat /data/data/%s/files/probeifs.txt' % (PKG, PKG))
    out = (r.stdout or '').strip()
    if not out:
        print('  !! 没拿到结果（探针入口可能还没编进 APK）')
        print('  stderr:', (r.stderr or '')[:200])
        return 1

    print(out)
    print()
    if 'Permission denied' in out or 'EACCES' in out:
        print('结论：App 本体内**读不到** /proc/net/dev（SELinux 拦）')
        print('     → 自检 ④ 不能靠 /proc/net/dev，得用 TProxyGetStats')
        return 0
    if 'tun0' in out:
        print('结论：App 本体内**能读到** /proc/net/dev，且能看到 tun0')
        print('     → 两种判据都可用；优先 TProxyGetStats（更准，直接是隧道计数）')
        return 0
    print('结论：读到了但没看到 tun0 —— 隧道可能没起')
    return 0


if __name__ == '__main__':
    sys.exit(main())

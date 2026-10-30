# -*- coding: utf-8 -*-
"""测手机经隧道的真实下载吞吐 —— 「刷视频卡」到底是多慢。

为什么要在手机上测、而不是本机：
  本机走的是 Windows 客户端那条链路，和手机的 hev 隧道是两套实现。
  用户报的是手机，就必须在手机上量。

方法：
  用 App 自己（不被 addDisallowedApplication 排除的是别的 App）——
  这里用 toysbox 的 `nc` 从 B 站 CDN 直接拉一段视频字节，前后夹时间。
  但 nc 在 toybox 上退出行为不可靠（见 measure-android-udp.py 的教训），
  所以改用 `curl`（vivo 自带）或 `wget`；都没有就退回 ping + 隧道计数。

用法：python tools/measure-android-download.py
"""
import os
import re
import subprocess
import sys
import time

sys.stdout.reconfigure(encoding='utf-8')

ADB = r'C:\tmp\ptools\platform-tools\adb.exe'
PHONE = '6e870b85'


def adb(*args, timeout=90):
    env = dict(os.environ)
    env['ADB_LIBUSB'] = '1'
    env['MSYS_NO_PATHCONV'] = '1'
    return subprocess.run([ADB, '-s', PHONE] + list(args),
                          capture_output=True, text=True,
                          encoding='utf-8', errors='replace',
                          env=env, timeout=timeout)


def tun_bytes():
    r = adb('shell', 'cat /proc/net/dev')
    for line in (r.stdout or '').splitlines():
        if 'tun0' in line:
            f = line.split(':')[1].split()
            return int(f[0]), int(f[8])       # rx bytes, tx bytes
    return None


def which(tool):
    r = adb('shell', 'which ' + tool)
    return (r.stdout or '').strip()


def main():
    print('=== 可用工具 ===')
    for t in ('curl', 'wget', 'ping'):
        print('  %-6s %s' % (t, which(t) or '(无)'))
    print()

    print('=== 隧道吞吐（拉一段真实 CDN 内容）===')
    before = tun_bytes()
    if not before:
        print('  !! 没有 tun0')
        return 1
    print('  before tun0: rx=%d tx=%d' % before)

    # 用一个稳定的大文件：vivo 自己的 OTA 或 B 站 CDN 都在隧道内可达。
    # 优先 curl（能限时），拿不到就用 ping 打一轮看延迟。
    curl = which('curl')
    if curl:
        cmd = ('timeout 12 curl -s -o /dev/null -w "%{size_download} %{speed_download} %{time_total}" '
               '--max-time 10 "http://upos-sz-mirrorhwo1.bilivideo.com/upgcxcode/00/00/00/0000000-1-208.mp4" '
               '2>/dev/null || echo "0 0 0"')
        r = adb('shell', cmd, timeout=40)
        print('  curl 结果(size/speed/time): %s' % (r.stdout or '').strip())
    after = tun_bytes()
    print('  after  tun0: rx=%d tx=%d' % after)
    rx = after[0] - before[0]
    print('  → 这轮隧道下行 %d 字节' % rx)
    print()

    print('=== 隧道内 RTT（对 pcs，10 包）===')
    r = adb('shell', 'ping -c 10 -W 2 10.13.24.1 2>&1 | tail -3', timeout=40)
    print((r.stdout or '').strip())
    print()

    print('=== 对比：不经隧道直连网关 ===')
    r = adb('shell', 'ping -c 10 -W 2 10.141.56.1 2>&1 | tail -3', timeout=40)
    print((r.stdout or '').strip())
    return 0


if __name__ == '__main__':
    sys.exit(main())

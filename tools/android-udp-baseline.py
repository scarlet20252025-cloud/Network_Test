# -*- coding: utf-8 -*-
"""给 Android 的 HevConfig 加「DNS 也走 UDP 隧道」的说明性断言。

为什么要单独写这个：M5 要量化的不只是游戏延迟，还有「DNS 查询是否也绕远」。
Android 侧 hev 的 `udp: 'udp'` 是全局开关 —— 一旦打开，**所有 UDP 都走隧道**，
包括 mapdns 外的普通 UDP。这和 Windows 端 1.0.26 犯的是同一个错误，
只是 Android 还没走到那一步（目前只在借网时才起隧道，影响面小一些）。

本脚本不做修改，只把现状打印出来，作为 M5 的基线记录。
"""
import io
import os
import re
import sys

ROOT = 'E:' + os.sep


def find(pattern):
    for name in os.listdir(ROOT):
        p = os.path.join(ROOT, name, 'borrow-net')
        if os.path.isdir(p):
            f = os.path.join(p, pattern)
            if os.path.exists(f):
                return f
    return None


def main():
    f = find(os.path.join('android', 'app', 'src', 'main', 'java', 'com', 'ferry', 'app', 'vpn', 'HevConfig.java'))
    if not f:
        print('找不到 HevConfig.java')
        return 1
    src = io.open(f, encoding='utf-8').read()

    print('=== HevConfig 现状（M5 基线）===')
    for line in src.split('\n'):
        s = line.strip()
        if s.startswith('sb.append("  udp:') or 'udp:' in s and 'append' in s:
            print('  ' + s)
        if 'mtu:' in s and 'append' in s:
            print('  ' + s)

    m = re.search(r"udp:\s*'([^']+)'", src)
    mode = m.group(1) if m else '(未找到)'
    print()
    print('  udp 模式 = %s' % mode)
    if mode == 'udp':
        print('  含义：所有 UDP 都经 SOCKS5 UDP ASSOCIATE 走远端 —— 包括游戏、QUIC、DNS。')
        print('  风险：和 Windows 端 1.0.26 同类 —— 本来能直连的 UDP 被绕远。')
        print('  M5 待量化：游戏 UDP 走隧道 vs 直连的 p50/p95 差值。')
    elif mode == 'tcp':
        print('  含义：UDP 也套在 TCP 里发（RFC 1928 没有这个模式，hev 特有）。')
    else:
        print('  含义：不转发 UDP，UDP 直接失败。')
    return 0


if __name__ == '__main__':
    sys.exit(main())

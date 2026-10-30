# -*- coding: utf-8 -*-
"""M5：Android 上「UDP 走隧道」到底贵多少 —— 以及**先证明它真的走了隧道**。

═══════════════════════════════════════════════════════════════════
这一轮最值钱的结论先说：**不能用 pcs 当 UDP 目标来测隧道**
═══════════════════════════════════════════════════════════════════

2026-10-30 实测：手机往 10.13.24.1:10820（pcs 上的 UDP echo）发包，
服务端记录的源地址是 **10.141.57.9（手机自己的 wlan0）**，不是中继的
10.13.24.1 —— 说明这些包根本没进隧道，测出来的是「直连有多快」。

原因在 `TunConfig.PUBLIC_V4`：隧道只收「公网单播」段，
10.0.0.0/8 是私网，**被显式排除**（这是对的，为的是让打印机/路由器/远端
SOCKS5 自己都不绕隧道）。所以 UDP 打 10.13.24.1 == UDP 打自己人，永不走隧道。

→ 测隧道内 UDP，目标必须是**公网地址**（下面默认用阿里 DNS 223.5.5.5:53）。

═══════════════════════════════════════════════════════════════════
怎么判定「真的走了隧道」
═══════════════════════════════════════════════════════════════════

只有一个判据不可自欺：**中继自己的日志里有没有这条 UDP 关联**。

  udp: 'udp' 生效 → mihomo 给每个 UDP 目标建 ASSOCIATE →
                     `udp-relay.log` 出现
                     `UDP 关联 N 条（峰值 M），本周期新建 K 条（最近一条 <手机IP>:<端口>）`

实测（2026-10-30 15:52:44）：
  `[15:52:44] UDP 关联 1 条（峰值 1），本周期新建 1 条（最近一条 10.141.57.9:54992）`
← 这一行就是「手机发的 UDP 进了隧道」的硬证据。

═══════════════════════════════════════════════════════════════════
RTT 怎么测
═══════════════════════════════════════════════════════════════════

手机上 toybox 的 `nc -u` 有三个坑，导致**没法用它量 RTT**：
  · `-w` 只管建连超时，UDP 无连接 → 不生效
  · `-W`（idle）实测也不退出
  · 只能用 `/system/bin/timeout N` 硬杀，于是测到的是「N 秒」，不是 RTT

所以 RTT 分两段取：
  · **中继本身的代价**：本机跑同一条 SOCKS5 UDP 路径 vs 本机直连，
    两者同一个出口、同一段校园网，差值就是「绕一圈」的净成本。
  · **手机侧**：只验「走没走隧道」+ 用 App 里的 hev 统计看字节数增长。

用法（先保证 pcs 上 UDP echo 在听：tools/start-udp-echo.ps1）：
    python tools/measure-android-udp.py            # 默认
    python tools/measure-android-udp.py --rounds 5
"""
import argparse
import os
import re
import statistics
import subprocess
import sys
import time

sys.stdout.reconfigure(encoding='utf-8')

ADB = r'C:\tmp\ptools\platform-tools\adb.exe'
PHONE = '6e870b85'
PCS_IP = '10.13.24.1'
SOCKS_PORT = 1080
ECHO_PORT = 10820

# 公网目标：必须走隧道才有意义（见文件头）
PUBLIC_DNS = '223.5.5.5'
DNS_QUERY = (b'\x12\x34\x01\x00\x00\x01\x00\x00\x00\x00\x00\x00'
             b'\x07aliyun\x03com\x00\x00\x01\x00\x01')


def adb(*args, timeout=60):
    env = dict(os.environ)
    env['ADB_LIBUSB'] = '1'
    env['MSYS_NO_PATHCONV'] = '1'
    return subprocess.run([ADB, '-s', PHONE] + list(args),
                          capture_output=True, text=True,
                          encoding='utf-8', errors='replace',
                          env=env, timeout=timeout)


def phone_tunnel_up():
    r = adb('shell', 'cat /proc/net/dev')
    return 'tun0' in (r.stdout or '')


def phone_wlan_ip():
    r = adb('shell', 'ip -4 addr show wlan0')
    m = re.search(r'inet (\d+\.\d+\.\d+\.\d+)/', r.stdout or '')
    return m.group(1) if m else '?'


def relay_udp_assoc_lines():
    """从中继日志尾部抓 UDP 关联汇总行（含源 IP:端口）。"""
    remote = (
        "import io\n"
        "p = r'C:\\Users\\PCserver\\AppData\\Local\\Ferry\\logs\\udp-relay.log'\n"
        "try:\n"
        "    d = io.open(p, encoding='gbk', errors='replace').read().splitlines()\n"
        "except OSError:\n"
        "    d = []\n"
        "for L in d[-500:]:\n"
        "    if 'UDP' in L and ('\\u5173\\u8054' in L):\n"
        "        print(L.encode('ascii','backslashreplace').decode())\n"
    )
    r = subprocess.run(['ssh', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10',
                        'pcs', 'python -'], input=remote, capture_output=True,
                       text=True, encoding='utf-8', errors='replace', timeout=60)
    return [L.strip() for L in (r.stdout or '').splitlines() if L.strip()]


def parse_assoc_close(line):
    """把 `... 关联关闭 <ip>:<port>  上行 N B / 下行 M B / K 包` 解析成 dict。

    返回 None 表示不是「关联关闭」那种行（比如「关联 N 条…」汇总行）。
    中继把中文行 encode('ascii','backslashreplace') 之后，中文变成
    \\u5173\\u8054 这种转义；这里用同样的转义字面量匹配，避免再被编码坑一次。
    """
    if '\\u5173\\u8054\\u5173\\u95ed' not in line:   # 「关联关闭」
        return None
    m = re.search(r'(\d+\.\d+\.\d+\.\d+):(\d+)', line)
    if not m:
        return None
    ip, port = m.group(1), int(m.group(2))
    # 上行/下行/包数：先取所有数字，按「上行 N B / 下行 M B / K 包」顺序
    nums = re.findall(r'(\d+)\s*B', line)
    pkts = re.search(r'(\d+)\s*\\u5305', line) or re.search(r'(\d+)\s*包', line)
    up = int(nums[0]) if len(nums) > 0 else 0
    down = int(nums[1]) if len(nums) > 1 else 0
    return {'ip': ip, 'port': port, 'up': up, 'down': down,
            'pkts': int(pkts.group(1)) if pkts else 0, 'raw': line}


def phone_send_public_udp():
    """手机往公网 DNS 发一个 UDP 查询。

    返回值没有意义 —— toybox 下 `... ; echo RC=$?` 实测打印的是字符串
    `RC=True`（不是数字），拿它判成败会永远判成失败（2026-10-30 踩到）。
    这本来也不需要：判据是中继日志里的关联行，不是命令退出码。
    """
    hexq = ''.join('\\x%02x' % b for b in DNS_QUERY)
    cmd = ("printf '" + hexq + "' | timeout 4 nc -u " +
           PUBLIC_DNS + " 53 > /dev/null 2>&1; true")
    try:
        adb('shell', cmd, timeout=30)
    except Exception:
        pass
    return True


def socks_udp_rtt(count=20, payload=b'x' * 32):
    """本机经 SOCKS5 UDP ASSOCIATE 打 echo，返回 RTT 列表（毫秒）。"""
    import socket
    s = socket.create_connection((PCS_IP, SOCKS_PORT), 10)
    s.settimeout(10)
    s.sendall(b'\x05\x01\x00')
    s.recv(2)
    s.sendall(b'\x05\x03\x00\x01' + b'\x00' * 6)
    s.recv(4)
    addr = s.recv(6)
    relay_port = int.from_bytes(addr[4:6], 'big')

    u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    u.settimeout(3)
    pkt = (b'\x00\x00\x00\x01' + socket.inet_aton(PCS_IP) +
           ECHO_PORT.to_bytes(2, 'big') + payload)
    rtts, lost = [], 0
    for _ in range(count):
        t0 = time.perf_counter()
        try:
            u.sendto(pkt, (PCS_IP, relay_port))
            u.recvfrom(2048)
            rtts.append((time.perf_counter() - t0) * 1000)
        except Exception:
            lost += 1
        time.sleep(0.02)
    u.close()
    s.close()
    return rtts, lost


def direct_udp_rtt(count=20, payload=b'x' * 32):
    """本机直连打 echo（不经中继）。"""
    import socket
    u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    u.settimeout(3)
    rtts, lost = [], 0
    for _ in range(count):
        t0 = time.perf_counter()
        try:
            u.sendto(payload, (PCS_IP, ECHO_PORT))
            u.recvfrom(2048)
            rtts.append((time.perf_counter() - t0) * 1000)
        except Exception:
            lost += 1
        time.sleep(0.02)
    u.close()
    return rtts, lost


def st(v):
    if not v:
        return None
    v = sorted(v)
    return dict(n=len(v), p50=v[len(v) // 2], p90=v[int(len(v) * 0.9)],
                p99=v[min(len(v) - 1, int(len(v) * 0.99))], mx=v[-1])


def fmt(d):
    if not d:
        return '（无样本）'
    return ('n=%d  p50=%.1f  p90=%.1f  p99=%.1f  max=%.1f'
            % (d['n'], d['p50'], d['p90'], d['p99'], d['mx']))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--rounds', type=int, default=3)
    ap.add_argument('--samples', type=int, default=20)
    args = ap.parse_args()

    print('=== M5 · Android UDP 走隧道 ===')
    print('手机 = %s   wlan0 = %s   隧道 = %s'
          % (PHONE, phone_wlan_ip(), '在' if phone_tunnel_up() else '不在'))
    print()

    print('=== 一、中继本身的代价（本机，同一出口两种路径）===')
    d1, l1 = direct_udp_rtt(args.samples)
    d2, l2 = socks_udp_rtt(args.samples)
    print('  直连 %s:%d        %s  丢 %d' % (PCS_IP, ECHO_PORT, fmt(st(d1)), l1))
    print('  经中继 SOCKS UDP    %s  丢 %d' % (fmt(st(d2)), l2))
    if d1 and d2:
        print('  → 绕一圈的净成本：p50 %+.1f ms'
              % (st(d2)['p50'] - st(d1)['p50']))
    print()

    print('=== 二、手机 UDP 是否真的进隧道（看中继日志的关联行）===')
    before = relay_udp_assoc_lines()
    print('  发包前关联行数 = %d' % len(before))
    wlan = phone_wlan_ip()
    for i in range(args.rounds):
        phone_send_public_udp()
        time.sleep(1.5)
    print('  已发送 %d 轮到 %s:53（公网目标）' % (args.rounds, PUBLIC_DNS))
    time.sleep(2)
    after = relay_udp_assoc_lines()
    new = after[len(before):] if len(after) > len(before) else after[-4:]
    print('  发包后新增关联行：')
    for L in new:
        print('    ' + L)

    # 关键：区分「关联建起来了」和「包真的转发过」。
    #
    # 2026-10-30 实测发现这两件事会分开：
    #   · 手机 (10.141.57.9) 的 UDP 关联**全部** `上行 0 B / 下行 0 B / 0 包`
    #   · 本机 Windows 客户端 (10.141.59.235) 同一时段 `上行 640 B / 下行 640 B / 20 包`
    # 只报「有手机 IP 的关联」会得出「隧道 UDP 生效」的错误结论。
    print()
    closes = [parse_assoc_close(L) for L in after]
    closes = [c for c in closes if c]
    phone_closes = [c for c in closes if c['ip'] == wlan]
    win_closes = [c for c in closes if c['ip'] != wlan]
    print('=== 三、按来源看「包有没有真的转发」===')
    if phone_closes:
        tot_up = sum(c['up'] for c in phone_closes)
        tot_pk = sum(c['pkts'] for c in phone_closes)
        print('  手机 %s：%d 条关联，上行合计 %d B / %d 包'
              % (wlan, len(phone_closes), tot_up, tot_pk))
        for c in phone_closes[-3:]:
            print('    :%d  上行 %d B  下行 %d B  %d 包'
                  % (c['port'], c['up'], c['down'], c['pkts']))
    else:
        print('  手机：没有「关联关闭」记录（可能还在打开中）')
    if win_closes:
        tot_up_w = sum(c['up'] for c in win_closes)
        print('  其它来源（对照）：%d 条关联，上行合计 %d B'
              % (len(win_closes), tot_up_w))
    print()
    if phone_closes and sum(c['pkts'] for c in phone_closes) > 0:
        print('  ✅ 手机的 UDP 确实经隧道转发过（有包数）')
    elif phone_closes:
        print('  ❌ 手机的 UDP 关联**建了但一个包都没转发**（上行 0 B / 0 包）')
        print('     → 不能说「隧道 UDP 生效」。这是 hev 侧的问题，见 M5 结论。')
    else:
        print('  ⚠ 样本不足，多跑几轮再看')
    print()
    print('=== 结论口径 ===')
    print('  · 隧道 UDP 是否生效：看上面「中继日志有没有手机 IP 的关联」')
    print('  · 隧道贵多少：看第一部分「绕一圈的净成本」')
    print('  · 手机 RTT 不用 nc 量（toybox nc -u 退不出去，量出来是超时值）')
    return 0


if __name__ == '__main__':
    sys.exit(main())

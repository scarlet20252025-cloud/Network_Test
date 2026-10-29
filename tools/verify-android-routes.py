#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""校验 TunConfig 的公网单播路由表。

为什么需要（2026-10-29 踩到的真 bug）：
  第一版写的是 addRoute("0.0.0.0",0) 加 addRoute("10.0.0.0",8)，
  以为后者能「排除局域网」。VpnService 的 addRoute 是「导进隧道」，
  越具体优先级越高 —— 结果是把局域网也塞进隧道，访问打印机/摄像头全超时。

  这张表是手工从 5G-Proxy-Client 的 LanRoutes.kt 抄来的 46 条，
  抄错一条就等于某个网段被错误地送进/送不进隧道。所以必须用程序校验：

    ① 覆盖完整：1.0.0.0–223.255.255.255 每一个地址都能被某条覆盖
    ② 互不重叠：任何两条不应重叠（重叠说明表抄错了）
    ③ 必须排除：私网 10/8、172.16/12、192.168/16，环回 127/8，
       link-local 169.254/16，组播/保留 224/4 以上 —— 全部不能命中
    ④ 必须包含：常见公网地址（8.8.8.8、223.5.5.5、1.1.1.1、114.114.114.114、
       远端出口所在段的典型地址）

用法：
    python tools/verify-android-routes.py
退出码 0 表示全部通过。
"""
import ipaddress
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TUNCONFIG = os.path.join(HERE, '..', 'android', 'app', 'src', 'main',
                         'java', 'com', 'ferry', 'app', 'vpn', 'TunConfig.java')


def parse_routes(path):
    """从 TunConfig.java 的 PUBLIC_V4 数组里抽出 (cidr_string, prefix)。"""
    src = open(path, encoding='utf-8').read()
    # 抓 {"1.0.0.0", "8"}, 这种形态
    pairs = re.findall(r'\{"(\d+\.\d+\.\d+\.\d+)",\s*"(\d+)"\}', src)
    if not pairs:
        raise SystemExit('没解析到任何路由条目，检查 TunConfig.java 的 PUBLIC_V4 格式')
    return [(a, int(p)) for a, p in pairs]


def to_nets(pairs):
    nets = []
    for addr, plen in pairs:
        nets.append(ipaddress.ip_network('%s/%d' % (addr, plen), strict=True))
    return nets


def check_no_overlap(nets):
    """两两检查重叠（46 条 → 1035 次比较，够快）。"""
    bad = []
    for i in range(len(nets)):
        for j in range(i + 1, len(nets)):
            if nets[i].overlaps(nets[j]):
                bad.append((str(nets[i]), str(nets[j])))
    return bad


def covered(nets, ip):
    a = ipaddress.ip_address(ip)
    for n in nets:
        if a in n:
            return n
    return None


def main():
    fails = 0

    def ok(cond, name, detail=''):
        nonlocal fails
        if cond:
            print('PASS  ' + name)
        else:
            print('FAIL  ' + name + (('  -- ' + detail) if detail else ''))
            fails += 1

    pairs = parse_routes(TUNCONFIG)
    print('解析到 %d 条路由' % len(pairs))

    nets = to_nets(pairs)
    ok(len(pairs) == 46, '条目数 == 46（和参考实现一致）', '实际 %d' % len(pairs))

    # ② 互不重叠
    overlap = check_no_overlap(nets)
    ok(not overlap, '任意两条不重叠', str(overlap[:3]))

    # ③ 必须排除的段
    must_exclude = [
        ('10.141.59.235', '10/8 私网（校园网本机 IP）'),
        ('10.13.24.1', '10/8 私网（远端 pcs）'),
        ('172.16.0.1', '172.16/12 私网 下界'),
        ('172.31.255.254', '172.16/12 私网 上界'),
        ('192.168.1.1', '192.168/16 私网（路由器后台）'),
        ('192.168.137.1', '192.168/16 私网（原热点网段）'),
        ('127.0.0.1', '127/8 环回'),
        ('169.254.154.25', '169.254/16 link-local（APIPA）'),
        ('0.0.0.0', '0/8'),
        ('224.0.0.1', '224/4 组播'),
        ('239.255.255.250', '224/4 组播（SSDP）'),
        ('255.255.255.255', '广播'),
    ]
    for ip, desc in must_exclude:
        n = covered(nets, ip)
        ok(n is None, '不进隧道：%s  [%s]' % (ip, desc), ('命中了 %s' % n) if n else '')

    # ④ 必须包含的公网地址
    must_include = [
        ('8.8.8.8', 'Google DNS'),
        ('1.1.1.1', 'Cloudflare'),
        ('223.5.5.5', '阿里 DNS'),
        ('114.114.114.114', '114DNS'),
        ('120.53.53.53', '腾讯 DNS'),
        ('218.107.0.47', '联通出口典型地址（远端出口）'),
        ('140.82.121.4', 'github.com'),
        ('223.255.255.254', '223/8 上界（表的最大前缀）'),
        ('1.1.1.1', '1/8 下界'),
        ('169.253.255.255', '169.253.x（link-local 之前）'),
        ('169.255.0.1', '169.255.x（link-local 之后）'),
        ('172.0.0.1', '172.0.x（私网段之前）'),
        ('172.32.0.1', '172.32.x（私网段之后）'),
        ('192.167.255.254', '192.167（私网段之前）'),
        ('192.169.0.1', '192.169（私网段之后）'),
    ]
    for ip, desc in must_include:
        n = covered(nets, ip)
        ok(n is not None, '进隧道：%s  [%s]' % (ip, desc), '没有任何路由覆盖' if n is None else '')

    # ① 覆盖完整性：1.0.0.0–223.255.255.255 里，**除了故意挖掉的段**之外都要被覆盖
    #
    #    第一版这里写成了「整段全覆盖」，结果报 10.0.0.1 / 127.0.0.1 / 169.254.x
    #    三个 FAIL —— 那三段本来就是故意排除的，是断言错了不是表错了。
    #    第二版只列了 10/127/169.254，又漏了 172.16/12 和 192.168/16 ——
    #    它们同样是私网、同样故意排除，漏了会误报「有空隙」。
    #    正确做法：把**所有**预期排除段列全，再断言每个空洞都落在其中。
    EXPECTED_EXCLUDED = [
        ipaddress.ip_network('10.0.0.0/8'),        # 私网 + 校园网 + 远端 pcs
        ipaddress.ip_network('127.0.0.0/8'),       # 环回
        ipaddress.ip_network('169.254.0.0/16'),    # link-local / APIPA
        ipaddress.ip_network('172.16.0.0/12'),     # 私网
        ipaddress.ip_network('192.168.0.0/16'),    # 私网（路由器后台、原热点网段）
    ]

    def in_expected_exclusion(ip_int):
        a = ipaddress.ip_address(ip_int)
        return any(a in n for n in EXPECTED_EXCLUDED)

    # 逐块扫描 1.0.0.0–223.255.255.255，收集不在任何路由里的地址
    gaps = []
    for first_octet in range(1, 224):
        for last in ('0.0.1', '127.0.255', '255.255.254'):
            ip = '%d.%s' % (first_octet, last)
            if covered(nets, ip) is None and not in_expected_exclusion(int(ipaddress.ip_address(ip))):
                gaps.append(ip)
    ok(not gaps, '覆盖完整：除 10/8、127/8、169.254/16 外无空洞', str(gaps[:6]))

    # ② 首尾相接：排序后相邻两条之间要么紧邻，要么间隙恰好是预期排除段
    sorted_nets = sorted(nets, key=lambda n: int(n.network_address))
    bad_seams = []
    for i in range(len(sorted_nets) - 1):
        cur_end = int(sorted_nets[i].broadcast_address)
        nxt_start = int(sorted_nets[i + 1].network_address)
        if nxt_start == cur_end + 1:
            continue
        # 中间有间隙 —— 检查这个间隙是不是「全在某个预期排除段之内」
        gap_lo, gap_hi = cur_end + 1, nxt_start - 1
        wholly_excluded = all(
            in_expected_exclusion(x) for x in (gap_lo, gap_hi)
        )
        if not wholly_excluded:
            bad_seams.append('%s 与 %s 之间有空隙 %s–%s，且不属于预期排除段' % (
                sorted_nets[i], sorted_nets[i + 1],
                ipaddress.ip_address(gap_lo), ipaddress.ip_address(gap_hi)))
    ok(not bad_seams, '相邻条目之间只有预期排除段造成的间隙', str(bad_seams[:3]))

    # ③ 预期排除段确实**完完整整**没被覆盖（逐段检查，不是抽点）
    for n in EXPECTED_EXCLUDED:
        leaked = [str(n) for other in nets if other.overlaps(n)]
        ok(not leaked, '排除段 %s 完全不在表内' % n, '被这些覆盖了：%s' % leaked)

    print()
    print('ALL PASS' if fails == 0 else ('%d FAILED' % fails))
    return 0 if fails == 0 else 1


if __name__ == '__main__':
    sys.exit(main())

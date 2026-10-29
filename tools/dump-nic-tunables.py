# -*- coding: utf-8 -*-
"""对比网卡高级属性里与「延迟/省电/聚合」相关的项，判断锯齿抖动来自哪里。

背景：同一台 AP（BSSID 10:2f:6e:84:73:8f）、同一信道 40，
手机 avg 5.2ms，这台电脑 avg 38ms，且是「爬升到 70-79 再掉回 3」的锯齿。
已排除：省电 IpsEn、信道干扰、AP 本身 —— 都在本机侧。

可疑方向：
  · 帧聚合（A-MSDU / A-MPDU）—— 聚合越多，单包等待越久
  · U-APSD / WMM 节能 —— 让包攒着一起送
  · 巨型帧 / 大 MTU 处理
  · 中断调节（interrupt moderation）—— 攒够 N 个包才中断一次

用法： python tools\dump-nic-tunables.py
"""
import re
import subprocess
import sys

sys.stdout.reconfigure(encoding='utf-8')

KEY = r'HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0007'

# 与延迟直接相关的关键词
PATTERNS = [
    'AMPDU', 'AMSDU', 'Aggregat', 'Burst', 'UAPSD', 'WMM', 'PS', 'Power',
    'IntModer', 'Interrupt', 'Coalesc', 'Lazy', 'LSO', 'RSS', 'MIMO',
    'Ant', 'Rate', 'Bcn', 'Beacon', 'DTIM', 'Listen', 'Sleep', 'Doze',
    'Uapsd', 'TxRx', 'Threshold', 'Delay', 'Latency',
]


def reg_dump():
    """用 reg query 拿全部值（比 powershell 好解析）。"""
    p = subprocess.run(['reg', 'query', KEY], capture_output=True, timeout=30)
    out = (p.stdout or b'').decode('gbk', 'replace')
    items = []
    for line in out.splitlines():
        m = re.match(r'\s+(\S+)\s+REG_(\w+)\s+(.*)', line)
        if m:
            items.append((m.group(1), m.group(2), m.group(3).strip()))
    return items


def interpret(name, value):
    """给几个常见项加一句人话解释。"""
    n = name.lower()
    notes = []
    if 'ampsdu' in n or 'amsdu' in n or 'ampdu' in n:
        notes.append('帧聚合：越大会让单包等待变长')
    if 'uapsd' in n:
        notes.append('U-APSD 节能：包会攒着一起送')
    if 'moderation' in n or 'intmoder' in n:
        notes.append('中断调节：攒够多个包才中断一次')
    if 'lazy' in n:
        notes.append('Lazy Roam')
    if 'coalesc' in n:
        notes.append('包合并')
    if 'dtim' in n or 'listen' in n:
        notes.append('休眠唤醒周期')
    return ' | '.join(notes)


def main():
    try:
        items = reg_dump()
    except Exception as e:
        print('读取注册表失败: %s' % e)
        return 1

    print('共 %d 个值\n' % len(items))
    print('=== 与延迟相关的项 ===')
    print('%-34s %-8s %s' % ('名称', '值', '备注'))
    print('-' * 90)
    hit = 0
    for name, typ, val in sorted(items):
        if any(k.lower() in name.lower() for k in PATTERNS):
            note = interpret(name, val)
            if note or name.lower() in (
                'ipsen', 'lpsen', 'lpswowen', 'coreamsdu', 'amfactor',
                'tbttinterval', 'ahanaradj', 'ah_timeradjsinfbcn',
            ):
                print('%-34s %-8s %s' % (name, val, note))
                hit += 1

    print()
    print('=== 重点关注（手工确认过含义的）===')
    known = {
        'ipsen': '智能省电（已改 0）',
        'lpsen': 'LEP/LPS 省电：0=关 1=开 2=自动',
        'lpswowen': 'WoWLAN 省电',
        'coreamsdu': 'A-MSDU 聚合：1=启用',
        'amfactor': '聚合因子',
        'bearlymodeenable': 'Early mode（提前上报）',
    }
    for name, typ, val in items:
        key = name.lower()
        if key in known:
            print('  %-22s = %-6s  %s' % (name, val, known[key]))

    print()
    print('=== 判读 ===')
    d = {n.lower(): v for n, t, v in items}
    lps = d.get('lpsen')
    if lps is not None:
        print('  LpsEn = %s' % lps)
        if lps != '0':
            print('    ⚠ 仍是省电状态。这是 1.0.47 之外唯一还没关的省电开关，')
            print('      可以试：reg add "<上面那个键>" /v LpsEn /t REG_DWORD /d 0 /f')
            print('      然后重启网卡。会断网几秒。')
    am = d.get('coreamsdu')
    if am == '1':
        print('  CoreAMSDU = 1（A-MSDU 聚合启用）')
        print('    聚合会让小包一起发、对端一起收 —— 延迟尾部会变长。')
        print('    游戏这类小包场景，关掉聚合通常能降低抖动上限。')


if __name__ == '__main__':
    sys.exit(main())

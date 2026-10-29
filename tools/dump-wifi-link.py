# -*- coding: utf-8 -*-
"""查网卡的「当前实际工作参数」——找出为什么同一 AP 下本机比手机慢 10 倍。

不靠猜，直接读网卡自报的链路状态：
  · 协商速率 / 频宽 / 空间流
  · 是否启用了 802.11ax 的 TWT（目标唤醒时间）—— 这是最可疑的一项
  · DTIM 周期 / 省电模式当前状态
  · 网卡自报的 RSSI 与噪声

用法： python tools\dump-wifi-link.py
"""
import re
import subprocess
import sys

sys.stdout.reconfigure(encoding='utf-8')


def run(cmd, timeout=25):
    try:
        p = subprocess.run(cmd, shell=isinstance(cmd, str), capture_output=True, timeout=timeout)
        return (p.stdout or b'') + (p.stderr or b'')
    except Exception as e:
        return str(e).encode()


def file_cmd_to_text(psfile):
    """让 PowerShell 把输出写 UTF-8 文件，再读回来 —— 绕开 GBK 乱码。"""
    tmp = r'C:\Users\63526\AppData\Local\Temp\_wiilink.txt'
    ps = ("[Console]::OutputEncoding=[Text.Encoding]::UTF8; "
          + psfile + " | Out-File '" + tmp + "' -Encoding utf8")
    run(['powershell', '-NoProfile', '-Command', ps])
    try:
        return open(tmp, encoding='utf-8-sig').read()
    except Exception:
        return ''


print('=== 1. 接口当前状态 ===')
txt = file_cmd_to_text('netsh wlan show interfaces')
if txt:
    for line in txt.splitlines():
        s = line.strip()
        if any(k in s for k in ('SSID', 'BSSID', 'Band', 'Channel', 'Radio type',
                                'Signal', 'Receive rate', 'Transmit rate',
                                '接收速率', '发送速率', '信号', '通道')):
            print('  ' + s)
else:
    print('  netsh 输出为空')

print()
print('=== 2. 网卡高级属性里与「唤醒/省电」相关的项 ===')
KEY = r'HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0007'
out = run(['reg', 'query', KEY]).decode('gbk', 'replace')
patterns = ('TWT', 'WOW', 'Wake', 'UAPSD', 'Uapsd', 'DTIM', 'Dtlm', 'Listen',
            'Sleep', 'Doze', 'Power', 'PS', 'Ps', 'Lps', 'Ips', 'Bcn', 'Beacon')
hits = []
for line in out.splitlines():
    m = re.match(r'\s+(\S+)\s+REG_(\w+)\s+(.*)', line)
    if m:
        name, val = m.group(1), m.group(3).strip()
        if name.startswith('*'):
            continue
        if any(pat in name for pat in patterns):
            hits.append((name, val))
for name, val in sorted(hits):
    print('  %-30s = %s' % (name, val))
if not hits:
    print('  （没有匹配项）')

print()
print('=== 3. 判读 ===')
d = {n.lower(): v for n, v in hits}
twt = [n for n in d if 'twt' in n]
if twt:
    print('  发现 TWT 相关项（目标唤醒时间）：')
    for n in twt:
        print('    %s = %s' % (n, d[n]))
    print('  ⚠ TWT 会让网卡按协商的时间窗口收发 —— 对时延敏感流量是灾难。')
    print('    表现正好是「几个包快、然后一批延迟 50-100ms」。')
    print('    如果值不为 0，关掉它（需要管理员）。')
else:
    print('  没有 TWT 项。')
    print('  → 说明网卡固件不在用 TWT，锯齿另有来源（可能是驱动内部的')
    print('    queue 深度或 AP 侧的 WMM 调度）。')
    print('    这种情况改注册表收益很小，换驱动/换网卡更实际。')

print()
print('=== 4. 一句话结论 ===')
print('  同一 AP：手机 avg 5-6ms，本机 p50 50-75ms 且锯齿。')
print('  已排除：IpsEn 省电、信道干扰、AP 本身、上层连接数、丢包、驱动 DPC 过载。')
print('  剩下：网卡固件的批处理/省电策略，或驱动版本本身的缺陷。')

#!/usr/bin/env python3
"""
fix-diag-ports.py — 让所有诊断脚本也走 Get-FerryPorts（端口自适应）

问题（自审发现）：
  dual-proxy / ferry.ps1 / 渡口助手.ps1 / startup.ps1 都已经读
  .proxy-state/ports.json，但 4 个诊断脚本还写死 10808：
    net-quality.ps1 / outage-watch.ps1 / simulate-outage.ps1 / verify-outage.ps1
  用户手动跑这些脚本时，会去探错误的端口，报「未监听」——
  于是「端口自适应」这个特性在最需要它的排障场景反而失效。

做法：在这 4 个脚本的 param 块后面插入统一的端口解析，
      并把 $SocksPort 默认值改成 0（0 = 自动）。
"""
import io
import os
import re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

TARGETS = [
    ('tools/net-quality.ps1', 'SocksPort'),
    ('tools/outage-watch.ps1', 'SocksPort'),
    ('tools/simulate-outage.ps1', 'SocksPort'),
    ('tools/verify-outage.ps1', 'SocksPort'),
]

IOBJ = """# ---- 端口解析（与 ferry.ps1 / 渡口助手.ps1 共用同一入口）----
# 不自带默认值：写死端口会让本脚本在「开了端口自适应」的环境里探错端口。
. (Join-Path $PSScriptRoot 'Get-FerryPorts.ps1')
$_fp = Get-FerryPorts -SocksPort $SocksPort -HttpPort $(if ($PSBoundParameters.ContainsKey('HttpPort')) { $HttpPort } else { 0 })
$SocksPort = $_fp.SocksPort
if ($PSBoundParameters.ContainsKey('HttpPort')) { $HttpPort = $_fp.HttpPort }
"""

for rel, var in TARGETS:
    p = os.path.join(ROOT, rel)
    if not os.path.exists(p):
        print(f'  跳过（不存在）: {rel}')
        continue
    src = io.open(p, encoding='utf-8').read()

    if 'Get-FerryPorts.ps1' in src:
        print(f'  已是自适应: {rel}')
        continue

    # 1) 默认值 10808 → 0
    src2 = re.sub(r'(\[int\]\$SocksPort\s*=\s*)10808', r'\g<1>0', src)
    changed_default = src2 != src
    src = src2

    # 2) 在 param(...) 块结束后插入端口解析
    # 找 param( ... ) 的结束位置：从 'param(' 开始配对括号
    idx = src.find('param(')
    if idx < 0:
        idx = src.find('param (')
    if idx < 0:
        print(f'  ❌ 找不到 param 块: {rel}')
        continue

    depth = 0
    end = -1
    started = False
    for i in range(idx, len(src)):
        ch = src[i]
        if ch == '(':
            depth += 1
            started = True
        elif ch == ')':
            depth -= 1
            if started and depth == 0:
                end = i + 1
                break
    if end < 0:
        print(f'  ❌ param 块不闭合: {rel}')
        continue

    # 插到 param 块之后（跳过紧随的换行）
    insert_at = end
    while insert_at < len(src) and src[insert_at] in '\r\n':
        insert_at += 1

    src = src[:insert_at] + IOBJ + '\n' + src[insert_at:]

    io.open(p, 'w', encoding='utf-8', newline='').write(src)
    print(f'  ✅ {rel}（默认值改为自动: {changed_default}）')

print('\n完成。')

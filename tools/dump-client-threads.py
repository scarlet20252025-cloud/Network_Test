# -*- coding: utf-8 -*-
"""抓渡口助手的 UI 线程栈 —— 定位「界面卡死、CPU 空转」到底卡在哪一行。

背景：1.0.47 装上后界面卡顿无比，实测 5 秒内 CPU 增量 4.09 秒（约 82% 单核），
而此时它并没有在借网（没有 TUN 网卡、没有 mihomo 子进程）。
关键线索：日志里 `[TUN] Auto detect interface ... get empty name` 刷了 343 行。

做法：用 .NET 的 ClrMD 思路太重，这里用更轻的办法 ——
把进程的所有线程 dump 出来，找出主线程（STA + 有窗口）当前的托管栈。

依赖：需要 dbghelp / 或者直接用 procdump。两者都可能没有，
所以这个脚本先探测有什么工具，有什么用什么。

用法： python tools\dump-client-threads.py [PID]
"""
import os
import subprocess
import sys

sys.stdout.reconfigure(encoding='utf-8')

TARGET = '渡口助手'


def run(cmd, timeout=30):
    try:
        p = subprocess.run(cmd, shell=isinstance(cmd, str), capture_output=True,
                           timeout=timeout)
        return (p.stdout or b'').decode('utf-8', 'replace') + (p.stderr or b'').decode('utf-8', 'replace')
    except Exception as e:
        return 'ERR: %s' % e


def find_pid():
    if len(sys.argv) > 1:
        return int(sys.argv[1])
    ps = ('Get-Process | Where-Object { $_.ProcessName -like "*渡口*" } '
          '| Select-Object -First 1 -ExpandProperty Id')
    out = run(['powershell', '-NoProfile', '-Command', ps]).strip()
    return int(out) if out.isdigit() else None


def main():
    pid = find_pid()
    if not pid:
        print('没找到渡口助手进程')
        return 1
    print('目标 PID: %d' % pid)

    # 1. 先看 CPU 是否真的在空转
    cpu = run(['powershell', '-NoProfile', '-Command',
               '$a=(Get-Process -Id %d).CPU; Start-Sleep -Seconds 5; '
               '$b=(Get-Process -Id %d).CPU; [math]::Round($b-$a,2)' % (pid, pid)]).strip()
    print('5 秒 CPU 增量: %s 秒' % cpu)

    # 2. 看线程状态
    print('\n=== 线程状态 ===')
    print(run(['powershell', '-NoProfile', '-Command',
               '(Get-Process -Id %d).Threads | Group-Object ThreadState | '
               'Select-Object Name,Count | Format-Table -AutoSize' % pid]))

    # 3. 找可用的栈抓取工具
    print('=== 可用工具探测 ===')
    tools = {}
    for name, probe in [
        ('procdump', r'where procdump.exe'),
        ('dotnet-dump', r'where dotnet-dump.exe'),
        ('cdb', r'where cdb.exe'),
        ('windbg', r'where windbg.exe'),
    ]:
        out = run(probe, timeout=10).strip()
        ok = out and 'ERR' not in out and '找不到' not in out and 'Could not' not in out
        tools[name] = out if ok else None
        print('  %-12s %s' % (name, out.splitlines()[0] if ok and out else '不可用'))

    # 4. 有 procdump 就用它抓 .dmp，再用 .NET 读
    dump = None
    if tools.get('procdump'):
        dump = os.path.join(os.environ.get('TEMP', '.'), 'ferry-client.dmp')
        print('\n用 procdump 抓 dump -> %s' % dump)
        print(run(['procdump', '-accepteula', '-ma', str(pid), dump], timeout=120))
    else:
        print('\n没有 procdump。改用 ETW / 采样法：')
        print('  （见下面的托管栈替代方案）')

    if dump and os.path.exists(dump):
        print('dump 大小: %.1f MB' % (os.path.getsize(dump) / 1024 / 1024))
        print('下一步：如果有 dotnet-dump，执行')
        print('  dotnet-dump analyze "%s" -c "clrstack -all" -c "exit"' % dump)

    return 0


if __name__ == '__main__':
    sys.exit(main())

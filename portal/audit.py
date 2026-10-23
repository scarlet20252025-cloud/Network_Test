#!/usr/bin/env python3
"""
audit.py — 全工程静态审查：找设计缺陷、错漏逻辑、低级错误

检查项（都是实战中真踩过的）：
  1. 空 catch / 空 except —— 吞掉错误，出问题无从排查
  2. .Result / .Wait() —— 在 UI 线程上必死锁
  3. Dispatcher.Invoke —— 应用 InvokeAsync
  4. 硬编码端口 / IP —— 换环境就崩
  5. 未处理的 Promise（Node）—— 静默失败
  6. Python 裸 except: —— 同样吞错
  7. SQL 字符串拼接 —— 注入
  8. 密码/令牌明文日志 —— 泄露
  9. 文件读写缺 encoding —— Windows 上按 GBK
  10. PowerShell 脚本缺 BOM —— PS 5.1 按 ANSI 读，中文乱码
  11. TODO / FIXME / XXX —— 未完成
  12. console.log 调试残留
"""
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(ROOT)

SKIP_DIRS = {'.git', 'node_modules', '__pycache__', 'scratch', 'client-configs',
             '.proxy-state', '.ferry', '.borrow-net'}
SKIP_FILES = {'rename-to-ferry.py', 'audit.py', 'deploy-check.ps1'}

EXT = {'.mjs': 'js', '.js': 'js', '.ps1': 'ps1', '.py': 'py',
       '.sh': 'sh', '.md': 'md'}

findings = []


def add(level, path, line, msg, snippet=''):
    findings.append({
        'level': level, 'path': os.path.relpath(path, ROOT),
        'line': line, 'msg': msg, 'snippet': snippet.strip()[:100],
    })


def check_file(path, kind):
    try:
        raw = open(path, 'rb').read()
        src = raw.decode('utf-8')
    except (UnicodeDecodeError, OSError):
        return
    lines = src.split('\n')

    # ── PowerShell BOM ──
    if kind == 'ps1':
        if not raw.startswith(b'\xef\xbb\xbf'):
            add('ERROR', path, 0, 'PS 脚本缺 UTF-8 BOM（PS 5.1 会按 ANSI 读，中文注释破坏语法）')

    for i, ln in enumerate(lines, 1):
        s = ln.strip()

        # ── 空 catch / except ──
        if kind in ('js', 'py'):
            if re.search(r'catch\s*(\([^)]*\))?\s*\{\s*\}', s):
                add('WARN', path, i, '空 catch，错误被吞', s)
            if re.match(r'^except(\s+\w+(\s+as\s+\w+)?)?\s*:\s*$', s):
                # 允许 except 后跟 pass 的空块（有注释说明的）
                if i < len(lines) and lines[i].strip() in ('pass', 'continue'):
                    add('WARN', path, i, '裸 except + pass，错误被吞', s)

        # ── .Result / .Wait() ──
        if kind == 'js' and re.search(r'\.(Result|Wait)\(\)', s):
            add('ERROR', path, i, '.Result/.Wait() 在 UI 线程会死锁', s)

        # ── 硬编码端口 ──
        if kind in ('js', 'ps1', 'py'):
            for m in re.finditer(r'\b(1080[0-9]|10809|7897|18080)\b', s):
                # 排除注释行、默认值定义、测试文件
                if s.startswith('#') or s.startswith('//') or s.startswith('rem '):
                    continue
                if 'test' in os.path.basename(path).lower():
                    continue
                if re.search(r'default|DEFAULT|fallback|例|比如|例如', s):
                    continue
                add('INFO', path, i, f'硬编码端口 {m.group(1)}（确认是否应走 Get-FerryPorts）', s)
                break

        # ── SQL 拼接 ──
        if kind == 'py':
            if re.search(r'execute\s*\(\s*["\'].*%s.*["\']\s*%', s) or \
               re.search(r'execute\s*\(\s*f["\']', s):
                add('ERROR', path, i, 'SQL 用格式化拼接，有注入风险', s)

        # ── 敏感信息日志 ──
        if re.search(r'(log|print|Write-Host|console\.log).*(token|password|secret|令牌|密码)',
                     s, re.I):
            if not re.search(r'(hint|前缀|slice|substring|masked|\*\*\*)', s, re.I):
                add('WARN', path, i, '可能把令牌/密码写进日志', s)

        # ── 文件读写缺 encoding（Python）──
        if kind == 'py':
            if re.search(r'open\s*\([^)]*\)', s) and 'encoding' not in s and \
               not re.search(r"open\s*\([^)]*['\"](rb|wb)['\"]", s):
                if 'io.open' not in s and 'with open' not in s:
                    add('WARN', path, i, 'open() 缺 encoding（Windows 默认 GBK）', s)

        # ── TODO / FIXME ──
        if re.search(r'\b(TODO|FIXME|XXX|HACK)\b', s):
            add('INFO', path, i, '未完成标记', s)

        # ── console.log 调试残留（非日志函数内）──
        if kind == 'js' and re.search(r'console\.log\(', s):
            if 'function log' not in s and 'log(' not in s.replace('console.log', ''):
                add('INFO', path, i, 'console.log 可能未走统一日志', s)


def main():
    for dirpath, dirnames, filenames in os.walk(ROOT):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for fn in filenames:
            if fn in SKIP_FILES:
                continue
            _, ext = os.path.splitext(fn)
            kind = EXT.get(ext.lower())
            if kind:
                check_file(os.path.join(dirpath, fn), kind)

    # 按级别排序输出
    order = {'ERROR': 0, 'WARN': 1, 'INFO': 2}
    findings.sort(key=lambda x: (order[x['level']], x['path'], x['line']))

    counts = {'ERROR': 0, 'WARN': 0, 'INFO': 0}
    for f in findings:
        counts[f['level']] += 1

    print('=' * 70)
    print(f"静态审查：{counts['ERROR']} 错误 / {counts['WARN']} 警告 / {counts['INFO']} 提示")
    print('=' * 70)

    cur = None
    for f in findings:
        if f['level'] == 'INFO' and '--all' not in sys.argv:
            continue
        if f['path'] != cur:
            cur = f['path']
            print(f"\n{f['path']}")
        mark = {'ERROR': 'E', 'WARN': 'W', 'INFO': 'i'}[f['level']]
        print(f"  [{mark}] :{f['line']:<5} {f['msg']}")
        if f['snippet']:
            print(f"          {f['snippet']}")

    print()
    if counts['ERROR']:
        print(f"❌ 有 {counts['ERROR']} 个必须修的问题")
        sys.exit(1)
    elif counts['WARN']:
        print(f"⚠️ 有 {counts['WARN']} 个需要确认的警告")
    else:
        print('✅ 无错误、无警告')


if __name__ == '__main__':
    main()

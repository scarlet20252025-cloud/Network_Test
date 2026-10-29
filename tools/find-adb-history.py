"""查会话历史：上午到底是走哪条 adb 路径连上手机的。

用法： python tools\find-adb-history.py [关键字]
"""
import glob
import json
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

KEY = sys.argv[1] if len(sys.argv) > 1 else 'adb'
root = r'C:\Users\63526\.dsh\storages\session_projcache\sessions'
files = sorted(glob.glob(os.path.join(root, '*.json')), key=os.path.getmtime)

hits = 0
for f in files:
    if os.path.getmtime(f) < 1793200000:      # 只看今天（10-29）
        continue
    try:
        txt = open(f, encoding='utf-8', errors='replace').read()
    except Exception:
        continue
    if KEY not in txt:
        continue
    for m in re.finditer(r'.{0,80}' + re.escape(KEY) + r'.{0,110}', txt):
        s = m.group(0)
        if any(k in s for k in ('platform-tools', 'adb.exe', 'adb devices', 'adb install',
                                'scp', 'adb shell', 'adb push', 'xt:', 'remotePort')):
            print('%-30s | %s' % (os.path.basename(f)[:30], s.strip()[:150]))
            hits += 1
            if hits >= 40:
                print('... (截断)')
                raise SystemExit
print('--- 命中 %d 条 ---' % hits)

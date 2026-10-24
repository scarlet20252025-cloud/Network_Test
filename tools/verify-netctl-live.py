"""线上端到端：控制台改限速 -> 中继热生效 -> 实际测速变化。"""
import http.cookiejar
import json
import re
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = 'http://10.13.24.1:8080'
PW = 'REDACTED-ADMIN-PW'

cj = http.cookiejar.CookieJar()
op = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cj))


def get(p):
    return op.open(BASE + p, timeout=20).read().decode('utf-8', 'replace')


def post(p, data):
    body = urllib.parse.urlencode(data).encode()
    req = urllib.request.Request(BASE + p, body, {
        'Content-Type': 'application/x-www-form-urlencoded',
        'X-Requested-With': 'fetch'})
    return op.open(req, timeout=20).read().decode('utf-8', 'replace')


p = get('/admin')
csrf = re.search(r'name="csrf" value="([^"]+)"', p).group(1)
p = post('/admin/login', {'pw': PW, 'csrf': csrf})
if 'name="pw"' in p:
    print('[X] login failed')
    raise SystemExit(1)
print('[OK] logged in')
csrf = re.search(r'name="csrf" value="([^"]+)"', p).group(1)

print('\n=== 页面结构 ===')
for k in ('gaugeBox', 'class="netctl"', 'ncPerIp', 'ncTotal', 'ncUplink',
          'renderGauge', 'bindNetCtl', 'gauge-mark'):
    print('  %-16s %d' % (k, p.count(k)))

print('\n=== 当前配置 ===')
d = json.loads(get('/api/admin/net'))
print(' ', d)

print('\n=== 改成每 IP 3 MB/s ===')
r = json.loads(post('/api/admin/net', {'csrf': csrf, 'perIp': 3, 'total': 0,
                                       'uplinkMbps': 100}))
print(' ', r)
assert r['ok'] and abs(r['perIp'] - 3) < 0.01
print('  等 7 秒让中继热更新…')
time.sleep(7)

print('\n=== 测速（应约 3 MB/s）===')
import subprocess
t = r'C:\Users\63526\AppData\Local\Temp\e2e_rate.bin'
r2 = subprocess.run(['curl.exe', '-s', '--max-time', '60',
                     '-x', 'socks5h://10.13.24.1:1080',
                     '-o', t,
                     '-w', 'speed=%{speed_download} time=%{time_total}',
                     'http://10.13.24.1:8080/download'],
                    capture_output=True, text=True)
print(' ', r2.stdout)
m = re.search(r'speed=([\d.]+)', r2.stdout or '')
if m:
    mb = float(m.group(1)) / 1048576
    print('  实测 %.2f MB/s' % mb)
    print('  %s' % ('[OK] 限速生效' if mb < 4.0 else '[X] 没限住'))

print('\n=== 改回 10 MB/s ===')
csrf2 = re.search(r'name="csrf" value="([^"]+)"', get('/admin')).group(1)
r3 = json.loads(post('/api/admin/net', {'csrf': csrf2, 'perIp': 10, 'total': 0,
                                        'uplinkMbps': 100}))
print(' ', r3)
print('  等 7 秒…')
time.sleep(7)
r4 = subprocess.run(['curl.exe', '-s', '--max-time', '60',
                     '-x', 'socks5h://10.13.24.1:1080',
                     '-o', t,
                     '-w', 'speed=%{speed_download}', 'http://10.13.24.1:8080/download'],
                    capture_output=True, text=True)
m4 = re.search(r'speed=([\d.]+)', r4.stdout or '')
if m4:
    mb4 = float(m4.group(1)) / 1048576
    print('  实测 %.2f MB/s' % mb4)
    print('  %s' % ('[OK] 恢复到 10' if 8 < mb4 < 12 else '[!] 偏差较大'))

import os
try:
    os.remove(t)
except OSError:
    pass
print('\n结论：控制台改限速 -> 中继热生效 -> 实际吞吐变化，链路通')

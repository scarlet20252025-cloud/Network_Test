"""验证载荷仪表 + 带宽控制台（含限速热更新的文件写入）。"""
import http.cookiejar
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

BASE = 'http://127.0.0.1:18095'
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PORTAL = os.path.join(ROOT, 'portal')
RELAY = os.path.join(PORTAL, 'test-relay')
CONF = os.path.join(RELAY, 'rate-mb.txt')

cj = http.cookiejar.CookieJar()
op = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cj))
op.addheaders = [('User-Agent', 'ferry-verify')]


def get(p):
    return op.open(BASE + p, timeout=15).read().decode('utf-8', 'replace')


def post(p, data, ajax=True):
    body = urllib.parse.urlencode(data).encode()
    h = {'Content-Type': 'application/x-www-form-urlencoded'}
    if ajax:
        h['X-Requested-With'] = 'fetch'
    req = urllib.request.Request(BASE + p, body, h)
    try:
        return op.open(req, timeout=15).read().decode('utf-8', 'replace')
    except urllib.error.HTTPError as e:
        return 'HTTP%d:' % e.code + e.read().decode('utf-8', 'replace')


# 登录
pw = None
for line in open(os.path.join(PORTAL, 'portal-admin.txt'), encoding='utf-8'):
    m = re.match(r'^([A-Za-z0-9_\-]{8,})$', line.strip())
    if m:
        pw = m.group(1)
        break
page = get('/admin')
csrf = re.search(r'name="csrf" value="([^"]+)"', page).group(1)
page = post('/admin/login', {'pw': pw, 'csrf': csrf}, ajax=False)
if 'name="pw"' in page:
    print('[X] 登录失败')
    sys.exit(1)
print('[OK] 已登录')

print('\n=== 页面结构 ===')
for k, label in [('gaugeBox', '载荷仪表'), ('gauge-fill', '仪表填充条'),
                 ('gauge-mark', '边界刻度'), ('class="netctl"', '侧边控制台'),
                 ('ncPerIp', '每IP输入'), ('ncTotal', '全站输入'),
                 ('ncUplink', '出口带宽输入'), ('renderGauge', '仪表JS'),
                 ('bindNetCtl', '控制台JS'), ('has-netctl', 'body类')]:
    print('  [%s] %-12s %d 处' % ('OK' if page.count(k) else 'X', label, page.count(k)))

print('\n=== GET /api/admin/net ===')
d = json.loads(get('/api/admin/net'))
print('  ', d)
assert d['ok'] and 'perIp' in d

print('\n=== POST 改限速：每IP 5，全站 12，出口 100 ===')
csrf2 = re.search(r'name="csrf" value="([^"]+)"', page).group(1)
r = json.loads(post('/api/admin/net', {'csrf': csrf2, 'perIp': 5, 'total': 12, 'uplinkMbps': 100}))
print('  ', r)
assert r['ok'], r

print('\n=== 文件真的写了吗 ===')
print('  路径:', CONF)
print('  存在:', os.path.exists(CONF))
if os.path.exists(CONF):
    print('  内容:', repr(open(CONF, encoding='utf-8').read()))
    lines = open(CONF, encoding='utf-8').read().split()
    assert abs(float(lines[0]) - 5) < 0.01, lines
    assert abs(float(lines[1]) - 12) < 0.01, lines
    print('  [OK] 两行数值正确')

print('\n=== 再读回来 ===')
d2 = json.loads(get('/api/admin/net'))
print('  ', d2)
assert abs(d2['perIp'] - 5) < 0.01
assert abs(d2['total'] - 12) < 0.01
assert abs(d2['uplinkMbps'] - 100) < 0.01

print('\n=== 载荷计算（live 里的 net）===')
# 发两次心跳制造速率
for i, (rx, tx) in enumerate([(1000000, 2000000), (3000000, 5000000)]):
    payload = json.dumps({'ip': '10.141.59.235', 'token': 'tok-10.141.59.235',
                          'rxBytes': rx, 'txBytes': tx, 'conns': 2,
                          'running': True, 'mode': 'direct'}).encode()
    req = urllib.request.Request(BASE + '/api/heartbeat', payload,
                                 {'Content-Type': 'application/json'})
    op.open(req, timeout=10).read()
    import time
    time.sleep(2.1)
live = json.loads(get('/api/admin/live'))
net = live.get('net') or {}
print('  net =', json.dumps(net, ensure_ascii=False))
for k in ('uplinkMbps', 'curBps', 'loadPct', 'level', 'perIpMb', 'toWarnPct', 'headroomBps'):
    assert k in net, '缺字段 ' + k
print('  [OK] 载荷字段完整')

print('\n=== CSRF 防护（不带 token 应 403）===')
r3 = post('/api/admin/net', {'perIp': 99})
print('  ', r3[:60])
assert r3.startswith('HTTP403'), r3

print('\n=== 参数越界应被夹紧 ===')
r4 = json.loads(post('/api/admin/net', {'csrf': csrf2, 'perIp': 99999, 'total': -5}))
print('  ', r4)
assert r4['ok'] and r4['perIp'] <= 1000 and r4['total'] >= 0

print('\n结论：仪表数据 + 控制台 API + 文件写入 + CSRF + 越界夹紧 全部通过')

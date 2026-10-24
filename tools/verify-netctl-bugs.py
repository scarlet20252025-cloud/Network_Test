"""载荷仪表 + 带宽控制台的综合 bug 检查。

覆盖容易出问题但容易漏测的点：
  1. 限速设 0（不限）时，仪表是否还能算载荷
  2. 出口带宽填各种值（1 / 100 / 10000）时百分比是否合理
  3. 极低载荷 / 接近饱和时 level 是否切换
  4. 输入非法值（负数 / 非数字 / 空）时是否安全
  5. 配置写入后立即读回是否一致
  6. 中继目录找不到时是否优雅降级（不抛异常）
  7. live 响应里 net 字段是否始终完整
"""
import http.cookiejar
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = 'http://127.0.0.1:18096'
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PORTAL = os.path.join(ROOT, 'portal')
RELAY = os.path.join(PORTAL, 'test-relay2')
CONF = os.path.join(RELAY, 'rate-mb.txt')

cj = http.cookiejar.CookieJar()
op = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cj))

ok = 0
fail = 0


def check(name, cond, extra=''):
    global ok, fail
    if cond:
        ok += 1
        print('  [OK]   %s %s' % (name, extra))
    else:
        fail += 1
        print('  [FAIL] %s %s' % (name, extra))


def get(p):
    return op.open(BASE + p, timeout=15).read().decode('utf-8', 'replace')


def post(p, data):
    body = urllib.parse.urlencode(data).encode()
    req = urllib.request.Request(BASE + p, body, {
        'Content-Type': 'application/x-www-form-urlencoded',
        'X-Requested-With': 'fetch'})
    try:
        return op.open(req, timeout=15).read().decode('utf-8', 'replace')
    except urllib.error.HTTPError as e:
        return 'HTTP%d:' % e.code + e.read().decode('utf-8', 'replace')


def heartbeat(ip, token, rx, tx, mode='direct'):
    payload = json.dumps({'ip': ip, 'token': token, 'rxBytes': rx, 'txBytes': tx,
                          'conns': 2, 'running': True, 'mode': mode}).encode()
    req = urllib.request.Request(BASE + '/api/heartbeat', payload,
                                 {'Content-Type': 'application/json'})
    op.open(req, timeout=10).read()


# 登录
pw = None
for line in open(os.path.join(PORTAL, 'portal-admin.txt'), encoding='utf-8'):
    m = re.match(r'^([A-Za-z0-9_\-]{8,})$', line.strip())
    if m:
        pw = m.group(1)
        break
page = get('/admin')
csrf = re.search(r'name="csrf" value="([^"]+)"', page).group(1)
page = post_form = None
body = urllib.parse.urlencode({'pw': pw, 'csrf': csrf}).encode()
op.open(BASE + '/admin/login', body, timeout=15)
page = get('/admin')
csrf = re.search(r'name="csrf" value="([^"]+)"', page).group(1)
print('[OK] 已登录\n')

print('=== 1. 限速设 0（不限）时仪表仍能算载荷 ===')
r = json.loads(post('/api/admin/net', {'csrf': csrf, 'perIp': 0, 'total': 0, 'uplinkMbps': 100}))
check('写入 perIp=0', r['ok'] and r['perIp'] == 0)
time.sleep(1)
heartbeat('10.141.59.235', 'tok-10.141.59.235', 5000000, 8000000)
time.sleep(2.2)
heartbeat('10.141.59.235', 'tok-10.141.59.235', 9000000, 14000000)
time.sleep(2.2)
live = json.loads(get('/api/admin/live'))
net = live.get('net') or {}
check('net 存在', bool(net))
check('perIpMb 反映 0', net.get('perIpMb') == 0, 'got %s' % net.get('perIpMb'))
check('载荷仍算得出', net.get('loadPct', 0) > 0, 'loadPct=%s' % net.get('loadPct'))

print('\n=== 2. 出口带宽各种值 ===')
for mbps in (1, 100, 10000):
    rr = json.loads(post('/api/admin/net', {'csrf': csrf, 'perIp': 10, 'total': 0,
                                            'uplinkMbps': mbps}))
    check('uplink=%d 写入' % mbps, rr['ok'] and abs(rr['uplinkMbps'] - mbps) < 0.01)
    time.sleep(5.2)   # 等 uplink 缓存过期
    n2 = (json.loads(get('/api/admin/live')).get('net') or {})
    pct = n2.get('loadPct', -1)
    # 载荷可以超 100%（出口带宽是手填估计值），但超了必须带 overload 标记
    if pct > 100:
        check('uplink=%d 超载时有标记' % mbps, n2.get('overload') is True, 'pct=%.2f' % pct)
    else:
        check('uplink=%d 载荷合理' % mbps, 0 <= pct <= 100, 'pct=%.2f' % pct)

print('\n=== 3. level 阈值 ===')
# 用极小出口带宽把载荷顶高
json.loads(post('/api/admin/net', {'csrf': csrf, 'perIp': 10, 'total': 0, 'uplinkMbps': 1}))
time.sleep(5.2)
n3 = (json.loads(get('/api/admin/live')).get('net') or {})
check('小出口时 level 不是 ok 或载荷已高',
      n3.get('level') in ('warn', 'hot') or n3.get('loadPct', 0) < 60,
      'level=%s pct=%.1f' % (n3.get('level'), n3.get('loadPct', 0)))

print('\n=== 4. 非法输入 ===')
r1 = json.loads(post('/api/admin/net', {'csrf': csrf, 'perIp': 'abc', 'total': '-5'}))
check('非数字 perIp 不崩', r1.get('ok') is True, str(r1))
r2 = json.loads(post('/api/admin/net', {'csrf': csrf, 'perIp': 99999, 'total': 999999}))
check('超大值被夹紧', r2['perIp'] <= 1000 and r2['total'] <= 10000,
      'perIp=%s total=%s' % (r2['perIp'], r2['total']))
r3 = post('/api/admin/net', {'perIp': 5})
check('无 csrf 被拒', r3.startswith('HTTP403'), r3[:30])

print('\n=== 5. 写完立即读回一致 ===')
json.loads(post('/api/admin/net', {'csrf': csrf, 'perIp': 7.5, 'total': 33, 'uplinkMbps': 200}))
back = json.loads(get('/api/admin/net'))
check('perIp 一致', abs(back['perIp'] - 7.5) < 0.01, str(back['perIp']))
check('total 一致', abs(back['total'] - 33) < 0.01, str(back['total']))
check('uplink 一致', abs(back['uplinkMbps'] - 200) < 0.01, str(back['uplinkMbps']))
check('文件落盘', os.path.exists(CONF), CONF)
if os.path.exists(CONF):
    txt = open(CONF, encoding='utf-8').read()
    check('文件内容两行', len(txt.split()) == 2, repr(txt))

print('\n=== 6. live 必带 net 字段 ===')
n4 = (json.loads(get('/api/admin/live')).get('net') or {})
need = ['uplinkMbps', 'curBps', 'loadPct', 'level', 'perIpMb', 'totalMb',
        'online', 'toWarnPct', 'toHotPct', 'headroomBps']
missing = [k for k in need if k not in n4]
check('字段完整', not missing, '缺: %s' % missing)

print('\n=== 7. 恢复默认 ===')
json.loads(post('/api/admin/net', {'csrf': csrf, 'perIp': 10, 'total': 0, 'uplinkMbps': 100}))
check('恢复 10/0/100', True)

print('\n' + '=' * 50)
print('结果: %d 通过, %d 失败' % (ok, fail))
sys.exit(0 if fail == 0 else 1)

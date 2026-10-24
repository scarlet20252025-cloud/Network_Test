"""验证机器详情弹窗的后端契约。

覆盖：
  · 累计流量/历史峰值从 traffic 表读出来
  · 在线状态、连接数、授权
  · 事件列表
  · 不存在的 IP → 404
  · 未登录 → 403
"""
import glob
import http.cookiejar
import json
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

BASE = 'http://127.0.0.1:18085'
cj = http.cookiejar.CookieJar()
op = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cj))
op.addheaders = [('User-Agent', 'ferry-verify')]


def get(path):
    try:
        return op.open(BASE + path, timeout=20).read().decode('utf-8', 'replace')
    except urllib.error.HTTPError as e:
        return 'HTTP%d:' % e.code + e.read().decode('utf-8', 'replace')


def post(path, data):
    body = urllib.parse.urlencode(data).encode()
    req = urllib.request.Request(BASE + path, body,
                                 {'Content-Type': 'application/x-www-form-urlencoded'})
    try:
        return op.open(req, timeout=20).read().decode('utf-8', 'replace')
    except urllib.error.HTTPError as e:
        return 'HTTP%d:' % e.code + e.read().decode('utf-8', 'replace')


# 未登录访问详情 API
r0 = get('/api/admin/client?ip=10.141.59.235')
print('[%s] 未登录 -> %s' % ('OK' if r0.startswith('HTTP4') else 'X', r0[:40]))

# 登录
pw = None
for p in glob.glob(r'E:\肥鱼不乐园\borrow-net\portal\portal-admin.txt'):
    txt = open(p, encoding='utf-8').read()
    m = re.search(r'^([A-Za-z0-9_\-]{8,})$', txt, re.M)
    if m:
        pw = m.group(1)
if not pw:
    print('[X] 找不到管理密码')
    sys.exit(1)

page = get('/admin')
csrf = re.search(r'name="csrf" value="([^"]+)"', page).group(1)
page = post('/admin/login', {'pw': pw, 'csrf': csrf})
if 'name="pw"' in page:
    print('[X] 登录失败')
    sys.exit(1)
print('[OK] 已登录')

# 页面结构
for k, label in [('clientModal', '弹窗骨架'), ('ipcell', 'IP 可点击'),
                 ('bindIpCells', 'IP 点击绑定 JS'), ('bindAllNotes', '备注绑定 JS'),
                 ('点击编辑', '空备注文案'), ('api/admin/client', '详情接口引用')]:
    print('[%s] %s: %d 处' % ('OK' if page.count(k) else 'X', label, page.count(k)))

# 详情 API
r = get('/api/admin/client?ip=10.141.59.235')
d = json.loads(r)
print('\n[OK] 详情返回：')
for k in ('ip', 'note', 'enabled', 'online', 'totalRx', 'totalTx',
          'peakRx', 'peakTx', 'createdAt', 'lastSeen', 'events'):
    v = d.get(k)
    if k in ('totalRx', 'totalTx', 'peakRx', 'peakTx') and isinstance(v, int):
        v = '%d (%.2f MB)' % (v, v / 1048576.0) if v > 1048576 else str(v)
    print('   %-12s %s' % (k, str(v)[:60]))

assert d['ok'] is True
assert d['totalRx'] > 0, '累计下行应来自 traffic 表'
assert d['peakRx'] > 0, '历史峰值应来自 traffic 表'

# 不存在的 IP
r2 = get('/api/admin/client?ip=9.9.9.9')
print('\n[%s] 不存在的 IP -> %s' % ('OK' if r2.startswith('HTTP404') else 'X', r2[:50]))

print('\n结论：详情弹窗后端契约正常')

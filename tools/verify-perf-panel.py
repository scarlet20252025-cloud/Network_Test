"""验证性能面板：折线图 + 六卡片。

检查点：
  1. 页面结构（perfpanel / chart svg / 六张 pcard）
  2. 旧元素是否彻底移除（否则 render 会抛 TypeError）
  3. live API 是否返回 series / totalRx / totalTx / memMB
  4. series 会随时间增长（采样点真的在追加）
  5. 卡片数值能对上（累计量 = traffic 表之和）
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

BASE = 'http://127.0.0.1:18101'
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PORTAL = os.path.join(ROOT, 'portal')

ok = fail = 0


def check(name, cond, extra=''):
    global ok, fail
    if cond:
        ok += 1
        print('  [OK]   %s %s' % (name, extra))
    else:
        fail += 1
        print('  [FAIL] %s %s' % (name, extra))


cj = http.cookiejar.CookieJar()
op = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cj))


def get(p):
    return op.open(BASE + p, timeout=15).read().decode('utf-8', 'replace')


def post(p, data):
    body = urllib.parse.urlencode(data).encode()
    req = urllib.request.Request(BASE + p, body, {
        'Content-Type': 'application/x-www-form-urlencoded',
        'X-Requested-With': 'fetch'})
    return op.open(req, timeout=15).read().decode('utf-8', 'replace')


pw = None
for line in open(os.path.join(PORTAL, 'portal-admin.txt'), encoding='utf-8'):
    m = re.match(r'^([A-Za-z0-9_\-]{8,})$', line.strip())
    if m:
        pw = m.group(1)
        break
page = get('/admin')
csrf = re.search(r'name="csrf" value="([^"]+)"', page).group(1)
post('/admin/login', {'pw': pw, 'csrf': csrf})
page = get('/admin')
print('[OK] 已登录\n')

print('=== 1. 页面结构 ===')
for k, label in [
    ('class="perfpanel"', '性能面板容器'),
    ('id="perfChart"', '折线图 SVG'),
    ('id="chartLineDown"', '下载折线'),
    ('id="chartLineUp"', '上传折线'),
    ('id="chartAreaDown"', '下载面积填充'),
    ('id="chartGrid"', '网格'),
    ('class="perfgrid"', '六卡片容器'),
    ('id="pcTxSpd"', '上传速度卡'),
    ('id="pcRxSpd"', '下载速度卡'),
    ('id="pcConns"', '活跃连接卡'),
    ('id="pcTxTot"', '上传量卡'),
    ('id="pcRxTot"', '下载量卡'),
    ('id="pcMem"', '服务端内存卡'),
    ('function renderChart', '图表 JS'),
    ('function buildPoints', '坐标计算 JS'),
    ('function renderPerf', '面板更新 JS'),
]:
    check(label, page.count(k) > 0, '(%d)' % page.count(k))

print('\n=== 2. 旧元素彻底移除（否则 render 抛 TypeError）===')
for k in ['id="lvOnline"', 'id="lvConns"', 'id="lvRx"', 'id="lvTx"',
          'class="livecard"', 'function setHot']:
    check('已移除 ' + k, page.count(k) == 0, '(%d)' % page.count(k))
check('perfgrid 有 6 张卡', page.count('class="pcard') == 6,
      '(%d)' % page.count('class="pcard'))

print('\n=== 3. live API 新字段 ===')
live = json.loads(get('/api/admin/live'))
need = ['series', 'totalRx', 'totalTx', 'memMB']
for k in need:
    check('字段 ' + k, k in live, repr(live.get(k))[:40])
check('series 是列表', isinstance(live.get('series'), list),
      'len=%s' % len(live.get('series') or []))
check('series 元素含 t/rx/tx',
      bool(live.get('series')) and
      all(x in live['series'][0] for x in ('t', 'rx', 'tx')),
      str(live['series'][0] if live.get('series') else None))

print('\n=== 4. 采样点会增长 ===')
n1 = len(live.get('series') or [])
# 发几次心跳并等缓存过期，让 sample_live 被重新采样
for i in range(3):
    payload = json.dumps({'ip': '10.141.59.235', 'token': 'tok-10.141.59.235',
                          'rxBytes': 5000000 + i * 3000000,
                          'txBytes': 9000000 + i * 1000000,
                          'conns': 3, 'running': True, 'mode': 'direct'}).encode()
    req = urllib.request.Request(BASE + '/api/heartbeat', payload,
                                 {'Content-Type': 'application/json'})
    op.open(req, timeout=10).read()
    time.sleep(2.2)
    get('/api/admin/live')          # 触发采样
n2 = len(json.loads(get('/api/admin/live')).get('series') or [])
check('采样点增加', n2 > n1, '%d -> %d' % (n1, n2))

print('\n=== 5. 累计量与 traffic 表一致 ===')
import sqlite3
c = sqlite3.connect(os.path.join(PORTAL, 'test-perf.db'))
c.row_factory = sqlite3.Row
rows = c.execute('SELECT ip,total_rx,total_tx FROM traffic').fetchall()
db_rx = sum(r['total_rx'] for r in rows)
db_tx = sum(r['total_tx'] for r in rows)
c.close()
live2 = json.loads(get('/api/admin/live'))
check('totalRx >= traffic 表之和', live2['totalRx'] >= db_rx,
      'api=%d db=%d' % (live2['totalRx'], db_rx))
check('totalTx >= traffic 表之和', live2['totalTx'] >= db_tx,
      'api=%d db=%d' % (live2['totalTx'], db_tx))

print('\n=== 6. memMB 合理 ===')
check('memMB 在 1..2000 之间', 1 <= (live2.get('memMB') or 0) <= 2000,
      '%s MB' % live2.get('memMB'))

print('\n' + '=' * 50)
print('结果: %d 通过, %d 失败' % (ok, fail))
sys.exit(0 if fail == 0 else 1)

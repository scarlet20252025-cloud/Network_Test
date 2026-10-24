"""核实上传/下载方向有没有弄反（先登录再测）。"""
import http.cookiejar
import json
import re
import time
import urllib.parse
import urllib.request

BASE = 'http://127.0.0.1:18101'
PORTAL = r'E:\肥鱼不乐园\borrow-net\portal'

cj = http.cookiejar.CookieJar()
op = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cj))


def get(p):
    return op.open(BASE + p, timeout=15).read().decode('utf-8', 'replace')


def post(p, data):
    body = urllib.parse.urlencode(data).encode()
    req = urllib.request.Request(BASE + p, body,
                                 {'Content-Type': 'application/x-www-form-urlencoded'})
    return op.open(req, timeout=15).read().decode('utf-8', 'replace')


def hb(ip, rx, tx):
    payload = json.dumps({'ip': ip, 'token': 'tok-' + ip,
                          'rxBytes': rx, 'txBytes': tx,
                          'conns': 1, 'running': True,
                          'mode': 'direct'}).encode()
    req = urllib.request.Request(BASE + '/api/heartbeat', payload,
                                 {'Content-Type': 'application/json'})
    return json.loads(op.open(req, timeout=10).read().decode('utf-8'))


# 先登录（live API 要管理员）
pw = None
for line in open(PORTAL + r'\portal-admin.txt', encoding='utf-8'):
    m = re.match(r'^([A-Za-z0-9_\-]{8,})$', line.strip())
    if m:
        pw = m.group(1)
        break
page = get('/admin')
csrf = re.search(r'name="csrf" value="([^"]+)"', page).group(1)
post('/admin/login', {'pw': pw, 'csrf': csrf})
page = get('/admin')
print('[OK] 已登录')

IP = '10.141.59.235'

print('\n=== 场景 A：只有 rxBytes 在涨（应算「下载」）===')
hb(IP, 0, 0)
time.sleep(1.5)
r = hb(IP, 10 * 1024 * 1024, 0)
print('  心跳返回: rxBps=%s txBps=%s' % (r.get('rxBps'), r.get('txBps')))
live = json.loads(get('/api/admin/live'))
print('  live 顶层: rxBps=%s txBps=%s' % (live.get('rxBps'), live.get('txBps')))
rx_only_ok = (r.get('rxBps') or 0) > 1000000 and (r.get('txBps') or 0) < 1000
print('  [%s] rx 涨 → rxBps 有值、txBps 为 0' % ('OK' if rx_only_ok else 'FAIL'))

print('\n=== 场景 B：只有 txBytes 在涨（应算「上传」）===')
time.sleep(1.5)
r2 = hb(IP, 10 * 1024 * 1024, 5 * 1024 * 1024)
print('  心跳返回: rxBps=%s txBps=%s' % (r2.get('rxBps'), r2.get('txBps')))
tx_only_ok = (r2.get('txBps') or 0) > 500000 and (r2.get('rxBps') or 0) < 1000
print('  [%s] tx 涨 → txBps 有值、rxBps 为 0' % ('OK' if tx_only_ok else 'FAIL'))

print('\n=== 页面里 rxBps/txBps 绑到哪张卡 ===')
m_rx = re.search(r"set\('(pc\w+)',\s*fmtSpeed\(d\.rxBps", page)
m_tx = re.search(r"set\('(pc\w+)',\s*fmtSpeed\(d\.txBps", page)
print('  d.rxBps ->', m_rx.group(1) if m_rx else '未找到')
print('  d.txBps ->', m_tx.group(1) if m_tx else '未找到')

for pid in ('pcRxSpd', 'pcTxSpd', 'pcRxTot', 'pcTxTot'):
    i = page.find('id="' + pid + '"')
    if i > 0:
        seg = page[max(0, i - 340):i]
        lab = re.findall(r'class="plbl">([^<]+)<', seg)
        print('  %-8s 标签 = %s' % (pid, lab[-1] if lab else '?'))

mapping_ok = bool(m_rx and m_rx.group(1) == 'pcRxSpd' and
                  m_tx and m_tx.group(1) == 'pcTxSpd')
print('  [%s] rxBps→pcRxSpd（下载）, txBps→pcTxSpd（上传）'
      % ('OK' if mapping_ok else 'FAIL'))

print('\n结论:', '映射正确' if (rx_only_ok and tx_only_ok and mapping_ok) else '有问题')

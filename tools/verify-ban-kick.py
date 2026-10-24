"""验证：账号被封禁时，客户端能否靠心跳快速切回直连。

不依赖界面，直接走 API 契约：
  1. 正常账号：心跳返回 enabled=true
  2. 管理员停用（clients.enabled=0）：心跳返回 enabled=false
  3. 授权到期：心跳返回 expired=true, enabled=false
  4. 续期后自动恢复：enabled 回到 true（软状态，不改库）

这四条决定了客户端「封禁后 5 秒内断开」是否真的成立。
"""
import http.cookiejar
import json
import os
import re
import sqlite3
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = 'http://127.0.0.1:18097'
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PORTAL = os.path.join(ROOT, 'portal')
DB = os.path.join(PORTAL, 'test-ban.db')

cj = http.cookiejar.CookieJar()
op = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cj))

ok = fail = 0


def check(name, cond, extra=''):
    global ok, fail
    if cond:
        ok += 1
        print('  [OK]   %s %s' % (name, extra))
    else:
        fail += 1
        print('  [FAIL] %s %s' % (name, extra))


def hb(ip, token, rx=1000, tx=2000):
    payload = json.dumps({'ip': ip, 'token': token, 'rxBytes': rx, 'txBytes': tx,
                          'conns': 1, 'running': True, 'mode': 'direct'}).encode()
    req = urllib.request.Request(BASE + '/api/heartbeat', payload,
                                 {'Content-Type': 'application/json'})
    try:
        return json.loads(op.open(req, timeout=10).read().decode('utf-8'))
    except urllib.error.HTTPError as e:
        return {'http': e.code}


IP = '10.141.59.235'
TOK = 'tok-ban'


def set_client(enabled=1, expires_at=0):
    c = sqlite3.connect(DB)
    c.execute('UPDATE clients SET enabled=?, expires_at=? WHERE ip=?',
              (enabled, expires_at, IP))
    c.commit()
    c.close()


print('=== 1. 正常账号 ===')
set_client(1, 0)
d = hb(IP, TOK)
check('心跳 ok', d.get('ok') is True, str(d))
check('enabled=true', d.get('enabled') is True, str(d.get('enabled')))
check('expired=false', d.get('expired') is False, str(d.get('expired')))

print('\n=== 2. 管理员停用 ===')
set_client(0, 0)
time.sleep(2.1)
d = hb(IP, TOK, 5000, 9000)
check('enabled=false', d.get('enabled') is False, str(d.get('enabled')))
check('expired=false（是停用不是到期）', d.get('expired') is False, str(d.get('expired')))

print('\n=== 3. 重新启用 ===')
set_client(1, 0)
time.sleep(2.1)
d = hb(IP, TOK, 9000, 15000)
check('enabled 回到 true', d.get('enabled') is True, str(d.get('enabled')))

print('\n=== 4. 授权到期 ===')
set_client(1, int(time.time()) - 60)   # 1 分钟前到期
time.sleep(2.1)
d = hb(IP, TOK, 13000, 21000)
check('expired=true', d.get('expired') is True, str(d.get('expired')))
check('enabled=false', d.get('enabled') is False, str(d.get('enabled')))

print('\n=== 5. 续期后自动恢复（软状态）===')
future = int(time.time()) + 86400 * 30
set_client(1, future)
time.sleep(2.1)
d = hb(IP, TOK, 17000, 27000)
check('enabled 恢复', d.get('enabled') is True, str(d.get('enabled')))
check('expired 恢复', d.get('expired') is False, str(d.get('expired')))

print('\n=== 6. /api/license 与心跳一致 ===')
qs = urllib.parse.urlencode({'ip': IP, 'token': TOK})
lic = json.loads(op.open(BASE + '/api/license?' + qs, timeout=10).read().decode('utf-8'))
d = hb(IP, TOK, 20000, 31000)
check('两接口 enabled 一致', lic.get('enabled') == d.get('enabled'),
      'license=%s heartbeat=%s' % (lic.get('enabled'), d.get('enabled')))
check('两接口 expired 一致', lic.get('expired') == d.get('expired'),
      'license=%s heartbeat=%s' % (lic.get('expired'), d.get('expired')))

print('\n=== 7. 无效 token 不泄露状态 ===')
d = hb(IP, 'wrong-token')
check('错 token 被拒', d.get('http') == 401 or d.get('ok') is False, str(d))

print('\n' + '=' * 50)
print('结果: %d 通过, %d 失败' % (ok, fail))
sys.exit(0 if fail == 0 else 1)

"""验证管理面板的「设备维度」+ 踢下线/删除两条链路（2026-10-29 新增）。

为什么单独一个脚本、不并进 verify-portal-pages.py：
    那边验的是三个公开页面和反馈链路；设备管理有一半行为**只看页面验不出来** ——
    「踢下线」之后设备不能靠下一次心跳自己复活。这曾经是个真 bug：
    device_bind 对已存在的设备无条件写 `enabled = 1`，于是管理员点了「踢下线」，
    App 下次启动一拨 /api/license 就把它写回启用，「踢」等于没踢。
    所以这里同时查页面（/admin/users 有没有设备表和按钮）和接口
    （被停用后 /api/license 必须 409 + device_disabled，且 DB 里 enabled 仍是 0）。

前提：本地测试实例已在跑，且用独立的库：
    cd portal
    python -u portal-server.py --port 18090 --db test-device.db

用法：
    python tools/verify-portal-device.py
    PORTAL_BASE=http://127.0.0.1:18090 PORTAL_DB=portal/test-device.db python tools/verify-portal-device.py

脚本会自己造一条 clients 记录 + 两台设备，结束前清理干净（不会碰线上库，
因为按 PORTAL_DB 指定的库操作；默认指向测试库）。
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

try:                      # Git Bash / cmd 的代码页不是 UTF-8，不重设会满屏乱码
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

BASE = os.environ.get('PORTAL_BASE', 'http://127.0.0.1:18090')
PORTAL = os.environ.get('PORTAL_DIR', r'E:\肥鱼不乐园\borrow-net\portal')
DBPATH = os.environ.get('PORTAL_DB', os.path.join(PORTAL, 'test-device.db'))

stamp = str(int(time.time()))
TOKEN = 'devtok' + stamp
CLIENT_IP = '10.99.%d.%d' % (int(stamp[-3:]) % 200 + 20, 9)
DEV_A = 'devtest-a-' + stamp
DEV_B = 'devtest-b-' + stamp
HEARTBEAT_IP = '127.0.0.1'    # 必须是本机来源网段，否则 /api/device/restore 会先被 IP 校验挡住

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


def get_code(p):
    """返回 (status, body)。4xx 也要拿到 body —— send_json 的错误说明在里面。"""
    try:
        r = op.open(BASE + p, timeout=15)
        return r.status, r.read().decode('utf-8', 'replace')
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode('utf-8', 'replace')


def post(p, data):
    body = urllib.parse.urlencode(data).encode()
    req = urllib.request.Request(BASE + p, body,
                                 {'Content-Type': 'application/x-www-form-urlencoded'})
    try:
        r = op.open(req, timeout=20)
        return r.status, r.read().decode('utf-8', 'replace')
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode('utf-8', 'replace')


def db():
    c = sqlite3.connect(DBPATH, timeout=10)
    c.row_factory = sqlite3.Row
    return c


def csrf_of(html_text):
    m = re.search(r'name="csrf" value="([^"]+)"', html_text)
    return m.group(1) if m else ''


def admin_pw():
    for line in open(os.path.join(PORTAL, 'portal-admin.txt'), encoding='utf-8'):
        m = re.match(r'^([A-Za-z0-9_\-]{8,})$', line.strip())
        if m:
            return m.group(1)
    raise SystemExit('找不到管理员密码（portal/portal-admin.txt）')


def jload(body):
    try:
        return json.loads(body)
    except Exception:
        return {}


# ───────────────────────── 开始 ─────────────────────────
print('=== 0. 造一条账号（clients）===')
c = db()
c.execute('DELETE FROM devices WHERE device_id IN (?,?)', (DEV_A, DEV_B))
c.execute('DELETE FROM clients WHERE token = ?', (TOKEN,))
c.execute('INSERT INTO clients (ip, token, enabled, created_at, last_seen, invite_id, expires_at) '
          'VALUES (?,?,1,?,?,0,?)',
          (CLIENT_IP, TOKEN, int(time.time()), int(time.time()), int(time.time()) + 86400))
c.commit()
check('账号已建', c.execute('SELECT COUNT(*) n FROM clients WHERE token = ?',
                          (TOKEN,)).fetchone()['n'] == 1, '(%s)' % CLIENT_IP)

print()
print('=== 1. 两台设备共用一个 token（心跳登记）===')
for dev, name in ((DEV_A, 'A'), (DEV_B, 'B')):
    st, body = get_code('/api/license?token=%s&device_id=%s&ip=%s&platform=android'
                        '&model=TestPhone-%s&app_version=1.0.47'
                        % (TOKEN, dev, HEARTBEAT_IP, name))
    j = jload(body)
    check('设备 %s 登记成功' % name, st == 200 and j.get('ok') is True,
          '(HTTP %d) %s' % (st, body[:80] if st != 200 else ''))
    if name == 'B':
        check('两台设备都算进名额', j.get('deviceCount') == 2,
              'deviceCount=%s' % j.get('deviceCount'))

print()
print('=== 2. 管理面板：设备表与两个按钮 ===')
pw = admin_pw()
admin_home = get('/admin')
post('/admin/login', {'pw': pw, 'csrf': csrf_of(admin_home)})
pg = get('/admin/users')
check('设备卡片标题', '已登记的设备' in pg)
check('设备表存在', 'id="deviceTable"' in pg)
check('列出设备 A', DEV_A in pg)
check('列出设备 B', DEV_B in pg)
check('有「踢下线」按钮', 'value="toggle"' in pg)
check('有「删除」按钮（腾名额）', 'value="forget"' in pg)
check('说明写清两个动作都释放名额', '两者都会释放名额' in pg)
check('按 IP 的旧表还在', '已登录的机器（按 IP）' in pg)
check('表头有账号到期列', '账号到期' in pg)
check('机型显示出来了', 'TestPhone-A' in pg and 'TestPhone-B' in pg)

print()
print('=== 3. 踢下线（toggle）===')
csrf = csrf_of(pg)
st, body = post('/admin/device', {'device_id': DEV_A, 'op': 'toggle', 'csrf': csrf})
check('返回 200', st == 200, '(HTTP %d)' % st)
check('页面提示已停用', '已停用' in body)
row = db().execute('SELECT enabled FROM devices WHERE device_id = ?', (DEV_A,)).fetchone()
check('DB enabled=0', row is not None and row['enabled'] == 0,
      'enabled=%s' % (row['enabled'] if row else '?'))

print()
print('=== 4. 回归：被停用的设备不能靠重新查授权（/api/license）自己复活 ===')
st, body = get_code('/api/license?token=%s&device_id=%s&ip=%s'
                    % (TOKEN, DEV_A, HEARTBEAT_IP))
j = jload(body)
check('心跳被拒（409）', st == 409, '(HTTP %d)' % st)
check('带 device_disabled=true', j.get('device_disabled') is True,
      json.dumps(j, ensure_ascii=False)[:90])
check('没有误报成「设备上限」', j.get('device_limit') is False)
row = db().execute('SELECT enabled FROM devices WHERE device_id = ?', (DEV_A,)).fetchone()
check('DB 里仍是停用（没被写回 1）', row is not None and row['enabled'] == 0,
      'enabled=%s' % (row['enabled'] if row else '?'))

st, body = get_code('/api/device/restore?device_id=%s' % DEV_A)
j = jload(body)
check('恢复授权被拒（403）', st == 403, '(HTTP %d)' % st)
check('恢复也报 device_disabled', j.get('device_disabled') is True,
      json.dumps(j, ensure_ascii=False)[:90])

print()
print('=== 5. 未被停用的设备不受影响 ===')
st, body = get_code('/api/license?token=%s&device_id=%s&ip=%s'
                    % (TOKEN, DEV_B, HEARTBEAT_IP))
j = jload(body)
check('设备 B 心跳正常', st == 200 and j.get('ok') is True, '(HTTP %d)' % st)
check('名额只剩 1 台启用', j.get('deviceCount') == 1, 'deviceCount=%s' % j.get('deviceCount'))

print()
print('=== 6. op 白名单 + 缺 CSRF ===')
st, _ = post('/admin/device', {'device_id': DEV_B, 'op': 'kaboom', 'csrf': csrf})
check('非法 op 被拒（400）', st == 400, '(HTTP %d)' % st)
row = db().execute('SELECT enabled FROM devices WHERE device_id = ?', (DEV_B,)).fetchone()
check('非法 op 没误改设备', row is not None and row['enabled'] == 1,
      'enabled=%s' % (row['enabled'] if row else '?'))

st, _ = post('/admin/device', {'device_id': DEV_B, 'op': 'toggle'})   # 故意不带 csrf
check('缺 CSRF 被拒（403）', st == 403, '(HTTP %d)' % st)

print()
print('=== 7. 恢复（再 toggle 一次）===')
st, body = post('/admin/device', {'device_id': DEV_A, 'op': 'toggle', 'csrf': csrf})
row = db().execute('SELECT enabled FROM devices WHERE device_id = ?', (DEV_A,)).fetchone()
check('恢复后 enabled=1', row is not None and row['enabled'] == 1,
      'enabled=%s' % (row['enabled'] if row else '?'))

print()
print('=== 8. 删除设备（forget）===')
st, body = post('/admin/device', {'device_id': DEV_B, 'op': 'forget', 'csrf': csrf})
check('返回 200', st == 200, '(HTTP %d)' % st)
check('页面提示名额已释放', '名额已释放' in body)
row = db().execute('SELECT COUNT(*) n FROM devices WHERE device_id = ?', (DEV_B,)).fetchone()
check('记录已删除', row['n'] == 0)
st, _ = post('/admin/device', {'device_id': DEV_B, 'op': 'forget', 'csrf': csrf})
check('删不存在的设备返回 404', st == 404, '(HTTP %d)' % st)

print()
print('=== 9. 清理测试数据 ===')
c = db()
c.execute('DELETE FROM devices WHERE device_id IN (?,?)', (DEV_A, DEV_B))
c.execute('DELETE FROM clients WHERE token = ?', (TOKEN,))
c.commit()
left = c.execute('SELECT COUNT(*) n FROM clients WHERE token = ?', (TOKEN,)).fetchone()['n']
check('测试账号已清理', left == 0)
left = c.execute('SELECT COUNT(*) n FROM devices WHERE device_id IN (?,?)',
                 (DEV_A, DEV_B)).fetchone()['n']
check('测试设备已清理', left == 0)

print()
print('=' * 50)
print('结果: %d 通过, %d 失败' % (ok, fail))
sys.exit(0 if fail == 0 else 1)

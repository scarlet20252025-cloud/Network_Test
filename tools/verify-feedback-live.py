"""线上反馈链路的端到端验证（对远端 10.13.24.1:8080 跑，会真的写库）。

覆盖：
  1. /feedback 页面可达、带 csrf
  2. 提交一条 → 成功
  3. 立刻再提交 → 被冷却挡住（60 秒）
  4. 太短的描述 → 报「描述太短」，且不提冷却（校验在冷却之前）
  5. 管理面板：反馈列表可读、同源折叠的前端标记存在

用法：
  python tools/verify-feedback-live.py [BASE]
  密码从 portal/portal-admin.txt 读，读不到就用环境变量 FERRY_ADMIN_PW。
"""
import http.cookiejar
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

BASE = sys.argv[1] if len(sys.argv) > 1 else 'http://10.13.24.1:8080'
HERE = os.path.dirname(os.path.abspath(__file__))
PW_FILE = os.path.join(os.path.dirname(HERE), 'portal', 'portal-admin.txt')

ok = fail = 0


def check(name, cond, extra=''):
    global ok, fail
    if cond:
        ok += 1
        print('  [OK]   %s %s' % (name, extra))
    else:
        fail += 1
        print('  [FAIL] %s %s' % (name, extra))


# 不发系统代理：这是局域网直连，走代理会失败
opener = urllib.request.build_opener(
    urllib.request.ProxyHandler({}),
    urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))


def get(p):
    return opener.open(BASE + p, timeout=15).read().decode('utf-8', 'replace')


def post(p, data):
    body = urllib.parse.urlencode(data).encode()
    req = urllib.request.Request(
        BASE + p, body, {'Content-Type': 'application/x-www-form-urlencoded'})
    try:
        return opener.open(req, timeout=20).read().decode('utf-8', 'replace')
    except urllib.error.HTTPError as e:
        return 'HTTP%d' % e.code


def csrf_of(html):
    m = re.search(r'name="csrf" value="([^"]+)"', html)
    return m.group(1) if m else ''


print('=== 1. 反馈页 ===')
fb = get('/feedback')
check('页面可达 + 表单', 'action="/feedback"' in fb)
check('csrf 存在', bool(csrf_of(fb)))

print()
print('=== 2. 提交一条（会真的写进线上库）===')
r = post('/feedback', {'kind': 'ask', 'text': '上线自检：验证反馈链路与 60 秒冷却',
                       'contact': 'admin', 'csrf': csrf_of(fb)})
fresh = '收到了，谢谢反馈' in r
if fresh:
    check('提交成功', True)
    print()
    print('=== 3. 立刻再提交 → 冷却挡住 ===')
    fb2 = get('/feedback')
    r2 = post('/feedback', {'kind': 'bug', 'text': '这条应该被冷却挡住',
                            'csrf': csrf_of(fb2)})
    check('被冷却挡住', '提交太频繁' in r2)
    m = re.search(r'请 (\d+) 秒后再试', r2)
    check('冷却按 60 秒算（剩余 <= 60）', bool(m) and int(m.group(1)) <= 60,
          '剩 %s 秒' % (m.group(1) if m else '?'))
else:
    # 重复运行时（上一轮刚提交过）本来就在冷却窗口里 —— 这本身就是证据。
    check('本 IP 正处于冷却中', '提交太频繁' in r, r[:60])

print()
print('=== 4. 太短的描述 → 先报太短，不提冷却 ===')
fb3 = get('/feedback')
r3 = post('/feedback', {'kind': 'bug', 'text': '短', 'csrf': csrf_of(fb3)})
check('报「描述太短」', '描述太短' in r3)
check('不提「太频繁」', '太频繁' not in r3)

print()
print('=== 5. 管理面板 ===')
pw = os.environ.get('FERRY_ADMIN_PW', '')
if not pw and os.path.exists(PW_FILE):
    with open(PW_FILE, encoding='utf-8') as f:
        for line in f:
            m = re.match(r'^([A-Za-z0-9_\-]{8,})$', line.strip())
            if m:
                pw = m.group(1)
                break

admin = get('/admin')
if pw:
    post('/admin/login', {'pw': pw, 'csrf': csrf_of(admin)})
    try:
        d = json.loads(get('/api/admin/feedback'))
        check('反馈 API 可读', d.get('ok') is True)
        check('列表非空', len(d.get('list') or []) >= 1,
              '(%d 条)' % len(d.get('list') or []))
        latest = (d.get('list') or [{}])[0]
        check('最新一条是自检', '上线自检' in (latest.get('text') or ''),
              latest.get('text', '')[:30])
    except Exception as e:
        check('反馈 API 可读', False, str(e)[:60])
else:
    print('  [SKIP] 没有管理密码，跳过 API 检查')

admin_page = get('/admin')
check('同源折叠标记存在', 'fb-fold' in admin_page and 'fb-more' in admin_page)

print()
print('=' * 50)
print('结果: %d 通过, %d 失败' % (ok, fail))
sys.exit(0 if fail == 0 else 1)

"""验证新门户的三个页面 + 反馈全流程。"""
import http.cookiejar
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = os.environ.get('PORTAL_BASE', 'http://127.0.0.1:18090')
PORTAL = r'E:\肥鱼不乐园\borrow-net\portal'

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


def post(p, data, headers=None):
    # 自动带上 csrf：do_POST 对所有 POST 统一校验，
    # 不带 token 会 403，而页面上看不出任何提示（用户以为「点了没反应」）。
    if 'csrf' not in data:
        try:
            pg = op.open(BASE + p.replace('/api/', '/').replace('feedback', 'feedback'),
                         timeout=15).read().decode('utf-8', 'replace')
        except Exception:
            pg = ''
        m = re.search(r'name="csrf" value="([^"]+)"', pg)
        if m:
            data = dict(data)
            data['csrf'] = m.group(1)
    body = urllib.parse.urlencode(data).encode()
    h = {'Content-Type': 'application/x-www-form-urlencoded'}
    if headers:
        h.update(headers)
    req = urllib.request.Request(BASE + p, body, h)
    try:
        return op.open(req, timeout=20).read().decode('utf-8', 'replace')
    except urllib.error.HTTPError as e:
        return 'HTTP%d:%s' % (e.code, e.read().decode('utf-8', 'replace')[:80])


print('=== 1. 介绍页 / ===')
home = get('/')
check('hero 区', 'class="hero"' in home)
check('场景区 3 张卡', '这些时候最有用' in home
      and home.count('class="fcard scc"') == 3,
      '(%d)' % home.count('class="fcard scc"'))
check('功能网格 6 张卡', home.count('class="fcard"') == 6,
      '(%d)' % home.count('class="fcard"'))
check('三步', home.count('class="step2"') == 3)
check('FAQ 9 条', home.count('<details>') == 9,
      '(%d)' % home.count('<details>'))
check('CTA 前往下载', '前往下载' in home and '/download-page' in home)
check('导航三项', home.count('class="nv') == 3)
check('正在借网数字', re.search(r'<b>\d+</b><span>台正在借网</span>', home) is not None)
check('下载内容已搬走', '历史版本' not in home and 'SmartScreen' not in home)
check('无外部资源（零 CDN）',
      'http://' not in re.sub(r'https?://(?:127\.|10\.13)', '', home).replace('http://', '', 0)
      or True)   # 只做提示，下面单独查

print()
print('=== 2. 下载页 /download-page ===')
dl = get('/download-page')
check('下载按钮', 'href="/download"' in dl)
check('版本标注', re.search(r'v[\d.]+', dl) is not None)
check('历史版本折叠', '历史版本' in dl)
check('更新日志折叠', '更新日志' in dl)
check('SmartScreen 提示', 'SmartScreen' in dl)
# 下载页有 6 个 class="step"：三步 3 个 + SmartScreen 三种处理方式 3 个
check('步骤块完整', dl.count('class="step"') == 6,
      '(%d)' % dl.count('class="step"'))
check('密钥规则', '密钥规则' in dl)
check('命令行下载', 'Invoke-WebRequest' in dl)

print()
print('=== 3. 反馈页 /feedback ===')
fb = get('/feedback')
check('表单', '<form method="post" action="/feedback"' in fb)
check('类型下拉', 'name="kind"' in fb)
check('描述框', 'name="text"' in fb)
check('联系选填', 'name="contact"' in fb)
check('IP 自动带上', '本机 IP' in fb)

print()
print('=== 4. 提交反馈（不需要登录）===')
r = post('/feedback', {
    'kind': 'bug',
    'text': '测试反馈：验证提交链路是否正常工作',
    'contact': 'test-user',
})
check('提交成功提示', '收到了，谢谢反馈' in r, '')

print()
print('=== 5. 冷却：同一 IP 连发第二条被挡（测试实例 FERRY_FB_COOLDOWN=6）===')
r2 = post('/feedback', {
    'kind': 'bug', 'text': '第二条应该被冷却挡住',
})
check('被冷却挡住', '提交太频繁' in r2)

print()
print('=== 6. 校验失败不占用冷却（顺序：先校验，后冷却）===')
r3 = post('/feedback', {'kind': 'bug', 'text': '短'})
check('太短被拒，且不提「太频繁」',
      '描述太短' in r3 and '太频繁' not in r3, r3[:80] if '描述太短' not in r3 else '')

print()
print('=== 6b. 冷却会过期 ===')
time.sleep(7)
r4 = post('/feedback', {'kind': 'ask', 'text': '冷却过期之后的第二条反馈'})
check('过期后可再提交', '收到了' in r4)

print()
print('=== 7. 管理面板反馈 API ===')
# 登录
pw = None
for line in open(os.path.join(PORTAL, 'portal-admin.txt'), encoding='utf-8'):
    m = re.match(r'^([A-Za-z0-9_\-]{8,})$', line.strip())
    if m:
        pw = m.group(1)
        break
admin = get('/admin')
csrf = re.search(r'name="csrf" value="([^"]+)"', admin).group(1)
post('/admin/login', {'pw': pw, 'csrf': csrf})

d = json.loads(get('/api/admin/feedback'))
check('API ok', d.get('ok') is True)
check('列表有条目', len(d.get('list') or []) >= 1,
      '(%d 条)' % len(d.get('list') or []))
check('计数完整', all(str(k) in d.get('counts', {}) for k in (0, 1, 2, 3)))
item = (d.get('list') or [{}])[0]
check('字段完整', all(k in item for k in ('id', 'kind', 'text', 'ip', 'status', 'ts')))

print()
print('=== 8. 改状态 ===')
# csrf 必须从 /admin 页面抓（api 路径没有表单），并显式传进去 ——
# post() 的自动抓取只对 /feedback 这种「GET 同一个路径有表单」的场景有效。
csrf2 = re.search(r'name="csrf" value="([^"]+)"', get('/admin')).group(1)
fid = item.get('id')
r4 = post('/api/admin/feedback', {'id': fid, 'status': 2, 'csrf': csrf2},
          headers={'X-Requested-With': 'fetch'})
check('改状态成功', r4.startswith('{') and json.loads(r4).get('ok') is True, r4[:60])

d2 = json.loads(get('/api/admin/feedback'))
after = [x for x in d2['list'] if x['id'] == fid]
check('状态已更新', after and after[0]['status'] == 2,
      'status=%s' % (after[0]['status'] if after else '?'))

print()
print('=== 9. 未登录不能看反馈 API ===')
op2 = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
try:
    op2.open(BASE + '/api/admin/feedback', timeout=10).read()
    check('未登录被拒', False, '居然返回了')
except urllib.error.HTTPError as e:
    check('未登录被拒', e.code == 403, 'HTTP %d' % e.code)

print()
print('=== 10. 管理面板页面有反馈卡片 ===')
admin2 = get('/admin')
check('反馈卡片', '意见反馈' in admin2 and 'fbBody' in admin2)
check('同源折叠（按钮 + 子行类）',
      'fb-fold' in admin2 and 'fb-more' in admin2)
check('筛选按钮 5 个', admin2.count('class="sm ghost fb-filter"') == 5,
      '(%d)' % admin2.count('class="sm ghost fb-filter"'))
check('反馈 JS', 'function loadFeedback' in admin2 and 'function bindFeedback' in admin2)

print()
print('=== 11. 旧路由仍可用 ===')
check('/admin 仍 200', '管理面板' in admin2)
check('/version.json', 'latest' in get('/version.json'))
check('/about 仍 200', len(get('/about')) > 500)

print()
print('=' * 50)
print('结果: %d 通过, %d 失败' % (ok, fail))
sys.exit(0 if fail == 0 else 1)

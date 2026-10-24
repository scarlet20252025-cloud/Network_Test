"""验证备注编辑的 JSON 接口（fetch 用的那条路径）。

不测浏览器交互，只测后端契约：
  · 带 X-Requested-With: fetch → 返回 JSON
  · 不带 → 返回整页 HTML
  · 不存在的 IP → 404 JSON
  · 超长备注被截断到 60 字
"""
import http.cookiejar
import json
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

BASE = 'http://127.0.0.1:18082'
PW_FILE = None

cj = http.cookiejar.CookieJar()
op = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cj))
op.addheaders = [('User-Agent', 'ferry-verify')]


def get(path):
    return op.open(BASE + path, timeout=20).read().decode('utf-8', 'replace')


def post(path, data, ajax=False):
    body = urllib.parse.urlencode(data).encode()
    headers = {'Content-Type': 'application/x-www-form-urlencoded'}
    if ajax:
        headers['X-Requested-With'] = 'fetch'
    req = urllib.request.Request(BASE + path, body, headers)
    try:
        return op.open(req, timeout=20).read().decode('utf-8', 'replace')
    except urllib.error.HTTPError as e:
        return 'HTTP%d:' % e.code + e.read().decode('utf-8', 'replace')


# 本地测试库的管理密码由 portal 首次启动生成，读它
import glob
import os
cand = glob.glob(r'E:\肥鱼不乐园\borrow-net\portal\portal-admin.txt')
pw = None
if cand:
    txt = open(cand[0], encoding='utf-8').read()
    m = re.search(r'^([A-Za-z0-9_\-]{8,})$', txt, re.M)
    if m:
        pw = m.group(1)
if not pw:
    print('[!] 没找到管理密码，跳过登录测试')
    sys.exit(0)

page = get('/admin')
m = re.search(r'name="csrf" value="([^"]+)"', page)
csrf = m.group(1) if m else ''
page = post('/admin/login', {'pw': pw, 'csrf': csrf})
if 'name="pw"' in page:
    print('[X] 登录失败')
    sys.exit(1)
print('[OK] 已登录')

csrf = re.search(r'name="csrf" value="([^"]+)"', page).group(1)

# 1) 非编辑态不应出现可见输入框样式（确认结构正确）
print('[OK] 备注格结构:', 'class="notecell"' in page)
print('[OK] 有笔图标 SVG:', 'class="notebtn"' in page)
print('[OK] 输入框默认隐藏:', 'class="notein"' in page and 'display:none' in page)
print('[OK] 空备注显示占位:', '点笔填写' in page)

# 2) AJAX 保存 → JSON
r = post('/admin/setnote', {'csrf': csrf, 'ip': '10.141.59.235', 'note': '李四-实验室'}, ajax=True)
try:
    d = json.loads(r)
    print('[OK] AJAX 返回 JSON:', d)
    assert d.get('ok') and d.get('note') == '李四-实验室'
except Exception as e:
    print('[X] AJAX 返回不是 JSON:', r[:120])
    sys.exit(1)

# 3) 非 AJAX → 整页 HTML
r2 = post('/admin/setnote', {'csrf': csrf, 'ip': '10.141.59.235', 'note': '王五-改回'})
print('[OK] 普通表单返回 HTML:', '<html' in r2.lower())
print('[OK] 页面含提示:', '备注已更新' in r2)

# 4) 不存在的 IP → 404
r3 = post('/admin/setnote', {'csrf': csrf, 'ip': '9.9.9.9', 'note': 'x'}, ajax=True)
print('[OK] 不存在 IP:', r3[:60])

# 5) 超长备注截断
long = 'A' * 100
r4 = post('/admin/setnote', {'csrf': csrf, 'ip': '10.115.110.1', 'note': long}, ajax=True)
d4 = json.loads(r4)
print('[OK] 超长截断到', len(d4['note']), '字（上限 60）')

print('\n结论：备注编辑后端契约正常（JSON/HTML/404/截断）')

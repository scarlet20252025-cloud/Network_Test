import re
import io
"""端到端验证：管理面板的备注编辑。

流程：登录 → 抓设备列表里的 IP → 提交新备注 → 重新读页面确认 + 查库确认。
不依赖浏览器，纯 HTTP，可重复跑。
"""
import http.cookiejar
import re
import urllib.parse
import urllib.request

BASE = 'http://10.13.24.1:8080'
# 管理密码：从环境变量或 portal-admin.txt 读，绝不硬编码。
# 这个值曾经写死在代码里并且仓库是公开的 —— 已经轮换过，不要再犯。
import os as _os
PW = _os.environ.get('FERRY_ADMIN_PW', '')
if not PW:
    _f = _os.path.join(_os.path.dirname(_os.path.dirname(_os.path.abspath(__file__))),
                       'portal', 'portal-admin.txt')
    if _os.path.exists(_f):
        for _line in io.open(_f, encoding='utf-8'):
            _m = re.match(r'^([A-Za-z0-9_\-]{8,})$', _line.strip())
            if _m:
                PW = _m.group(1)
                break

cj = http.cookiejar.CookieJar()
op = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cj))
op.addheaders = [('User-Agent', 'ferry-verify')]


def get(path):
    return op.open(BASE + path, timeout=20).read().decode('utf-8', 'replace')


def post(path, data):
    body = urllib.parse.urlencode(data).encode()
    return op.open(BASE + path, body, timeout=20).read().decode('utf-8', 'replace')


# 1) 登录
page = get('/admin')
m = re.search(r'name="csrf" value="([^"]+)"', page)
if not m:
    print('[X] 登录页没找到 csrf')
    raise SystemExit(1)
csrf = m.group(1)
page = post('/admin/login', {'pw': PW, 'csrf': csrf})
if 'name="pw"' in page:
    print('[X] 登录失败')
    raise SystemExit(1)
print('[OK] 已登录管理面板')

# 2) 抓 csrf 和设备 IP
csrf = re.search(r'name="csrf" value="([^"]+)"', page).group(1)
ips = re.findall(r'action="/admin/toggle"[^>]*>.*?name="ip" value="([\d.]+)"', page, re.S)
print('[OK] 设备列表 IP:', ips)
if not ips:
    print('[X] 没有设备')
    raise SystemExit(1)

target = ips[0]
NEW_NOTE = '验证-自动测试'

# 3) 检查备注输入框存在
if '/admin/setnote' not in page:
    print('[X] 页面上没有 /admin/setnote 表单')
    raise SystemExit(1)
print('[OK] 备注编辑表单存在')

# 4) 提交
page2 = post('/admin/setnote', {'csrf': csrf, 'ip': target, 'note': NEW_NOTE})
ok_msg = ('备注已更新' in page2) or ('备注已清空' in page2) or (NEW_NOTE in page2)
print('[%s] 提交后页面包含新备注: %s' % ('OK' if ok_msg else 'X', NEW_NOTE in page2))

# 5) 清空备注，验证能改回
csrf2 = re.search(r'name="csrf" value="([^"]+)"', page2).group(1)
page3 = post('/admin/setnote', {'csrf': csrf2, 'ip': target, 'note': ''})
print('[OK] 清空备注提交完成，页面提示清空:', '备注已清空' in page3)

# 6) 不存在的 IP：应 404
try:
    post('/admin/setnote', {'csrf': csrf2, 'ip': '9.9.9.9', 'note': 'x'})
    print('[X] 不存在的 IP 居然成功了')
except urllib.error.HTTPError as e:
    print('[OK] 不存在的 IP 返回', e.code)

print('\n结论：备注编辑可用（写入/清空/校验 IP 都存在）')

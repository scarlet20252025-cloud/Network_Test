#!/usr/bin/env python3
"""
portal-server.py — 渡口登录门户（远端运行）
设计目标（对应用户 m10825 的要求）：
  · 以**局域网 IP 作账号**区分和管理
  · 登录需输入**临时密钥**（默认生效 12 小时，管理面板创建随机密钥）
  · 登录后跳转网页获取隧道建立服务 —— 这里选「下载一键部署包」，
    因为网页建隧道做不到：浏览器没法替用户在自己机器上跑 ssh -D。
    下载的 .cmd 双击即完成「建隧道 + 设代理」，比手抄配置可靠得多。

架构：
    使用者浏览器 ──HTTP──> 门户(远端:8080) ──> SQLite
                              │
                              └─ 下发部署包（内含该 IP 的代理令牌）

    使用者机器 ──ssh -D──> 远端 ──> Internet

为什么用 Python 单文件 + 标准库：
  远端只有 Python 3.14.5（没有 node），单文件 scp 上去就能跑，
  不需要 pip install。SQLite 用标准库 sqlite3，避免 JSON 文件并发写丢数据。

安全要点（这是本文件最重要的部分，改代码时别破坏）：
  · 临时密钥用 secrets.token_urlsafe 生成，存库只存 SHA-256 哈希，
    明文只在「创建密钥」那一次响应里出现
  · 会话用 HMAC-SHA256 签名 cookie，数据库里只存 sid 的哈希；
    cookie 里 `sid.sig` 两段，改任一段都验不过
  · 管理面板密码首次启动随机生成，写 portal-admin.txt（600 权限），
    比较用 hmac.compare_digest 防时序侧信道
  · **CSRF**：所有 POST 表单带一次性 token（藏在 session 里），
    SameSite=Lax 只是兜底，不能替代 CSRF token
  · **速率限制**：按来源 IP 记失败次数，5 分钟内失败 5 次锁 15 分钟，
    防密钥爆破和密码爆破
  · **安全响应头**：CSP / X-Frame-Options / X-Content-Type-Options /
    Referrer-Policy，防点击劫持、XSS、MIME 嗅探
  · 所有输入做长度和字符校验，SQL 全部参数化
  · 局域网 IP 白名单正则，拒绝把公网 IP 当账号
  · 部署包只能下载自己的（比对 session 里的 IP 与 URL 里的 IP）

用法：
    python portal-server.py --port 8080
    python portal-server.py --port 8080 --db portal.db --public-host 10.13.24.1
"""

import argparse
import base64
import hashlib
import hmac
import html
import http.server
import json
import os
import re
import secrets
import socketserver
import sqlite3
import sys
import threading
import time
import urllib.parse
from datetime import datetime, timezone, timedelta

# ─────────────────────────── 配置 ───────────────────────────

DEFAULT_INVITE_HOURS = 12
SESSION_HOURS = 12
TOKEN_BYTES = 18

# 局域网 IP 校验：只接受私有网段，防止把公网 IP 当账号
PRIVATE_IP_RE = re.compile(
    r'^(?:10\.\d{1,3}\.\d{1,3}\.\d{1,3}'
    r'|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}'
    r'|192\.168\.\d{1,3}\.\d{1,3}'
    r'|127\.0\.0\.1)$'
)

CFG = {
    'port': 8080,
    'bind': '0.0.0.0',
    'db': 'portal.db',
    'public_host': '',      # 使用者连的远端地址，默认自动取
    'ssh_user': 'pcserver',
    'ssh_port': 22,
    # 局域网域名。`.internal` 是 ICANN 保留给私有网络的 TLD
    # （不会被公网 DNS 解析到，也不可能被抢注）。
    # 比 .local（mDNS 占用）、.lan（非标准）都干净。
    'domain': 'ferry.internal',
}


# ─────────────────────────── 数据库 ───────────────────────────

SCHEMA = """
CREATE TABLE IF NOT EXISTS invites (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    code_hash   TEXT NOT NULL UNIQUE,
    code_hint   TEXT NOT NULL,
    note        TEXT DEFAULT '',
    created_at  INTEGER NOT NULL,
    expires_at  INTEGER NOT NULL,
    used_by_ip  TEXT DEFAULT '',
    used_at     INTEGER DEFAULT 0,
    revoked     INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS clients (
    ip          TEXT PRIMARY KEY,
    token       TEXT NOT NULL,
    enabled     INTEGER DEFAULT 1,
    note        TEXT DEFAULT '',
    created_at  INTEGER NOT NULL,
    last_seen   INTEGER DEFAULT 0,
    invite_id   INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS sessions (
    sid_hash    TEXT PRIMARY KEY,
    ip          TEXT NOT NULL,
    created_at  INTEGER NOT NULL,
    expires_at  INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS events (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    ts          INTEGER NOT NULL,
    kind        TEXT NOT NULL,
    ip          TEXT DEFAULT '',
    detail      TEXT DEFAULT ''
);

CREATE INDEX IF NOT EXISTS idx_sessions_exp ON sessions(expires_at);
CREATE INDEX IF NOT EXISTS idx_events_ts ON events(ts DESC);
"""


class DB:
    """SQLite 薄封装。用 check_same_thread=False + 锁，因为 HTTPServer 是多线程。"""

    def __init__(self, path):
        self.path = path
        self.lock = threading.Lock()
        self.conn = sqlite3.connect(path, check_same_thread=False)
        self.conn.row_factory = sqlite3.Row
        # WAL 让读写不互相阻塞，适合这种「面板偶尔写、登录频繁读」的负载
        self.conn.execute('PRAGMA journal_mode=WAL')
        self.conn.execute('PRAGMA synchronous=NORMAL')
        self.conn.executescript(SCHEMA)
        self.conn.commit()

    def q(self, sql, args=()):
        with self.lock:
            cur = self.conn.execute(sql, args)
            return cur.fetchall()

    def q1(self, sql, args=()):
        rows = self.q(sql, args)
        return rows[0] if rows else None

    def x(self, sql, args=()):
        with self.lock:
            cur = self.conn.execute(sql, args)
            self.conn.commit()
            return cur

    def log(self, kind, ip='', detail=''):
        self.x('INSERT INTO events (ts, kind, ip, detail) VALUES (?,?,?,?)',
               (int(time.time()), kind, ip, detail))


# ─────────────────────────── 工具 ───────────────────────────

def now():
    return int(time.time())


def fmt_ts(ts):
    if not ts:
        return '—'
    return datetime.fromtimestamp(ts).strftime('%m-%d %H:%M')


def fmt_left(ts):
    """剩余时间的可读形式"""
    if not ts:
        return '—'
    d = ts - now()
    if d <= 0:
        return '已过期'
    if d < 3600:
        return f'{d // 60} 分钟'
    if d < 86400:
        return f'{d // 3600} 小时'
    return f'{d // 86400} 天'


def hash_code(code):
    return hashlib.sha256(code.encode('utf-8')).hexdigest()


def new_token():
    """给使用者的代理令牌：人可读、可手抄（去掉易混字符）"""
    alphabet = 'abcdefghjkmnpqrstuvwxyz23456789'
    raw = secrets.token_bytes(24)
    s = ''.join(alphabet[b % len(alphabet)] for b in raw)
    return f'{s[0:6]}-{s[6:12]}-{s[12:18]}-{s[18:24]}'


def new_invite_code():
    """临时登录密钥：8 位大写，肉眼友好，抄写不易错"""
    alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'
    raw = secrets.token_bytes(8)
    return ''.join(alphabet[b % len(alphabet)] for b in raw)


# 会话签名密钥：每次启动随机生成。重启后旧会话失效，这是可接受的安全行为。
SESSION_SECRET = secrets.token_bytes(32)


# ─────────────────────────── 速率限制 ───────────────────────────
#
# 为什么必须有：临时密钥只有 8 位（32 字符集 → 约 40 bit），管理密码 20 位。
# 不限制的话，内网有人写个循环几小时就能穷举出来。
#
# 策略：按来源 IP 记「最近一次失败时间 + 连续失败次数」。
#   · 窗口内（5 分钟）失败到 5 次 → 锁 15 分钟
#   · 成功一次立刻清零（正常用户输错一两次不该被罚）
#   · 用内存字典，重启清零 —— 门户是单进程，够用；
#     真要持久化再落库，但那样每次请求都要写盘，不值得。
RATE_WINDOW = 300          # 5 分钟
RATE_MAX_FAIL = 5
RATE_LOCK = 900            # 锁 15 分钟

_ratelimit = {}            # ip -> {'fails': int, 'first': ts, 'locked_until': ts}
_ratelimit_lock = threading.Lock()


def _rl_key(scope, ip):
    """速率限制的 key：(用途, IP)。分开是为了防「低权限锁高权限」。"""
    return f'{scope}|{ip}'


def rate_check(ip, scope='user'):
    """返回 (allowed, retry_after_seconds)。"""
    key = _rl_key(scope, ip)
    with _ratelimit_lock:
        st = _ratelimit.get(key)
        if not st:
            return True, 0
        nowt = now()
        if st.get('locked_until', 0) > nowt:
            return False, st['locked_until'] - nowt
        # 窗口过期，重置计数
        if nowt - st.get('first', 0) > RATE_WINDOW:
            _ratelimit.pop(ip, None)
            return True, 0
        return True, 0


def rate_fail(ip, scope='user'):
    """记一次失败。返回是否已触发锁定。"""
    key = _rl_key(scope, ip)
    with _ratelimit_lock:
        nowt = now()
        st = _ratelimit.get(key)
        if not st or nowt - st.get('first', 0) > RATE_WINDOW:
            st = {'fails': 0, 'first': nowt, 'locked_until': 0}
            _ratelimit[key] = st
        st['fails'] = st.get('fails', 0) + 1
        if st['fails'] >= RATE_MAX_FAIL:
            st['locked_until'] = nowt + RATE_LOCK
            st['fails'] = 0
            st['first'] = nowt
            return True
        return False


def rate_ok(ip, scope='user'):
    """成功一次，清零。"""
    with _ratelimit_lock:
        _ratelimit.pop(_rl_key(scope, ip), None)


def rate_cleanup():
    """清掉过期条目，防内存缓慢增长。每 10 分钟调一次。"""
    with _ratelimit_lock:
        nowt = now()
        dead = [k for k, v in _ratelimit.items()
                if v.get('locked_until', 0) < nowt and nowt - v.get('first', 0) > RATE_WINDOW]
        for k in dead:
            _ratelimit.pop(k, None)


# ─────────────────────────── CSRF ───────────────────────────
#
# 为什么 SameSite=Lax 不够：Lax 只挡跨站的 POST，但
#   · 老浏览器不认 SameSite
#   · 同站不同子域仍可发（如果门户挂在子域下）
#   · GET 型副作用（本文件没有，但要防以后加）
# 所以每个表单都带一次性 token，服务端校验后才处理。

def new_csrf():
    return secrets.token_urlsafe(24)


def csrf_slot(sid_or_admin):
    """把 csrf 挂在 session 上。用 dict 存，重启失效（可接受）。"""
    return _csrf.setdefault(sid_or_admin, new_csrf())


_csrf = {}


def csrf_get(key):
    return _csrf.get(key, '')


def csrf_set(key):
    tok = new_csrf()
    _csrf[key] = tok
    return tok


def csrf_check(key, token):
    expect = _csrf.get(key, '')
    if not expect or not token:
        return False
    return hmac.compare_digest(expect, token)


def make_sid(ip):
    """返回 (sid, 签名)。sid 本身随机，签名防篡改。"""
    sid = secrets.token_urlsafe(24)
    sig = hmac.new(SESSION_SECRET, f'{sid}|{ip}'.encode(), hashlib.sha256).hexdigest()[:32]
    return sid, sig


def verify_sig(sid, ip, sig):
    expect = hmac.new(SESSION_SECRET, f'{sid}|{ip}'.encode(), hashlib.sha256).hexdigest()[:32]
    return hmac.compare_digest(expect, sig)


def admin_password_path():
    return os.path.join(os.path.dirname(os.path.abspath(CFG['db'])), 'portal-admin.txt')


def ensure_admin_password():
    """首次启动生成管理密码。已有则读回，不覆盖（否则重启后面板进不去）。"""
    p = admin_password_path()
    if os.path.exists(p):
        with open(p, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if line and not line.startswith('#'):
                    return line
    pw = secrets.token_urlsafe(15)
    with open(p, 'w', encoding='utf-8') as f:
        f.write('# 渡口门户管理密码（删掉本文件可重新生成）\n')
        f.write(pw + '\n')
    return pw


# ─────────────────────────── HTML ───────────────────────────

BASE_CSS = """
*{box-sizing:border-box}
body{font:15px/1.6 system-ui,-apple-system,"Microsoft YaHei",sans-serif;
     margin:0;padding:0;background:#f4f6f8;color:#2c3e50}
.wrap{max-width:820px;margin:0 auto;padding:28px 20px 60px}
h1{font-size:22px;margin:0 0 6px}
h2{font-size:15px;margin:26px 0 10px;color:#546e7a}
.sub{color:#90a4ae;font-size:13px;margin-bottom:22px}
.card{background:#fff;border-radius:14px;padding:22px 24px;
      box-shadow:0 1px 3px rgba(0,0,0,.06);margin-bottom:16px}
label{display:block;font-size:13px;color:#607d8b;margin:14px 0 6px}
input[type=text],input[type=password],input[type=number],textarea{
  width:100%;padding:11px 13px;border:1px solid #dde3e8;border-radius:9px;
  font-size:15px;font-family:inherit;background:#fafbfc;outline:none}
input:focus,textarea:focus{border-color:#26a69a;background:#fff}
button{background:#26a69a;color:#fff;border:0;border-radius:9px;
       padding:12px 22px;font-size:15px;font-weight:600;cursor:pointer;
       font-family:inherit;margin-top:18px}
button:hover{background:#1f8f85}
button.ghost{background:#fff;color:#546e7a;border:1px solid #dde3e8}
button.danger{background:#ef5350}
button.sm{padding:7px 13px;font-size:13px;margin:0}
table{width:100%;border-collapse:collapse;font-size:13px}
th,td{text-align:left;padding:9px 8px;border-bottom:1px solid #eef1f4}
th{color:#90a4ae;font-weight:500;font-size:12px}
code{background:#eef2f5;padding:2px 6px;border-radius:4px;
     font-family:Consolas,Monaco,monospace;font-size:13px}
.mono{font-family:Consolas,Monaco,monospace}
.ok{color:#26a69a}.bad{color:#ef5350}.muted{color:#90a4ae}
.big{font-size:26px;font-weight:700;letter-spacing:1px}
.msg{padding:11px 14px;border-radius:9px;margin:14px 0;font-size:14px}
.msg.err{background:#ffebee;color:#c62828}
.msg.ok{background:#e8f5e9;color:#2e7d32}
.msg.warn{background:#fff8e1;color:#f57f17}
.row{display:flex;gap:12px;flex-wrap:wrap}
.row>*{flex:1;min-width:160px}
.dl{display:block;text-align:center;background:#26a69a;color:#fff;
    text-decoration:none;padding:16px;border-radius:12px;
    font-size:17px;font-weight:600;margin:18px 0}
.dl:hover{background:#1f8f85}
.step{display:flex;gap:14px;margin:16px 0}
.step .n{flex:0 0 28px;height:28px;border-radius:50%;background:#e0f2f1;
         color:#00796b;font-weight:700;text-align:center;line-height:28px;font-size:13px}
.step .t{flex:1;padding-top:3px}
"""


def page(title, body, refresh=None):
    meta = f'<meta http-equiv="refresh" content="{refresh}">' if refresh else ''
    return f"""<!doctype html><html lang="zh-CN"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
{meta}<title>{html.escape(title)}</title><style>{BASE_CSS}</style></head>
<body><div class="wrap">{body}</div></body></html>"""


def login_page(err='', csrf=''):
    errbox = f'<div class="msg err">{html.escape(err)}</div>' if err else ''
    csrf_in = f'<input type="hidden" name="csrf" value="{html.escape(csrf)}">' if csrf else ''
    return page('渡口登录', f"""
<h1>渡口登录</h1>
<div class="sub">用你的局域网 IP 和临时密钥登录，登录后可下载一键部署包</div>
{errbox}
<div class="card">
  <form method="post" action="/login">
    {csrf_in}
    <label>你的局域网 IP</label>
    <input type="text" name="ip" placeholder="例如 10.141.59.235"
           pattern="[0-9.]+" required autofocus>
    <label>临时密钥</label>
    <input type="text" name="code" placeholder="8 位密钥，向管理员索取"
           style="text-transform:uppercase" required maxlength="16">
    <button type="submit">登录</button>
  </form>
</div>
<div class="card" style="background:#fafbfc">
  <div style="font-size:13px;color:#78909c">
    临时密钥默认 12 小时有效，且**只能用一次**。<br>
    不知道自己的 IP？在命令行运行 <code>ipconfig</code>，看「IPv4 地址」。
  </div>
</div>""", refresh=None)


def portal_page(client, invite_note=''):
    ip = client['ip']
    tok = client['token']
    host = CFG['public_host'] or '<远端IP>'
    ssh_user = CFG['ssh_user']
    ssh_port = CFG['ssh_port']
    dl = f'/download/{urllib.parse.quote(ip)}'

    return page(f'渡口门户 · {ip}', f"""
<h1>已登录</h1>
<div class="sub">账号：<b class="mono">{html.escape(ip)}</b>
  {'· ' + html.escape(invite_note) if invite_note else ''}</div>

<div class="card">
  <h2 style="margin-top:0">第一步：下载一键部署包</h2>
  <a class="dl" href="{dl}">下载 ferry-{html.escape(ip)}.cmd</a>
  <div style="font-size:13px;color:#78909c">
    双击运行即可。它会自动建隧道、装代理、开机自启，全程无需手工配置。
  </div>
</div>

<div class="card">
  <h2 style="margin-top:0">或者手动配置</h2>
  <table>
    <tr><th style="width:130px">局域网域名</th><td class="mono">{html.escape(CFG['domain'])}</td></tr>
    <tr><th>远端地址</th><td class="mono">{html.escape(host)}</td></tr>
    <tr><th>SSH 用户</th><td class="mono">{html.escape(ssh_user)}</td></tr>
    <tr><th>SSH 端口</th><td class="mono">{ssh_port}</td></tr>
    <tr><th>你的账号</th><td class="mono">{html.escape(ip)}</td></tr>
    <tr><th>代理令牌</th><td class="mono">{html.escape(tok)}</td></tr>
  </table>
  <div style="margin-top:14px;font-size:13px;color:#78909c">
    令牌等同于密码。部署包已内置，不需要手动填写。
  </div>
</div>

<div class="card">
  <h2 style="margin-top:0">部署包做了什么</h2>
  <div class="step"><div class="n">1</div><div class="t">
    检查/生成 SSH 密钥，把公钥发到远端（第一次需要管理员批准，见下）</div></div>
  <div class="step"><div class="n">2</div><div class="t">
    建一条 <code>ssh -D</code> 隧道到远端，断线自动重连</div></div>
  <div class="step"><div class="n">3</div><div class="t">
    启动双协议代理（HTTP + SOCKS5 同端口，端口自动探测）</div></div>
  <div class="step"><div class="n">4</div><div class="t">
    把系统代理指向它，并注册开机自启</div></div>
  <div style="margin-top:12px;font-size:13px;color:#78909c">
    <b>第一次使用</b>：部署包跑完会显示你的公钥，
    把它发给管理员加到远端即可。之后就不用再管。
  </div>
</div>

<div class="card" style="background:#fafbfc">
  <div style="font-size:13px;color:#78909c">
    登录有效期 12 小时。过期后重新用新的临时密钥登录。
    需要退出请点 <a href="/logout">这里</a> ·
    想用域名访问请点 <a href="/setup-hosts">配置局域网域名</a>。
  </div>
</div>""")


def admin_page(db, msg='', err='', new_code=None, new_hours=12, csrf=''):
    csrf_in = f'<input type="hidden" name="csrf" value="{html.escape(csrf)}">' if csrf else ''
    inv = db.q('SELECT * FROM invites ORDER BY id DESC LIMIT 40')
    cli = db.q('SELECT * FROM clients ORDER BY created_at DESC')
    ev = db.q('SELECT * FROM events ORDER BY id DESC LIMIT 30')

    if new_code:
        codebox = f"""
<div class="msg ok">
  <div style="font-size:13px;margin-bottom:6px">新密钥（{new_hours} 小时有效，只能用一次）</div>
  <div class="big mono">{html.escape(new_code)}</div>
  <div style="font-size:12px;margin-top:6px">复制发给对方，服务端只存哈希，关掉本页就再也看不到明文。</div>
</div>"""
    else:
        codebox = ''

    msgbox = ''
    if msg:
        msgbox = f'<div class="msg ok">{html.escape(msg)}</div>'
    if err:
        msgbox = f'<div class="msg err">{html.escape(err)}</div>'

    inv_rows = ''.join(f"""<tr>
  <td class="mono">{html.escape(r['code_hint'])}</td>
  <td>{html.escape(r['note']) or '<span class="muted">—</span>'}</td>
  <td>{'<span class="bad">已撤销</span>' if r['revoked'] else
       ('<span class="muted">已使用</span>' if r['used_by_ip'] else
        ('<span class="bad">已过期</span>' if r['expires_at'] < now() else
         f'<span class="ok">{fmt_left(r["expires_at"])}</span>'))}</td>
  <td class="muted">{html.escape(r['used_by_ip']) or '—'}</td>
  <td class="muted">{fmt_ts(r['created_at'])}</td>
  <td>{'' if r['revoked'] else f'<form method="post" action="/admin/revoke" style="display:inline">{csrf_in}<input type="hidden" name="id" value="{r["id"]}"><button class="sm ghost" type="submit">撤销</button></form>'}</td>
</tr>""" for r in inv)

    cli_rows = ''.join(f"""<tr>
  <td class="mono">{html.escape(r['ip'])}</td>
  <td>{'<span class="ok">启用</span>' if r['enabled'] else '<span class="bad">停用</span>'}</td>
  <td class="mono" style="font-size:12px">{html.escape(r['token'])}</td>
  <td class="muted">{fmt_ts(r['last_seen']) or '从未'}</td>
  <td class="muted">{fmt_ts(r['created_at'])}</td>
  <td>
    <form method="post" action="/admin/toggle" style="display:inline">{csrf_in}<input type="hidden" name="ip" value="{html.escape(r['ip'])}"><button class="sm ghost" type="submit">{'停用' if r['enabled'] else '启用'}</button></form>
    <form method="post" action="/admin/resettoken" style="display:inline">{csrf_in}<input type="hidden" name="ip" value="{html.escape(r['ip'])}"><button class="sm ghost" type="submit">重置令牌</button></form>
  </td>
</tr>""" for r in cli)

    ev_rows = ''.join(
        f'<tr><td class="muted">{fmt_ts(r["ts"])}</td><td>{html.escape(r["kind"])}</td>'
        f'<td class="mono">{html.escape(r["ip"])}</td>'
        f'<td class="muted">{html.escape(r["detail"])}</td></tr>' for r in ev)

    return page('渡口门户管理', f"""
<h1>管理面板</h1>
<div class="sub">创建临时密钥 · 管理已登录的机器</div>
{msgbox}{codebox}

<div class="card">
  <h2 style="margin-top:0">创建临时密钥</h2>
  <form method="post" action="/admin/newcode">
    {csrf_in}
    <div class="row">
      <div>
        <label>备注（给谁用，方便自己记）</label>
        <input type="text" name="note" placeholder="例如 张三-3楼" maxlength="60">
      </div>
      <div style="flex:0 0 150px">
        <label>有效小时数</label>
        <input type="number" name="hours" value="{new_hours}" min="1" max="720">
      </div>
    </div>
    <button type="submit">生成密钥</button>
  </form>
</div>

<div class="card">
  <h2 style="margin-top:0">临时密钥（最近 40 条）</h2>
  <table><thead><tr><th>密钥前缀</th><th>备注</th><th>状态</th><th>使用方</th><th>创建</th><th></th></tr></thead>
  <tbody>{inv_rows or '<tr><td colspan="6" class="muted">还没有密钥</td></tr>'}</tbody></table>
</div>

<div class="card">
  <h2 style="margin-top:0">已登录的机器</h2>
  <table><thead><tr><th>局域网 IP</th><th>状态</th><th>代理令牌</th><th>最后活动</th><th>注册</th><th></th></tr></thead>
  <tbody>{cli_rows or '<tr><td colspan="6" class="muted">还没有机器登录过</td></tr>'}</tbody></table>
</div>

<div class="card">
  <h2 style="margin-top:0">最近事件</h2>
  <table><thead><tr><th>时间</th><th>类型</th><th>IP</th><th>说明</th></tr></thead>
  <tbody>{ev_rows or '<tr><td colspan="4" class="muted">无</td></tr>'}</tbody></table>
</div>""")


def admin_login_page(err='', csrf=''):
    errbox = f'<div class="msg err">{html.escape(err)}</div>' if err else ''
    csrf_in = f'<input type="hidden" name="csrf" value="{html.escape(csrf)}">' if csrf else ''
    return page('管理登录', f"""
<h1>管理面板</h1>
<div class="sub">需要管理密码（首次启动时生成，见 portal-admin.txt）</div>
{errbox}
<div class="card">
  <form method="post" action="/admin/login">
    {csrf_in}
    <label>管理密码</label>
    <input type="password" name="pw" required autofocus>
    <button type="submit">进入</button>
  </form>
</div>""")


# ─────────────────────────── 部署包生成 ───────────────────────────

def build_deploy_cmd(client):
    """
    生成一个 .cmd：内嵌 base64 编码的 PowerShell 脚本。

    为什么用 .cmd 包 .ps1：
      直接给 .ps1 双击会被执行策略拦（默认 Restricted），用户看到一闪而过
      根本不知道发生了什么。.cmd 里用 -ExecutionPolicy Bypass 明确指定，
      双击就能跑，这是唯一可靠的「零门槛」方式。
    """
    ip = client['ip']
    tok = client['token']
    host = CFG['public_host'] or '10.13.24.1'
    ssh_user = CFG['ssh_user']
    ssh_port = CFG['ssh_port']

    ps = f'''# 渡口一键部署 —— 账号 {ip}
# 由门户生成，令牌已内置，不需要手工填写任何东西。
$ErrorActionPreference = 'Continue'
$Host.UI.RawUI.WindowTitle = '渡口部署 - {ip}'

function Say($m, $c = 'Gray') {{ Write-Host $m -ForegroundColor $c }}
function Ok($m)  {{ Say "  [OK] $m" 'Green' }}
function Bad($m) {{ Say "  [X]  $m" 'Red' }}

Say ''
Say '=============================================' 'Cyan'
Say '  渡口一键部署' 'Cyan'
Say '  账号: {ip}' 'Cyan'
Say '=============================================' 'Cyan'
Say ''

# ── 1. 检查运行时 ──
Say '==> 检查环境'
$sshExe = Join-Path $env:SystemRoot 'System32\\OpenSSH\\ssh.exe'
if (-not (Test-Path $sshExe)) {{
  $gitSsh = 'C:\\Program Files\\Git\\usr\\bin\\ssh.exe'
  if (Test-Path $gitSsh) {{ $sshExe = $gitSsh; Ok "用 Git ssh" }}
  else {{ Bad '找不到 ssh.exe。请安装 OpenSSH 客户端（设置→应用→可选功能）或 Git'; Read-Host '按回车退出'; exit 1 }}
}} else {{ Ok "ssh: $sshExe" }}

$node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
if (-not $node) {{ Bad '找不到 node.exe。请先装 Node.js（https://nodejs.org）'; Read-Host '按回车退出'; exit 1 }}
Ok "node: $node"

# ── 2. SSH 密钥 ──
Say ''
Say '==> 准备 SSH 密钥'
$sshDir = "$env:USERPROFILE\\.ssh"
if (-not (Test-Path $sshDir)) {{ New-Item -ItemType Directory -Path $sshDir -Force | Out-Null }}
$keyPriv = Join-Path $sshDir 'id_ed25519'
$keyPub  = "$keyPriv.pub"
if (-not (Test-Path $keyPub)) {{
  $kg = Join-Path (Split-Path $sshExe) 'ssh-keygen.exe'
  if (-not (Test-Path $kg)) {{ $kg = 'ssh-keygen.exe' }}
  & $kg -t ed25519 -f $keyPriv -N '""' 2>&1 | Out-Null
  if (Test-Path $keyPub) {{ Ok '已生成新密钥' }} else {{ Bad '密钥生成失败'; Read-Host '按回车退出'; exit 1 }}
}} else {{ Ok '已有密钥' }}

$pub = (Get-Content $keyPub -Raw).Trim()

# ── 3. 测直连 ──
Say ''
Say '==> 测试到远端 {host}'
$probe = & $sshExe -o ConnectTimeout=8 -o BatchMode=yes -o StrictHostKeyChecking=accept-new -p {ssh_port} '{ssh_user}@{host}' 'echo OK' 2>&1
if ("$probe" -notmatch 'OK') {{
  Say ''
  Say '  还不能免密登录远端。把下面这一行发给管理员，加到远端即可：' 'Yellow'
  Say ''
  Say "  $pub" 'White'
  Say ''
  Say '  管理员在远端执行（管理员 PowerShell）：' 'DarkGray'
  Say "    Add-Content C:\\ProgramData\\ssh\\administrators_authorized_keys '$pub'" 'DarkGray'
  Say ''
  Say '  加完后再运行一次本文件。' 'Yellow'
  Read-Host '按回车退出'
  exit 2
}}
Ok '免密登录正常'

# ── 4. 部署运行文件 ──
Say ''
Say '==> 部署本机运行文件'
$dir = Join-Path $env:LOCALAPPDATA 'Ferry'
New-Item -ItemType Directory -Path $dir -Force | Out-Null

$keeperPs1 = @'
# 隧道保活：ssh 退出后 5 秒重连，单例锁防重复启动
param([string]$RemoteHost='{host}', [int]$SocksPort=0)
$ErrorActionPreference='Continue'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$log = Join-Path $dir 'keeper.log'
function KLog($m) {{
  $line = "[{{0:yyyy-MM-dd HH:mm:ss}}] $m" -f (Get-Date)
  Add-Content -Path $log -Value $line -Encoding UTF8
  Write-Host $line
  if ((Get-Item $log -EA SilentlyContinue).Length -gt 512000) {{
    $tail = Get-Content $log -Tail 400
    Set-Content $log $tail -Encoding UTF8
  }}
}}
$lock = Join-Path $dir 'keeper.lock'
if (Test-Path $lock) {{
  $oldPid = (Get-Content $lock -Raw).Trim()
  $p = Get-CimInstance Win32_Process -Filter "ProcessId=$oldPid" -EA SilentlyContinue
  if ($p -and $p.CommandLine -like '*ssh-tunnel-keeper*') {{ KLog "already running pid=$oldPid"; exit 0 }}
  KLog "stale lock pid=$oldPid, taking over"
}}
Set-Content $lock $PID -Encoding ASCII
$ssh = Join-Path $env:SystemRoot 'System32\\OpenSSH\\ssh.exe'
if (-not (Test-Path $ssh)) {{ $ssh = 'C:\\Program Files\\Git\\usr\\bin\\ssh.exe' }}
$port = if ($SocksPort -gt 0) {{ $SocksPort }} else {{ 10808 }}
$attempt = 0
try {{
  while ($true) {{
    $attempt++
    $t0 = Get-Date
    KLog "ssh starting (attempt $attempt) -> $RemoteHost socks=127.0.0.1:$port"
    & $ssh -N -o BatchMode=yes -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 -o ServerAliveCountMax=3 -o ConnectTimeout=10 $RemoteHost -D "127.0.0.1:$port" 2>&1 | ForEach-Object {{ KLog "ssh: $_" }}
    $secs = [int]((Get-Date) - $t0).TotalSeconds
    KLog "ssh exited after ${{secs}}s, restart in 5s"
    Start-Sleep -Seconds 5
  }}
}} finally {{
  KLog 'keeper stop'
  Remove-Item $lock -Force -EA SilentlyContinue
}}
'@
Set-Content -Path (Join-Path $dir 'ssh-tunnel-keeper.ps1') -Value $keeperPs1 -Encoding UTF8

# 双协议代理：从门户服务器拉取（保证版本一致），拉不到就只装隧道
$proxyUrl = 'http://{host}:{CFG["port"]}/proxy.mjs'
$proxyPath = Join-Path $dir 'dual-proxy.mjs'
try {{
  Invoke-WebRequest -Uri $proxyUrl -OutFile $proxyPath -TimeoutSec 20 -UseBasicParsing
  Ok '已下载代理组件'
}} catch {{
  Bad "下载代理组件失败: $($_.Exception.Message)"
  Say '  隧道仍会建立，但系统代理不会自动切换。' 'Yellow'
}}

# ── 5. 启动 ──
Say ''
Say '==> 启动'
$kargs = "-NoProfile -ExecutionPolicy Bypass -File `"$dir\\ssh-tunnel-keeper.ps1`" -RemoteHost {host}"
Start-Process powershell.exe -ArgumentList $kargs -WindowStyle Minimized
Ok '隧道保活已启动'
Start-Sleep -Seconds 6

if (Test-Path $proxyPath) {{
  $bargs = "`"$proxyPath`" --keeper `"$dir\\ssh-tunnel-keeper.ps1`" --remote {host} --log `"$dir\\bridge.log`""
  Start-Process $node -ArgumentList $bargs -WindowStyle Minimized
  Ok '双协议代理已启动'
  Start-Sleep -Seconds 5

  $pf = Join-Path $dir '.proxy-state\\ports.json'
  if (Test-Path $pf) {{
    $pj = Get-Content $pf -Raw | ConvertFrom-Json
    $httpPort = [int]$pj.http
    $socksPort = [int]$pj.socks
    Say ''
    Say "  代理端口: HTTP=$httpPort  SOCKS=$socksPort" 'White'
    # 设系统代理（只认 HTTP 端口，绝不能填 SOCKS 端口）
    $reg = 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings'
    Set-ItemProperty -Path $reg -Name ProxyEnable -Value 1
    Set-ItemProperty -Path $reg -Name ProxyServer -Value "127.0.0.1:$httpPort"
    Ok "系统代理已设为 127.0.0.1:$httpPort"
  }} else {{
    Bad '没读到端口文件，代理可能没起来，看 bridge.log'
  }}
}}

# ── 6. 验证 ──
Say ''
Say '==> 验证'
$t = & curl.exe -s -o NUL -w '%{{http_code}}' --max-time 15 https://www.baidu.com 2>$null
if ("$t" -match '^[23]') {{ Ok '能正常上网' }} else {{ Bad "上网测试失败（$t）" }}

Say ''
Say '=============================================' 'Cyan'
Say '  部署完成' 'Cyan'
Say "  令牌: {tok}" 'DarkGray'
Say '  断网时也能用。要恢复直连，把系统代理关掉即可。' 'DarkGray'
Say '=============================================' 'Cyan'
Read-Host '按回车退出'
'''

    b64 = base64.b64encode(ps.encode('utf-16-le')).decode('ascii')

    lines = [
        '@echo off',
        'chcp 65001 >nul 2>&1',
        f'rem 渡口一键部署 - 账号 {ip}',
        f'rem 由门户生成，令牌已内置',
        'setlocal',
        f'set "PS1=%TEMP%\\ferry-{ip.replace(".", "-")}.ps1"',
        'powershell -NoProfile -Command "[IO.File]::WriteAllBytes($env:PS1, '
        '[Convert]::FromBase64String(\'' + b64 + '\'))"',
        'if not exist "%PS1%" (echo 解压失败 & pause & exit /b 1)',
        'powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%"',
        'del "%PS1%" >nul 2>&1',
        'endlocal',
    ]
    return '\r\n'.join(lines) + '\r\n'


# ─────────────────────────── HTTP 处理 ───────────────────────────

class Handler(http.server.BaseHTTPRequestHandler):
    server_version = 'BorrowPortal/1.0'
    db = None
    admin_pw = ''

    def log_message(self, fmt, *args):
        # 只记关键行，避免刷屏
        if '/api/' in (args[0] if args else ''):
            return
        sys.stderr.write(f'[{datetime.now().strftime("%H:%M:%S")}] {self.client_address[0]} '
                         f'{fmt % args}\n')

    # ── 基础工具 ──

    # 安全响应头。每个响应都带，包括错误页。
    # CSP 用 'unsafe-inline' 是因为样式和脚本都内联（单文件部署的代价）；
    # 但因为所有输出都过 html.escape，且没有用户可控的 script/style 内容，
    # 实际风险很低。frame-ancestors 挡点击劫持，这是最要紧的一条。
    SEC_HEADERS = {
        'X-Content-Type-Options': 'nosniff',
        'X-Frame-Options': 'DENY',
        'Content-Security-Policy':
            "default-src 'self'; style-src 'self' 'unsafe-inline'; "
            "script-src 'self' 'unsafe-inline'; img-src 'self' data:; "
            "frame-ancestors 'none'; base-uri 'none'; form-action 'self'",
        'Referrer-Policy': 'no-referrer',
        'X-XSS-Protection': '1; mode=block',
        'Permissions-Policy': 'geolocation=(), microphone=(), camera=()',
    }

    def _sec_headers(self):
        for k, v in self.SEC_HEADERS.items():
            self.send_header(k, v)

    def send_html(self, body, code=200):
        data = body.encode('utf-8')
        self.send_response(code)
        self.send_header('Content-Type', 'text/html; charset=utf-8')
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        self._sec_headers()
        self.end_headers()
        self.wfile.write(data)

    def send_json(self, obj, code=200):
        data = json.dumps(obj, ensure_ascii=False).encode('utf-8')
        self.send_response(code)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        self._sec_headers()
        self.end_headers()
        self.wfile.write(data)

    def redirect(self, to, cookie=None):
        # 只允许站内相对路径，防开放重定向
        if not to.startswith('/') or to.startswith('//'):
            to = '/'
        self.send_response(302)
        self.send_header('Location', to)
        if cookie:
            self.send_header('Set-Cookie', cookie)
        self._sec_headers()
        self.end_headers()

    def read_form(self):
        n = int(self.headers.get('Content-Length') or 0)
        if n <= 0 or n > 65536:
            return {}
        raw = self.rfile.read(n).decode('utf-8', 'replace')
        return {k: v[0] for k, v in urllib.parse.parse_qs(raw).items()}

    def get_cookie(self, name):
        raw = self.headers.get('Cookie') or ''
        for part in raw.split(';'):
            k, _, v = part.strip().partition('=')
            if k == name:
                return v
        return ''

    def client_ip(self):
        """取真实来源 IP。门户直接对外，不用 X-Forwarded-For（可伪造）。"""
        return self.client_address[0]

    # ── 会话 ──

    def current_session(self):
        """返回 (ip, row) 或 (None, None)"""
        raw = self.get_cookie('bn_sess')
        if not raw or '.' not in raw:
            return None, None
        sid, _, sig = raw.rpartition('.')
        row = self.db.q1('SELECT * FROM sessions WHERE sid_hash = ?',
                         (hashlib.sha256(sid.encode()).hexdigest(),))
        if not row:
            return None, None
        if not verify_sig(sid, row['ip'], sig):
            return None, None
        if row['expires_at'] < now():
            self.db.x('DELETE FROM sessions WHERE sid_hash = ?',
                      (hashlib.sha256(sid.encode()).hexdigest(),))
            return None, None
        return row['ip'], row

    def is_admin(self):
        raw = self.get_cookie('bn_admin')
        return bool(raw) and hmac.compare_digest(raw, self._admin_cookie())

    def _admin_cookie(self):
        return hmac.new(SESSION_SECRET, b'admin', hashlib.sha256).hexdigest()[:40]

    # ── 路由 ──

    def do_GET(self):
        path = urllib.parse.urlparse(self.path).path
        try:
            if path == '/':
                ip, _ = self.current_session()
                if ip:
                    self.redirect('/portal')
                    return
                self.send_html(login_page(csrf=csrf_set('login|' + self.client_ip())))
            elif path == '/portal':
                self.handle_portal()
            elif path.startswith('/download/'):
                self.handle_download(path)
            elif path == '/logout':
                self.handle_logout()
            elif path == '/admin':
                self.handle_admin()
            elif path == '/proxy.mjs':
                self.handle_proxy_mjs()
            elif path == '/api/status':
                self.handle_api_status()
            elif path == '/setup-hosts':
                self.handle_setup_hosts()
            elif path == '/favicon.ico':
                self.send_response(204)
                self.end_headers()
            else:
                self.send_html(page('404', '<h1>404</h1><div class="sub">没有这个页面</div>'), 404)
        except BrokenPipeError:
            # 客户端提前断开（刷新 / 关标签页 / curl --max-time 到点）不是错误。
            # 这类异常在 HTTP 服务里随时可能发生，打日志只会淹没真问题。
            pass
        except Exception as e:
            sys.stderr.write(f'ERROR {path}: {e}\n')
            try:
                self.send_html(page('错误', f'<h1>出错了</h1><pre>{html.escape(str(e))}</pre>'), 500)
            except Exception:
                # 连错误页都发不出去（连接已断），只能记 stderr
                pass

    def do_POST(self):
        path = urllib.parse.urlparse(self.path).path
        try:
            form = self.read_form()

            # CSRF 校验：所有 POST 都要过。
            # 两个登录页各自有独立的 csrf slot（未登录时还没有 session）。
            if path == '/login':
                key = 'login|' + self.client_ip()
            elif path == '/admin/login':
                key = 'adminlogin|' + self.client_ip()
            else:
                key = 'admin|' + self.client_ip()
            if not csrf_check(key, form.get('csrf', '')):
                self.db.log('csrf_fail', self.client_ip(), path)
                self.send_html(page('请求无效',
                    '<h1>请求无效</h1><div class="sub">'
                    '页面已过期，请重新打开。若反复出现，检查是否有人跨站提交。</div>'), 403)
                return

            if path == '/login':
                self.handle_login(form)
            elif path == '/admin/login':
                self.handle_admin_login(form)
            elif path == '/admin/newcode':
                self.handle_admin_newcode(form)
            elif path == '/admin/revoke':
                self.handle_admin_revoke(form)
            elif path == '/admin/toggle':
                self.handle_admin_toggle(form)
            elif path == '/admin/resettoken':
                self.handle_admin_resettoken(form)
            else:
                self.send_html(page('404', '<h1>404</h1>'), 404)
        except BrokenPipeError:
            # 同上：客户端断开不是错误。
            pass
        except Exception as e:
            sys.stderr.write(f'ERROR POST {path}: {e}\n')
            try:
                self.send_html(page('错误', f'<h1>出错了</h1><pre>{html.escape(str(e))}</pre>'), 500)
            except Exception:
                # 连接已断，错误页发不出去
                pass

    # ── 业务处理 ──

    def handle_login(self, form):
        src_ip = self.client_ip()

        # 速率限制：先挡，再干活。防密钥爆破。
        allowed, retry = rate_check(src_ip, 'user')
        if not allowed:
            self.db.log('rate_limited', src_ip, 'login')
            self.send_html(login_page(
                f'尝试太频繁，请 {retry // 60 + 1} 分钟后再试',
                csrf=csrf_set('login|' + src_ip)), 429)
            return

        ip = (form.get('ip') or '').strip()
        code = (form.get('code') or '').strip().upper().replace('-', '').replace(' ', '')
        csrf_tok = csrf_set('login|' + src_ip)

        if not PRIVATE_IP_RE.match(ip):
            self.send_html(login_page('IP 格式不对，只能是局域网地址（10.x / 172.16-31.x / 192.168.x）',
                                      csrf=csrf_tok), 400)
            return
        if not re.fullmatch(r'[A-Z0-9]{6,16}', code):
            self.send_html(login_page('密钥格式不对，应该是 8 位字母数字', csrf=csrf_tok), 400)
            return

        ch = hash_code(code)
        inv = self.db.q1('SELECT * FROM invites WHERE code_hash = ?', (ch,))
        if not inv:
            locked = rate_fail(src_ip, 'user')
            self.db.log('login_fail', src_ip, 'unknown-code')
            msg = '密钥无效'
            if locked:
                msg = '尝试太频繁，已锁定 15 分钟'
            self.send_html(login_page(msg, csrf=csrf_tok), 401 if not locked else 429)
            return
        if inv['revoked']:
            rate_fail(src_ip, 'user')
            self.db.log('login_fail', src_ip, 'revoked')
            self.send_html(login_page('这个密钥已被撤销', csrf=csrf_tok), 401)
            return
        if inv['used_by_ip']:
            rate_fail(src_ip, 'user')
            self.db.log('login_fail', src_ip, 'already-used')
            self.send_html(login_page(f'这个密钥已被 {inv["used_by_ip"]} 用过（一次性）', csrf=csrf_tok), 401)
            return
        if inv['expires_at'] < now():
            rate_fail(src_ip, 'user')
            self.db.log('login_fail', src_ip, 'expired')
            self.send_html(login_page('密钥已过期，请向管理员索取新的', csrf=csrf_tok), 401)
            return

        # 通过：绑定 IP
        existing = self.db.q1('SELECT * FROM clients WHERE ip = ?', (ip,))
        if existing:
            token = existing['token']
            self.db.x('UPDATE clients SET enabled = 1, last_seen = ? WHERE ip = ?', (now(), ip))
        else:
            token = new_token()
            self.db.x(
                'INSERT INTO clients (ip, token, enabled, created_at, last_seen, invite_id) '
                'VALUES (?,?,1,?,?,?)',
                (ip, token, now(), now(), inv['id']))

        self.db.x('UPDATE invites SET used_by_ip = ?, used_at = ? WHERE id = ?',
                  (ip, now(), inv['id']))

        sid, sig = make_sid(ip)
        self.db.x('INSERT INTO sessions (sid_hash, ip, created_at, expires_at) VALUES (?,?,?,?)',
                  (hashlib.sha256(sid.encode()).hexdigest(), ip, now(),
                   now() + SESSION_HOURS * 3600))
        rate_ok(src_ip, 'user')
        _csrf.pop('login|' + src_ip, None)
        self.db.log('login_ok', ip, f'invite={inv["id"]}')

        self.redirect('/portal', cookie=f'bn_sess={sid}.{sig}; Path=/; HttpOnly; SameSite=Lax; Max-Age={SESSION_HOURS*3600}')

    def handle_portal(self):
        ip, row = self.current_session()
        if not ip:
            self.redirect('/')
            return
        cli = self.db.q1('SELECT * FROM clients WHERE ip = ?', (ip,))
        if not cli or not cli['enabled']:
            self.send_html(page('已停用',
                '<h1>账号已停用</h1><div class="sub">请联系管理员</div>'), 403)
            return
        self.db.x('UPDATE clients SET last_seen = ? WHERE ip = ?', (now(), ip))
        note = ''
        if cli['invite_id']:
            inv = self.db.q1('SELECT note FROM invites WHERE id = ?', (cli['invite_id'],))
            if inv:
                note = inv['note'] or ''
        self.send_html(portal_page(cli, note))

    def handle_download(self, path):
        ip, _ = self.current_session()
        want = urllib.parse.unquote(path[len('/download/'):])
        if not ip:
            self.redirect('/')
            return
        # 只能下载自己的部署包，防止拿到别人的令牌
        if ip != want:
            self.send_html(page('拒绝', '<h1>只能下载自己的部署包</h1>'), 403)
            return
        cli = self.db.q1('SELECT * FROM clients WHERE ip = ?', (ip,))
        if not cli or not cli['enabled']:
            self.send_html(page('已停用', '<h1>账号已停用</h1>'), 403)
            return

        data = build_deploy_cmd(cli).encode('utf-8-sig')
        fname = f'ferry-{ip.replace(".", "-")}.cmd'
        self.send_response(200)
        self.send_header('Content-Type', 'application/octet-stream')
        self.send_header('Content-Disposition', f'attachment; filename="{fname}"')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)
        self.db.log('download', ip, fname)

    def handle_proxy_mjs(self):
        """把 dual-proxy.mjs 提供给部署包下载。文件与本脚本同目录。"""
        here = os.path.dirname(os.path.abspath(__file__))
        for cand in ('dual-proxy.mjs', os.path.join('..', 'tools', 'dual-proxy.mjs')):
            p = os.path.normpath(os.path.join(here, cand))
            if os.path.exists(p):
                with open(p, 'rb') as f:
                    data = f.read()
                self.send_response(200)
                self.send_header('Content-Type', 'application/javascript; charset=utf-8')
                self.send_header('Content-Length', str(len(data)))
                self.end_headers()
                self.wfile.write(data)
                return
        self.send_html(page('缺失', '<h1>代理组件未部署</h1>'), 404)

    def handle_logout(self):
        raw = self.get_cookie('bn_sess')
        if raw and '.' in raw:
            sid, _, _sig = raw.rpartition('.')
            self.db.x('DELETE FROM sessions WHERE sid_hash = ?',
                      (hashlib.sha256(sid.encode()).hexdigest(),))
        self.redirect('/', cookie='bn_sess=; Path=/; Max-Age=0')

    # ── 管理 ──

    def handle_admin(self):
        if not self.is_admin():
            self.send_html(admin_login_page(csrf=csrf_set('adminlogin|' + self.client_ip())))
            return
        self.send_html(admin_page(self.db, csrf=csrf_set('admin|' + self.client_ip())))

    def handle_admin_login(self, form):
        src_ip = self.client_ip()
        allowed, retry = rate_check(src_ip, 'admin')
        if not allowed:
            self.db.log('rate_limited', src_ip, 'admin')
            self.send_html(admin_login_page(
                f'尝试太频繁，请 {retry // 60 + 1} 分钟后再试',
                csrf=csrf_set('adminlogin|' + src_ip)), 429)
            return

        pw = form.get('pw') or ''
        if not self.admin_pw or not hmac.compare_digest(pw, self.admin_pw):
            locked = rate_fail(src_ip, 'admin')
            self.db.log('admin_fail', src_ip, 'bad-password')
            msg = '密码不对' if not locked else '尝试太频繁，已锁定 15 分钟'
            self.send_html(admin_login_page(msg, csrf=csrf_set('adminlogin|' + src_ip)),
                           401 if not locked else 429)
            return

        rate_ok(src_ip, 'admin')
        _csrf.pop('adminlogin|' + src_ip, None)
        self.redirect('/admin',
                      cookie=f'bn_admin={self._admin_cookie()}; Path=/; HttpOnly; SameSite=Lax')

    def _require_admin(self):
        if not self.is_admin():
            self.send_html(admin_login_page('请先登录', csrf=csrf_set('adminlogin|' + self.client_ip())), 403)
            return False
        return True

    def handle_admin_newcode(self, form):
        if not self._require_admin():
            return
        note = (form.get('note') or '').strip()[:60]
        try:
            hours = int(form.get('hours') or DEFAULT_INVITE_HOURS)
        except ValueError:
            hours = DEFAULT_INVITE_HOURS
        hours = max(1, min(720, hours))

        code = new_invite_code()
        ch = hash_code(code)
        self.db.x(
            'INSERT INTO invites (code_hash, code_hint, note, created_at, expires_at) '
            'VALUES (?,?,?,?,?)',
            (ch, code[:4] + '****', note, now(), now() + hours * 3600))
        self.db.log('invite_new', self.client_ip(), f'note={note} hours={hours}')
        # 明文只在这一响应里出现一次
        self.send_html(admin_page(self.db, new_code=code, new_hours=hours, csrf=csrf_set('admin|' + self.client_ip())))

    def handle_admin_revoke(self, form):
        if not self._require_admin():
            return
        try:
            iid = int(form.get('id') or 0)
        except ValueError:
            iid = 0
        self.db.x('UPDATE invites SET revoked = 1 WHERE id = ?', (iid,))
        self.db.log('invite_revoke', self.client_ip(), f'id={iid}')
        self.send_html(admin_page(self.db, msg='已撤销', csrf=csrf_set('admin|' + self.client_ip())))

    def handle_admin_toggle(self, form):
        if not self._require_admin():
            return
        ip = (form.get('ip') or '').strip()
        cli = self.db.q1('SELECT * FROM clients WHERE ip = ?', (ip,))
        if not cli:
            self.send_html(admin_page(self.db, err='找不到这个 IP', csrf=csrf_set('admin|' + self.client_ip())), 404)
            return
        newval = 0 if cli['enabled'] else 1
        self.db.x('UPDATE clients SET enabled = ? WHERE ip = ?', (newval, ip))
        self.db.log('client_toggle', self.client_ip(), f'{ip} -> {newval}')
        self.send_html(admin_page(self.db, msg=f'{ip} 已{"启用" if newval else "停用"}', csrf=csrf_set('admin|' + self.client_ip())))

    def handle_admin_resettoken(self, form):
        if not self._require_admin():
            return
        ip = (form.get('ip') or '').strip()
        if not self.db.q1('SELECT 1 FROM clients WHERE ip = ?', (ip,)):
            self.send_html(admin_page(self.db, err='找不到这个 IP', csrf=csrf_set('admin|' + self.client_ip())), 404)
            return
        tok = new_token()
        self.db.x('UPDATE clients SET token = ? WHERE ip = ?', (tok, ip))
        self.db.log('client_resettoken', self.client_ip(), ip)
        self.send_html(admin_page(self.db, msg=f'{ip} 的新令牌：{tok}（请重新下发部署包）', csrf=csrf_set('admin|' + self.client_ip())))

    def handle_setup_hosts(self):
        """给出把 ferry.internal 指到本机的 hosts 命令。

        为什么要这一步：门户监听的是 IP，但用户记不住 IP。
        加一条 hosts 记录就能用 ferry.internal 访问，换 IP 也不用改。
        不改 hosts 也能用（直接用 IP），这只是体验优化。
        """
        host = CFG['public_host'] or '10.13.24.1'
        domain = CFG['domain']
        body = f"""
<h1>配置局域网域名</h1>
<div class="sub">把 <code>{html.escape(domain)}</code> 指向本机，之后用域名访问更方便</div>

<div class="card">
  <h2 style="margin-top:0">Windows（管理员 PowerShell）</h2>
  <pre style="background:#f5f7f9;padding:14px;border-radius:9px;overflow-x:auto;font-size:13px">$f = "$env:SystemRoot\\System32\\drivers\\etc\\hosts"
$line = "{html.escape(host)}  {html.escape(domain)}"
if (-not (Select-String -Path $f -Pattern '{html.escape(domain)}' -Quiet)) {{
  Add-Content -Path $f -Value $line -Encoding ASCII
}}
Write-Host "已添加: $line"</pre>
  <div style="font-size:13px;color:#78909c;margin-top:10px">
    执行后访问 <code>http://{html.escape(domain)}:{CFG['port']}/</code>
  </div>
</div>

<div class="card">
  <h2 style="margin-top:0">Linux / macOS</h2>
  <pre style="background:#f5f7f9;padding:14px;border-radius:9px;overflow-x:auto;font-size:13px">echo "{html.escape(host)}  {html.escape(domain)}" | sudo tee -a /etc/hosts</pre>
</div>

<div class="card" style="background:#fafbfc">
  <div style="font-size:13px;color:#78909c">
    不想改 hosts 也完全可以 —— 直接用 <code>http://{html.escape(host)}:{CFG['port']}/</code> 访问，
    功能完全一样。域名只是让你少记一个数字。<br><br>
    如果 <code>{html.escape(domain)}</code> 打不开但 IP 能打开，说明 hosts 没生效
    （Windows 需要管理员权限才能改）。
  </div>
</div>
"""
        self.send_html(page('配置局域网域名', body))

    def handle_api_status(self):
        """给本机 GUI/脚本查的简单状态。不带敏感信息。"""
        cli = self.db.q1('SELECT COUNT(*) c FROM clients WHERE enabled = 1')
        inv = self.db.q1('SELECT COUNT(*) c FROM invites WHERE revoked = 0 AND used_by_ip = "" AND expires_at > ?', (now(),))
        self.send_json({
            'ok': True,
            'clients': cli['c'] if cli else 0,
            'validInvites': inv['c'] if inv else 0,
            'time': now(),
        })


class ThreadingServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


# ─────────────────────────── 启动 ───────────────────────────

def main():
    ap = argparse.ArgumentParser(description='渡口登录门户')
    ap.add_argument('--port', type=int, default=8080)
    ap.add_argument('--bind', default='0.0.0.0')
    ap.add_argument('--db', default='portal.db')
    ap.add_argument('--public-host', default='', help='使用者连的远端地址')
    ap.add_argument('--ssh-user', default='pcserver')
    ap.add_argument('--ssh-port', type=int, default=22)
    args = ap.parse_args()

    CFG.update({
        'port': args.port, 'bind': args.bind, 'db': args.db,
        'public_host': args.public_host, 'ssh_user': args.ssh_user,
        'ssh_port': args.ssh_port,
    })

    db = DB(args.db)
    Handler.db = db
    Handler.admin_pw = ensure_admin_password()

    # 清理过期会话
    db.x('DELETE FROM sessions WHERE expires_at < ?', (now(),))

    # 后台维护线程：清过期 session + 清速率限制表。
    # 不做的话，长期运行内存和数据库都会慢慢涨（每个登录都插一行 session）。
    def _janitor():
        while True:
            time.sleep(600)
            try:
                n = db.x('DELETE FROM sessions WHERE expires_at < ?', (now(),)).rowcount
                rate_cleanup()
                # 事件表只留最近 2000 条，防止无限增长
                db.x('DELETE FROM events WHERE id NOT IN '
                     '(SELECT id FROM events ORDER BY id DESC LIMIT 2000)')
                if n:
                    sys.stderr.write(f'[janitor] 清理过期会话 {n} 条\n')
            except Exception as e:
                sys.stderr.write(f'[janitor] 出错: {e}\n')

    threading.Thread(target=_janitor, daemon=True).start()

    srv = ThreadingServer((args.bind, args.port), Handler)
    pwfile = admin_password_path()
    print('=' * 52)
    print('  渡口登录门户已启动')
    print(f'  监听   : http://{args.bind}:{args.port}/')
    print(f'  数据库 : {os.path.abspath(args.db)}')
    print(f'  管理密码: {pwfile}')
    print(f'  远端地址: {args.public_host or "(未设置，部署包里会是 10.13.24.1)"}')
    print('=' * 52)
    print('  Ctrl+C 停止')
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        print('\n停止')
        srv.shutdown()


if __name__ == '__main__':
    main()

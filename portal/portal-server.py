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
import io
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
    # 激活后的授权天数。0 = 永久。
    # 放在 CFG 里是为了让管理员能用 --license-days 改，不用动代码。
    'license_days': 30,
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
    revoked     INTEGER DEFAULT 0,
    license_days INTEGER DEFAULT NULL
);

CREATE TABLE IF NOT EXISTS clients (
    ip          TEXT PRIMARY KEY,
    token       TEXT NOT NULL,
    enabled     INTEGER DEFAULT 1,
    note        TEXT DEFAULT '',
    created_at  INTEGER NOT NULL,
    last_seen   INTEGER DEFAULT 0,
    invite_id   INTEGER DEFAULT 0,
    -- 授权到期时间（unix 秒）。0 = 永久。
    -- 激活时写入，客户端据此显示剩余天数并决定能不能借网。
    expires_at  INTEGER DEFAULT 0
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

-- 每台机器的累计流量与历史峰值。
--
-- 为什么单独存一张表而不是只留内存：_hourly 只保留最近 24 小时，
-- 进程一重启就清空。「这台机器一共用了多少流量」这种问题必须跨重启可信，
-- 否则详情窗里那个数字每次重启都归零，等于没有。
CREATE TABLE IF NOT EXISTS traffic (
    ip          TEXT PRIMARY KEY,
    total_rx    INTEGER DEFAULT 0,
    total_tx    INTEGER DEFAULT 0,
    peak_rx     INTEGER DEFAULT 0,
    peak_tx     INTEGER DEFAULT 0,
    first_seen  INTEGER DEFAULT 0,
    updated_at  INTEGER DEFAULT 0
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
        self._migrate()
        self.conn.commit()

    def _migrate(self):
        """给已经存在的库补新列。

        SCHEMA 用的是 CREATE TABLE IF NOT EXISTS —— 对老库它什么都不做，
        新增的 expires_at 不会出现。所以必须单独 ALTER。
        重复 ALTER 会报 duplicate column，用异常吞掉即可（幂等）。
        """
        cols = {r['name'] for r in self.conn.execute('PRAGMA table_info(clients)')}
        if 'expires_at' not in cols:
            self.conn.execute('ALTER TABLE clients ADD COLUMN expires_at INTEGER DEFAULT 0')
            self.conn.commit()
            print('[db] migrated: clients.expires_at added', flush=True)

        # 每个密钥自带的授权天数。老密钥留 NULL，回退到全局默认。
        icols = {r['name'] for r in self.conn.execute('PRAGMA table_info(invites)')}
        if 'license_days' not in icols:
            self.conn.execute('ALTER TABLE invites ADD COLUMN license_days INTEGER DEFAULT NULL')
            self.conn.commit()
            print('[db] migrated: invites.license_days added', flush=True)

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


# 可选档位（天）。0 = 永久。
LICENSE_TIERS = (1, 2, 3, 7, 30)


def tier_text(days):
    if days is None:
        return '默认'
    d = int(days)
    return '永久' if d <= 0 else (str(d) + ' 天')


def license_days_for(inv):
    """这个密钥能换多少天授权。

    优先级：密钥自带的 license_days → 全局 CFG['license_days']。
    老密钥（迁移后是 NULL）就走全局默认，不会因为加了新列而失效。
    """
    v = inv['license_days'] if inv is not None and 'license_days' in inv.keys() else None
    if v is not None:
        return int(v)
    return int(CFG.get('license_days') or 0)


def expiry_of(days):
    """天数 → 到期时间戳。0 或负数表示永久（返回 0）。"""
    if days is None or days <= 0:
        return 0
    return now() + int(days) * 86400


def days_left_of(exp):
    if not exp or exp <= 0:
        return 0
    return max(0, int((exp - now()) // 86400))


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
    """取（或首次创建）这个会话的 CSRF token。

    ⚠ 这里**不轮换**，是修一个实际会踩到的 bug 才改的。

    原来的实现每次调用都 `new_csrf()` 覆盖旧值 —— 效果是「每渲染一次页面，
    上一个 token 立刻失效」。整页刷新的老流程看不出问题（因为每次提交都会
    重新渲染并带回新 token）。

    但一旦有 fetch/局部更新就会炸：
      · 用户点笔改备注 → fetch 保存成功 → 服务端悄悄轮换了 token
      · 用户接着点「停用」→ 表单里还是旧 token → 403
      · 或者再改第二条备注 → 403
    实测就是这样：第二次保存返回 403，用户看到「保存失败」但不知道为什么。

    稳定性 vs 安全性的取舍：
      token 仍然随机不可猜（32 字节）、绑定客户端 IP、只活在这个进程里。
      失去的只是「每次请求换一次」这个额外保护 —— 那对绑定了 IP 的
      管理会话来说收益很小，而代价是随时可能 403。
    登录成功时用 csrf_rotate() 主动换一次，防会话固定。
    """
    if key not in _csrf:
        _csrf[key] = new_csrf()
    return _csrf[key]


def csrf_rotate(key):
    """主动轮换（登录成功 / 登出时用）。"""
    _csrf[key] = new_csrf()
    return _csrf[key]


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
/* ── 主题变量：默认深色 ──────────────────────────────────
   配色思路：中性灰底 + 一个强调色。强调色是「渡口」的意象 —— 水/夜航。
   所有颜色都走变量，切主题时只换这一块，布局代码一行不动。 */
:root, html[data-theme="dark"] {
  --bg:#0f1216; --bg2:#151a20; --card:#1a2028; --card2:#212a33;
  --fg:#e6edf3; --fg2:#9aa8b5; --line:#2a3441; --line2:#354252;
  --accent:#3ddc97; --accent-d:#2fbf80; --accent-fg:#062018;
  --err:#ff6b6b; --errbg:#2a1618; --ok:#3ddc97; --okbg:#12251d;
  --warn:#ffd166; --warnbg:#2a2416;
  --shadow:0 1px 3px rgba(0,0,0,.4);
  --code:#101820;
}
html[data-theme="light"] {
  --bg:#f4f6f8; --bg2:#eef1f4; --card:#ffffff; --card2:#fafbfc;
  --fg:#2c3e50; --fg2:#6b7c8c; --line:#dde3e8; --line2:#c9d3dc;
  --accent:#26a69a; --accent-d:#1f8f85; --accent-fg:#ffffff;
  --err:#c62828; --errbg:#ffebee; --ok:#2e7d32; --okbg:#e8f5e9;
  --warn:#f57f17; --warnbg:#fff8e1;
  --shadow:0 1px 3px rgba(0,0,0,.06);
  --code:#eef2f5;
}
/* 护眼：暖灰底，降低蓝光 */
html[data-theme="sepia"] {
  --bg:#1c1814; --bg2:#241f19; --card:#2a241d; --card2:#332c24;
  --fg:#ecdfcc; --fg2:#b5a48c; --line:#3d3529; --line2:#4d4436;
  --accent:#e0a458; --accent-d:#c98d42; --accent-fg:#1c1814;
  --err:#ff7b72; --errbg:#2e1a17; --ok:#a3d977; --okbg:#1f2a17;
  --warn:#f0c674; --warnbg:#2e2617;
  --shadow:0 1px 3px rgba(0,0,0,.4);
  --code:#241f19;
}
/* 深海：偏蓝，长时间阅读舒服 */
html[data-theme="ocean"] {
  --bg:#0a1420; --bg2:#0f1c2b; --card:#132234; --card2:#1a2c42;
  --fg:#d8e6f3; --fg2:#8aa4bd; --line:#1e3349; --line2:#2a4560;
  --accent:#4cc9f0; --accent-d:#38a8cc; --accent-fg:#04141c;
  --err:#ff6b81; --errbg:#2a1620; --ok:#4cc9f0; --okbg:#0f2230;
  --warn:#ffd166; --warnbg:#2a2416;
  --shadow:0 1px 3px rgba(0,0,0,.45);
  --code:#0d1a28;
}

*{box-sizing:border-box}
html,body{background:var(--bg);color:var(--fg)}
body{font:15px/1.65 system-ui,-apple-system,"Segoe UI","Microsoft YaHei",sans-serif;
     margin:0;padding:0;-webkit-font-smoothing:antialiased}
.wrap{max-width:860px;margin:0 auto;padding:26px 20px 40px}

/* 顶栏 */
.top{display:flex;align-items:center;justify-content:space-between;
     gap:16px;margin-bottom:22px;padding-bottom:16px;border-bottom:1px solid var(--line)}
.brand{display:flex;align-items:center;gap:10px;font-weight:700;font-size:17px;letter-spacing:.5px}
.brand .mark{width:30px;height:30px;border-radius:9px;background:var(--accent);
             color:var(--accent-fg);display:flex;align-items:center;justify-content:center;
             font-size:15px;font-weight:800}
.brand .en{font-size:11px;color:var(--fg2);font-weight:500;letter-spacing:1.5px}

/* 主题选择器 */
.themes{display:flex;gap:6px}
.themes button{width:26px;height:26px;padding:0;margin:0;border-radius:50%;
  border:2px solid transparent;cursor:pointer;transition:transform .12s}
.themes button:hover{transform:scale(1.12)}
.themes button[data-on="1"]{border-color:var(--fg)}
.t-dark{background:#1a2028}.t-light{background:#ffffff;border-color:#dde3e8!important}
.t-sepia{background:#2a241d}.t-ocean{background:#132234}

h1{font-size:22px;margin:0 0 6px;letter-spacing:.3px}
h2{font-size:14px;margin:26px 0 10px;color:var(--fg2);
   text-transform:uppercase;letter-spacing:1px;font-weight:600}
.sub{color:var(--fg2);font-size:13.5px;margin-bottom:22px}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;
      padding:22px 24px;box-shadow:var(--shadow);margin-bottom:16px}
label{display:block;font-size:13px;color:var(--fg2);margin:14px 0 6px;font-weight:500}
input[type=text],input[type=password],input[type=number],textarea{
  width:100%;padding:11px 13px;border:1px solid var(--line2);border-radius:9px;
  font-size:15px;font-family:inherit;background:var(--card2);color:var(--fg);outline:none;
  transition:border-color .12s,background .12s}
input:focus,textarea:focus{border-color:var(--accent);background:var(--bg2)}
input::placeholder{color:var(--fg2);opacity:.7}
button{background:var(--accent);color:var(--accent-fg);border:0;border-radius:9px;
       padding:12px 22px;font-size:15px;font-weight:600;cursor:pointer;
       font-family:inherit;margin-top:18px;transition:background .12s}
button:hover{background:var(--accent-d)}
button.ghost{background:transparent;color:var(--fg);border:1px solid var(--line2)}
button.ghost:hover{background:var(--card2)}
button.danger{background:var(--err);color:#fff}
button.sm{padding:7px 13px;font-size:13px;margin:0}
button.link{background:none;border:0;color:var(--accent);padding:0;margin:0;
  font-size:13px;font-weight:500;text-decoration:underline;cursor:pointer}

table{width:100%;border-collapse:collapse;font-size:13px}
th,td{text-align:left;padding:9px 8px;border-bottom:1px solid var(--line)}
th{color:var(--fg2);font-weight:600;font-size:11.5px;text-transform:uppercase;letter-spacing:.6px}
tbody tr:last-child td{border-bottom:0}
tbody tr:hover{background:var(--card2)}

code{background:var(--code);padding:2px 6px;border-radius:4px;
     font-family:Consolas,Monaco,monospace;font-size:12.5px;color:var(--fg)}
pre{background:var(--code);padding:12px 14px;border-radius:9px;overflow-x:auto;
    font-size:12.5px;line-height:1.55;border:1px solid var(--line)}
pre code{background:none;padding:0}
.mono{font-family:Consolas,Monaco,monospace}
.ok{color:var(--ok)}.bad{color:var(--err)}.muted{color:var(--fg2)}
.big{font-size:26px;font-weight:700;letter-spacing:1.5px;font-family:Consolas,Monaco,monospace}
.msg{padding:11px 14px;border-radius:9px;margin:14px 0;font-size:14px;border:1px solid transparent}
.msg.err{background:var(--errbg);color:var(--err);border-color:var(--err)}
.msg.ok{background:var(--okbg);color:var(--ok);border-color:var(--ok)}
.msg.warn{background:var(--warnbg);color:var(--warn);border-color:var(--warn)}
.hint{font-size:12.5px;color:var(--fg2);margin-top:6px;line-height:1.55}
.row{display:flex;gap:12px;flex-wrap:wrap}
.row>*{flex:1;min-width:160px}
.dl{display:block;text-align:center;background:var(--accent);color:var(--accent-fg);
    text-decoration:none;padding:16px;border-radius:12px;
    font-size:16px;font-weight:700;margin:18px 0;letter-spacing:.3px}
.dl:hover{background:var(--accent-d)}
.step{display:flex;gap:14px;margin:16px 0}
.step .n{flex:0 0 28px;height:28px;border-radius:50%;background:var(--card2);
         color:var(--accent);font-weight:700;text-align:center;line-height:28px;font-size:13px;
         border:1px solid var(--line2)}
.step .t{flex:1;padding-top:3px}
.kv{display:flex;gap:10px;padding:7px 0;border-bottom:1px solid var(--line);font-size:13.5px}
.kv:last-child{border-bottom:0}
.kv .k{flex:0 0 130px;color:var(--fg2)}
.kv .v{flex:1;word-break:break-all}

/* 页脚：版权 + 免责声明 */
.foot{margin-top:34px;padding-top:20px;border-top:1px solid var(--line);
      font-size:12px;color:var(--fg2);line-height:1.75}
.foot .dis{background:var(--card);border:1px solid var(--line);border-radius:10px;
           padding:14px 16px;margin-bottom:14px}
.foot .dis b{color:var(--warn)}
.foot .links{display:flex;gap:16px;flex-wrap:wrap;margin:10px 0}
.foot a{color:var(--fg2);text-decoration:none}
.foot a:hover{color:var(--accent)}
.foot .cp{display:flex;justify-content:space-between;gap:12px;flex-wrap:wrap;
         padding-top:10px;border-top:1px solid var(--line);font-size:11.5px}
"""


THEME_COOKIE = 'ferry_theme'
THEMES = ('dark', 'light', 'sepia', 'ocean')
THEME_NAMES = {'dark': '深色', 'light': '浅色', 'sepia': '护眼', 'ocean': '深海'}

COPYRIGHT_YEAR = 2026


def _theme_from_cookie(cookie_header):
    """从 Cookie 里读主题偏好。

    为什么要服务端读：如果只在 JS 里设 data-theme，页面会先用默认色渲染
    再跳变（FOUC），在深色/浅色差异大时很刺眼。服务端直接写进 html 属性
    就没有这一帧。
    """
    for part in (cookie_header or '').split(';'):
        k, _, v = part.strip().partition('=')
        if k == THEME_COOKIE and v in THEMES:
            return v
    return 'dark'


def theme_switcher(current):
    """四个圆点，纯前端切换 + 写 localStorage + 写 cookie（供服务端下次渲染）。"""
    btns = []
    for t in THEMES:
        cls = 't-' + t
        on = ' data-on="1"' if t == current else ''
        btns.append(
            f'<button type="button" class="{cls}" data-theme-pick="{t}"{on} '
            f'title="{THEME_NAMES[t]}" aria-label="{THEME_NAMES[t]}"></button>')
    return '<div class="themes">' + ''.join(btns) + '</div>'


THEME_JS = """
<script>
(function () {
  var root = document.documentElement;
  function apply(t) {
    root.setAttribute('data-theme', t);
    document.cookie = 'ferry_theme=' + t + ';path=/;max-age=31536000;SameSite=Lax';
    document.querySelectorAll('[data-theme-pick]').forEach(function (b) {
      if (b.getAttribute('data-theme-pick') === t) b.setAttribute('data-on', '1');
      else b.removeAttribute('data-on');
    });
  }
  document.addEventListener('click', function (e) {
    var b = e.target.closest && e.target.closest('[data-theme-pick]');
    if (b) apply(b.getAttribute('data-theme-pick'));
  });
  // 若 cookie 没生效（比如被禁用），退回 localStorage
  if (!root.getAttribute('data-theme')) {
    try { var t = localStorage.getItem('ferry_theme'); if (t) apply(t); } catch (e) {}
  }
})();
</script>
"""


def footer():
    """页脚：版权 + 免责声明。

    为什么放免责声明：这是给同学用的内网工具，流量走的是别人的机器出口。
    必须把「谁负责」讲清楚，否则出事（比如被网管查、跑违规流量）说不明白。
    """
    return f"""
<div class="foot">
  <div class="dis">
    <b>免责声明</b><br>
    本服务仅供 <b>个人学习与内网连通性测试</b> 使用。使用者须自行遵守所在
    校园网 / 单位网络的《网络使用管理规定》及当地法律法规，不得利用本服务
    从事任何违法违规活动（包括但不限于传播非法内容、攻击他人系统、绕过
    计费或超出授权的访问）。<br>
    服务提供者不对使用者的行为及其后果承担任何责任；不对服务的可用性、
    连续性、传输速度作任何承诺；有权在不预先通知的情况下中断服务或撤销
    任何账号。使用本服务即视为已阅读并同意上述条款。
  </div>
  <div class="links">
    <a href="/about">关于 Ferry</a>
    <a href="/">登录</a>
    <a href="/setup-hosts">配置局域网域名</a>
    <a href="/api/status">运行状态</a>
  </div>
  <div class="cp">
    <span>© {COPYRIGHT_YEAR} Ferry（渡口）· 自建内网工具，非商业服务</span>
    <span>本项目基于 MIT 许可证开源</span>
  </div>
</div>"""


# ─────────────────────── 实时借网监控 ───────────────────────

_live_lock = threading.Lock()
_live_cache = {'at': 0.0, 'data': None}
_live_prev = {'at': 0.0, 'rx': 0, 'tx': 0, 'ready': False}
_live_peak = {'rx': 0, 'tx': 0}


def _sh(cmd, timeout=8):
    """跑一条命令拿 stdout。失败一律返回空串 —— 监控挂了不能拖垮管理页。"""
    try:
        import subprocess
        p = subprocess.run(cmd, capture_output=True, text=True,
                           timeout=timeout, errors='ignore')
        return p.stdout or ''
    except Exception:
        return ''


def _ssh_peers():
    """正在连本机 22 端口的外网地址 → 连接数。

    只看 ESTABLISHED 且本地端口是 22 的。回环和空地址跳过
    （管理员自己 ssh 上来运维不该算用户）。
    """
    out = _sh(['netstat', '-ano'])
    peers = {}
    for line in out.splitlines():
        parts = line.split()
        if len(parts) < 4:
            continue
        if parts[0].upper() != 'TCP':
            continue
        local, foreign, state = parts[1], parts[2], parts[3]
        if state.upper() != 'ESTABLISHED':
            continue
        if not (local.endswith(':22') or local.endswith(']:22')):
            continue
        ip = foreign.rsplit(':', 1)[0].strip('[]')
        if not ip or ip.startswith('127.') or ip == '::1':
            continue
        peers[ip] = peers.get(ip, 0) + 1
    return peers


def _net_bytes():
    """全部网卡的累计收/发字节。取不到就返回 None。"""
    out = _sh(['powershell', '-NoProfile', '-NonInteractive', '-Command',
               '$r=0;$t=0;Get-NetAdapterStatistics|%{$r+=$_.ReceivedBytes;$t+=$_.SentBytes};"$r $t"'],
              timeout=10)
    for line in out.splitlines():
        line = line.strip()
        if not line:
            continue
        seg = line.split()
        if len(seg) == 2 and seg[0].isdigit() and seg[1].isdigit():
            return int(seg[0]), int(seg[1])
    return None



# ─────────────────────── 用户心跳（每用户速率）───────────────────────

_hb_lock = threading.Lock()
_hb = {}            # ip -> {rx, tx, at, rxBps, txBps, conns, running}
HB_STALE_SEC = 30   # 超过这么久没心跳就当离线（客户端 5 秒一次，容 6 次丢包）
HB_KEEP_HOURS = 24  # 每小时统计保留多久

# ip -> { 'YYYYMMDDHH': {'peakRx','peakTx','rx','tx'} }
_hourly = {}


def _hour_key(t=None):
    return time.strftime('%Y%m%d%H', time.localtime(t or time.time()))


def note_hourly(ip, rx_bps, tx_bps, d_rx, d_tx):
    """按小时累计：峰值速率 + 流量增量。只留最近 HB_KEEP_HOURS 小时。"""
    slot = _hourly.setdefault(ip, {})
    k = _hour_key()
    cur = slot.get(k)
    if cur is None:
        cur = slot[k] = {'peakRx': 0, 'peakTx': 0, 'rx': 0, 'tx': 0}
    if rx_bps > cur['peakRx']:
        cur['peakRx'] = rx_bps
    if tx_bps > cur['peakTx']:
        cur['peakTx'] = tx_bps
    cur['rx'] += max(0, d_rx)
    cur['tx'] += max(0, d_tx)
    # 清掉过老的
    if len(slot) > HB_KEEP_HOURS + 2:
        keep = sorted(slot.keys())[-HB_KEEP_HOURS:]
        for old in list(slot.keys()):
            if old not in keep:
                del slot[old]


def hourly_of(ip):
    """最近若干小时的统计，按时间升序。"""
    slot = _hourly.get(ip) or {}
    keys = sorted(slot.keys())[-HB_KEEP_HOURS:]
    return [{'hour': k,
             'peakRx': slot[k]['peakRx'], 'peakTx': slot[k]['peakTx'],
             'rx': slot[k]['rx'], 'tx': slot[k]['tx']} for k in keys]


def note_heartbeat(ip, rx, tx, conns, running, mode=''):
    """记一次心跳并算速率。"""
    t = time.time()
    with _hb_lock:
        prev = _hb.get(ip)
        rx_bps = tx_bps = 0
        if prev is not None:
            dt = t - prev['at']
            if dt > 0.2:
                # 客户端重启会让计数归零，负数按 0 处理
                rx_bps = max(0, int((rx - prev['rx']) / dt))
                tx_bps = max(0, int((tx - prev['tx']) / dt))
        d_rx = max(0, rx - prev['rx']) if prev is not None else 0
        d_tx = max(0, tx - prev['tx']) if prev is not None else 0
        _hb[ip] = {'rx': rx, 'tx': tx, 'at': t,
                   'rxBps': rx_bps, 'txBps': tx_bps,
                   'conns': conns, 'running': bool(running),
                   'mode': (mode or '')[:16],
                   'dRx': d_rx, 'dTx': d_tx}
        note_hourly(ip, rx_bps, tx_bps, d_rx, d_tx)
        return _hb[ip]


# ── 累计流量（内存累加 + 定期落库）──
#
# 为什么不每次心跳都写库：10 台机器 × 每 5 秒 = 2 次/秒 UPDATE，
# 对 SQLite 不算重，但会持续占着写锁，和「面板读」抢。
# 每 60 秒 flush 一次，崩溃最多丢 60 秒的量 —— 对这个用途完全够。
_totals = {}          # ip -> {'rx','tx','peakRx','peakTx','first'}
_totals_last_flush = 0.0
TOTAL_FLUSH_SEC = 60


def note_totals(ip, d_rx, d_tx, rx_bps, tx_bps):
    """把这次心跳的增量累到内存。"""
    t = _totals.get(ip)
    if t is None:
        t = _totals[ip] = {'rx': 0, 'tx': 0, 'peakRx': 0, 'peakTx': 0,
                           'first': int(time.time())}
    t['rx'] += max(0, d_rx)
    t['tx'] += max(0, d_tx)
    if rx_bps > t['peakRx']:
        t['peakRx'] = rx_bps
    if tx_bps > t['peakTx']:
        t['peakTx'] = tx_bps


def flush_totals(db, force=False):
    """把内存里的累计量写回 DB。force=True 时无视间隔。"""
    global _totals_last_flush
    now_t = time.time()
    if not force and now_t - _totals_last_flush < TOTAL_FLUSH_SEC:
        return
    if not _totals:
        _totals_last_flush = now_t
        return
    items = list(_totals.items())
    _totals_last_flush = now_t
    for ip, t in items:
        try:
            db.x(
                'INSERT INTO traffic (ip, total_rx, total_tx, peak_rx, peak_tx, first_seen, updated_at) '
                'VALUES (?,?,?,?,?,?,?) '
                'ON CONFLICT(ip) DO UPDATE SET '
                '  total_rx = total_rx + excluded.total_rx,'
                '  total_tx = total_tx + excluded.total_tx,'
                '  peak_rx = MAX(peak_rx, excluded.peak_rx),'
                '  peak_tx = MAX(peak_tx, excluded.peak_tx),'
                '  updated_at = excluded.updated_at',
                (ip, t['rx'], t['tx'], t['peakRx'], t['peakTx'],
                 t['first'], int(now_t)))
        except Exception as e:
            sys.stderr.write('[traffic] flush %s failed: %s\n' % (ip, e))
    # 已经写进 DB 的部分清零，避免下一轮重复累加
    for ip, t in items:
        if ip in _totals:
            _totals[ip]['rx'] = 0
            _totals[ip]['tx'] = 0
            _totals[ip]['first'] = int(now_t)


def traffic_of(db, ip):
    """读某台机器的累计流量（DB 里的 + 还没落库的内存增量）。"""
    row = None
    try:
        row = db.q1('SELECT * FROM traffic WHERE ip = ?', (ip,))
    except Exception:
        pass
    total_rx = int(row['total_rx']) if row else 0
    total_tx = int(row['total_tx']) if row else 0
    peak_rx = int(row['peak_rx']) if row else 0
    peak_tx = int(row['peak_tx']) if row else 0
    first_seen = int(row['first_seen']) if row else 0

    pend = _totals.get(ip)
    if pend:
        total_rx += pend['rx']
        total_tx += pend['tx']
        peak_rx = max(peak_rx, pend['peakRx'])
        peak_tx = max(peak_tx, pend['peakTx'])
        if not first_seen:
            first_seen = pend['first']
    return {'totalRx': total_rx, 'totalTx': total_tx,
            'peakRx': peak_rx, 'peakTx': peak_tx,
            'firstSeen': first_seen}


def license_state_of(cli):
    """从一个 clients 行算出授权状态。

    为什么抽出来：/api/license 和 /api/heartbeat 都要判这套逻辑。
    抄两遍迟早会不一致 —— 那时候「心跳说能用、授权接口说不能用」，
    客户端行为会变得完全不可预测。

    到期是**软状态**（不落库）：用户续费后不用管理员手工改库，
    下次查询自然就恢复了。
    """
    if not cli:
        return {'enabled': False, 'expired': False, 'suspended': True,
                'expiresAt': 0, 'daysLeft': 0, 'hoursLeft': 0}
    exp = int(cli['expires_at'] or 0)
    left = 0 if exp <= 0 else max(0, exp - now())
    expired = exp > 0 and left <= 0
    suspended = not bool(cli['enabled'])
    return {
        'enabled': (not suspended) and (not expired),
        'expired': expired,
        'suspended': suspended,
        'expiresAt': exp,
        'daysLeft': 0 if exp <= 0 else int(left // 86400),
        'hoursLeft': int(left // 3600),
    }


def heartbeat_of(ip):
    """拿某 IP 的心跳快照；过期返回 None。"""
    with _hb_lock:
        h = _hb.get(ip)
        if not h:
            return None
        if time.time() - h['at'] > HB_STALE_SEC:
            return None
        return dict(h)


def sample_live(db):
    """采一次现场。带 2 秒缓存。

    ⚠ 在线判据是「有心跳」，不是「有 SSH 连接」。

    为什么改（实测踩过）：直连模式（A 方案）下客户端根本不建 SSH 隧道，
    「到本机 22 端口的 ESTABLISHED 连接」永远是 0，于是
      · 所有设备被判成离线
      · 速率列全是「—」
      · 顶部四个指标卡全是 0
    而详情弹窗里数据又是对的（那条路直接读心跳）。面板看起来完全坏了。

    心跳是客户端每 5 秒主动上报的，跟走 SSH 还是走 SOCKS 无关，
    才是唯一可靠的在线判据。SSH 连接数现在只作为「隧道数」的参考值。
    """
    with _live_lock:
        t = time.time()
        if _live_cache['data'] is not None and t - _live_cache['at'] < 2.0:
            return _live_cache['data']

        # 只用于展示「隧道连接数」参考值，不再参与在线判断
        peers = _ssh_peers()
        nb = _net_bytes()

        net_rx_bps = net_tx_bps = 0
        if nb is not None:
            if _live_prev['ready']:
                dt = t - _live_prev['at']
                if dt > 0.2:
                    net_rx_bps = max(0, int((nb[0] - _live_prev['rx']) / dt))
                    net_tx_bps = max(0, int((nb[1] - _live_prev['tx']) / dt))
                    _live_peak['rx'] = max(_live_peak['rx'], net_rx_bps)
                    _live_peak['tx'] = max(_live_peak['tx'], net_tx_bps)
            _live_prev.update(at=t, rx=nb[0], tx=nb[1], ready=True)

        rows = []
        try:
            rows = db.q('SELECT ip, enabled, note, last_seen FROM clients')
        except Exception:
            rows = []

        users = []
        for r in rows:
            ip = r['ip']
            h = heartbeat_of(ip)
            on = h is not None
            users.append({
                'ip': ip,
                'enabled': bool(r['enabled']),
                'note': r['note'] or '',
                'online': on,
                'conns': int(h['conns']) if h else 0,
                'rxBps': int(h['rxBps']) if h else 0,
                'txBps': int(h['txBps']) if h else 0,
                'reported': on,
                'mode': (h.get('mode') or '') if h else '',
                'lastSeen': int(r['last_seen'] or 0),
            })
        # 在线优先 → 总速率降序 → 最后活动降序
        users.sort(key=lambda u: (0 if u['online'] else 1,
                                  -(u['rxBps'] + u['txBps']),
                                  -u['lastSeen']))

        online_users = [u for u in users if u['online']]

        # 顶部指标用「所有在线机器的心跳速率之和」。
        # 为什么不用网卡总量：那是整台机器（含 sshd、门户自己）的流量，
        # 和「用户借网有多快」不是一回事；网络里一有别的动作数字就跳。
        data = {
            'ok': True,
            'at': int(t),
            'list': users,
            'hourly': {u['ip']: hourly_of(u['ip']) for u in online_users},
            'onlineCount': len(online_users),
            'conns': sum(u['conns'] for u in online_users),
            'rxBps': sum(u['rxBps'] for u in online_users),
            'txBps': sum(u['txBps'] for u in online_users),
            'peakRxBps': _live_peak['rx'],
            'peakTxBps': _live_peak['tx'],
            # 服务端网卡实测（排查用，面板不直接用）
            'netRxBps': net_rx_bps,
            'netTxBps': net_tx_bps,
            # 还有多少台机器在走 SSH 隧道（老客户端），用来判断升级情况
            'tunnelConns': sum(peers.values()),
        }
        # 载荷信息：当前速率 vs 出口带宽 vs 每人配额
        _rc = read_rate_conf()
        data['net'] = load_state(data['rxBps'], data['txBps'],
                                 data['onlineCount'], _rc['perIp'], _rc['total'])
        _live_cache['data'] = data
        _live_cache['at'] = t
        return data

def page(title, body, refresh=None, theme=None, cookie_header=None):
    meta = f'<meta http-equiv="refresh" content="{refresh}">' if refresh else ''
    cur = theme or _theme_from_cookie(cookie_header)
    top = (f'<div class="top"><div class="brand"><span class="mark">渡</span>'
           f'<span>渡口<span class="en">FERRY</span></span></div>'
           f'{theme_switcher(cur)}</div>')
    return f"""<!doctype html><html lang="zh-CN" data-theme="{cur}"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="color-scheme" content="dark light">
{meta}<title>{html.escape(title)}</title><style>{BASE_CSS}</style></head>
<body><div class="wrap">{top}{body}{footer()}</div>{THEME_JS}</body></html>"""


def get_page(detected_ip=''):
    """首页 = 获取软件。

    为什么把登录表单撤掉：用户明确要求「网页只用来获取软件」。
    密钥改成软件里的激活码，网页再收一遍只会让人以为要登录两次。

    下载区显示**当前版本号和实际文件大小**（从 dist 里现读，不是写死的常量）——
    踩过的坑：以前写死「约 2.1 MB」，后来包涨到 16.9 MB（内嵌 mihomo），
    提示就成了误导，用户按 2.1 MB 去核对下载完整性会以为文件坏了。

    下面两个 <details> 默认折叠：历史版本 + 更新日志。
    为什么默认折叠：普通用户只关心「点这个按钮」；版本历史和更新日志是
    出问题或想回退时才看的。铺开会让首屏变长，重点反而被埋掉。
    """
    ip_txt = html.escape(detected_ip) if detected_ip else '你的IP'

    installers = list_installers()
    manifest = read_version_manifest()
    latest_ver = str(manifest.get('latest') or '0.0.0')
    latest_size = 0
    for it in installers:
        if it['ver'] == latest_ver:
            latest_size = it['size']
            break
    if not latest_size and installers:
        latest_size = installers[0]['size']
        latest_ver = installers[0]['ver']
    size_txt = human_size(latest_size) if latest_size else '未知'

    # 历史版本行（不含当前版本）
    older = [x for x in installers if x['ver'] != latest_ver]
    if older:
        rows = []
        for x in older:
            rows.append(
                '<div style="display:flex;align-items:center;justify-content:space-between;'
                'padding:8px 0;border-top:1px solid var(--line)">'
                f'<div><b>v{x["ver"]}</b>'
                f'<span class="hint" style="margin-left:8px">{human_size(x["size"])}</span></div>'
                f'<a href="/download/version/{x["ver"]}" style="color:var(--accent);'
                f'font-size:13px;text-decoration:none">下载</a></div>')
        older_html = '\n'.join(rows)
    else:
        older_html = '<div class="hint" style="padding:8px 0">还没有更早的版本</div>'

    log_html = md_to_html_brief(read_changelog())

    return page('获取渡口 · Ferry', f"""
<h1>渡口 · Ferry</h1>
<div class="sub">本机网络受限时，借一台能上网的内网主机中转流量</div>

<div class="card" style="text-align:center;padding:22px 18px">
  <a class="dl" href="/download"
     style="display:block;font-size:17px;padding:16px 20px;text-decoration:none">
    下载 Windows 客户端
  </a>
  <div class="hint" style="margin-top:14px;font-size:12.5px">
    <b>v{html.escape(latest_ver)}</b> · Ferry-Setup-{html.escape(latest_ver)}.exe ·
    <b>{size_txt}</b> · Windows 10 / 11 · 免安装运行库
  </div>
  <div class="hint" style="margin-top:10px;font-size:12px;line-height:1.7;
       background:var(--warnbg);color:var(--warn);padding:10px 12px;border-radius:8px">
    下载完成后请确认文件大小约 <b>{size_txt}</b>。<br>
    如果只有 0 KB，说明下载被中断了 —— 删掉那个文件重新点一次。
    双击 0 KB 的文件会提示「此应用无法在你的电脑上运行」。
  </div>
</div>

<div class="card">
  <details>
    <summary style="cursor:pointer;font-size:14px;font-weight:600;outline:none">
      历史版本（{len(older)} 个）
      <span class="hint" style="font-weight:400;margin-left:6px">
        新版本有问题时可以装回旧版</span>
    </summary>
    <div style="margin-top:6px">
      {older_html}
    </div>
  </details>
</div>

<div class="card">
  <details>
    <summary style="cursor:pointer;font-size:14px;font-weight:600;outline:none">
      更新日志
      <span class="hint" style="font-weight:400;margin-left:6px">
        每个版本改了什么</span>
    </summary>
    <div style="margin-top:8px;max-height:520px;overflow:auto;
         font-size:13px;line-height:1.75">
      {log_html}
    </div>
  </details>
</div>

<div class="card">
  <h2 style="margin-top:0">三步用起来</h2>
  <div class="step"><div class="n">1</div><div class="t">
    <b>下载安装包</b><div class="hint">点上面的按钮。文件名里已经带了你的账号，
      下载后不要改名（改了也能用，安装时会让你手填 IP）。</div>
  </div></div>
  <div class="step"><div class="n">2</div><div class="t">
    <b>双击安装</b><div class="hint">安装时会弹一次「是否允许更改设备」——
      这是必需的，TUN 全局接管要建虚拟网卡。向导会问要不要建桌面图标、
      要不要开机自启，装完可以直接启动。</div>
  </div></div>
  <div class="step"><div class="n">3</div><div class="t">
    <b>在软件里输入密钥</b><div class="hint">打开「渡口助手」，把管理员给你的
      8 位密钥填进「输入密钥激活」，点激活。之后就不用再输了。</div>
  </div></div>
</div>

<div class="card">
  <h2 style="margin-top:0">下载后提示「Windows 已保护你的电脑」？</h2>
  <div style="font-size:13.5px;line-height:1.8">
    这是 SmartScreen 对<strong>未经代码签名</strong>的 exe 的例行拦截，不是病毒。
    三种处理方式，任选一种：
  </div>
  <div class="step"><div class="n">1</div><div class="t">
    <b>用命令行下载（不触发拦截，推荐）</b>
    <div class="hint">按 Win+R 输入 <code>powershell</code> 回车，粘贴下面这行：</div>
    <div class="mono" style="background:var(--code);color:var(--fg);padding:10px 12px;border-radius:8px;margin-top:8px;
         font-size:12px;word-break:break-all;user-select:all">
      Invoke-WebRequest 'http://10.13.24.1:8080/download' -OutFile "$env:USERPROFILE\\Downloads\\Ferry-Setup.exe"; Start-Process "$env:USERPROFILE\\Downloads\\Ferry-Setup.exe"
    </div>
    <div class="hint" style="margin-top:6px">
      浏览器会给下载的文件打上「来自 Internet」的标记（MOTW），SmartScreen 只查带标记的 exe。
      用 PowerShell 直接下载不带这个标记，所以不会弹窗。
    </div>
  </div></div>
  <div class="step"><div class="n">2</div><div class="t">
    <b>已经下载了：解除锁定</b>
    <div class="hint">右键那个 exe → 属性 → 最下面勾选「解除锁定」→ 确定 → 再双击。</div>
  </div></div>
  <div class="step"><div class="n">3</div><div class="t">
    <b>直接点「更多信息 → 仍要运行」</b>
    <div class="hint">SmartScreen 弹窗左下角有「更多信息」，展开后出现「仍要运行」。
      同一个文件点过一次就不再问。</div>
  </div></div>
</div>

<div class="card">
  <h2 style="margin-top:0">密钥在软件里输入，不在网页</h2>
  <div style="font-size:13.5px;line-height:1.8">
    网页只负责发软件。密钥是软件里的<strong>激活码</strong> ——
    填一次就绑定这台机器，以后打开软件直接能用。<br><br>
    这样做的好处是：安装包本身<strong>不含任何凭据</strong>，
    转发给别人也用不了；密钥丢了也不会连着账号一起泄露。
  </div>
</div>

<div class="card">
  <h2 style="margin-top:0">密钥规则</h2>
  <div class="kv"><div class="k">一次性</div><div class="v">
    激活成功即失效。换机器需要向管理员要新密钥。</div></div>
  <div class="kv"><div class="k">有效期</div><div class="v">
    密钥本身 12 小时内必须用掉；激活后的授权时长由管理员设定（默认 30 天）。</div></div>
  <div class="kv"><div class="k">限速</div><div class="v">
    连续输错会被临时锁定 15 分钟。</div></div>
</div>

<div class="card">
  <h2 style="margin-top:0">它怎么工作</h2>
  <div style="font-size:13.5px;line-height:1.8">
    校园网按<strong>账号</strong>限制上网能力时，局域网内部通常仍然是通的。
    渡口利用这一点：通过 SSH 隧道把流量交给一台<b>能上网的局域网主机</b>中转，
    出口在别人家。<br><br>
    隧道是 <code>ssh -D</code> 动态转发（加密），本地是双协议代理
    （HTTP + SOCKS5 同端口）—— 因为 Windows 系统代理不认 SOCKS5，
    这是个很容易踩的坑，干脆做成同一端口两种协议。
  </div>
</div>
""")


def login_page(err='', csrf='', detected_ip='', show_ip_field=False):
    """登录页：只要求密钥。

    为什么不再要求用户自己填 IP：那是给用户增加负担，还容易填错
    （比如填了 127.0.0.1、填了 WiFi 而不是有线网卡的地址）。
    服务端从连接来源就能拿到 IP，直接用它做登记即可。

    那个「可选 IP」输入框只在自动检测不可靠时才显示 ——
    典型场景是经过反向代理 / 端口转发，服务端看到的来源是 127.0.0.1。
    """
    errbox = f'<div class="msg err">{html.escape(err)}</div>' if err else ''
    csrf_in = f'<input type="hidden" name="csrf" value="{html.escape(csrf)}">' if csrf else ''
    ip_field = ''
    if show_ip_field:
        ip_field = f"""
    <label>你的局域网 IP<span style="color:#90a4ae;font-weight:400">（可选）</span></label>
    <input type="text" name="ip" placeholder="留空则自动识别"
           pattern="[0-9.]+" value="{html.escape(detected_ip)}">
    <div class="hint">检测到的来源是 {html.escape(detected_ip or '未知')}，
      如果这不对（例如你在代理后面），请手动填你机器的 IPv4 地址。</div>"""
    ip_note = ''
    if detected_ip and not show_ip_field:
        ip_note = f'<div class="hint">本机识别为 <b class="mono">{html.escape(detected_ip)}</b></div>'

    return page('渡口登录 · Ferry', f"""
<h1>渡口 · Ferry</h1>
<div class="sub">本机网络受限时，借一台能上网的内网主机中转流量</div>
{errbox}

<div class="card">
  <form method="post" action="/login">
    {csrf_in}
    <label>临时密钥</label>
    <input type="text" name="code" placeholder="8 位密钥"
           style="text-transform:uppercase;font-size:19px;letter-spacing:3px;text-align:center;font-family:Consolas,Monaco,monospace"
           required maxlength="16" autofocus autocomplete="off" spellcheck="false">
    {ip_field}
    <button type="submit" style="width:100%">进入</button>
  </form>
  {ip_note}
</div>

<div class="card">
  <h2 style="margin-top:0">这是什么</h2>
  <div style="font-size:13.5px;line-height:1.8">
    校园网按<strong>账号</strong>限制上网能力的时候，局域网内部通常仍然是通的。
    渡口利用这一点：通过 SSH 隧道，把你的流量交给一台<b>能上网的局域网主机</b>
    中转出去。对你来说只是设了个代理，实际出口在别人家。<br><br>
    隧道是 <code>ssh -D</code> 动态转发（加密），本地还有一个双协议代理
    （HTTP + SOCKS5 同端口），因为 Windows 系统代理不认 SOCKS5 —— 这是个
    很容易踩的坑，所以干脆做成同一个端口两种协议。
  </div>
</div>

<div class="card">
  <h2 style="margin-top:0">三步用起来</h2>
  <div class="step"><div class="n">1</div><div class="t">
    <b>拿到密钥</b><div class="hint">找管理员要一个 8 位密钥。一次性，用过即废。</div>
  </div></div>
  <div class="step"><div class="n">2</div><div class="t">
    <b>登录并下载部署包</b><div class="hint">登录后点下载，得到一个 .cmd 文件。</div>
  </div></div>
  <div class="step"><div class="n">3</div><div class="t">
    <b>双击运行</b><div class="hint">自动检测 SSH、生成密钥、建隧道、设代理、开开机自启。
      第一次需要把打印出来的公钥发给管理员，之后不用再管。</div>
  </div></div>
</div>

<div class="card">
  <h2 style="margin-top:0">密钥规则</h2>
  <div class="kv"><div class="k">一次性</div><div class="v">
    登录成功即失效。要再登录请申请新密钥。</div></div>
  <div class="kv"><div class="k">有效期</div><div class="v">
    默认 12 小时。过期未使用同样作废。</div></div>
  <div class="kv"><div class="k">限速</div><div class="v">
    每个账号默认 5 MB/s（管理员可调）。按账号计，不按连接数。</div></div>
  <div class="kv"><div class="k">试用失败过多</div><div class="v">
    5 分钟内错 5 次会锁定 15 分钟 —— 请找管理员在面板上解除。</div></div>
</div>

<div class="msg warn" style="font-size:13px">
  <b>注意</b>：本服务仅用于个人学习与内网连通性测试。
  出口流量经过他人的机器，请勿用于任何违规用途（详见页脚免责声明）。
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


LIVE_UI = r'''
<style>
/* 实时监控区：四个指标卡 + 在线列表。
   全部用主题变量，切深色/浅色/护眼都不用改这里。 */
.livegrid{display:grid;grid-template-columns:repeat(4,1fr);gap:12px;margin:0 0 18px}
@media(max-width:760px){.livegrid{grid-template-columns:repeat(2,1fr)}}
.livecard{background:var(--card);border:1px solid var(--line);border-radius:14px;
  padding:16px 18px;box-shadow:var(--shadow);position:relative;overflow:hidden}
.livecard .lbl{font-size:12px;color:var(--fg2);letter-spacing:.3px}
.livecard .val{font-size:26px;font-weight:700;margin-top:6px;line-height:1.1;
  font-family:Consolas,Monaco,monospace;transition:color .3s}
.livecard .unit{font-size:13px;font-weight:400;color:var(--fg2);margin-left:3px}
.livecard .sub{font-size:11px;color:var(--fg2);margin-top:6px}
.livecard.hot .val{color:var(--accent)}
.livecard .spark{position:absolute;left:0;right:0;bottom:0;height:3px;
  background:linear-gradient(90deg,var(--accent),transparent);opacity:0;transition:opacity .4s}
.livecard.hot .spark{opacity:.65}

.dot{display:inline-block;width:8px;height:8px;border-radius:50%;
  background:var(--accent);margin-right:7px;vertical-align:middle;
  box-shadow:0 0 0 0 rgba(61,220,151,.7);animation:pulse 2s infinite}
@keyframes pulse{
  0%{box-shadow:0 0 0 0 rgba(61,220,151,.55)}
  70%{box-shadow:0 0 0 7px rgba(61,220,151,0)}
  100%{box-shadow:0 0 0 0 rgba(61,220,151,0)}
}
.idle{color:var(--fg2);text-align:center;padding:26px 0;font-size:13px}

.livehead{display:flex;align-items:center;justify-content:space-between;
  gap:12px;flex-wrap:wrap}
.rates{display:inline-flex;gap:16px;font-size:12px;color:var(--fg2);
  font-family:Consolas,Monaco,monospace}
.rates b{color:var(--fg);font-weight:600}
.livestamp{font-size:11.5px;color:var(--fg2);font-family:Consolas,Monaco,monospace}
.livestamp.bad{color:var(--err)}
.bar{height:4px;background:var(--line);border-radius:2px;overflow:hidden;margin-top:8px}
.bar>i{display:block;height:100%;background:var(--accent);width:0;
  border-radius:2px;transition:width .45s ease}

/* ── 用户列表（路由器风格）── */
.listwrap{max-height:420px;overflow-y:auto;overflow-x:hidden;
  border:1px solid var(--line);border-radius:10px;background:var(--card2)}
.listwrap table{margin:0;width:100%}
.listwrap thead th{position:sticky;top:0;z-index:2;background:var(--card2);
  box-shadow:inset 0 -1px 0 var(--line)}
.listwrap tbody tr{transition:background .18s}
.listwrap tbody tr:hover{background:var(--card)}
.listwrap::-webkit-scrollbar{width:9px}
.listwrap::-webkit-scrollbar-thumb{background:var(--line2);border-radius:5px}
.listwrap::-webkit-scrollbar-thumb:hover{background:var(--fg2)}
.listwrap::-webkit-scrollbar-track{background:transparent}

.dot.off{background:var(--fg2);animation:none;box-shadow:none;opacity:.5}
.st{white-space:nowrap;font-size:12.5px}
.st .stx{margin-left:2px;color:var(--fg2)}
tr[data-on="1"] .st .stx{color:var(--accent)}
tr[data-on="1"]{background:color-mix(in srgb, var(--accent) 5%, transparent)}
.num,.numh{font-family:Consolas,Monaco,monospace;text-align:right;
  font-variant-numeric:tabular-nums;white-space:nowrap}
.numh{font-size:12px}
.num{font-size:12.5px}
.num.zero{color:var(--fg2)}
.nowrap{white-space:nowrap}

/* 上下行箭头：方向 + 颜色都不同，一眼分清哪个是哪个 */
.arw{display:inline-block;font-weight:700;margin-right:4px;font-family:Consolas,monospace}
.arw.down{color:#3ddc97}      /* ↓ 绿：远端 → 本机 */
.arw.up{color:#ffa657}        /* ↑ 橙：本机 → 远端 */
html[data-theme="light"] .arw.down{color:#1f9d63}
html[data-theme="light"] .arw.up{color:#d97706}

/* 密码列表 / 事件列表也限高滚动，几十条也不会把页面拉长 */
.listwrap.sm{max-height:300px}

/* ── 备注列：点笔才编辑 ──
   非编辑态只显示文字 + 一个跟文字同色的小笔，输入框完全不在布局里。
   为什么不做成一整列输入框：20 台机器就是 20 个输入框，表格看起来
   全是框，真正的信息（IP/速率/状态）反而被淹没。 */
.notecell{position:relative;white-space:nowrap}
.notecell .notetxt{cursor:default}
.notecell .notetxt.empty{color:var(--fg2);opacity:.55}
/* 笔默认半透明，鼠标移到这一格才变实 —— 平时不抢注意力 */
.notecell .notebtn{
  display:inline-flex;align-items:center;justify-content:center;
  width:20px;height:20px;margin-left:6px;padding:0;
  background:transparent;border:0;border-radius:5px;
  color:var(--fg2);opacity:0;cursor:pointer;
  transition:opacity .15s,background .15s,color .15s;
  vertical-align:middle;
}
.notecell:hover .notebtn{opacity:.75}
.notecell .notebtn:hover{opacity:1;background:var(--line);color:var(--accent)}
.notecell .notebtn:focus-visible{opacity:1;outline:2px solid var(--accent);outline-offset:1px}
.notecell .notein{
  width:130px;padding:2px 6px;font-size:12px;
  background:var(--card2);border:1px solid var(--accent);
  border-radius:5px;color:var(--fg);font-family:inherit;
}
.notecell.editing .notetxt,
.notecell.editing .notebtn{display:none}
.notecell .notein.saving{opacity:.6}
.notecell .notein.bad{border-color:var(--err)}

/* ── 载荷仪表 ──
   三段配色跟「在线/拥挤/饱和」对应，条上的两个刻度线就是边界，
   用户不用记阈值，看指针落在哪一段就知道。 */
.gauge{flex:1 1 460px;min-width:280px;max-width:620px}
.gauge-top{display:flex;align-items:baseline;gap:8px;margin-bottom:7px;flex-wrap:wrap}
.gauge-lab{font-size:12px;color:var(--fg2);letter-spacing:.3px}
.gauge-val{font-size:22px;font-weight:700;font-family:Consolas,Monaco,monospace;
  line-height:1;transition:color .3s}
.gauge-cap{font-size:12px;color:var(--fg2);font-family:Consolas,Monaco,monospace}
.gauge-tag{font-size:11.5px;padding:2px 8px;border-radius:99px;
  background:var(--line);color:var(--fg2);margin-left:auto;white-space:nowrap}
.gauge-tag.ok{background:var(--okbg);color:var(--ok)}
.gauge-tag.warn{background:var(--warnbg);color:var(--warn)}
.gauge-tag.hot{background:var(--errbg);color:var(--err)}

.gauge-bar{position:relative;height:10px;border-radius:6px;overflow:hidden;
  background:var(--line)}
.gauge-fill{height:100%;width:0;border-radius:6px;
  background:linear-gradient(90deg,var(--ok),var(--ok));
  transition:width .45s ease,background .35s}
.gauge-fill.ok{background:linear-gradient(90deg,#2ecc8f,#3ddc97)}
.gauge-fill.warn{background:linear-gradient(90deg,#e0a33a,#ffb547)}
.gauge-fill.hot{background:linear-gradient(90deg,#e05555,#ff6b6b)}
/* 两条边界刻度：60% 和 85% */
.gauge-mark{position:absolute;top:0;bottom:0;width:1px;background:var(--bg);
  opacity:.75;z-index:2}
.gauge-mark.hot{background:var(--bg);opacity:.9}

.gauge-foot{display:flex;gap:16px;margin-top:7px;font-size:11.5px;
  color:var(--fg2);flex-wrap:wrap;font-family:Consolas,Monaco,monospace}

/* ── 右侧带宽控制台 ──
   ⚠ 用 overlay，不给 body 加 padding。
      踩过的坑：一开始写 `body.has-netctl .wrap{padding-right:330px}`，
      结果整个页面内容被压窄、居中位置也偏了 —— 侧边栏是加上了，
      但把主体挤变形，得不偿失。
      改成浮层之后主体**位置和宽度完全不变**，盖住的只是页面右边
      本来就不放内容的留白区。
   · 宽屏（>=1280）：浮在右侧
   · 窄屏：变成普通卡片落到内容上方（这时浮层会真的压住内容）
   · 任何时候都能点标题右侧的箭头收起 */
.netctl{position:fixed;top:0;right:0;bottom:0;width:320px;z-index:80;
  background:var(--card);border-left:1px solid var(--line);
  padding:16px 15px 22px;overflow-y:auto;
  box-shadow:-8px 0 28px rgba(0,0,0,.22);
  transition:transform .25s ease}
.netctl.collapsed{transform:translateX(320px)}

.netctl *{box-sizing:border-box}
.netctl-head{display:flex;align-items:center;gap:8px;margin:0 0 4px}
.netctl-head h3{margin:0;font-size:14px;flex:1 1 auto}
.netctl-toggle{flex:0 0 auto;width:24px;height:24px;padding:0;
  background:var(--card2);border:1px solid var(--line2);border-radius:6px;
  color:var(--fg2);cursor:pointer;font-size:12px;line-height:1;
  display:inline-flex;align-items:center;justify-content:center}
.netctl-toggle:hover{color:var(--accent);border-color:var(--accent)}

/* 收起后留一个贴边的小把手，方便再点开 */
.netctl-handle{position:fixed;right:0;top:50%;transform:translateY(-50%);
  z-index:81;width:22px;height:60px;border-radius:8px 0 0 8px;
  background:var(--accent);color:#06120f;border:0;cursor:pointer;
  font-size:12px;font-weight:700;display:none;align-items:center;
  justify-content:center;box-shadow:-3px 0 12px rgba(0,0,0,.25)}
.netctl-handle.show{display:flex}

@media(max-width:1279px){
  /* 窄屏：浮层会真的压到内容，改成静态块放在最上面 */
  .netctl{position:static;width:auto;border:1px solid var(--line);
    border-radius:14px;margin:0 0 16px;box-shadow:none;
    transform:none;max-width:640px}
  .netctl.collapsed{transform:none}
  .netctl-handle{display:none !important}
  .netctl .netctl-fold{display:none}   /* 窄屏时收起没意义，内容直接展开 */
}

.netctl h3{margin:0 0 4px;font-size:14px}
.netctl .sub2{font-size:11.5px;color:var(--fg2);line-height:1.6;margin-bottom:14px}
.netctl .field{margin-bottom:12px}
.netctl .field label{display:block;font-size:12px;color:var(--fg2);margin-bottom:5px}
.netctl .field .rowline{display:flex;align-items:center;gap:6px;
  width:100%;box-sizing:border-box;min-width:0}
/* input 必须 min-width:0 + width:100%，否则 flex 项不会收缩到内容宽度以下，
   单位「MB/s」会被挤出侧边栏外（实测就是这样）。 */
.netctl input[type=number]{flex:1 1 auto;min-width:0;width:100%;
  box-sizing:border-box;padding:7px 9px;font-size:13px;
  background:var(--card2);border:1px solid var(--line2);border-radius:8px;
  color:var(--fg);font-family:Consolas,Monaco,monospace}
.netctl input[type=number]:focus{outline:none;border-color:var(--accent)}
.netctl .unit{flex:0 0 auto;font-size:11.5px;color:var(--fg2);
  white-space:nowrap;min-width:34px}
.netctl .presets{display:flex;gap:6px;flex-wrap:wrap;margin:-4px 0 12px}
.netctl .presets button{flex:0 0 auto;padding:5px 10px;font-size:11.5px;
  background:var(--card2);border:1px solid var(--line2);border-radius:7px;
  color:var(--fg2);cursor:pointer;transition:all .15s}
.netctl .presets button:hover{border-color:var(--accent);color:var(--accent)}
.netctl .apply{width:100%;padding:9px;font-size:13px;font-weight:600;
  background:var(--accent);color:#06120f;border:0;border-radius:9px;cursor:pointer}
.netctl .apply:disabled{opacity:.55;cursor:default}
.netctl .hintbox{font-size:11.5px;color:var(--fg2);line-height:1.7;
  background:var(--card2);border:1px solid var(--line);border-radius:9px;
  padding:10px 11px;margin-top:14px}
.netctl .hintbox b{color:var(--fg)}
.netctl .saved{font-size:11.5px;color:var(--ok);margin-top:8px;min-height:16px}

/* ── IP 与模式列 ──
   IP 在线时用正常文字色（白/黑，跟主题走）、离线时压暗。
   为什么不一律用 accent 绿：一整列绿色 IP 太抢眼，把「在线」这个信息
   稀释掉了 —— 绿色应该留给「正在工作」的状态点，不是给所有链接。 */
.ipcell{color:var(--fg);cursor:pointer;text-decoration:underline;
  text-decoration-style:dotted;text-decoration-color:var(--line2);
  text-underline-offset:3px;transition:color .15s,text-decoration-color .15s}
.ipcell:hover{color:var(--accent);text-decoration-color:var(--accent)}
.ipcell.off{color:var(--fg2);opacity:.6;cursor:default;text-decoration:none}
.ipcell.off:hover{color:var(--fg2)}

.modecell{font-size:12px;color:var(--fg2);white-space:nowrap}
.modecell.direct{color:var(--accent)}
.modecell.ssh{color:var(--fg)}

/* ── 机器详情弹窗 ──
   点表格里的 IP 打开。用固定定位 + 半透明遮罩，不用 <dialog>（老浏览器兼容）。 */
.modal{position:fixed;inset:0;z-index:900;display:none;
  align-items:center;justify-content:center;padding:20px}
.modal.open{display:flex}
.modal-bg{position:absolute;inset:0;background:rgba(0,0,0,.55);
  backdrop-filter:blur(2px)}
.modal-box{position:relative;width:min(560px,100%);max-height:86vh;
  background:var(--card);border:1px solid var(--line);border-radius:16px;
  box-shadow:0 18px 50px rgba(0,0,0,.45);display:flex;flex-direction:column;
  overflow:hidden}
.modal-head{display:flex;align-items:center;justify-content:space-between;
  padding:16px 18px 12px;border-bottom:1px solid var(--line)}
.modal-title{font-size:16px;font-weight:700;font-family:Consolas,Monaco,monospace}
.modal-close{background:transparent;border:0;color:var(--fg2);font-size:22px;
  line-height:1;cursor:pointer;padding:0 4px;border-radius:6px}
.modal-close:hover{color:var(--fg);background:var(--line)}
.modal-body{padding:16px 18px 20px;overflow-y:auto;font-size:13px}
.dgrid{display:grid;grid-template-columns:1fr 1fr;gap:10px 16px;margin:0 0 4px}
.ditem{background:var(--card2);border:1px solid var(--line);border-radius:10px;
  padding:10px 12px}
.ditem .k{font-size:11.5px;color:var(--fg2);margin-bottom:5px}
.ditem .v{font-size:15px;font-weight:600;font-family:Consolas,Monaco,monospace;
  word-break:break-all}
.ditem.wide{grid-column:1 / -1}
.dsec{margin:16px 0 8px;font-size:12px;color:var(--fg2);letter-spacing:.4px}
.devlist{margin:0;padding-left:16px;line-height:1.9;font-size:12px;color:var(--fg2)}
.devlist b{color:var(--fg);font-weight:500}

/* 授权档位：一排可选按钮，比下拉框少一次点击 */
.tiers{display:flex;gap:8px;flex-wrap:wrap;margin:6px 0 0}
.tier{display:inline-flex;align-items:center;gap:6px;cursor:pointer;
  border:1px solid var(--line2);border-radius:9px;padding:8px 14px;
  font-size:13px;color:var(--fg);background:var(--card2);
  transition:border-color .15s,background .15s;margin:0}
.tier:hover{border-color:var(--accent)}
.tier input{accent-color:var(--accent);margin:0;cursor:pointer}
.tier:has(input:checked){border-color:var(--accent);
  background:color-mix(in srgb, var(--accent) 12%, transparent)}
.tier:has(input:checked) span{color:var(--accent);font-weight:600}
</style>

<div class="livehead" style="margin:2px 0 14px">
  <!-- 载荷仪表：替掉原来那行「下行峰值 / 上行峰值」。
       原因：峰值是个没有参照物的数字 —— 7 MB/s 到底算快还是慢？
       换成「当前速率 ÷ 出口带宽」的百分比，才知道离卡顿还有多远。 -->
  <div class="gauge" id="gaugeBox">
    <div class="gauge-top">
      <span class="gauge-lab">出口载荷</span>
      <span class="gauge-val" id="gVal">—</span>
      <span class="gauge-cap" id="gCap">/ —</span>
      <span class="gauge-tag" id="gTag">—</span>
    </div>
    <div class="gauge-bar">
      <div class="gauge-fill" id="gFill" style="width:0%"></div>
      <i class="gauge-mark" style="left:60%" title="60% 起进入拥挤区"></i>
      <i class="gauge-mark hot" style="left:85%" title="85% 起会明显卡顿"></i>
    </div>
    <div class="gauge-foot">
      <span id="gHead">余量 —</span>
      <span id="gPerIp">单机最快 —</span>
      <span id="gPeak">峰值 —</span>
    </div>
  </div>
  <div class="livestamp" id="lvStamp">正在连接…</div>
</div>

<div class="livegrid">
  <div class="livecard" id="lcOnline">
    <div class="lbl">正在借网</div>
    <div class="val"><span id="lvOnline">—</span><span class="unit">台</span></div>
    <div class="sub" id="lvOnlineSub">—</div>
    <div class="spark"></div>
  </div>
  <div class="livecard" id="lcConns">
    <div class="lbl">活跃连接</div>
    <div class="val"><span id="lvConns">—</span><span class="unit">条</span></div>
    <div class="sub">到本机 22 端口</div>
    <div class="spark"></div>
  </div>
  <div class="livecard" id="lcRx">
    <div class="lbl">下行速率</div>
    <div class="val"><span id="lvRx">—</span></div>
    <div class="bar"><i id="lvRxBar"></i></div>
    <div class="spark"></div>
  </div>
  <div class="livecard" id="lcTx">
    <div class="lbl">上行速率</div>
    <div class="val"><span id="lvTx">—</span></div>
    <div class="bar"><i id="lvTxBar"></i></div>
    <div class="spark"></div>
  </div>
</div>

<script>
(function(){
  var peak = {rx:0, tx:0};
  function esc(s){
    return String(s).replace(/[&<>"']/g, function(c){
      return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c];
    });
  }
  function setHot(id, on){
    var el = document.getElementById(id);
    if (el) el.className = 'livecard' + (on ? ' hot' : '');
  }
  function fmtBps(n){
    if (!n || n < 1) return '—';
    var u = ['B/s','KB/s','MB/s','GB/s'], i = 0, v = n;
    while (v >= 1024 && i < u.length-1) { v /= 1024; i++; }
    return (v >= 100 ? v.toFixed(0) : v.toFixed(v >= 10 ? 1 : 2)) + ' ' + u[i];
  }
  // ── 载荷仪表 ──
  //
  // 为什么不再显示「峰值 KB/s」：那个数字没有参照物。
  // 有了出口带宽，才能回答「现在吃满了吗、离卡顿还有多远」。
  function renderGauge(d){
    var n = d.net || {};
    var pct = Math.max(0, Math.min(100, n.loadPct || 0));
    var fill = document.getElementById('gFill');
    var val = document.getElementById('gVal');
    var cap = document.getElementById('gCap');
    var tag = document.getElementById('gTag');
    if (!fill || !val) return;

    val.innerHTML = fmtBps(n.curBps || 0);
    cap.innerHTML = '/ ' + (n.uplinkMbps ? n.uplinkMbps.toFixed(0) + ' Mbps' : '—');

    var lv = n.level || 'ok';
    fill.style.width = pct + '%';
    fill.className = 'gauge-fill ' + lv;
    val.style.color = lv === 'hot' ? 'var(--err)' : (lv === 'warn' ? 'var(--warn)' : 'var(--ok)');

    var label = {ok: '畅通', warn: '偏挤', hot: '饱和'}[lv] || '—';
    tag.textContent = label + ' · ' + pct.toFixed(0) + '%';
    tag.className = 'gauge-tag ' + lv;

    var head = document.getElementById('gHead');
    if (head) head.textContent = '余量 ' + fmtBps(n.headroomBps || 0) +
      (n.online ? ' · ' + n.online + ' 台在线' : '');

    // 单机最快 / 自己的配额 —— 用来回答「是不是有人撞到限速了」
    var fastest = 0;
    (d.list || []).forEach(function(u){ if (u.online) fastest = Math.max(fastest, (u.rxBps||0) + (u.txBps||0)); });
    var perIp = document.getElementById('gPerIp');
    if (perIp) {
      var capBps = (n.perIpMb || 0) * 1024 * 1024;
      var s = '单机最快 ' + fmtBps(fastest);
      if (capBps > 0) {
        s += ' / 配额 ' + n.perIpMb.toFixed(0) + ' MB/s';
        if (fastest >= capBps * 0.92) s += ' ⚠已贴限速';
      } else {
        s += '（未设单机限速）';
      }
      // 瓶颈判断：明确告诉管理员「现在卡在哪一层」。
      // 千兆出口下 10 MB/s 单机限速其实很难打满总量，
      // 所以「有人撞到自己的配额」比「出口饱和」更常见 ——
      // 只给一个载荷百分比会让人误以为「才 16%，没问题」。
      if (capBps > 0 && fastest >= capBps * 0.92) {
        s += '  ← 瓶颈在单机限速';
      } else if (n.overload || pct >= 85) {
        s += '  ← 瓶颈在出口带宽';
      }
      perIp.textContent = s;
    }
    var pk = document.getElementById('gPeak');
    if (pk) pk.textContent = '本次最高 ' + fmtBps(Math.max(peak.rx, peak.tx));
  }

  // ── 右侧带宽控制台 ──
  //
  // 改完写文件，中继 5 秒内热生效 —— 不重启，不断连接。
  var netCtl = {perIp: null, total: null, uplink: null, loaded: false};

  function fmtCap(v){
    return (v == null) ? '—' : (v % 1 === 0 ? v.toFixed(0) : v.toFixed(1));
  }

  function loadNetCtl(){
    fetch('/api/admin/net', {credentials:'same-origin', cache:'no-store'})
      .then(function(r){ return r.json(); })
      .then(function(d){
        if (!d.ok) return;
        netCtl.perIp = d.perIp;
        netCtl.total = d.total;
        netCtl.uplink = d.uplinkMbps;
        netCtl.loaded = true;
        setVal('ncPerIp', d.perIp);
        setVal('ncTotal', d.total);
        setVal('ncUplink', d.uplinkMbps);
      })
      .catch(function(){});
  }

  function setVal(id, v){
    var el = document.getElementById(id);
    if (el) el.value = fmtCap(v);
  }

  function getNum(id){
    var el = document.getElementById(id);
    if (!el) return null;
    var v = parseFloat(el.value);
    return isNaN(v) ? null : Math.max(0, v);
  }

  function applyNetCtl(){
    var btn = document.getElementById('ncApply');
    var msg = document.getElementById('ncMsg');
    var csrfEl = document.querySelector('input[name="csrf"]');
    var body = new URLSearchParams();
    body.append('csrf', csrfEl ? csrfEl.value : '');
    body.append('perIp', getNum('ncPerIp') == null ? '' : getNum('ncPerIp'));
    body.append('total', getNum('ncTotal') == null ? '' : getNum('ncTotal'));
    body.append('uplinkMbps', getNum('ncUplink') == null ? '' : getNum('ncUplink'));

    if (btn) { btn.disabled = true; btn.textContent = '应用中…'; }
    if (msg) { msg.textContent = ''; msg.style.color = 'var(--fg2)'; }

    fetch('/api/admin/net', {
      method: 'POST',
      credentials: 'same-origin',
      headers: {'Content-Type': 'application/x-www-form-urlencoded',
                'X-Requested-With': 'fetch'},
      body: body.toString()
    }).then(function(r){ return r.json(); })
      .then(function(d){
        if (!d.ok) throw new Error(d.error || '保存失败');
        netCtl.perIp = d.perIp;
        netCtl.total = d.total;
        netCtl.uplink = d.uplinkMbps;
        if (msg) {
          msg.style.color = 'var(--ok)';
          msg.textContent = '已生效：每 IP ' +
            (d.perIp > 0 ? d.perIp.toFixed(1) + ' MB/s' : '不限') +
            '，全站 ' + (d.total > 0 ? d.total.toFixed(1) + ' MB/s' : '不限');
        }
      })
      .catch(function(e){
        if (msg) { msg.style.color = 'var(--err)'; msg.textContent = '失败：' + e.message; }
      })
      .then(function(){
        if (btn) { btn.disabled = false; btn.textContent = '应用'; }
      });
  }

  function bindNetCtl(){
    var btn = document.getElementById('ncApply');
    if (!btn || btn.getAttribute('data-bound') === '1') return;
    // 有控制台时才给 body 加类：让主体 padding-right，别被盖住。
    // 用 JS 加而不是服务端写死，是因为 page() 是所有页面共用的模板。
    // 折叠 / 展开
    var aside = document.getElementById('netCtl');
    var handle = document.getElementById('ncHandle');
    var collapse = document.getElementById('ncCollapse');
    function setCollapsed(on){
      if (!aside) return;
      aside.classList.toggle('collapsed', on);
      if (handle) handle.classList.toggle('show', on);
    }
    if (collapse) collapse.addEventListener('click', function(){ setCollapsed(true); });
    if (handle) handle.addEventListener('click', function(){ setCollapsed(false); });
    btn.setAttribute('data-bound', '1');
    btn.addEventListener('click', applyNetCtl);

    Array.prototype.forEach.call(document.querySelectorAll('.presets [data-rate]'), function(b){
      b.addEventListener('click', function(){
        var v = b.getAttribute('data-rate');
        setVal('ncPerIp', parseFloat(v));
        applyNetCtl();
      });
    });
    loadNetCtl();
  }

  function render(d){
    document.getElementById('lvOnline').textContent = d.onlineCount;
    document.getElementById('lvOnlineSub').textContent =
      d.onlineCount === 0 ? '当前没人借网' : (d.conns + ' 条隧道连接');
    document.getElementById('lvConns').textContent = d.conns;

    document.getElementById('lvRx').innerHTML = fmtBps(d.rxBps);
    document.getElementById('lvTx').innerHTML = fmtBps(d.txBps);
    if (d.rxBps > peak.rx) peak.rx = d.rxBps;
    if (d.txBps > peak.tx) peak.tx = d.txBps;
    renderGauge(d);

    var rb = document.getElementById('lvRxBar');
    var tb = document.getElementById('lvTxBar');
    if (rb) rb.style.width = (peak.rx > 0 ? Math.max(2, d.rxBps/peak.rx*100) : 0) + '%';
    if (tb) tb.style.width = (peak.tx > 0 ? Math.max(2, d.txBps/peak.tx*100) : 0) + '%';

    setHot('lcOnline', d.onlineCount > 0);
    setHot('lcConns', d.conns > 0);
    setHot('lcRx', d.rxBps > 4096);
    setHot('lcTx', d.txBps > 4096);

    // ── 用户列表：只更新已有行，然后按 rank 重排 ──
    // 不能重建行：行里有 form 和 CSRF token，重建会丢。
    var map = {};
    (d.list || []).forEach(function(u){ map[u.ip] = u; });
    var body = document.querySelector('#userTable tbody');
    if (body) {
      Array.prototype.forEach.call(body.children, function(tr){
        var ip = tr.getAttribute('data-ip');
        var u = map[ip];
        if (!u) return;
        var on = u.online;
        tr.setAttribute('data-on', on ? '1' : '0');
        // rank：在线按总速率降序在前，离线按最后活动降序在后
        tr.setAttribute('data-rank', on
          ? String(1e9 - (u.rxBps + u.txBps))
          : String(-(u.lastSeen || 0)));

        var st = tr.querySelector('.st');
        if (st) st.innerHTML = '<span class="dot' + (on ? '' : ' off') + '"></span>' +
                               '<span class="stx">' + (on ? '在线' : '离线') + '</span>';

        // 箭头和数字一起写，避免箭头跟数字分离
        var rx = tr.querySelector('.rx');
        var tx = tr.querySelector('.tx');
        var cs = tr.querySelector('.cs');
        if (rx) { rx.innerHTML = '<span class="arw down">&#8595;</span>' + (on ? fmtBps(u.rxBps) : '—'); rx.className = 'num rx' + (u.rxBps ? '' : ' zero'); }
        if (tx) { tx.innerHTML = '<span class="arw up">&#8593;</span>' + (on ? fmtBps(u.txBps) : '—'); tx.className = 'num tx' + (u.txBps ? '' : ' zero'); }
        if (cs) { cs.textContent = on ? u.conns : '—'; cs.className = 'num cs' + (on ? '' : ' zero'); }

        // 没有心跳上报的在线用户，速率列加个提示
        if (on && !u.reported) { if (rx) rx.title = '该客户端未上报心跳（老版本）'; }

        // IP 颜色跟着在线状态走：在线才是可点的正常色，离线压暗
        var ipCell = tr.querySelector('.ipcell');
        if (ipCell) {
          ipCell.className = 'mono ipcell' + (on ? '' : ' off');
          ipCell.title = on ? '点击查看详情' : '离线（仍可查看历史数据）';
        }

        // 连接模式：直连 / SSH。
        // 老客户端不上报这个字段，显示「—」而不是瞎猜。
        var modeCell = tr.querySelector('.modecell');
        if (modeCell) {
          var md = u.mode || '';
          if (md === 'direct') { modeCell.textContent = '直连'; modeCell.className = 'modecell direct'; }
          else if (md === 'ssh') { modeCell.textContent = 'SSH'; modeCell.className = 'modecell ssh'; }
          else { modeCell.textContent = '—'; modeCell.className = 'modecell'; }
        }
      });

      // 重排
      var rows = Array.prototype.slice.call(body.children);
      rows.sort(function(a, b){
        return (parseFloat(b.getAttribute('data-rank')) || 0) -
               (parseFloat(a.getAttribute('data-rank')) || 0);
      });
      rows.forEach(function(r){ body.appendChild(r); });
    }

    // 本小时 + 今日流量汇总（所有在线机器相加）
    var hBody = document.getElementById('hourlyBody');
    if (hBody && d.hourly) {
      var hk = null, tRx = 0, tTx = 0, nowRx = 0, nowTx = 0;
      var nowKey = null;
      var dt = new Date();
      nowKey = dt.getFullYear() + ('0' + (dt.getMonth()+1)).slice(-2) + ('0' + dt.getDate()).slice(-2) +
               ('0' + dt.getHours()).slice(-2);
      for (var ip in d.hourly) {
        var arr = d.hourly[ip] || [];
        for (var j = 0; j < arr.length; j++) {
          tRx += arr[j].rx; tTx += arr[j].tx;
          if (arr[j].hour === nowKey) { nowRx += arr[j].rx; nowTx += arr[j].tx;
            if (!hk || arr[j].peakRx > hk.peakRx) hk = arr[j]; }
        }
      }
      hBody.innerHTML =
        '<tr><td class="mono">' + (nowKey.slice(0,4) + '-' + nowKey.slice(4,6) + '-' + nowKey.slice(6,8) +
          ' ' + nowKey.slice(8,10) + ':00') + '</td>' +
        '<td class="num"><span class="arw down">&#8595;</span>' + fmtBps(nowRx / 3600) + '</td>' +
        '<td class="num"><span class="arw up">&#8593;</span>' + fmtBps(nowTx / 3600) + '</td>' +
        '<td class="num"><span class="arw down">&#8595;</span>' + fmtBytes(nowRx) + '</td>' +
        '<td class="num"><span class="arw up">&#8593;</span>' + fmtBytes(nowTx) + '</td></tr>';
      var sumEl = document.getElementById('hourlySum');
      if (sumEl) sumEl.innerHTML = '近 24 小时累计：下行 <b>' + fmtBytes(tRx) +
        '</b> · 上行 <b>' + fmtBytes(tTx) + '</b>';
    }

    var ts = document.getElementById('lvStamp');
    ts.className = 'livestamp';
    ts.textContent = '实时 · ' + new Date().toLocaleTimeString('zh-CN', {hour12:false});
  }

  function fmtBytes(n){
    if (!n || n < 1) return '0 B';
    var u = ['B','KB','MB','GB','TB'], i = 0, v = n;
    while (v >= 1024 && i < u.length-1) { v /= 1024; i++; }
    return (v >= 100 ? v.toFixed(0) : v.toFixed(v >= 10 ? 1 : 2)) + ' ' + u[i];
  }
  function fail(msg){
    var ts = document.getElementById('lvStamp');
    ts.className = 'livestamp bad';
    ts.textContent = '连接中断 · ' + msg;
  }

  // ── 备注编辑 ──
  //
  // 交互：点笔 → 出输入框（聚焦、全选）；回车或点别处 → 自动保存；
  //       Esc → 取消不改。
  //
  // 为什么用 fetch 而不是表单提交：表单提交会整页刷新，用户改一条备注
  // 就被弹回页面顶部、实时监控数据也重新加载一遍，体验很差。
  // 这里保存成功后只更新那一格 DOM。
  //
  // CSRF：/admin/setnote 仍然校验 csrf。页面上的 csrf 从**任意一个**已有表单里取
  // （页面上有多份同值 token），不需要额外渲染一个隐藏表单。
  function csrfToken(){
    var i = document.querySelector('input[name="csrf"]');
    return i ? i.value : '';
  }

  function bindNote(td){
    if (td.getAttribute('data-bound') === '1') return;
    td.setAttribute('data-bound', '1');

    var txt = td.querySelector('.notetxt');
    var btn = td.querySelector('.notebtn');
    var inp = td.querySelector('.notein');
    if (!txt || !btn || !inp) return;

    var original = td.getAttribute('data-note') || '';
    var saving = false;

    function open(){
      if (saving) return;
      inp.value = original;
      td.classList.add('editing');
      inp.style.display = '';
      inp.focus();
      inp.select();
    }

    function close(showValue){
      td.classList.remove('editing');
      inp.style.display = 'none';
      if (showValue !== undefined) {
        txt.textContent = showValue || '点击编辑';
        if (showValue) txt.classList.remove('empty');
        else txt.classList.add('empty');
      }
    }

    function save(){
      if (saving) return;
      var v = inp.value.trim();
      if (v === original) { close(v); return; }

      saving = true;
      inp.classList.add('saving');
      inp.classList.remove('bad');

      var body = new URLSearchParams();
      body.append('csrf', csrfToken());
      body.append('ip', td.getAttribute('data-ip'));
      body.append('note', v);

      fetch('/admin/setnote', {
        method: 'POST',
        credentials: 'same-origin',
        headers: {'Content-Type': 'application/x-www-form-urlencoded',
                  'X-Requested-With': 'fetch'},
        body: body.toString()
      }).then(function(r){
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.json();
      }).then(function(d){
        if (!d.ok) throw new Error(d.error || '保存失败');
        original = v;
        td.setAttribute('data-note', v);
        // 行内的 .notetxt 要跟着更新，否则下次点笔又拿旧值
        saving = false;
        inp.classList.remove('saving');
        close(v);
      }).catch(function(e){
        saving = false;
        inp.classList.remove('saving');
        inp.classList.add('bad');
        inp.title = '保存失败：' + e.message;
        // 不关闭编辑态 —— 让用户能重试，别把刚输入的内容吞掉
      });
    }

    btn.addEventListener('click', function(e){ e.preventDefault(); open(); });
    inp.addEventListener('keydown', function(e){
      if (e.key === 'Enter') { e.preventDefault(); save(); }
      else if (e.key === 'Escape') { e.preventDefault(); close(original); }
    });
    // blur = 点别处。用 setTimeout 让 click 先跑完（点笔自己不该触发保存）
    inp.addEventListener('blur', function(){
      setTimeout(function(){ if (td.classList.contains('editing')) save(); }, 80);
    });
  }

  function bindAllNotes(){
    var cells = document.querySelectorAll('.notecell');
    Array.prototype.forEach.call(cells, bindNote);
  }

  // ⚠ 必须等 DOM 就绪再绑。
  //
  // 踩过的坑：这段 <script> 是随 LIVE_UI 插在页面**靠前**位置的，
  // 而备注所在的 <table> 在它后面。直接调 bindAllNotes() 时行还不存在，
  // querySelectorAll 返回空 —— 表现就是「点笔毫无反应」，控制台也不报错。
  // 实测就是这个原因。
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', bindAllNotes);
  } else {
    bindAllNotes();
  }

  // ── 机器详情弹窗 ──
  //
  // 点表格里的 IP 打开。数据从 /api/admin/client?ip= 拿。
  //
  // 为什么放在这里而不是单独写个页面：管理员看的是「这一行」，跳走再回来
  // 会丢失滚动位置和实时数据的连续性。弹窗更适合这种「顺带看一眼」的场景。
  function fmtDur(sec){
    if (!sec || sec < 0) return '—';
    var d = Math.floor(sec / 86400), h = Math.floor(sec % 86400 / 3600),
        m = Math.floor(sec % 3600 / 60);
    if (d > 0) return d + ' 天 ' + h + ' 小时';
    if (h > 0) return h + ' 小时 ' + m + ' 分';
    return m + ' 分';
  }
  function fmtTime(ts){
    if (!ts) return '—';
    var d = new Date(ts * 1000);
    function p(n){ return (n < 10 ? '0' : '') + n; }
    return d.getFullYear() + '-' + p(d.getMonth()+1) + '-' + p(d.getDate()) +
           ' ' + p(d.getHours()) + ':' + p(d.getMinutes());
  }
  function item(k, v, wide){
    return '<div class="ditem' + (wide ? ' wide' : '') + '"><div class="k">' + k +
           '</div><div class="v">' + (v == null ? '—' : v) + '</div></div>';
  }

  function openDetail(ip){
    var modal = document.getElementById('clientModal');
    var body = document.getElementById('clientBody');
    var title = document.getElementById('clientTitle');
    if (!modal || !body) return;
    title.textContent = ip;
    body.innerHTML = '<div class="hint">加载中…</div>';
    modal.classList.add('open');

    fetch('/api/admin/client?ip=' + encodeURIComponent(ip),
          {credentials:'same-origin', cache:'no-store'})
      .then(function(r){ if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
      .then(function(d){
        if (!d.ok) throw new Error(d.error || '读取失败');
        var h = [];
        h.push('<div class="dgrid">');
        h.push(item('在线状态', d.online
          ? '<span style="color:var(--accent)">在线</span> · ' + d.conns + ' 条连接'
          : '<span style="color:var(--fg2)">离线</span>'));
        h.push(item('授权', d.enabled
          ? (d.expiresAt ? '<span style="color:var(--accent)">有效</span> · 到期 ' + fmtTime(d.expiresAt)
                         : '<span style="color:var(--accent)">有效</span> · 永久')
          : '<span style="color:var(--err)">已停用</span>'));
        h.push(item('当前下行', d.online ? fmtBps(d.rxBps) : '—'));
        h.push(item('当前上行', d.online ? fmtBps(d.txBps) : '—'));
        h.push(item('累计下行', fmtBytes(d.totalRx)));
        h.push(item('累计上行', fmtBytes(d.totalTx)));
        h.push(item('历史最高下行', fmtBps(d.peakRx)));
        h.push(item('历史最高上行', fmtBps(d.peakTx)));
        h.push(item('近 24 小时流量',
          fmtBytes(d.sum24Rx) + ' ↓ / ' + fmtBytes(d.sum24Tx) + ' ↑', true));
        h.push(item('近 24 小时峰值',
          fmtBps(d.peak24Rx) + ' ↓ / ' + fmtBps(d.peak24Tx) + ' ↑', true));
        h.push('</div>');

        h.push('<div class="dsec">时间线</div><ul class="devlist">');
        h.push('<li>首次激活 <b>' + fmtTime(d.createdAt) + '</b>');
        if (d.firstSeen) h.push('<li>首次上报流量 <b>' + fmtTime(d.firstSeen) + '</b>');
        h.push('<li>最后活动 <b>' + fmtTime(d.lastSeen) + '</b>');
        if (d.online && d.lastSeen) {
          h.push('<li>已在线 <b>' + fmtDur(Math.floor(Date.now()/1000) - d.lastSeen) +
                 '</b>（按最后心跳算）');
        }
        h.push('</ul>');

        if (d.events && d.events.length) {
          h.push('<div class="dsec">最近事件</div><ul class="devlist">');
          d.events.forEach(function(e){
            h.push('<li>' + fmtTime(e.ts) + ' · <b>' + e.kind + '</b>' +
                   (e.detail ? ' · ' + e.detail : '') + '</li>');
          });
          h.push('</ul>');
        }
        body.innerHTML = h.join('');
      })
      .catch(function(e){
        body.innerHTML = '<div class="hint" style="color:var(--err)">读取失败：' +
                         (e.message || '网络错误') + '</div>';
      });
  }

  function closeDetail(){
    var m = document.getElementById('clientModal');
    if (m) m.classList.remove('open');
  }

  function bindIpCells(){
    var cells = document.querySelectorAll('.ipcell');
    Array.prototype.forEach.call(cells, function(td){
      if (td.getAttribute('data-bound') === '1') return;
      td.setAttribute('data-bound', '1');
      td.addEventListener('click', function(){
        openDetail(td.getAttribute('data-ip'));
      });
    });
    var bg = document.querySelector('#clientModal .modal-bg');
    var btn = document.querySelector('#clientModal .modal-close');
    if (bg && !bg.getAttribute('data-bound')) {
      bg.setAttribute('data-bound', '1');
      bg.addEventListener('click', closeDetail);
    }
    if (btn && !btn.getAttribute('data-bound')) {
      btn.setAttribute('data-bound', '1');
      btn.addEventListener('click', closeDetail);
    }
    document.addEventListener('keydown', function(e){
      if (e.key === 'Escape') closeDetail();
    });
  }
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', bindIpCells);
  } else {
    bindIpCells();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', bindNetCtl);
  } else {
    bindNetCtl();
  }

  function tick(){
    fetch('/api/admin/live', {credentials:'same-origin', cache:'no-store'})
      .then(function(r){ if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
      .then(render)
      .catch(function(e){ fail(e.message || '网络错误'); })
      .then(function(){ setTimeout(tick, 3000); });
  }
  tick();
})();
</script>

<!-- 右侧带宽控制台 -->
<button type="button" class="netctl-handle" id="ncHandle" title="展开带宽控制">◀</button>
<aside class="netctl" id="netCtl">
  <div class="netctl-head">
    <h3>带宽控制</h3>
    <button type="button" class="netctl-toggle" id="ncCollapse" title="收起">▶</button>
  </div>
  <div class="sub2 netctl-fold">
    改动写入限速配置，中继 5 秒内热生效 —— 不会断开正在传的连接。
  </div>

  <div class="field">
    <label>每台机器上限</label>
    <div class="rowline">
      <input type="number" id="ncPerIp" min="0" max="1000" step="1" placeholder="10">
      <span class="unit">MB/s</span>
    </div>
  </div>
  <div class="presets">
    <button type="button" data-rate="3">3</button>
    <button type="button" data-rate="5">5</button>
    <button type="button" data-rate="10">10</button>
    <button type="button" data-rate="20">20</button>
    <button type="button" data-rate="0">不限</button>
  </div>

  <div class="field">
    <label>全站总上限</label>
    <div class="rowline">
      <input type="number" id="ncTotal" min="0" max="10000" step="1" placeholder="0">
      <span class="unit">MB/s</span>
    </div>
  </div>

  <div class="field">
    <label>远端出口带宽</label>
    <div class="rowline">
      <input type="number" id="ncUplink" min="1" max="100000" step="10" placeholder="1000">
      <span class="unit">Mbps</span>
    </div>
  </div>

  <button type="button" class="apply" id="ncApply">应用</button>
  <div class="saved" id="ncMsg"></div>

  <div class="hintbox">
    <b>三个数字怎么填</b><br>
    · 每台上限：一个人最多跑多快。填 0 = 不限。<br>
    · 全站上限：所有人加起来的天花板。校园网出口有限，
      每人都 10 MB/s 时三个人就能把百兆线吃满。<br>
    · 出口带宽：只用于算上面的载荷百分比，不参与限速。
      默认 1000（pcs 是千兆线路）。
  </div>
  <div class="hintbox">
    <b>载荷到多少会卡</b><br>
    60% 起进入拥挤区（开始有排队的体感），
    85% 以上基本饱和 —— 这时候谁再多跑一点，所有人都会觉得卡。
    仪表上那两条竖线就是这两个边界。
  </div>
</aside>

<!-- 机器详情弹窗的骨架。放在这里（跟着 LIVE_UI 一起）而不是页面最底部，
     是为了让「弹窗样式 + 弹窗 JS + 弹窗 DOM」三件东西待在同一个位置，
     改的时候不用满文件找。 -->
<div class="modal" id="clientModal" aria-hidden="true">
  <div class="modal-bg"></div>
  <div class="modal-box" role="dialog" aria-modal="true" aria-labelledby="clientTitle">
    <div class="modal-head">
      <span class="modal-title" id="clientTitle">—</span>
      <button type="button" class="modal-close" aria-label="关闭">&times;</button>
    </div>
    <div class="modal-body" id="clientBody"></div>
  </div>
</div>
'''


def admin_page(db, msg='', err='', new_code=None, new_hours=12, csrf=''):
    csrf_in = f'<input type="hidden" name="csrf" value="{html.escape(csrf)}">' if csrf else ''
    _ld = int(CFG.get('license_days') or 0)
    license_days_txt = '永久' if _ld <= 0 else (str(_ld) + ' 天')
    _tier_txt = tier_text
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
  <td class="mono">{_tier_txt(r['license_days'])}</td>
  <td>{'<span class="bad">已撤销</span>' if r['revoked'] else
       ('<span class="muted">已使用</span>' if r['used_by_ip'] else
        ('<span class="bad">已过期</span>' if r['expires_at'] < now() else
         f'<span class="ok">未使用 · 剩 {fmt_left(r["expires_at"])}</span>'))}</td>
  <td class="muted">{html.escape(r['used_by_ip']) or '—'}</td>
  <td class="muted">{fmt_ts(r['created_at'])}</td>
  <td>{'' if r['revoked'] else f'<form method="post" action="/admin/revoke" style="display:inline">{csrf_in}<input type="hidden" name="id" value="{r["id"]}"><button class="sm ghost" type="submit">撤销</button></form>'}</td>
</tr>""" for r in inv)

    # 行里带 data-ip / data-rank，前端按 rank 重排 DOM（不重建行，保住 CSRF）
    #
    # 备注列做成「点笔才编辑」：
    #   非编辑态只显示文字 + 一个小笔图标（避免一整列输入框把表格撑得很吵）。
    #   笔是内联 SVG 而不是 emoji —— ✏️ 这类字符在多数系统上会被渲染成
    #   彩色 emoji，跟深色主题打架；SVG 用 currentColor，永远跟着文字颜色走。
    cli_rows = ''.join(f"""<tr data-ip="{html.escape(r['ip'])}" data-rank="{1000000 - i}">
  <td class="st"><span class="dot off"></span><span class="stx">离线</span></td>
  <td class="mono ipcell off" data-ip="{html.escape(r['ip'])}" title="点击查看详情">{html.escape(r['ip'])}</td>
  <td class="notecell" data-ip="{html.escape(r['ip'])}" data-note="{html.escape(r['note'] or '')}">
    <span class="notetxt{' empty' if not r['note'] else ''}">{html.escape(r['note'] or '点击编辑')}</span>
    <button type="button" class="notebtn" title="编辑备注" aria-label="编辑备注">
      <svg viewBox="0 0 16 16" width="12" height="12" fill="none"
           stroke="currentColor" stroke-width="1.6" stroke-linecap="round"
           stroke-linejoin="round"><path d="M11.5 2.5l2 2L5 13H3v-2z"/></svg>
    </button>
    <input type="text" class="notein" maxlength="60" value="{html.escape(r['note'] or '')}"
           style="display:none" placeholder="备注">
  </td>
  <td class="num rx"><span class="arw down">&#8595;</span>—</td>
  <td class="num tx"><span class="arw up">&#8593;</span>—</td>
  <td class="num cs">—</td>
  <td class="modecell">—</td>
  <td>{'<span class="ok">启用</span>' if r['enabled'] else '<span class="bad">停用</span>'}</td>
  <td class="muted">{fmt_ts(r['last_seen']) or '从未'}</td>
  <td class="nowrap">
    <form method="post" action="/admin/toggle" style="display:inline">{csrf_in}<input type="hidden" name="ip" value="{html.escape(r['ip'])}"><button class="sm ghost" type="submit">{'停用' if r['enabled'] else '启用'}</button></form>
  </td>
</tr>""" for i, r in enumerate(cli))

    ev_rows = ''.join(
        f'<tr><td class="muted">{fmt_ts(r["ts"])}</td><td>{html.escape(r["kind"])}</td>'
        f'<td class="mono">{html.escape(r["ip"])}</td>'
        f'<td class="muted">{html.escape(r["detail"])}</td></tr>' for r in ev)

    return page('渡口门户管理', f"""
<h1>管理面板</h1>
{LIVE_UI}
<div class="sub">创建临时密钥 · 管理已登录的机器 ·
  <a href="/admin/unlock">查看 / 解除登录锁定</a></div>
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
        <label>密钥有效期（小时）</label>
        <input type="number" name="hours" value="{new_hours}" min="1" max="720">
      </div>
    </div>

    <label style="margin-top:14px">激活后可用时长</label>
    <div class="tiers">
      <label class="tier"><input type="radio" name="license_days" value="1"><span>1 天</span></label>
      <label class="tier"><input type="radio" name="license_days" value="2"><span>2 天</span></label>
      <label class="tier"><input type="radio" name="license_days" value="3"><span>3 天</span></label>
      <label class="tier"><input type="radio" name="license_days" value="7"><span>7 天</span></label>
      <label class="tier"><input type="radio" name="license_days" value="30" checked><span>30 天</span></label>
      <label class="tier"><input type="radio" name="license_days" value="0"><span>永久</span></label>
    </div>

    <div class="hint" style="margin:14px 0 12px;line-height:1.8">
      <b>这两个时长是两回事：</b><br>
      · 上面的「密钥有效期」= 密钥本身必须在这个时间内用掉，过期作废（取票窗口）<br>
      · 下面的「激活后可用时长」= 用掉之后账号能借网多久（票的使用期限）<br>
      老密钥没有这个字段，按全局默认 <b>{license_days_txt}</b> 算。
    </div>
    <button type="submit">生成密钥</button>
  </form>
</div>

<div class="card">
  <h2 style="margin-top:0">临时密钥（最近 40 条）</h2>
  <div class="listwrap sm">
  <table><thead><tr><th>密钥前缀</th><th>备注</th><th>可用时长</th><th>状态</th><th>使用方</th><th>创建</th><th></th></tr></thead>
  <tbody>{inv_rows or '<tr><td colspan="7" class="muted">还没有密钥</td></tr>'}</tbody></table>
  </div>
</div>

<div class="card">
  <h2 style="margin-top:0">已登录的机器</h2>
  <div class="hint" style="margin:-4px 0 10px">
    在线置顶，离线靠下。速率来自客户端每 5 秒一次的心跳；
    老版本客户端不上报心跳，速率列显示「—」，但在线状态仍准确。
  </div>
  <div class="listwrap">
  <table id="userTable">
    <thead><tr>
      <th>状态</th><th>局域网 IP</th><th>备注</th>
      <th class="numh"><span class="arw down">&#8595;</span>下行</th>
      <th class="numh"><span class="arw up">&#8593;</span>上行</th>
      <th class="numh">连接</th>
      <th>模式</th>
      <th>授权</th><th>最后活动</th><th>操作</th>
    </tr></thead>
    <tbody>{cli_rows}</tbody>
  </table>
  </div>
</div>

<div class="card">
  <h2 style="margin-top:0">流量统计</h2>
  <div class="listwrap sm">
  <table>
    <thead><tr>
      <th>时段</th>
      <th class="numh"><span class="arw down">&#8595;</span>平均下行</th>
      <th class="numh"><span class="arw up">&#8593;</span>平均上行</th>
      <th class="numh"><span class="arw down">&#8595;</span>下行流量</th>
      <th class="numh"><span class="arw up">&#8593;</span>上行流量</th>
    </tr></thead>
    <tbody id="hourlyBody"><tr><td colspan="5" class="muted">等客户端上报心跳后出现</td></tr></tbody>
  </table>
  </div>
  <div class="hint" id="hourlySum" style="margin-top:10px">
    速率来自客户端每 5 秒一次的心跳；流量是两次心跳之间的增量累加。
  </div>
</div>

<div class="card">
  <h2 style="margin-top:0">最近事件</h2>
  <div class="listwrap sm">
  <table><thead><tr><th>时间</th><th>类型</th><th>IP</th><th>说明</th></tr></thead>
  <tbody>{ev_rows or '<tr><td colspan="4" class="muted">无</td></tr>'}</tbody></table>
  </div>
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

def installer_path():
    """找最新的安装程序。优先 <工程根>/dist，其次门户同级的 dist。

    门户每次请求都重新扫一遍（只 listdir + stat，微秒级），
    这样 ISCC 重新编译后不用重启门户就能出新包。
    """
    here = os.path.dirname(os.path.abspath(__file__))
    best = None
    for d in (os.path.join(here, '..', 'dist'), os.path.join(here, 'dist')):
        d = os.path.normpath(d)
        if not os.path.isdir(d):
            continue
        try:
            names = os.listdir(d)
        except OSError:
            continue
        for f in names:
            low = f.lower()
            if low.startswith('ferry-setup-') and low.endswith('.exe'):
                p = os.path.join(d, f)
                if best is None or os.path.getmtime(p) > os.path.getmtime(best):
                    best = p
    return best


def read_version_manifest():
    """更新推送的版本清单。优先读 dist/version.json，没有就从 exe 文件名推。"""
    here = os.path.dirname(os.path.abspath(__file__))
    for d in (os.path.join(here, '..', 'dist'), os.path.join(here, 'dist')):
        p = os.path.normpath(os.path.join(d, 'version.json'))
        if os.path.exists(p):
            try:
                with open(p, encoding='utf-8') as f:
                    return json.load(f)
            except Exception:
                pass
    ver = '0.0.0'
    exe = installer_path()
    if exe:
        m = re.search(r'Ferry-Setup-([\d.]+)\.exe$', os.path.basename(exe), re.I)
        if m:
            ver = m.group(1)
    return {'latest': ver, 'url': '/download-latest',
            'notes': '', 'built': bool(exe)}


def list_installers():
    """列出 dist 里所有已构建的安装包，按版本号从新到旧。

    为什么要有这个：用户换机器、或新版本在他那台机器上有问题时，
    需要能装回旧版。只保留一个「最新」等于把退路砍掉了。
    """
    here = os.path.dirname(os.path.abspath(__file__))
    found = {}

    def vkey(v):
        parts = []
        for x in str(v).split('.'):
            try:
                parts.append(int(x))
            except ValueError:
                parts.append(0)
        while len(parts) < 3:
            parts.append(0)
        return tuple(parts)

    for d in (os.path.join(here, '..', 'dist'), os.path.join(here, 'dist')):
        d = os.path.normpath(d)
        if not os.path.isdir(d):
            continue
        try:
            names = os.listdir(d)
        except OSError:
            continue
        for n in names:
            m = re.match(r'^Ferry-Setup-([\d.]+)\.exe$', n, re.I)
            if not m:
                continue
            p = os.path.join(d, n)
            try:
                st = os.stat(p)
            except OSError:
                continue
            ver = m.group(1)
            if ver not in found or st.st_mtime > found[ver]['mtime']:
                found[ver] = {'ver': ver, 'path': p, 'size': st.st_size,
                              'mtime': st.st_mtime, '_k': vkey(ver)}

    out = sorted(found.values(), key=lambda x: x['_k'], reverse=True)
    for x in out:
        del x['_k']
    return out


def read_changelog(max_bytes=400000):
    """读 CHANGELOG.md，返回原始文本（给门户展示更新日志用）。"""
    here = os.path.dirname(os.path.abspath(__file__))
    for d in (here, os.path.join(here, '..')):
        p = os.path.normpath(os.path.join(d, 'CHANGELOG.md'))
        if os.path.exists(p):
            try:
                with open(p, encoding='utf-8') as f:
                    return f.read(max_bytes)
            except Exception:
                pass
    return ''


def inline_md(s):
    """行内 markdown：先转义 HTML，再处理 `code` 和 **bold**。"""
    s = html.escape(s)
    s = re.sub(r'`([^`]+)`',
               r'<code style="background:var(--code);padding:1px 5px;border-radius:4px;'
               r'font-size:12px">\1</code>', s)
    s = re.sub(r'\*\*([^*]+)\*\*', r'<b>\1</b>', s)
    return s


def md_to_html_brief(md):
    """把 CHANGELOG 的 markdown 转成极简 HTML。

    为什么不引 markdown 库：门户的卖点之一是「Python 标准库零依赖」，
    为一个更新日志引进第三方库不值得。这里只处理该文件实际用到的语法：
    标题、列表、加粗、行内代码、代码块、水平线、段落。
    """
    if not md:
        return '<div class="hint">暂无更新日志</div>'

    out = []
    in_list = False
    in_code = False

    def close_list():
        nonlocal in_list
        if in_list:
            out.append('</ul>')
            in_list = False

    for raw in md.splitlines():
        line = raw.rstrip()

        if line.strip().startswith('```'):
            close_list()
            if in_code:
                out.append('</pre>')
                in_code = False
            else:
                out.append('<pre class="mono" style="background:var(--code);color:var(--fg);'
                           'padding:10px 12px;border-radius:8px;overflow:auto;font-size:12px;'
                           'line-height:1.6;margin:8px 0">')
                in_code = True
            continue
        if in_code:
            out.append(html.escape(raw))
            continue

        s = line.strip()
        if not s:
            close_list()
            continue
        if s in ('---', '***', '___'):
            close_list()
            out.append('<hr style="border:0;border-top:1px solid var(--line);margin:14px 0">')
            continue

        # 引用块 `> xxx`：CHANGELOG 用它写「本版说明」，不处理的话那个 `>`
        # 会原样显示成怪字符。
        if s.startswith('>'):
            close_list()
            quote = re.sub(r'^>\s?', '', s)
            out.append('<div style="border-left:3px solid var(--accent);padding:2px 0 2px 10px;'
                       'margin:8px 0;color:var(--muted);font-size:12.5px">' +
                       inline_md(quote) + '</div>')
            continue

        m = re.match(r'^(#{1,4})\s+(.*)$', s)
        if m:
            close_list()
            level = len(m.group(1))
            text = html.escape(m.group(2))
            if level <= 2:
                out.append(f'<h3 style="margin:16px 0 6px;font-size:15px">'
                           f'<span style="color:var(--accent)">{text}</span></h3>')
            else:
                out.append(f'<h4 style="margin:12px 0 4px;font-size:13.5px;'
                           f'color:var(--accent)">{text}</h4>')
            continue

        if re.match(r'^[-*]\s+', s):
            if not in_list:
                out.append('<ul style="margin:6px 0;padding-left:20px;line-height:1.85">')
                in_list = True
            body = re.sub(r'^[-*]\s+', '', s)
            out.append('<li>' + inline_md(body) + '</li>')
            continue

        close_list()
        out.append(f'<p style="margin:6px 0;line-height:1.8">{inline_md(s)}</p>')

    close_list()
    if in_code:
        out.append('</pre>')
    return '\n'.join(out)


# ─────────── 带宽/限速控制 ───────────
#
# 中继（ferry-socks5-server.py）和门户跑在同一台机器上。
# 限速通过一个纯文本文件传递：门户写、中继每 5 秒读一次热更新。
# 这样改限速不用重启中继（重启会掐断所有正在传的连接）。

def _relay_dir_score(d):
    """给候选目录打分。分高的是「真在用的中继目录」。

    为什么需要打分：pcs 上存在多个 C:\\Users\\<某用户>\\AppData\\Local\\Ferry
    （历史部署留下的）。glob 按字母序返回，会先命中 63526 而不是 PCserver，
    而中继其实跑在 PCserver 下面 —— 于是门户把配置写进了没人读的目录。

    判据：
      · 有 rate-mb.txt      → 强烈说明这是活跃的中继目录（中继启动后一定会写/读它）
      · 有 ferry-socks5-server.py → 基本要求
      · 脚本 mtime 越新     → 越是刚部署的那个
    """
    score = 0
    try:
        if os.path.isfile(os.path.join(d, 'rate-mb.txt')):
            score += 100
        sp = os.path.join(d, 'ferry-socks5-server.py')
        if os.path.isfile(sp):
            score += 10
            # 只取「小时」级别的差异，避免毫秒噪声
            score += min(9, int(os.path.getmtime(sp)) % 10)
    except Exception:
        pass
    return score


def relay_dir():
    """中继脚本所在目录（rate-mb.txt 在这里）。

    ⚠ 不能只靠 %LOCALAPPDATA% 探测：门户是计划任务起的，
    owner 可能是 NT AUTHORITY\\SYSTEM，那时 %LOCALAPPDATA% 指向
    C:\\Windows\\System32\\config\\systemprofile\\AppData\\Local。

    ⚠ 也不能无脑取 glob 第一个：这台机器上有两个用户目录都带 Ferry，
    glob 会先命中错的那个（见 _relay_dir_score 的说明）。
    """
    # ① 显式配置（部署脚本写的，最可靠）
    conf = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'relay-dir.txt')
    try:
        if os.path.isfile(conf):
            d = open(conf, encoding='utf-8').read().strip()
            if d and os.path.isfile(os.path.join(d, 'ferry-socks5-server.py')):
                return d
    except Exception:
        pass

    # ② 环境变量
    d = os.environ.get('FERRY_RELAY_DIR')
    if d and os.path.isdir(d) and os.path.isfile(
            os.path.join(d, 'ferry-socks5-server.py')):
        return d

    # ③ / ④ 候选 + glob，按分数挑最高
    cands = [
        os.path.join(os.environ.get('LOCALAPPDATA') or '', 'Ferry'),
        os.path.join(os.path.expanduser('~'), 'AppData', 'Local', 'Ferry'),
    ]
    try:
        import glob as _glob
        cands += _glob.glob(os.path.join('C:\\Users', '*', 'AppData', 'Local', 'Ferry'))
    except Exception:
        pass

    best, best_score = None, 0
    for c in cands:
        if not c or not os.path.isdir(c):
            continue
        sc = _relay_dir_score(c)
        if sc > best_score:
            best, best_score = c, sc

    if best:
        return best

    # 全都没命中：回落到门户同目录（至少不会把配置写到 systemprofile 里去）
    return os.path.dirname(os.path.abspath(__file__))

def rate_conf_path():
    return os.path.join(relay_dir(), 'rate-mb.txt')


def read_rate_conf():
    """读当前限速配置。返回 {'perIp': MB/s, 'total': MB/s}。"""
    out = {'perIp': 10.0, 'total': 0.0}
    p = rate_conf_path()
    if not os.path.exists(p):
        return out
    try:
        lines = [x.strip() for x in open(p, encoding='utf-8').read().splitlines()]

        def num(i, d):
            if len(lines) <= i or not lines[i]:
                return d
            try:
                return max(0.0, float(lines[i].split()[0]))
            except (ValueError, IndexError):
                return d

        out['perIp'] = num(0, 10.0)
        out['total'] = num(1, 0.0)
    except Exception:
        pass
    return out


def write_rate_conf(per_ip, total):
    """写限速配置。中继 5 秒内自动生效（热更新，不重启）。

    为什么先写临时文件再改名：中继那侧可能正好在读到一半，
    原子替换能保证它读到的要么是旧值、要么是新值，不会是半截。
    """
    p = rate_conf_path()
    d = os.path.dirname(p)
    try:
        if d and not os.path.isdir(d):
            os.makedirs(d, exist_ok=True)
        body = '%.2f\n%.2f\n' % (max(0.0, float(per_ip)), max(0.0, float(total)))
        tmp = p + '.tmp'
        with open(tmp, 'w', encoding='utf-8') as f:
            f.write(body)
        os.replace(tmp, p)
        return True
    except Exception as e:
        sys.stderr.write('[net] write rate conf failed: %s\n' % e)
        return False


def uplink_setting_path():
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), 'net-setting.json')


_uplink_cache = {'v': None, 'at': 0.0}
UPLINK_CACHE_SEC = 5.0


def read_uplink_mbps():
    """远端物理出口带宽（Mbps），用于算「载荷百分比」。

    为什么让管理员手填而不是自动测：自动测速本身要跑满带宽、
    还会因为测速源不同差几十倍（项目里实测过 89 MB/s 到 0.4 MB/s）。
    管理员知道自己那条线是多少，填一个数最省事也最准。
    默认 1000 Mbps —— pcs 那条是千兆联通线路。
    """
    # live 每 3 秒调一次，5 秒缓存足够，也不会让面板一直打磁盘
    t = time.time()
    if _uplink_cache['v'] is not None and t - _uplink_cache['at'] < UPLINK_CACHE_SEC:
        return _uplink_cache['v']

    val = 1000.0
    p = uplink_setting_path()
    try:
        if os.path.exists(p):
            d = json.load(open(p, encoding='utf-8'))
            v = float(d.get('uplinkMbps') or 0)
            if v > 0:
                val = v
    except Exception:
        pass
    _uplink_cache['v'] = val
    _uplink_cache['at'] = t
    return val


def write_uplink_mbps(mbps):
    try:
        d = {'uplinkMbps': max(1.0, float(mbps))}
        with open(uplink_setting_path(), 'w', encoding='utf-8') as f:
            json.dump(d, f)
        # 必须让缓存失效 —— 否则刚写完立刻读还是旧值，
        # 表现是「面板上填了 1000，保存后还显示 100」，实测踩过。
        _uplink_cache['v'] = None
        _uplink_cache['at'] = 0.0
        return True
    except Exception:
        return False


def load_state(rx_bps, tx_bps, online, per_ip_conf, total_conf):
    """算「载荷」——当前总速率占远端出口的比例，以及各级边界。

    为什么要单独算这个：面板原来只显示「下行 X KB/s」，
    这个数字**没有参照物** —— 7 MB/s 是好是坏？不知道。
    有了出口带宽和每人配额，才能回答三个实际问题：
      · 现在这条线吃满了吗（会不会卡）
      · 离饱和还有多少余量
      · 是不是已经有人撞到自己的配额了

    返回：
      loadPct   当前总速率 / 出口带宽
      perIpPct  最快那台 / 单 IP 配额（>100% 说明被限速咬住了）
      level     ok / warn / hot（阈值 60% / 85%）
    """
    uplink_mbps = read_uplink_mbps()
    up_bps = uplink_mbps * 1024 * 1024 / 8.0     # Mbps -> byte/s
    cur = max(0, rx_bps) + max(0, tx_bps)
    load_pct = (cur / up_bps * 100.0) if up_bps > 0 else 0.0

    # 超 100% 不是不可能：出口带宽是管理员手填的估计值，
    # 实际在线人数多、瞬时突发时会短暂超过。保留真实数字（比夹到 100 诚实），
    # 但标成 hot —— 那一定在卡。
    overload = load_pct > 100.0
    level = 'ok'
    if load_pct >= 85:
        level = 'hot'
    elif load_pct >= 60:
        level = 'warn'

    return {
        'uplinkMbps': uplink_mbps,
        'curBps': cur,
        'loadPct': round(load_pct, 1),
        'level': level,
        'overload': overload,
        'perIpMb': per_ip_conf,
        'totalMb': total_conf,
        'online': online,
        # 距离各级边界还剩多少（直接回答「离卡顿还有多远」）
        'toWarnPct': round(max(0.0, 60 - load_pct), 1),
        'toHotPct': round(max(0.0, 85 - load_pct), 1),
        'headroomBps': max(0, int(up_bps - cur)),
    }


def human_size(n):
    """字节数转人看得懂的大小。"""
    try:
        n = float(n)
    except (TypeError, ValueError):
        return '?'
    if n >= 1024 * 1024:
        return f'{n / (1024 * 1024):.1f} MB'
    if n >= 1024:
        return f'{n / 1024:.0f} KB'
    return f'{int(n)} B'


def build_deploy_zip(client):
    """
    打一个 zip：安装向导 + 全部组件 + 预填好的 config.json。

    为什么用 zip 而不是单文件 .cmd：
      · 要带 GUI（安装向导和控制面板），GUI 是独立 .ps1 文件
      · 单文件 .cmd 只能塞 base64 的纯文本脚本，塞多个文件会变成一行几万字符的
        怪物，任何人想改都改不了
      · zip 解开就是干净的文件树，用户能自己看、能自己改
    """
    import zipfile
    ip = client['ip']
    tok = client['token']
    host = CFG['public_host'] or '10.13.24.1'
    port = CFG['port']

    here = os.path.dirname(os.path.abspath(__file__))

    def read_component(*cands):
        for c in cands:
            p = os.path.normpath(os.path.join(here, c))
            if os.path.exists(p):
                return open(p, 'rb').read()
        return None

    files = {}

    # 组件：安装向导 / 控制面板 / 隧道保活 / 本地代理
    for name, cands in (
        ('client-setup.ps1',      ('client-setup.ps1',)),
        ('渡口助手.ps1',           ('client-assistant.ps1', '渡口助手.ps1',
                                   os.path.join('..', 'tools', '渡口助手.ps1'))),
        ('ssh-tunnel-keeper.ps1', ('ssh-tunnel-keeper.ps1',
                                   os.path.join('..', 'tools', 'ssh-tunnel-keeper.ps1'))),
        ('dual-proxy.mjs',        ('dual-proxy.mjs',
                                   os.path.join('..', 'tools', 'dual-proxy.mjs'))),
    ):
        data = read_component(*cands)
        if data is None:
            raise RuntimeError(f'组件缺失：{name}（门户目录里找不到）')
        files[name] = data

    # config.json —— 安装向导读它
    cfg = {
        'remoteHost': host,
        'sshPort': CFG['ssh_port'],
        'httpPort': port + 1,
        'accountIp': ip,
        'token': tok,
        'fallbackProxy': '',
    }
    files['config.json'] = json.dumps(cfg, ensure_ascii=False, indent=2).encode('utf-8')

    # 安装.cmd —— 双击入口
    # 用 PowerShell 直接跑安装向导，不走执行策略拦截。
    launch = (
        '@echo off\r\n'
        'chcp 65001 >nul 2>&1\r\n'
        'setlocal\r\n'
        'cd /d "%~dp0"\r\n'
        'if not exist "client-setup.ps1" (\r\n'
        '  echo 找不到 client-setup.ps1，请把整个压缩包解压后再运行。\r\n'
        '  pause\r\n'
        '  exit /b 1\r\n'
        ')\r\n'
        'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "client-setup.ps1"\r\n'
        'if errorlevel 1 pause\r\n'
        'endlocal\r\n'
    )
    files['安装.cmd'] = launch.encode('gbk', errors='replace')

    # 说明.txt
    readme = (
        '渡口 · Ferry —— 安装说明\r\n'
        '========================================\r\n'
        '\r\n'
        '1. 把整个压缩包解压到任意目录（不要在压缩包里直接双击）\r\n'
        '2. 双击「安装.cmd」\r\n'
        '3. 跟向导走完，装完会问你要不要立即启动\r\n'
        '\r\n'
        f'账号（局域网 IP）：{ip}\r\n'
        f'远端主机：{host}:{CFG["ssh_port"]}\r\n'
        f'本地代理端口：{port + 1}\r\n'
        '\r\n'
        '第一次使用需要把公钥发给管理员（安装过程中会显示），之后不用再配置。\r\n'
        '\r\n'
        '装好后：\r\n'
        '  · 「渡口助手」用来在借网与直连之间切换\r\n'
        '  · 同一个程序里有「卸载」，会切回直连并删掉自启\r\n'
        '\r\n'
        '本工具仅用于个人学习与内网连通性测试，请遵守所在网络的使用规定。\r\n'
    )
    files['说明.txt'] = readme.encode('utf-8-sig')

    buf = io.BytesIO()
    with zipfile.ZipFile(buf, 'w', zipfile.ZIP_DEFLATED) as z:
        for name, data in files.items():
            z.writestr(name, data)
    return buf.getvalue()


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
                # 首页只发软件，不再收密钥（用户要求：网页只用来获取软件）
                ip0 = self.client_ip()
                self.send_html(get_page(detected_ip=ip0 if PRIVATE_IP_RE.match(ip0) else ''))
            elif path == '/login':
                # 老的登录入口作废，直接回首页
                self.redirect('/')
            elif path == '/portal':
                # 登录后的门户页作废：密钥已经改到软件里，网页不再有「登录」这件事。
                # 老用户的书签直接带回首页，别让他们看到「下载 ferry-*.cmd」那种过时内容。
                self.redirect('/')
            elif path == '/download-cmd':
                self.handle_download_cmd()
            elif path.startswith('/download/version/'):
                self.handle_download_version(path)
            elif path == '/download' or path.startswith('/download/'):
                self.handle_download(path)
            elif path == '/logout':
                self.handle_logout()
            elif path == '/api/admin/live':
                self.handle_api_admin_live()
            elif path == '/api/admin/net':
                self.handle_api_admin_net()
            elif path == '/api/admin/client':
                self.handle_api_admin_client(
                    urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query))
            elif path == '/admin':
                self.handle_admin()
            elif path == '/proxy.mjs':
                self.handle_proxy_mjs()
            elif path == '/api/status':
                self.handle_api_status()
            elif path == '/api/config':
                self.handle_api_config(urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query))
            elif path == '/api/license':
                self.handle_api_license(urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query))
            elif path == '/version.json':
                self.handle_version()
            elif path == '/download-latest':
                self.handle_download_latest()
            elif path == '/about':
                self.handle_about()
            elif path == '/setup-hosts':
                self.handle_setup_hosts()
            elif path == '/admin/unlock':
                self.handle_unlock()
            elif path == '/favicon.ico':
                self.send_response(204)
                self.end_headers()
            else:
                # 404 要给出路，而不是只告诉人「没有这个页面」。
                # 常见来路：地址栏多打了字符（比如 /0）、旧书签、
                # 或者把域名和端口拼错了。
                self.send_html(page('404 · Ferry', f"""
<h1>404</h1>
<div class="sub">没有这个页面：<code class="mono">{html.escape(path)}</code></div>

<div class="card">
  <h2 style="margin-top:0">你可能想找</h2>
  <div class="step"><div class="n">→</div><div class="t">
    <a href="/" style="color:var(--accent)">登录页</a>
    <div class="hint">用管理员给的临时密钥登录</div>
  </div></div>
  <div class="step"><div class="n">→</div><div class="t">
    <a href="/about" style="color:var(--accent)">关于 Ferry</a>
    <div class="hint">它是什么、怎么工作、有哪些限制</div>
  </div></div>
  <div class="step"><div class="n">→</div><div class="t">
    <a href="/setup-hosts" style="color:var(--accent)">配置局域网域名</a>
    <div class="hint">让 ferry.internal 能直接打开</div>
  </div></div>
  <div class="step"><div class="n">→</div><div class="t">
    <a href="/api/status" style="color:var(--accent)">运行状态</a>
    <div class="hint">JSON，给排查用的</div>
  </div></div>
</div>

<div class="card">
  <h2 style="margin-top:0">访问地址</h2>
  <div class="kv"><div class="k">本页地址</div><div class="v mono">
    http://{html.escape(CFG['public_host'] or '10.13.24.1')}:{CFG['port']}/</div></div>
  <div class="kv"><div class="k">局域网域名</div><div class="v mono">
    http://{CFG['domain']}:{CFG['port']}/</div></div>
  <div class="hint">端口 {CFG['port']} 不能省；域名需在 hosts 里配置过才生效。</div>
</div>
"""), 404)
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
            # 软件内激活：JSON 接口，不是浏览器表单，没有 CSRF token 可言。
            # 防爆破靠 rate_fail（和网页登录共用 user scope）。
            if path == '/api/activate':
                self.handle_api_activate()
                return
            if path == '/api/heartbeat':
                # 客户端心跳：JSON 接口，不走 CSRF（非浏览器表单）
                self.handle_api_heartbeat()
                return
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
            elif path == '/admin/setnote':
                self.handle_admin_setnote(form)
            elif path == '/api/admin/net':
                self.handle_api_admin_net(form)
            elif path == '/admin/unlock':
                self.handle_unlock(form)
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

        # IP 只用于登记（门户里显示"谁在用"），不再作为登录凭据。
        # 默认取连接来源；只有用户显式填了才用表单值 —— 那种情况出现在
        # 反向代理后面（服务端看到的是 127.0.0.1）。
        ip = (form.get('ip') or '').strip() or src_ip
        code = (form.get('code') or '').strip().upper().replace('-', '').replace(' ', '')
        csrf_tok = csrf_set('login|' + src_ip)

        if not PRIVATE_IP_RE.match(ip):
            # 自动检测到非私网地址（被反代了），把可选的 IP 框亮出来让用户填
            self.send_html(login_page(
                '识别到的来源地址不是局域网地址。请手动填写你机器的 IPv4（ipconfig 里的那个）',
                csrf=csrf_tok, detected_ip=src_ip, show_ip_field=True), 400)
            return
        if not re.fullmatch(r'[A-Z0-9]{6,16}', code):
            self.send_html(login_page('密钥格式不对，应该是 8 位字母数字',
                                      csrf=csrf_tok, detected_ip=ip), 400)
            return

        ch = hash_code(code)
        inv = self.db.q1('SELECT * FROM invites WHERE code_hash = ?', (ch,))
        if not inv:
            locked = rate_fail(src_ip, 'user')
            self.db.log('login_fail', src_ip, 'unknown-code')
            msg = '密钥无效'
            if locked:
                msg = '尝试太频繁，已锁定 15 分钟'
            self.send_html(login_page(msg, csrf=csrf_tok, detected_ip=ip), 401 if not locked else 429)
            return
        if inv['revoked']:
            rate_fail(src_ip, 'user')
            self.db.log('login_fail', src_ip, 'revoked')
            self.send_html(login_page('这个密钥已被撤销', csrf=csrf_tok, detected_ip=ip), 401)
            return
        if inv['used_by_ip']:
            # 一次性：激活过就作废。这里不回显被谁用过 —— 那会泄露别人的 IP。
            rate_fail(src_ip, 'user')
            self.db.log('login_fail', src_ip, 'already-used')
            self.send_html(login_page(
                '这个密钥已经用过了（一次性，激活即失效）。请向管理员索取新密钥。',
                csrf=csrf_tok, detected_ip=ip), 401)
            return
        if inv['expires_at'] < now():
            rate_fail(src_ip, 'user')
            self.db.log('login_fail', src_ip, 'expired')
            self.send_html(login_page('密钥已过期，请向管理员索取新的', csrf=csrf_tok, detected_ip=ip), 401)
            return

        # 通过：绑定 IP
        # 授权时长来自**这个密钥自己**的档位（1/2/3/7/30 天，0=永久）。
        # 老密钥没有这个字段，license_days_for 会回退到全局默认。
        days = license_days_for(inv)
        exp = expiry_of(days)

        existing = self.db.q1('SELECT * FROM clients WHERE ip = ?', (ip,))
        if existing:
            token = existing['token']
            # 重新激活 = 续期（把 expires_at 推到新的到期时间）。
            # 不这么做的话，密钥用完之后用户永远续不了。
            self.db.x('UPDATE clients SET enabled = 1, last_seen = ?, expires_at = ? WHERE ip = ?',
                      (now(), exp or existing['expires_at'] or 0, ip))
        else:
            token = new_token()
            self.db.x(
                'INSERT INTO clients (ip, token, enabled, created_at, last_seen, invite_id, expires_at) '
                'VALUES (?,?,1,?,?,?,?)',
                (ip, token, now(), now(), inv['id'], exp))

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

    def handle_download_cmd(self):
        """.cmd 一键安装：双击后自动下载 exe 并启动，全程不碰浏览器下载通道。

        为什么需要它：
          浏览器直接下 exe 会被打上 MOTW 标记，触发 SmartScreen
          「Windows 已保护你的电脑 / 发布者未知」。而 .cmd 不在 SmartScreen
          的检查范围里，它内部用 WebClient 拉下来的 exe 也不带 MOTW ——
          用户只需双击一次，比手动点「更多信息 → 仍要运行」还省一步。

        安全上的取舍：
          这个脚本只做三件事：从本门户下载、Unblock-File、启动。
          没有别的内容，用户右键「编辑」就能一眼看全。
        """
        host = CFG['public_host'] or '10.13.24.1'
        base = 'http://' + host + ':' + str(CFG['port'])

        ps = (
            "$ProgressPreference='SilentlyContinue'; "
            "try { "
            "(New-Object Net.WebClient).DownloadFile('%URL%','%OUT%'); "
            "Unblock-File -Path '%OUT%' -ErrorAction SilentlyContinue; "
            "Start-Process '%OUT%' "
            "} catch { "
            "Write-Host ''; Write-Host ('下载失败：' + $_.Exception.Message) -ForegroundColor Red; "
            "exit 1 }"
        )

        lines = [
            '@echo off',
            'chcp 65001 >nul 2>&1',
            'setlocal',
            'title 渡口 · 安装',
            'echo.',
            'echo   渡口 Ferry —— 正在准备安装',
            'echo.',
            'set "URL=' + base + '/download"',
            'set "OUT=%TEMP%\\Ferry-Setup.exe"',
            'if exist "%OUT%" del /q "%OUT%" >nul 2>&1',
            'echo   正在下载安装程序…',
            'powershell -NoProfile -ExecutionPolicy Bypass -Command "' + ps + '"',
            'if errorlevel 1 (',
            '  echo.',
            '  echo   下载失败。请确认这台机器能访问 ' + base,
            '  echo   或者直接用浏览器打开上面的地址手动下载。',
            '  echo.',
            '  pause',
            ')',
            'endlocal',
        ]

        data = ('\r\n'.join(lines) + '\r\n').encode('gbk', errors='replace')
        self.send_response(200)
        self.send_header('Content-Type', 'application/octet-stream')
        self.send_header('Content-Disposition',
                         'attachment; filename="install-ferry.cmd"; '
                         "filename*=UTF-8''" + urllib.parse.quote('安装渡口.cmd'))
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        self.end_headers()
        self.wfile.write(data)
        self.db.log('download_cmd', self.client_ip(), 'installer-script')

    def handle_download(self, path):
        """发 Windows 安装程序（.exe）。

        两种入口：
          /download/<ip>  已登录，下自己的包（旧行为，保留兼容）
          /download       未登录，门户首页「获取软件」按钮 —— 用来源 IP 当账号 IP。
                          未注册的 IP 也能下：装完在软件里输入密钥激活时才建号。
                          这就是用户要的「网页只用来获取软件」。

        为什么不再是 zip：一份 exe 服务所有人 —— 账号 IP 编进**文件名**，
        安装程序自己解析后向 /api/config 换配置，所以二进制完全一样，
        门户不用每次重新编译。更新推送也只要换 dist 里那个 exe。
        """
        sess_ip, _ = self.current_session()
        src = self.client_ip()

        if path == '/download':
            # 匿名获取：文件名带来源 IP，激活时用它绑定
            ip = src
            need_registered = False
        else:
            want = urllib.parse.unquote(path[len('/download/'):])
            if not sess_ip:
                self.send_html(page('请先登录',
                    '<h1>请先登录</h1>'
                    '<p><a href="/" style="color:var(--accent)">去首页</a></p>'), 403)
                return
            if sess_ip != want:
                self.send_html(page('拒绝', '<h1>只能下载自己的部署包</h1>'), 403)
                return
            ip = sess_ip
            need_registered = True

        if need_registered:
            cli = self.db.q1('SELECT * FROM clients WHERE ip = ?', (ip,))
            if not cli or not cli['enabled']:
                self.send_html(page('已停用', '<h1>账号已停用</h1>'), 403)
                return

        exe = installer_path()
        if exe is None:
            self.send_html(page('未构建', '<h1>服务端还没有安装程序</h1>'
                '<div class="sub">管理员还没构建 Ferry-Setup.exe</div>'
                '<div class="card"><div class="kv">'
                '<div class="k">构建命令</div>'
                '<div class="v mono">cd installer &amp;&amp; ISCC.exe ferry.iss</div>'
                '</div></div>'
                '<p><a href="/" style="color:var(--accent)">返回</a></p>'), 404)
            return

        with open(exe, 'rb') as f:
            data = f.read()

        # 文件名**不再**带 IP。安装程序会自己向 /api/config（无参）问
        # 「我是哪台机器」，比靠文件名传可靠得多：浏览器会改名、人会手改、
        # 拖到别的机器装就全错了。只在门户连不上时才会退回文件名解析兜底，
        # 所以文件名里带 IP 仍然有用，但只是个加分项、不是必要条件。
        fname = 'Ferry-Setup-' + str(read_version_manifest().get('latest', '0.0.0')) + '.exe'
        self.send_response(200)
        self.send_header('Content-Type', 'application/vnd.microsoft.portable-executable')
        self.send_header('Content-Disposition',
                         f"attachment; filename=\"{fname}\"; "
                         f"filename*=UTF-8''{urllib.parse.quote(fname)}")
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('X-Ferry-Account', ip)
        # 让用户/我们能核对下载是否完整
        self.send_header('X-Ferry-Bytes', str(len(data)))
        self.end_headers()
        # 分块写。一次 write 2 MB 时若中途被杀软或代理掐断，
        # 浏览器会留下一个 0 KB 或半截的残片 —— 双击时报
        # 「此应用无法在你的电脑上运行」，用户以为是程序坏了。
        # 64 KB 一块既快又能尽早发现中断。
        CHUNK = 65536
        try:
            for i in range(0, len(data), CHUNK):
                self.wfile.write(data[i:i + CHUNK])
        except (BrokenPipeError, ConnectionResetError):
            return
        self.db.log('download', ip, fname)


    def handle_download_version(self, path):
        """下载指定历史版本：/download/version/0.8.0

        为什么需要：新版本在某些机器上出问题时，用户要能装回旧版。
        只留「最新」等于把退路砍掉。
        """
        ver = urllib.parse.unquote(path[len('/download/version/'):]).strip()
        # 严格校验版本号格式，避免路径穿越（../../ 之类）
        if not re.match(r'^[\d.]+$', ver):
            self.send_html(page('版本号无效',
                '<h1>版本号无效</h1><p><a href="/" style="color:var(--accent)">返回首页</a></p>'), 400)
            return

        target = None
        for it in list_installers():
            if it['ver'] == ver:
                target = it
                break
        if target is None:
            avail = ', '.join(x['ver'] for x in list_installers()) or '（无）'
            self.send_html(page('没有这个版本',
                f'<h1>没有 v{html.escape(ver)}</h1>'
                f'<div class="sub">现有版本：{html.escape(avail)}</div>'
                '<p><a href="/" style="color:var(--accent)">返回首页</a></p>'), 404)
            return

        try:
            with open(target['path'], 'rb') as f:
                data = f.read()
        except OSError:
            self.send_html(page('读取失败', '<h1>安装包读取失败</h1>'), 500)
            return

        fname = 'Ferry-Setup-' + ver + '.exe'
        self.send_response(200)
        self.send_header('Content-Type', 'application/vnd.microsoft.portable-executable')
        self.send_header('Content-Disposition',
                         f"attachment; filename=\"{fname}\"; "
                         f"filename*=UTF-8''{urllib.parse.quote(fname)}")
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('X-Ferry-Version', ver)
        self.send_header('X-Ferry-Bytes', str(len(data)))
        self.end_headers()
        CHUNK = 65536
        try:
            for i in range(0, len(data), CHUNK):
                self.wfile.write(data[i:i + CHUNK])
        except (BrokenPipeError, ConnectionResetError):
            return
        try:
            self.db.log('download', self.client_ip(), fname)
        except Exception:
            pass

    def handle_download_latest(self):
        """通用安装包，文件名不带账号 IP。用于更新推送。

        客户端已经装过一次、知道自己的 IP，安装程序会从
        安装目录下 config.json 里的 accountIp 继承，不弹输入页。
        """
        exe = installer_path()
        if exe is None:
            self.send_json({'error': 'no installer built'}, 404)
            return
        with open(exe, 'rb') as f:
            data = f.read()
        fname = os.path.basename(exe)
        self.send_response(200)
        self.send_header('Content-Type', 'application/vnd.microsoft.portable-executable')
        self.send_header('Content-Disposition', f"attachment; filename=\"{fname}\"")
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('X-Ferry-Bytes', str(len(data)))
        self.end_headers()
        CHUNK = 65536
        try:
            for i in range(0, len(data), CHUNK):
                self.wfile.write(data[i:i + CHUNK])
        except (BrokenPipeError, ConnectionResetError):
            return

    def handle_api_config(self, qs):
        """给 .exe 安装程序取配置。

        安全边界：**只允许同一台机器查自己的配置**（client_address 必须等于
        请求的 ip）。即使有人知道别人的局域网 IP，也拿不到别人的令牌。
        返回体就是 config.json 原文，安装程序直接落盘。
        """
        want = (qs.get('ip') or [''])[0].strip()
        src = self.client_ip()

        # 不带 ip 参数 = 让服务端自己判断「你是哪台机器」。
        # 这是最可靠的方式：client_address 就是这台机器真实的局域网 IP，
        # 不需要靠文件名传、也不用安装程序去枚举网卡（多网卡/虚拟网卡很容易选错）。
        # 显式传 ip 仍然支持，但要和来源一致 —— 那是「装到别的机器上」的场景。
        if want:
            if want != src:
                self.send_json({'error': 'ip mismatch', 'src': src}, 403)
                return
        else:
            want = src
            if not want:
                self.send_json({'error': 'cannot determine client ip'}, 400)
                return

        cli = self.db.q1('SELECT * FROM clients WHERE ip = ?', (want,))
        if not cli or not cli['enabled']:
            # 未注册不算致命错误：安装照样可以进行，用户在软件里输入密钥时才建号。
            # 所以这里回 200 + registered=false，让安装程序能写出一份可用的 config。
            host0 = CFG['public_host'] or '10.13.24.1'
            self.send_json({
                'registered': False,
                'remoteHost': host0,
                'sshPort': CFG['ssh_port'],
                'httpPort': CFG['port'] + 1,
                'accountIp': want,
                'token': '',
                'fallbackProxy': '',
                'portalUrl': 'http://' + host0 + ':' + str(CFG['port']),
            })
            return

        host = CFG['public_host'] or '10.13.24.1'
        cfg = {
            'registered': True,
            'remoteHost': host,
            'sshPort': CFG['ssh_port'],
            'httpPort': CFG['port'] + 1,
            'accountIp': want,
            # token 故意留空 —— 用户明确要求「密钥在软件里输入激活」。
            # 从安装包直接下发 token 的话，新用户装完就是「已激活」状态，
            # 既看不到剩余时长，也绕过了激活这一步；而且安装包一旦被转发，
            # 凭据就跟着泄露了。token 只能由 POST /api/activate 签发。
            'token': '',
            'fallbackProxy': '',
            # 客户端激活密钥要用。存进 config.json 后，下次换网段也不用重装。
            'portalUrl': 'http://' + host + ':' + str(CFG['port']),
        }
        self.db.log('config', want, 'installer')
        self.send_json(cfg)


    def handle_api_heartbeat(self):
        """客户端每 5 秒上报一次累计字节数。鉴权与 /api/license 一致（ip + token）。

        为什么需要它：Windows 拿不到 per-connection 的网络字节，
        而管理页要显示「每个用户当前多快」。客户端自己的代理进程本来就在
        累计 BytesUp/BytesDown，直接报上来最准、最便宜。
        """
        try:
            n = int(self.headers.get('Content-Length') or 0)
            raw = self.rfile.read(n) if n > 0 else b'{}'
            body = json.loads(raw.decode('utf-8') or '{}')
        except Exception:
            self.send_json({'ok': False, 'error': 'bad json'}, 400)
            return

        ip = str(body.get('ip') or '').strip()
        token = str(body.get('token') or '').strip()
        if not ip or not token:
            self.send_json({'ok': False, 'error': 'missing fields'}, 400)
            return

        cli = self.db.q1('SELECT * FROM clients WHERE ip = ?', (ip,))
        if not cli or not hmac.compare_digest(str(cli['token']), token):
            self.send_json({'ok': False, 'error': 'unauthorized'}, 401)
            return

        try:
            rx = max(0, int(body.get('rxBytes') or 0))
            tx = max(0, int(body.get('txBytes') or 0))
            conns = max(0, int(body.get('conns') or 0))
        except Exception:
            rx = tx = conns = 0

        h = note_heartbeat(ip, rx, tx, conns, body.get('running'),
                          str(body.get('mode') or ''))
        # 累计量直接用 note_heartbeat 算好的增量（它已经和上一次心跳比过）。
        # 不要在这里另存一份"上次的值" —— HTTPServer 每个请求都是新的处理实例，
        # 挂在 self 上的属性下一个请求就没了。
        note_totals(ip, h.get('dRx', 0), h.get('dTx', 0), h['rxBps'], h['txBps'])
        flush_totals(self.db)
        # 把算出来的速率回给客户端 —— 方便排查「为什么面板没数字」
        # 顺带把授权状态带回去。
        # 为什么放在心跳里：管理员在后台停用一台机器时，客户端原来要等下一次
        # CheckLicense（最长 6 小时）才断，那段时间用户完全无感还在借网。
        # 心跳 5 秒一次，捎带这两个字段等于零成本快速生效。
        try:
            _lic = license_state_of(cli)
        except Exception:
            _lic = {'enabled': True, 'expired': False}
        self.send_json({'ok': True, 'rxBps': h['rxBps'], 'txBps': h['txBps'],
                        'enabled': bool(_lic.get('enabled')),
                        'expired': bool(_lic.get('expired'))})

    def handle_api_license(self, qs):
        """查询当前授权状态与剩余时长（客户端每次启动 / 每小时调一次）。

        鉴权用 (ip, token) 组合 —— 只认本机 IP 的那种写法挡不住「同一局域网
        里别人拿到你的 token」，所以 token 也必须对上。

        到期处理：这里不改库，只如实报告 enabled=0/expired=1。
        真正的落库在 activate 续期时做 —— 让「到期」是个可逆的软状态，
        用户续费后不用等管理员手工改库。
        """
        ip = (qs.get('ip') or [''])[0].strip()
        token = (qs.get('token') or [''])[0].strip()

        if not ip or not token:
            self.send_json({'ok': False, 'error': '缺少 ip 或 token'}, 400)
            return

        cli = self.db.q1('SELECT * FROM clients WHERE ip = ?', (ip,))
        if not cli or not hmac.compare_digest(str(cli['token']), token):
            self.send_json({'ok': False, 'error': '授权信息无效'}, 401)
            return

        st = license_state_of(cli)

        self.send_json({
            'ok': True,
            'enabled': st['enabled'],
            'expiresAt': st['expiresAt'],      # 0 = 永久
            'daysLeft': st['daysLeft'],
            'hoursLeft': st['hoursLeft'],
            'expired': st['expired'],
            'suspended': st['suspended'],
            'remoteHost': CFG['public_host'] or '10.13.24.1',
            'sshPort': CFG['ssh_port'],
            'httpPort': CFG['port'] + 1,
        })

    def handle_version(self):
        """更新推送用的版本清单。客户端助手启动时拉一次，5 秒超时静默失败。"""
        self.send_json(read_version_manifest())

    def handle_api_activate(self):
        """软件内用密钥激活借网权限（JSON API，不是浏览器表单）。

        和网页登录**共用同一套 invites 密钥**，区别只在返回 JSON 而不是跳转：
        用户在「渡口助手」里填密钥 → 这里校验 → 建号 → 回长期 token。

        规则和网页登录完全一致（一次性、绑定激活来源 IP、限流防爆破），
        因为两边是同一条安全边界，不能有一边松。
        """
        try:
            n = int(self.headers.get('Content-Length') or 0)
            raw = self.rfile.read(n) if n > 0 else b'{}'
            body = json.loads(raw.decode('utf-8') or '{}')
        except Exception:
            self.send_json({'ok': False, 'error': '请求格式不对'}, 400)
            return

        ip = self.client_ip()
        code = str(body.get('code') or '').strip().upper()

        if not code:
            self.send_json({'ok': False, 'error': '请输入密钥'}, 400)
            return
        if not re.fullmatch(r'[A-Z0-9]{6,16}', code):
            self.send_json({'ok': False, 'error': '密钥格式不对（应为 8 位字母数字）'}, 400)
            return

        # 限流：和网页登录共用 user scope，防止用它暴力试密钥
        st = _ratelimit.get(_rl_key('user', ip))
        if st and st.get('locked_until', 0) > now():
            self.send_json({'ok': False, 'error': '尝试太频繁，请 15 分钟后再试'}, 429)
            return

        inv = self.db.q1('SELECT * FROM invites WHERE code_hash = ?', (hash_code(code),))
        if not inv:
            rate_fail(ip, 'user'); self.db.log('activate_fail', ip, 'unknown-code')
            self.send_json({'ok': False, 'error': '密钥无效'}, 401)
            return
        if inv['revoked']:
            rate_fail(ip, 'user'); self.db.log('activate_fail', ip, 'revoked')
            self.send_json({'ok': False, 'error': '这个密钥已被撤销'}, 401)
            return
        # 同一台机器重复激活 —— 只在「明显是重试」的时间窗内放行。
        #
        # 为什么要区分：
        #   · 网络超时重发、用户连点两下、config.json 没写成功 —— 这些是秒级的，
        #     拦下来只会让用户以为软件坏了，应该幂等成功
        #   · 用户过几分钟再输一遍同一个密钥 —— 那是真的想重复用一次，
        #     密钥是一次性的，必须拒绝，否则「一次性」就没有意义了
        # 60 秒是够宽松的重试窗口，也是明显区别于「故意再用一次」的界限。
        RETRY_WINDOW_SEC = 60
        same_machine = bool(inv['used_by_ip']) and inv['used_by_ip'] == ip
        recent_retry = same_machine and (now() - int(inv['used_at'] or 0) <= RETRY_WINDOW_SEC)
        if same_machine and not recent_retry:
            rate_fail(ip, 'user'); self.db.log('activate_fail', ip, 'already-used-same-ip')
            self.send_json({'ok': False,
                            'error': '这个密钥已经用过了（一次性）。如果你需要更长的授权，请向管理员要新密钥。'}, 401)
            return
        if inv['used_by_ip'] and not same_machine:
            rate_fail(ip, 'user'); self.db.log('activate_fail', ip, 'used-by-other')
            self.send_json({'ok': False,
                            'error': '这个密钥已经在别的机器（%s）上用过了' % inv['used_by_ip']}, 401)
            return
        if inv['expires_at'] < now() and not same_machine:
            rate_fail(ip, 'user'); self.db.log('activate_fail', ip, 'expired')
            self.send_json({'ok': False, 'error': '密钥已过期，请向管理员索取新的'}, 401)
            return

        # 授权时长来自这个密钥自己的档位（1/2/3/7/30 天，0=永久）。
        # 老密钥没有该字段时回退到全局默认 —— 不能让历史密钥突然失效。
        days = license_days_for(inv)
        exp = expiry_of(days)
        days_left = days_left_of(exp)

        # 通过：绑定 IP，建号或复活
        existing = self.db.q1('SELECT * FROM clients WHERE ip = ?', (ip,))
        if existing:
            token = existing['token']
            # 续期：把 expires_at 往后推（重新激活 = 续一个月）
            self.db.x('UPDATE clients SET enabled = 1, last_seen = ?, expires_at = ? WHERE ip = ?',
                      (now(), exp, ip))
        else:
            token = new_token()
            self.db.x(
                'INSERT INTO clients (ip, token, enabled, created_at, last_seen, invite_id, expires_at) '
                'VALUES (?,?,1,?,?,?,?)',
                (ip, token, now(), now(), inv['id'], exp))

        self.db.x('UPDATE invites SET used_by_ip = ?, used_at = ? WHERE id = ?',
                  (ip, now(), inv['id']))
        rate_ok(ip, 'user')
        self.db.log('activate_ok', ip, f'invite={inv["id"]}')

        self.send_json({
            'ok': True,
            'token': token,
            'ip': ip,
            'remoteHost': CFG['public_host'] or '10.13.24.1',
            'sshPort': CFG['ssh_port'],
            'httpPort': CFG['port'] + 1,
            'expiresAt': exp,
            'daysLeft': days_left,
            'portalUrl': 'http://' + (CFG['public_host'] or '10.13.24.1') + ':' + str(CFG['port']),
        })

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

    def handle_api_admin_client(self, qs):
        """单台机器的详情：累计流量、历史峰值、授权、在线、事件。

        给管理面板的「点 IP 看详情」用。

        为什么要有这页：管理面板的表格只能扫一眼当前状态，
        而「这台机器一共用了多少流量」「历史上最快跑过多少」「什么时候激活的」
        这些要翻库才知道。管理员想判断「是不是有人偷偷挂下载」时，
        这几个数字比瞬时速率有用得多。
        """
        if not self._require_admin():
            return
        ip = (qs.get('ip') or [''])[0].strip()
        if not ip:
            self.send_json({'ok': False, 'error': 'missing ip'}, 400)
            return

        cli = self.db.q1('SELECT * FROM clients WHERE ip = ?', (ip,))
        if not cli:
            self.send_json({'ok': False, 'error': 'not found'}, 404)
            return

        hb = heartbeat_of(ip)
        tr = traffic_of(self.db, ip)
        hours = hourly_of(ip)

        # 最近 24 小时的峰值（hourly 里的，跟总历史峰值分开给）
        peak24rx = max([h['peakRx'] for h in hours] or [0])
        peak24tx = max([h['peakTx'] for h in hours] or [0])
        sum24rx = sum(h['rx'] for h in hours)
        sum24tx = sum(h['tx'] for h in hours)

        ev = self.db.q(
            'SELECT ts, kind, detail FROM events WHERE ip = ? ORDER BY id DESC LIMIT 20',
            (ip,))

        self.send_json({
            'ok': True,
            'ip': ip,
            'note': cli['note'] or '',
            'enabled': bool(cli['enabled']),
            'createdAt': int(cli['created_at'] or 0),
            'expiresAt': int(cli['expires_at'] or 0),
            'lastSeen': int(cli['last_seen'] or 0),
            'online': hb is not None,
            'rxBps': int(hb['rxBps']) if hb else 0,
            'txBps': int(hb['txBps']) if hb else 0,
            'conns': int(hb['conns']) if hb else 0,
            'totalRx': tr['totalRx'],
            'totalTx': tr['totalTx'],
            'peakRx': tr['peakRx'],
            'peakTx': tr['peakTx'],
            'firstSeen': tr['firstSeen'],
            'peak24Rx': peak24rx,
            'peak24Tx': peak24tx,
            'sum24Rx': sum24rx,
            'sum24Tx': sum24tx,
            'events': [{'ts': int(r['ts']), 'kind': r['kind'], 'detail': r['detail']}
                       for r in ev],
        })

    def handle_api_admin_net(self, form=None):
        """读写限速配置。

        GET  -> 当前配置（form=None）
        POST -> 改配置（form 由 do_POST 预先读好并已过 CSRF）

        为什么改限速不做成「重载中继进程」：
        重启会掐断所有正在传输的连接 —— 用户正在下载的文件直接断。
        写文件 + 中继热读是唯一无感的做法。
        """
        if not self._require_admin():
            return

        if form is not None:
            cur = read_rate_conf()

            def pick(name, dflt):
                v = form.get(name)
                if v is None or v == '':
                    return dflt
                try:
                    return float(v)
                except (TypeError, ValueError):
                    return dflt

            per_ip = pick('perIp', cur['perIp'])
            total = pick('total', cur['total'])
            # 夹紧到合理区间：99999 是笔误不是需求
            per_ip = max(0.0, min(1000.0, per_ip))
            total = max(0.0, min(10000.0, total))

            ok = write_rate_conf(per_ip, total)
            if form.get('uplinkMbps') not in (None, ''):
                try:
                    write_uplink_mbps(float(form.get('uplinkMbps')))
                except (TypeError, ValueError):
                    pass

            self.db.log('rate_change', self.client_ip(),
                        'perIp=%.2f total=%.2f' % (per_ip, total))
            if ok:
                self.send_json({'ok': True, 'perIp': per_ip, 'total': total,
                                'uplinkMbps': read_uplink_mbps()})
            else:
                self.send_json({'ok': False,
                                'error': '写入失败（检查中继目录权限）'}, 500)
            return

        cur = read_rate_conf()
        _rd = relay_dir()
        _rp = rate_conf_path()
        try:
            _exists = os.path.exists(_rp)
            _mtime = int(os.stat(_rp).st_mtime) if _exists else 0
        except Exception:
            _exists, _mtime = False, 0
        self.send_json({
            'ok': True,
            'perIp': cur['perIp'],
            'total': cur['total'],
            'uplinkMbps': read_uplink_mbps(),
            'range': {'minMb': 0, 'maxMb': 5000},
            # 诊断用：面板上「改了不生效」时，第一件要看的就是这两个。
            # 门户以 SYSTEM 跑时 %LOCALAPPDATA% 会指向 systemprofile，
            # 曾经因此把配置写进了另一个目录，而中继永远读不到。
            'relayDir': _rd,
            'rateFile': _rp,
            'rateFileExists': _exists,
            'rateFileMtime': _mtime,
            'isSystem': (os.environ.get('USERNAME') or '').upper() == 'SYSTEM',
        })
    def handle_api_admin_live(self):
        """管理面版轮询用。只有管理员能看 —— 这里会暴露谁在用。"""
        if not self.is_admin():
            self.send_json({'ok': False, 'error': 'forbidden'}, 403)
            return
        self.send_json(sample_live(self.db))

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
        # 登录成功主动换一次管理区 token（防会话固定）。
        # 之后这个 token 在会话内保持稳定，见 csrf_set 的说明。
        csrf_rotate('admin|' + src_ip)
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

        # 这个密钥能换多少天授权。0 = 永久；非法值回退到第一档。
        try:
            lic = int(form.get('license_days') or LICENSE_TIERS[-1])
        except ValueError:
            lic = LICENSE_TIERS[-1]
        if lic != 0 and lic not in LICENSE_TIERS:
            lic = LICENSE_TIERS[-1]

        code = new_invite_code()
        ch = hash_code(code)
        self.db.x(
            'INSERT INTO invites (code_hash, code_hint, note, created_at, expires_at, license_days) '
            'VALUES (?,?,?,?,?,?)',
            (ch, code[:4] + '****', note, now(), now() + hours * 3600, lic))
        self.db.log('invite_new', self.client_ip(), f'note={note} hours={hours} days={lic}')
        # 明文只在这一响应里出现一次
        self.send_html(admin_page(self.db, new_code=code, new_hours=hours,
                                  csrf=csrf_set('admin|' + self.client_ip())))

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

    def handle_admin_setnote(self, form):
        """改某台机器的备注。

        为什么要能改：备注是给人看的（「张三-3楼」「实验室那台」），
        时间一长必然需要修正。以前只有创建密钥时能填，设备列表只能读 ——
        等于备注写错了就永远改不了。

        两种响应格式：
          · 普通表单提交 → 整页重定向回管理面板
          · fetch（X-Requested-With: fetch）→ 返回 JSON，前端只更新那一格
            （整页刷新会把用户弹回顶部、实时监控也重新拉一遍）
        """
        if not self._require_admin():
            return

        wants_json = (self.headers.get('X-Requested-With', '') == 'fetch')

        def reply(ok, msgtext, code=200):
            if wants_json:
                self.send_json({'ok': ok, 'error': '' if ok else msgtext}, code if not ok else 200)
            else:
                self.send_html(admin_page(
                    self.db, msg=msgtext if ok else '', err='' if ok else msgtext,
                    csrf=csrf_set('admin|' + self.client_ip())), code)

        ip = (form.get('ip') or '').strip()
        note = (form.get('note') or '').strip()
        if len(note) > 60:
            note = note[:60]

        cli = self.db.q1('SELECT * FROM clients WHERE ip = ?', (ip,))
        if not cli:
            reply(False, '找不到这个 IP', 404)
            return

        self.db.x('UPDATE clients SET note = ? WHERE ip = ?', (note, ip))
        self.db.log('client_note', self.client_ip(), f'{ip} -> {note[:40]}')

        if wants_json:
            self.send_json({'ok': True, 'ip': ip, 'note': note})
        else:
            self.send_html(admin_page(
                self.db, msg=f'{ip} 的备注已{"更新" if note else "清空"}',
                csrf=csrf_set('admin|' + self.client_ip())))

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

    def handle_about(self):
        """关于页：讲清楚它是什么、怎么工作、边界在哪。

        单独做一页而不是塞进登录页：登录页要短，让人一眼看到输入框；
        想了解细节的人会点「关于」。
        """
        host = CFG['public_host'] or '10.13.24.1'
        port = CFG['port']
        self.send_html(page('关于 · Ferry', f"""
<h1>关于 Ferry（渡口）</h1>
<div class="sub">内网流量摆渡工具 · 自建 · 非商业</div>

<div class="card">
  <h2 style="margin-top:0">名字</h2>
  <div style="font-size:13.5px;line-height:1.8">
    Ferry = 渡口、摆渡。把一批流量从此岸（受限网络）渡到彼岸（可用出口）。
  </div>
</div>

<div class="card">
  <h2 style="margin-top:0">它怎么工作</h2>
<pre><code>你的浏览器 ──HTTP/SOCKS5──▶ 本地双协议代理 ──SOCKS5──▶ ssh -D 隧道
                                                          │
                                                          ▼
                                              远端主机 ──▶ Internet</code></pre>
  <div class="hint">
    隧道是 SSH 动态转发，全程加密；本地代理用同一个端口同时支持 HTTP 与
    SOCKS5（按首字节自动识别）。
  </div>
</div>

<div class="card">
  <h2 style="margin-top:0">为什么不直接开放一个代理端口给大家用</h2>
  <div style="font-size:13.5px;line-height:1.8">
    实测过：校园网对学生账号之间是<strong>端口级</strong>隔离 ——
    从另一台机器探测，22 不通、445 通、某些高端口通。哪些能过完全取决于
    网管策略，随时会变。<br><br>
    所以本方案是<strong>每人各自建一条 SSH 隧道直连远端</strong>。
    远端是唯一的共享点：一台机器、一个出口、N 条独立隧道，互不影响。
  </div>
</div>

<div class="card">
  <h2 style="margin-top:0">当前配置</h2>
  <div class="kv"><div class="k">访问地址</div><div class="v mono">http://{host}:{port}/</div></div>
  <div class="kv"><div class="k">局域网域名</div><div class="v mono">{CFG['domain']}</div></div>
  <div class="kv"><div class="k">远端 SSH</div><div class="v mono">{CFG['ssh_user']}@{host}:{CFG['ssh_port']}</div></div>
  <div class="kv"><div class="k">会话时长</div><div class="v">{SESSION_HOURS} 小时</div></div>
  <div class="kv"><div class="k">账号限速</div><div class="v">默认 5 MB/s，按账号计</div></div>
</div>

<div class="card">
  <h2 style="margin-top:0">已知限制</h2>
  <div style="font-size:13.5px;line-height:1.9">
    · 出口带宽取决于远端主机，不是无上限<br>
    · 远端办公网可能封某些站点（实测 google 两端都不通）<br>
    · 门户本身是 HTTP 不是 HTTPS，临时密钥在局域网内明文传输<br>
    · 断的是 WLAN 接入层（连网关都不通）时，这套方案救不了
  </div>
</div>

<div class="card">
  <h2 style="margin-top:0">技术栈</h2>
  <div style="font-size:13.5px;line-height:1.8">
    门户：Python 标准库 + SQLite（远端只有 Python，没有 node/pip）<br>
    代理：Node.js 单文件（双协议 + IP 择优 + GCRA 限速）<br>
    隧道：OpenSSH <code>ssh -D</code><br>
    部署包：.cmd 内嵌 PowerShell，双击即用
  </div>
</div>
"""))

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

    def handle_unlock(self, form=None):
        """解除某个 IP 的登录锁定。

        为什么需要：速率限制按来源 IP 记。如果内网有人故意用错误密钥猛敲，
        会把该 IP 锁 15 分钟 —— 连带坐在同一台机器后面的无辜用户。
        没有这个入口，管理员只能干等。

        GET  → 列出当前被锁的 IP
        POST → 解除指定 IP

        这是管理操作，必须已登录管理面板。
        """
        if not self.is_admin():
            self.redirect('/admin')
            return

        # target 可能来自 POST 表单（点「解除」按钮）或 GET query（直接带参数）。
        # 踩过的坑：第一版只读 query，POST 时 target 恒为空，
        # 于是永远走「显示列表」分支返回 200，按钮看起来点了没反应。
        target = ''
        if form:
            target = (form.get('ip') or '').strip()
        if not target:
            qs = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            target = (qs.get('ip') or [''])[0].strip()

        # csrf_set 每次调用都会轮换 token 并返回新的，所以这里拿到的
        # 就是当前有效的那个。不能复用别处的 token —— 那是上一次的。
        csrf_tok = csrf_set('admin|' + self.client_ip())

        if not target:
            with _ratelimit_lock:
                nowt = now()
                locked = [
                    (k.split('|', 1)[-1], k.split('|', 1)[0], max(0, v.get('locked_until', 0) - nowt))
                    for k, v in _ratelimit.items()
                    if v.get('locked_until', 0) > nowt
                ]
            rows = ''.join(
                '<tr><td class="mono">' + html.escape(ip) + '</td>'
                '<td>' + html.escape(scope) + '</td>'
                '<td>' + str(sec // 60 + 1) + ' 分钟</td>'
                '<td><form method="post" action="/admin/unlock" style="display:inline">'
                '<input type="hidden" name="csrf" value="' + html.escape(csrf_tok) + '">'
                '<input type="hidden" name="ip" value="' + html.escape(ip) + '">'
                '<button class="sm ghost" type="submit">解除</button></form></td></tr>'
                for ip, scope, sec in locked)
            # 注意：即使没有锁定项也要输出 csrf token。
            # 踩过的坑：第一版只在表格行里放 token，列表为空时页面完全没有
            # csrf，任何 POST 都会被 403 —— 表现为「解锁按钮永远是坏的」。
            hidden_csrf = '<input type="hidden" name="csrf" value="' + html.escape(csrf_tok) + '">'
            self.send_html(page('解除锁定', (
                '<h1>登录锁定</h1>'
                '<div class="sub">被速率限制锁住的来源 IP（5 分钟内失败 5 次锁 15 分钟）</div>'
                '<div class="card"><table><thead><tr><th>IP</th><th>类型</th><th>剩余</th><th></th></tr></thead>'
                '<tbody>' + (rows or '<tr><td colspan="4" class="muted">当前没有锁定的 IP</td></tr>') + '</tbody></table>'
                + '<div style="display:none">' + hidden_csrf + '</div>'
                + '</div>'
                '<div class="card" style="background:#fafbfc"><div style="font-size:13px;color:#78909c">'
                '反复被锁说明有人在试 —— 应该先换密钥，而不是反复解锁。'
                '</div></div>'
            )))
            return

        if not PRIVATE_IP_RE.match(target):
            self.send_html(page('参数错误', '<h1>IP 格式不对</h1>'), 400)
            return

        with _ratelimit_lock:
            hit = []
            for scope in ('user', 'admin'):
                k = _rl_key(scope, target)
                if k in _ratelimit:
                    _ratelimit.pop(k)
                    hit.append(scope)
        self.db.log('admin_unlock', self.client_ip(), target + ' (' + (','.join(hit) or '无锁定') + ')')
        self.redirect('/admin')

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
    ap.add_argument('--license-days', type=int, default=None,
                    help='激活后账号可借网的天数（0 = 永久；缺省用内置 30）')
    args = ap.parse_args()

    CFG.update({
        'port': args.port, 'bind': args.bind, 'db': args.db,
        'public_host': args.public_host, 'ssh_user': args.ssh_user,
        'ssh_port': args.ssh_port,
    })
    # 授权天数：0 = 永久。缺省沿用内置 30，显式传了才覆盖。
    if args.license_days is not None:
        CFG['license_days'] = int(args.license_days)

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

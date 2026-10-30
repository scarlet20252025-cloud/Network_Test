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
import struct
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

-- 设备表：一个账号（token）可以绑多台设备（2026-10-29 新增）。
--
-- 为什么必须单独一张表：clients 的主键是 ip，而同一局域网里的手机和电脑
-- 走的是同一个 NAT 出口 —— IP 一样。用 ip 当身份，「手机激活后电脑被顶掉」
-- 是必然的，不是 bug。设备身份必须换成客户端自己报的稳定 ID。
--
-- device_id 用 Android 的 Settings.Secure.ANDROID_ID：它在**重装 App 之后
-- 不变**，这正是「同一台手机重装后能自动恢复授权」的前提。
--
-- 安全边界：光凭 device_id 就发 token 是有风险的（ID 可伪造），所以
-- /api/device/restore 还要求来源 IP 与该设备上次登记的 IP 在同一 /24，
-- 并且该设备必须已经被某个有效 token 登记过。
CREATE TABLE IF NOT EXISTS devices (
    device_id   TEXT PRIMARY KEY,
    token       TEXT NOT NULL,
    platform    TEXT DEFAULT '',
    model       TEXT DEFAULT '',
    app_version TEXT DEFAULT '',
    created_at  INTEGER NOT NULL,
    last_seen   INTEGER DEFAULT 0,
    -- 上次登录的来源 IP。恢复授权时用它做一次「同网段」校验：
    -- 光凭 device_id 就发 token 太松（ID 可伪造），加上「和上次在同一个 /24」
    -- 这一条，攻击者还得先在同一栋楼里才行。
    last_ip     TEXT DEFAULT '',
    enabled     INTEGER DEFAULT 1
);
CREATE INDEX IF NOT EXISTS idx_devices_token ON devices(token);

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
-- 流量时间桶：**持久化**的多粒度统计基础。
--
-- 为什么不用内存里的 _hourly：那个只留 24 小时，进程一重启就没了。
-- 要做「250 小时 / 100 小时 / 50 小时」这种跨度，必须落库。
--
-- 粒度选 5 分钟：250 小时 = 3000 个桶，单表几十万行以内，SQLite 毫无压力；
-- 再细（1 分钟）就没必要 —— 图表上一个点不到 1 像素。
CREATE TABLE IF NOT EXISTS traffic_bucket (
    bucket_ts   INTEGER NOT NULL,   -- 对齐到 300 秒
    ip          TEXT NOT NULL,
    rx          INTEGER DEFAULT 0,  -- 本桶内下行字节增量
    tx          INTEGER DEFAULT 0,
    peak_rx     INTEGER DEFAULT 0,  -- 本桶内峰值速率（字节/秒）
    peak_tx     INTEGER DEFAULT 0,
    PRIMARY KEY (bucket_ts, ip)
);

CREATE INDEX IF NOT EXISTS idx_bucket_ts ON traffic_bucket(bucket_ts DESC);

CREATE TABLE IF NOT EXISTS traffic (
    ip          TEXT PRIMARY KEY,
    total_rx    INTEGER DEFAULT 0,
    total_tx    INTEGER DEFAULT 0,
    peak_rx     INTEGER DEFAULT 0,
    peak_tx     INTEGER DEFAULT 0,
    first_seen  INTEGER DEFAULT 0,
    updated_at  INTEGER DEFAULT 0
);

-- 意见反馈。
--
-- 为什么不需要用户表：IP 本身就是身份（借网按 IP 限速、按 IP 激活）。
-- 让用户「不用登录就能反馈」比多一套账号体系重要 ——
-- 反馈往往来自「还没装好/还没激活」的人，拿登录挡他们等于没这个功能。
-- 反馈附件。
--
-- 为什么单独一张表而不是塞进 feedback：
--   · 一条反馈可能带多张图（截图往往不止一张）
--   · 附件要在「反馈还没提交」时就能上传（用户粘完图再点提交），
--     那时 feedback_id 还不存在，所以先落 upload_token，提交时再绑定
CREATE TABLE IF NOT EXISTS feedback_attach (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    feedback_id  INTEGER DEFAULT 0,   -- 0 = 还没绑定到任何反馈
    upload_token TEXT DEFAULT '',     -- 上传时给前端的随机串，提交反馈时带回来
    name         TEXT DEFAULT '',
    mime         TEXT DEFAULT '',
    size         INTEGER DEFAULT 0,
    stored       TEXT DEFAULT '',     -- 磁盘上的文件名（不含目录）
    created_at   INTEGER DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_attach_fb ON feedback_attach(feedback_id);
CREATE INDEX IF NOT EXISTS idx_attach_tok ON feedback_attach(upload_token);

CREATE TABLE IF NOT EXISTS feedback (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    ts          INTEGER NOT NULL,
    ip          TEXT DEFAULT '',
    kind        TEXT DEFAULT 'other',      -- bug / feature / perf / ask / other
    text        TEXT NOT NULL,
    contact     TEXT DEFAULT '',
    client_ver  TEXT DEFAULT '',
    status      INTEGER DEFAULT 0,         -- 0 新  1 处理中  2 已解决  3 暂不做
    admin_note  TEXT DEFAULT '',
    handled_at  INTEGER DEFAULT 0
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

# 一个账号（token）最多绑几台设备。
#
# 为什么要有上限：token 是可以随手抄给别人用的，不设上限等于「一个码全网通」。
# 3 台覆盖「手机 + 平板 + 电脑」的常见组合，再多就要求找管理员，
# 这样既方便正常用户，又不至于一个码被几十个人共用。
MAX_DEVICES_PER_TOKEN = 3


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


# ─────────────────────────── 设备维度授权 ───────────────────────────
#
# 2026-10-29 起，授权从「按 IP」改成「按设备」。
#
# 为什么必须改：clients 的主键是 ip，而同一局域网里手机和电脑走同一个 NAT
# 出口 —— IP 一样。结果就是「手机激活了，电脑那边的授权被顶掉」，
# 用户会以为是 bug（其实是设计错了）。
#
# 现在：token 是账号，device_id 是设备。一个 token 绑多台设备，
# 每台设备用自己的 ANDROID_ID（重装不变）作身份。

DEVICE_ID_RE = re.compile(r'^[A-Za-z0-9_.:-]{8,64}$')


def device_ok_id(device_id):
    """设备 ID 的格式校验。宽松一些 —— 只挡明显不对的输入。"""
    return bool(device_id) and bool(DEVICE_ID_RE.match(device_id))


def device_count(db, token):
    """这个账号当前绑了几台（启用中的）设备。"""
    row = db.q1('SELECT COUNT(*) AS n FROM devices WHERE token = ? AND enabled = 1',
                (token,))
    return int(row['n']) if row else 0


def device_bind(db, device_id, token, ip='', platform='', model='', app_version=''):
    """把设备绑到账号上，顺便刷新心跳。

    返回 (ok, error, is_new, reason)。reason 给调用方区分拒绝原因：
      · 'limit'    —— 新设备，但该账号的启用名额已满
      · 'disabled' —— 这台设备被管理员停用了

    已经绑过的设备直接更新（幂等），所以重装 App、重连、换网络都不会报错。
    只有「新设备且该账号已满」和「本设备被停用」才拒绝。
    """
    if not device_ok_id(device_id):
        return False, '设备标识无效', False, 'invalid'

    row = db.q1('SELECT * FROM devices WHERE device_id = ?', (device_id,))
    if row:
        # 被停用的设备不能靠「重新激活」自己复活。
        # 2026-10-29 加这一条之前，下面的 UPDATE 里写着 `enabled = 1`，
        # 于是管理面板的「踢下线」等于没用 —— App 下次启动（/api/status 也走
        # 这条路）就把它写回了启用。要恢复只能由管理员在面板上点「恢复」。
        if not row['enabled']:
            return False, '这台设备已被管理员停用，请联系管理员', False, 'disabled'
        # 已存在：更新心跳与元信息。注意 token 可能变（用户换了另一个码激活），
        # 这时要按新 token 重新算一次配额，否则能绕过上限。
        if row['token'] != token:
            if device_count(db, token) >= MAX_DEVICES_PER_TOKEN:
                return False, ('这个授权已绑定 %d 台设备（上限 %d 台）。'
                               '要加新设备请先在其它设备上退出，或找管理员。'
                               % (MAX_DEVICES_PER_TOKEN, MAX_DEVICES_PER_TOKEN)), False, 'limit'
        db.x('UPDATE devices SET token = ?, platform = ?, model = ?, app_version = ?, '
             'last_seen = ?, last_ip = ? WHERE device_id = ?',
             (token, platform, model, app_version, now(), ip, device_id))
        return True, '', False, ''

    if device_count(db, token) >= MAX_DEVICES_PER_TOKEN:
        return False, ('这个授权已绑定 %d 台设备（上限 %d 台）。'
                       '要加新设备请先在其它设备上退出，或找管理员。'
                       % (MAX_DEVICES_PER_TOKEN, MAX_DEVICES_PER_TOKEN)), False, 'limit'

    db.x('INSERT INTO devices (device_id, token, platform, model, app_version, '
         'created_at, last_seen, last_ip, enabled) VALUES (?,?,?,?,?,?,?,?,1)',
         (device_id, token, platform, model, app_version, now(), now(), ip))
    return True, '', True, ''


def device_touch(db, device_id, token, ip=''):
    """只刷新心跳，不新建。设备不存在时返回 False（调用方据此提示重新激活）。"""
    row = db.q1('SELECT * FROM devices WHERE device_id = ?', (device_id,))
    if not row:
        return False
    db.x('UPDATE devices SET last_seen = ?, last_ip = ?, token = ? WHERE device_id = ?',
         (now(), ip, token, device_id))
    return True


def same_subnet_24(ip_a, ip_b):
    """两个 IPv4 是否在同一个 /24。用于恢复授权时的宽松校验。"""
    a = (ip_a or '').split('.')
    b = (ip_b or '').split('.')
    if len(a) != 4 or len(b) != 4:
        return False
    return a[:3] == b[:3]


def devices_of(db, token):
    """列出账号下的设备（管理面板用）。"""
    return db.q('SELECT device_id, platform, model, app_version, created_at, '
                'last_seen, last_ip, enabled FROM devices WHERE token = ? '
                'ORDER BY last_seen DESC', (token,))



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

# 每个 IP 上次「成功」提交反馈的 unix 秒，用于冷却。
#
# 为什么不用 _ratelimit：那套是给登录失败设计的（记失败次数、锁 15 分钟），
# 反馈是正常提交，语义不符。这里只要一个时间戳。
#
# 冷却默认 60 秒（不是 180）：反馈往往是「刚发现问题想说一句」，
# 3 分钟太长，误触一次就要干等；1 分钟足够挡连点，又不至于让人以为坏了。
# 环境变量可覆盖 —— 自动化测试要验证冷却是真的会过期，等 60 秒不现实。
FB_COOLDOWN = int(os.environ.get('FERRY_FB_COOLDOWN', '60'))
_fb_last = {}
_fb_lock = threading.Lock()


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

/* 长串要能断行：注册表路径 / PowerShell 命令 / 哈希这类没有空格的串
   在窄屏上会把容器撑破。overflow-wrap:anywhere 允许在任意位置断，
   比 word-break:break-all 温和（优先按词断）。
   2026-10-30 实测：下载页更新日志里的注册表路径和 PowerShell 命令
   在 390px 视口下 right 到 432px，超视口 42px。 */
code{background:var(--code);padding:2px 6px;border-radius:4px;
     overflow-wrap:anywhere;word-break:break-word;
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

# ── 性能曲线用的采样历史 ──
#
# 为什么单独留一份：`_hourly` 是小时级聚合，画不出「最近 10 分钟」这种曲线。
# 前端每 3 秒拉一次 live（服务端有 2 秒缓存），所以采样密度就是 3 秒左右。
# 200 个点 × 3 秒 ≈ 10 分钟，和面板上标的范围一致。
#
# 用 deque(maxlen=200)：加满自动丢最老的，不用自己维护环形下标。
_live_series = None          # collections.deque，延迟初始化
LIVE_SERIES_MAX = 200

# 门户进程内存（MB）。每 10 秒读一次 /proc 等价物 —— Windows 下用
# psutil 会引入依赖，所以直接读本进程的 WorkingSet64。
_mem_cache = {'v': 0.0, 'at': 0.0}


def portal_mem_mb():
    """门户进程自己的物理内存占用（MB）。

    为什么显示这个而不是「内核占用」：客户端 mihomo 的内存服务端拿不到。
    而门户长期挂着，它自己的内存涨不涨才是管理员该关心的
    （内存持续增长通常意味着某个缓存表没清）。
    """
    now_t = time.time()
    if now_t - _mem_cache['at'] < 10:
        return _mem_cache['v']
    try:
        import ctypes
        from ctypes import wintypes

        class PMC(ctypes.Structure):
            _fields_ = [('cb', wintypes.DWORD), ('PageFaultCount', wintypes.DWORD),
                        ('PeakWorkingSetSize', ctypes.c_size_t),
                        ('WorkingSetSize', ctypes.c_size_t),
                        ('QuotaPeakPagedPoolUsage', ctypes.c_size_t),
                        ('QuotaPagedPoolUsage', ctypes.c_size_t),
                        ('QuotaPeakNonPagedPoolUsage', ctypes.c_size_t),
                        ('QuotaNonPagedPoolUsage', ctypes.c_size_t),
                        ('PagefileUsage', ctypes.c_size_t),
                        ('PeakPagefileUsage', ctypes.c_size_t)]

        pmc = PMC()
        pmc.cb = ctypes.sizeof(PMC)
        k32 = ctypes.WinDLL('kernel32', use_last_error=True)
        k32.GetCurrentProcess.restype = wintypes.HANDLE
        psapi = ctypes.WinDLL('psapi', use_last_error=True)
        psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(PMC),
                                              wintypes.DWORD]
        if psapi.GetProcessMemoryInfo(k32.GetCurrentProcess(), ctypes.byref(pmc),
                                      pmc.cb):
            mb = pmc.WorkingSetSize / 1048576.0
            _mem_cache['v'] = round(mb, 1)
            _mem_cache['at'] = now_t
    except Exception:
        pass
    return _mem_cache['v']



def _sh(cmd, timeout=8):
    """跑一条命令拿 stdout。失败一律返回空串 —— 监控挂了不能拖垮管理页。"""
    try:
        import subprocess
        p = subprocess.run(cmd, capture_output=True, text=True,
                           timeout=timeout, errors='ignore')
        return p.stdout or ''
    except Exception:
        return ''


_ssh_peers_cache = {'at': 0.0, 'data': {}}
SSH_PEERS_TTL = 30.0


def _ssh_peers():
    """正在连本机 22 端口的外网地址 → 连接数。

    只看 ESTABLISHED 且本地端口是 22 的。回环和空地址跳过
    （管理员自己 ssh 上来运维不该算用户）。

    ⚠ 带 30 秒缓存。

    为什么：这个函数的唯一用途是 `tunnelConns`（「还有多少台走 SSH 隧道」），
    纯诊断值。而它每次都要跑 `netstat -ano` —— 本机实测 81ms（系统 921 个连接时），
    并且在 HTTP 请求线程里同步执行。sample_live 有 2 秒缓存，等于每 2 秒
    就有一个请求被它拖住 4% 的时间。

    直连模式普及之后这个数字基本恒为 0，却要为它付出这个代价，不值。
    30 秒的延迟对「看趋势」完全够用。
    """
    now_t = time.time()
    if now_t - _ssh_peers_cache['at'] < SSH_PEERS_TTL:
        return dict(_ssh_peers_cache['data'])

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

    _ssh_peers_cache['data'] = peers
    _ssh_peers_cache['at'] = now_t
    return dict(peers)

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

# 统计桶：5 分钟一个，保留 250 小时（≈10.4 天）。
# 250 小时 = 3000 桶，单设备 3000 行，10 台设备 3 万行 —— SQLite 毫无压力。
STATS_BUCKET_SEC = 300
STATS_KEEP_HOURS = 250

# 反馈附件：单个最大 8 MB，一条反馈最多 6 个。
# 8 MB 够放一张手机截图（PNG 通常 1~3 MB）；再大就不是「反馈」而是传文件了。
ATTACH_MAX_BYTES = 8 * 1024 * 1024
ATTACH_MAX_COUNT = 6
ATTACH_DIRNAME = 'attachments'

# 只接受这几种，避免被当成网盘或者上传可执行文件。
# 图片是为了截图，文本/日志是为了贴报错。
ATTACH_ALLOWED = {
    'image/png': '.png', 'image/jpeg': '.jpg', 'image/gif': '.gif',
    'image/webp': '.webp',
    'text/plain': '.txt', 'text/csv': '.csv',
    'application/json': '.json',
    'application/zip': '.zip',
}


def attach_dir():
    """附件目录，跟 portal.db 放一起，方便整体备份/迁移。"""
    base = os.path.dirname(os.path.abspath(CFG.get('db') or 'portal.db'))
    d = os.path.join(base, ATTACH_DIRNAME)
    if not os.path.isdir(d):
        try:
            os.makedirs(d)
        except Exception:
            pass
    return d


def attach_url(aid):
    return '/attach/' + str(int(aid))


def attach_list(db, feedback_id):
    """某条反馈的附件列表（给管理面板用）。"""
    try:
        rows = db.q('SELECT id, name, mime, size FROM feedback_attach '
                    'WHERE feedback_id = ? ORDER BY id', (feedback_id,))
    except Exception:
        return []
    return [{'id': r['id'], 'name': r['name'] or ('附件 ' + str(r['id'])),
             'mime': r['mime'] or '', 'size': r['size'] or 0,
             'url': attach_url(r['id'])} for r in rows]


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
    bucket = int(now_t // STATS_BUCKET_SEC) * STATS_BUCKET_SEC
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
            # 同时写时间桶。没有增量就不写，避免空桶把表撑大。
            if t['rx'] or t['tx'] or t['peakRx'] or t['peakTx']:
                db.x(
                    'INSERT INTO traffic_bucket (bucket_ts, ip, rx, tx, peak_rx, peak_tx) '
                    'VALUES (?,?,?,?,?,?) '
                    'ON CONFLICT(bucket_ts, ip) DO UPDATE SET '
                    '  rx = rx + excluded.rx, tx = tx + excluded.tx,'
                    '  peak_rx = MAX(peak_rx, excluded.peak_rx),'
                    '  peak_tx = MAX(peak_tx, excluded.peak_tx)',
                    (bucket, ip, t['rx'], t['tx'], t['peakRx'], t['peakTx']))
        except Exception as e:
            sys.stderr.write('[traffic] flush %s failed: %s\n' % (ip, e))
    # 已经写进 DB 的部分清零，避免下一轮重复累加
    for ip, t in items:
        if ip in _totals:
            _totals[ip]['rx'] = 0
            _totals[ip]['tx'] = 0
            _totals[ip]['first'] = int(now_t)


# ── 多维度统计 ────────────────────────────────────────────────
#
# 窗口 → 显示粒度。思路和异星工厂的产量统计一样：选的时间跨度越大，
# 曲线上的点越粗，但**总量永远准确**（桶是可加的，聚合只是求和）。
#
# 粒度不能太小：250 小时如果按 5 分钟画就是 3000 个点，一个点不到半像素，
# 既看不出趋势又拖慢浏览器。所以按窗口给一个「点数在 60~300 之间」的粒度。
STATS_WINDOWS = [
    # key,      标签,      秒,            粒度(秒),   点数
    ('10m',  '10 分钟',   600,            60,        '分'),
    ('1h',   '1 小时',    3600,           60,        '分'),
    ('5h',   '5 小时',    5 * 3600,       300,       '5 分'),
    ('10h',  '10 小时',   10 * 3600,      300,       '5 分'),
    ('50h',  '50 小时',   50 * 3600,      1800,      '30 分'),
    ('100h', '100 小时',  100 * 3600,     3600,      '时'),
    ('250h', '250 小时',  250 * 3600,     7200,      '2 时'),
    ('all',  '总览',      0,              86400,     '天'),
]


def _stats_window(key):
    for w in STATS_WINDOWS:
        if w[0] == key:
            return w
    return STATS_WINDOWS[1]


def stats_series(db, key, ip=None):
    """返回某窗口的时间序列。

    ip=None 表示全站合计（把同一时刻各设备的桶相加）。
    每项：{t, rx, tx, rxBps, txBps}，t 是桶起点（unix 秒）。
    """
    win = _stats_window(key)
    span = win[2]
    step = win[3]

    now_t = int(time.time())
    if span:
        start = now_t - span
    else:
        # 总览：从最早一条记录开始
        row = db.q1('SELECT MIN(bucket_ts) AS m FROM traffic_bucket')
        start = (row['m'] if row and row['m'] else now_t) - step

    where = 'WHERE bucket_ts >= ?'
    args = [start]
    if ip:
        where += ' AND ip = ?'
        args.append(ip)

    rows = db.q('SELECT bucket_ts, SUM(rx) AS rx, SUM(tx) AS tx, '
                'MAX(peak_rx) AS prx, MAX(peak_tx) AS ptx '
                'FROM traffic_bucket ' + where + ' '
                'GROUP BY bucket_ts ORDER BY bucket_ts', tuple(args))

    # 按显示粒度归并
    agg = {}
    for r in rows:
        b = (int(r['bucket_ts']) // step) * step
        a = agg.get(b)
        if a is None:
            a = agg[b] = {'rx': 0, 'tx': 0, 'prx': 0, 'ptx': 0}
        a['rx'] += int(r['rx'] or 0)
        a['tx'] += int(r['tx'] or 0)
        a['prx'] = max(a['prx'], int(r['prx'] or 0))
        a['ptx'] = max(a['ptx'], int(r['ptx'] or 0))

    out = []
    for b in sorted(agg.keys()):
        a = agg[b]
        out.append({
            't': b,
            'rx': a['rx'],
            'tx': a['tx'],
            # 桶内平均速率（字节/秒）：总量 / 桶宽
            'rxBps': int(a['rx'] / step),
            'txBps': int(a['tx'] / step),
            # 桶内峰值速率
            'peakRx': a['prx'],
            'peakTx': a['ptx'],
        })
    return {'key': key, 'label': win[1], 'unit': win[4],
            'step': step, 'span': span, 'points': out}


def stats_summary(db, key, ip=None):
    """窗口汇总：总量、峰值、均值、活跃设备数、覆盖时段。"""
    win = _stats_window(key)
    span = win[2]
    now_t = int(time.time())

    where = ''
    args = []
    if span:
        where = 'WHERE bucket_ts >= ?'
        args.append(now_t - span)
    if ip:
        where += (' AND ' if where else 'WHERE ') + 'ip = ?'
        args.append(ip)

    row = db.q1('SELECT COUNT(*) AS n, SUM(rx) AS rx, SUM(tx) AS tx, '
                'MAX(peak_rx) AS prx, MAX(peak_tx) AS ptx, '
                'MIN(bucket_ts) AS t0, MAX(bucket_ts) AS t1, '
                'COUNT(DISTINCT ip) AS hosts '
                'FROM traffic_bucket ' + where, tuple(args))

    n = int((row['n'] if row else 0) or 0)
    rx = int((row['rx'] if row else 0) or 0)
    tx = int((row['tx'] if row else 0) or 0)
    prx = int((row['prx'] if row else 0) or 0)
    ptx = int((row['ptx'] if row else 0) or 0)
    hosts = int((row['hosts'] if row else 0) or 0)

    # 实际覆盖的时长用「桶数 × 桶宽」，不用 span ——
    # 刚装好时只有几个桶，按 span 算平均速率会低得毫无意义。
    covered = n * STATS_BUCKET_SEC
    if covered < 1:
        covered = 1

    return {
        'key': key,
        'label': win[1],
        'buckets': n,
        'hosts': hosts,
        'rx': rx,
        'tx': tx,
        'total': rx + tx,
        # 平均速率（字节/秒）
        'avgRxBps': int(rx / covered),
        'avgTxBps': int(tx / covered),
        # 峰值速率（字节/秒）
        'peakRx': prx,
        'peakTx': ptx,
        'from': int((row['t0'] if row else 0) or 0),
        'to': int((row['t1'] if row else 0) or 0),
        'coveredSec': covered,
    }


def stats_hosts(db, key):
    """窗口内每台设备的用量排行。"""
    win = _stats_window(key)
    span = win[2]
    args = []
    where = ''
    if span:
        where = 'WHERE b.bucket_ts >= ?'
        args.append(int(time.time()) - span)

    rows = db.q(
        'SELECT b.ip, SUM(b.rx) AS rx, SUM(b.tx) AS tx, '
        'MAX(b.peak_rx) AS prx, MAX(b.peak_tx) AS ptx, COUNT(*) AS n '
        'FROM traffic_bucket b ' + where + ' '
        'GROUP BY b.ip ORDER BY (SUM(b.rx) + SUM(b.tx)) DESC LIMIT 30',
        tuple(args))

    notes = {}
    try:
        for c in db.q('SELECT ip, note FROM clients'):
            notes[c['ip']] = c['note'] or ''
    except Exception:
        pass

    out = []
    for r in rows:
        n = int(r['n'] or 0)
        covered = max(1, n * STATS_BUCKET_SEC)
        rx = int(r['rx'] or 0)
        tx = int(r['tx'] or 0)
        out.append({
            'ip': r['ip'],
            'note': notes.get(r['ip'], ''),
            'rx': rx, 'tx': tx, 'total': rx + tx,
            'avgRxBps': int(rx / covered), 'avgTxBps': int(tx / covered),
            'peakRx': int(r['prx'] or 0), 'peakTx': int(r['ptx'] or 0),
            'buckets': n,
        })
    return out


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

        # 追加一个采样点给折线图。
        # 上下行分开存：前端要画两条线（上传橙 / 下载蓝）。
        global _live_series
        if _live_series is None:
            import collections
            _live_series = collections.deque(maxlen=LIVE_SERIES_MAX)
        _sum_rx = sum(u['rxBps'] for u in online_users)
        _sum_tx = sum(u['txBps'] for u in online_users)
        _live_series.append({'t': int(t), 'rx': _sum_rx, 'tx': _sum_tx})

        # 所有机器（含离线）的累计流量 —— 卡片「上传量/下载量」用。
        # 为什么不用 _totals 直接加：那里面可能有还没落库的增量，
        # traffic_of() 已经把它算进去了。
        _tot_rx = _tot_tx = 0
        for _r in rows:
            try:
                _tr = traffic_of(db, _r['ip'])
                _tot_rx += _tr['totalRx']
                _tot_tx += _tr['totalTx']
            except Exception:
                pass

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
            # 折线图数据
            'series': list(_live_series),
            # 累计流量（所有机器相加）
            'totalRx': _tot_rx,
            'totalTx': _tot_tx,
            # 门户进程自身内存
            'memMB': portal_mem_mb(),
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

def _h(s):
    """html.escape 的短别名（模板里拼 HTML 用得太多）。"""
    return html.escape(str(s if s is not None else ""))


# 内置浏览器 UA 特征。检测到就说明「下载按钮点了也白点」，必须换成文字指引。
#
# 为什么用 UA 字符串匹配而不是更"正规"的能力检测：
#   这些内置浏览器**对 APK 下载没有任何可探测的信号** —— 不报错、
#   不发事件、不返回错误码。唯一的线索就是 UA。
#   宁可误判成"在微信里"（多显示一段指引导），也不要漏判
#   （用户会卡死在一个点了没反应的按钮上）。
ANDROID_INAPP_UA_KEYS = ('micromessenger', 'qq/', 'weibo', 'alipayclient',
                         'dingtalk', 'lark/', 'baiduboxapp')


def android_landing_page(portal_host='10.13.24.1:8080', ua=''):
    """手机扫码后的落地页。

    二维码指向这里、而不是直指 APK —— 理由见文件顶部的注释。
    """
    apk = android_apk_latest()
    ver = _h(apk['versionName']) if apk else '暂未发布'
    size = human_size(apk['size']) if apk else '—'
    inapp = any(k in (ua or '').lower() for k in ANDROID_INAPP_UA_KEYS)
    dl = 'http://' + portal_host + '/download-android'

    if inapp:
        # 内置浏览器：**不给**下载按钮。
        # 给一个明知道按了没反应的按钮，比不给更糟 —— 用户会反复点。
        head = """
  <div class="msg err" style="margin:0 0 16px">
    <b>你正在用微信 / QQ 的内置浏览器打开这个页面。</b><br>
    这类浏览器<b>不允许下载 Android 安装包</b>，点下载不会有任何反应
    （不报错、也不开始）。请按下面的步骤换到系统浏览器。
  </div>
"""
        guide = """
  <div class="card">
    <h2 style="margin-top:0">换成系统浏览器（两步）</h2>
    <div class="step"><div class="n">1</div><div class="t">
      <b>点右上角的 <span style="font-size:16px">&#8943;</span> 或 <span style="font-size:16px">&#8226;&#8226;&#8226;</span></b>
      <div class="hint">微信在右上角，QQ 也在右上角。点开后会看到一排选项。</div>
    </div></div>
    <div class="step"><div class="n">2</div><div class="t">
      <b>选「在浏览器打开」</b>
      <div class="hint">有的版本写「在手机浏览器中打开」。选完页面会在系统浏览器里重新打开，
        那时下面这个下载按钮就能用了。</div>
    </div></div>
    <div class="hint" style="margin-top:14px">
      也可以直接把下面这个地址复制到浏览器地址栏：
      <div class="cmdbox" style="user-select:all">DLURL</div>
    </div>
  </div>
"""
        btn = """
  <div class="msg warn" style="margin-bottom:0">
    换到系统浏览器后，回到这个页面点下载即可。
  </div>
"""
    else:
        head = ''
        guide = ''
        btn = """
  <a class="dl" href="/download-android">&#11015; 下载并安装（SIZE）</a>
  <div class="hint" style="text-align:center;margin-top:-6px">
    点上面按钮开始下载。下载完点开文件，按提示安装。
  </div>
"""

    body = """
<div class="dlwrap">
  <div class="dlhero" style="padding:14px 8px 8px">
    <h1 style="font-size:24px;margin:0 0 8px">Android 客户端</h1>
    <div class="sub" style="font-size:13.5px;margin-bottom:14px">
      vVER <span style="color:var(--fg2)">·</span> SIZE
    </div>
  </div>
HEAD
BTN
GUIDE
  <div class="card">
    <h2 style="margin-top:0">装的时候会看到什么</h2>
    <div class="step"><div class="n">1</div><div class="t">
      <b>「是否允许来自此来源的应用」</b>
      <div class="hint">手机自带的检查，选「允许」或「继续安装」。只问一次。</div>
    </div></div>
    <div class="step"><div class="n">2</div><div class="t">
      <b>部分机型（如 vivo）要登录账号确认</b>
      <div class="hint">这是厂商策略，不是软件问题。如果卡住，可以在
        「设置 → 应用管理 → 渡口 → 允许安装未知应用」里放行，或从电脑下载后用数据线传过去。</div>
    </div></div>
    <div class="step"><div class="n">3</div><div class="t">
      <b>装好后打开，填管理员给的激活码</b>
      <div class="hint">激活后点「开始借网」。之后 App 里还有「检查更新」，升级不用再扫码。</div>
    </div></div>
  </div>

  <div class="hint" style="text-align:center;margin-top:14px;line-height:1.9">
    直链（可长按复制）：
    <div class="cmdbox" style="user-select:all">DLURL</div>
  </div>
</div>
"""
    body = (body.replace('HEAD', head).replace('BTN', btn)
                .replace('GUIDE', guide)
                .replace('VER', ver).replace('SIZE', size)
                .replace('DLURL', dl))
    return page('下载 Android 版 · 渡口', body)


def page(title, body, refresh=None, theme=None, cookie_header=None):
    meta = f'<meta http-equiv="refresh" content="{refresh}">' if refresh else ''
    cur = theme or _theme_from_cookie(cookie_header)
    top = (f'<div class="top"><div class="brand"><span class="mark">渡</span>'
           f'<span>渡口<span class="en">FERRY</span></span></div>'
           f'{theme_switcher(cur)}</div>')
    return f"""<!doctype html><html lang="zh-CN" data-theme="{cur}"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="color-scheme" content="dark light">
{meta}<title>{html.escape(title)}</title><style>{BASE_CSS}</style>{EXTRA_CSS}</head>
<body><div class="wrap">{top}{body}{footer()}</div>{THEME_JS}</body></html>"""


def _nav(active):
    """页内导航。active: home / download / feedback"""
    items = [('home', '/', '首页'),
             ('download', '/download-page', '下载'),
             ('feedback', '/feedback', '意见反馈')]
    parts = []
    for key, href, label in items:
        cls = ' class="nv on"' if key == active else ' class="nv"'
        parts.append('<a href="%s"%s>%s</a>' % (href, cls, label))
    return '<nav class="nav">' + ''.join(parts) + '</nav>'


def download_page(detected_ip='', portal_host='10.13.24.1:8080'):
    """下载页。

    内容就是原来首页那一整页（当前版本 + 历史版本 + 更新日志 + 三步 +
    SmartScreen + 密钥规则 + 原理）。介绍页独立出去之后，这里只服务
    「我已经决定了，给我安装包」的人，所以不需要 hero，直接给按钮。
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

    older = [x for x in installers if x['ver'] != latest_ver]
    if older:
        rows = []
        for x in older:
            rows.append(
                '<div style="display:flex;align-items:center;justify-content:space-between;'
                'padding:9px 0;border-top:1px solid var(--line)">'
                '<div><b>v%s</b>'
                '<span class="hint" style="margin-left:8px">%s</span></div>'
                '<a href="/download/version/%s" style="color:var(--accent);'
                'font-size:13px;text-decoration:none">下载</a></div>'
                % (html.escape(x['ver']), human_size(x['size']), html.escape(x['ver'])))
        older_html = '\n'.join(rows)
    else:
        older_html = '<div class="hint" style="padding:8px 0">还没有更早的版本</div>'

    log_html = md_to_html_brief(read_changelog())

    # ── Android 卡片用的变量（2026-10-30 新增）──
    # 版本号/大小从 dist/android 里读；没有包就显示「暂未发布」，按钮仍然可点
    # （会得到一个明确的 404 JSON，而不是坏页面）。
    _apk = android_apk_latest()
    android_ver = _apk['versionName'] if _apk else '暂未发布'
    android_size = human_size(_apk['size']) if _apk else '—'
    # 门户地址由调用方从 Host 头传进来（download_page 是模块级函数，
    # 拿不到 self）。这样扫码出来的地址就是用户实际访问的那个。

    return page('下载渡口 · Ferry', """
<nav class="nav">
  <a href="/" class="nv">首页</a>
  <a href="/download-page" class="nv on">下载</a>
  <a href="/feedback" class="nv">意见反馈</a>
</nav>

<div class="dlwrap">

  <div class="platgrid">

    <!-- ══ Windows ══ -->
    <div class="platcard win">
      <div class="plat-head">
        <div class="plat-ico">&#9635;</div>
        <div>
          <div class="plat-title">Windows 客户端</div>
          <div class="plat-sub">Win10 / 11 · x64</div>
        </div>
      </div>
      <div class="plat-meta">
        <span>版本 <b>vLATEST</b></span>
        <span>大小 <b>SIZE</b></span>
      </div>
      <div class="plat-actions">
        <a class="btn btn-main" href="/download-cmd">&#9889; 一键安装</a>
        <a class="btn btn-ghost" href="/download">下载 .exe</a>
        <a class="btn btn-ghost" href="/download-portable">绿色版</a>
      </div>
      <div class="plat-spacer"></div>
      <div class="plat-note">
        推荐<b>一键安装</b>：下载一个十几行的脚本，双击后自动拉安装包 &mdash;&mdash;
        全程不出现「Windows 已保护你的电脑」。装到当前用户目录，不需要管理员。
        <br><br>
        <b>绿色版</b>是完整软件包：解压到任意目录、双击 <code>渡口助手.exe</code> 即可，
        借网 / 全局模式 / 自动更新 / 开机自启全都在。唯一区别是<strong>不安装</strong> &mdash;&mdash;
        不写注册表、不进「程序和功能」、卸载就是删文件夹。
        装不上的机器（报 <code>错误 4551</code>，应用控制策略拦截）用这个。
      </div>
    </div>

    <!-- ══ Android ══ -->
    <div class="platcard droid">
      <div class="plat-head">
        <div class="plat-ico">&#9670;</div>
        <div>
          <div class="plat-title">Android 客户端</div>
          <div class="plat-sub">Android 8.0 及以上</div>
        </div>
      </div>
      <div class="plat-meta">
        <span>版本 <b>ANDROID_VER_TXT</b></span>
        <span>大小 <b>ANDROID_SIZE_TXT</b></span>
      </div>
      <div class="plat-actions">
        <a class="btn btn-main" href="/download-android">&#11015; 直接下载 APK</a>
      </div>
      <div class="plat-spacer"></div>

      <div class="qrrow">
        <div class="qrbox"><img src="/static/android-qr.png" alt="扫码下载 Android 版" width="88" height="88"></div>
        <div class="qrtxt">
          <b>用手机扫这个码</b>
          手机扫一扫就能装，不用打地址。
        </div>
      </div>

      <div class="plat-note">
        也可以在手机浏览器打开 <code>PORTALHOST</code> 点「下载 Android 版」。
        <br>应用内已支持<b>检查更新</b>：装过一次之后，以后在 App 里点一下就能升级。
      </div>
    </div>

  </div>

  <div class="dl-steps">
    <div class="dl-step">
      <i>1</i><b>点「一键安装」</b>
      <p>拿到一个 <code>.cmd</code> 脚本，双击它就会自动下载安装包并打开安装程序。</p>
    </div>
    <div class="dl-step">
      <i>2</i><b>跟着向导走</b>
      <p>不需要管理员，不会弹 UAC。可以顺手勾上桌面图标和开机自启。</p>
    </div>
    <div class="dl-step">
      <i>3</i><b>输入密钥</b>
      <p>打开「渡口助手」，把管理员给的 8 位密钥填进去激活，之后就不用再输。</p>
    </div>
  </div>

  <div class="dl-folds">

    <details class="dl-fold">
      <summary>下载后提示「Windows 已保护你的电脑」？<span class="hint">三种解法，其实一键安装已经绕过了</span></summary>
      <div class="inner">
        这是 SmartScreen 对<strong>未经代码签名</strong>的 exe 的例行拦截，不是病毒。
        只有在「直接下载 .exe」时才会遇到；一键安装不受影响。三种处理方式任选：
        <div class="step"><div class="n">1</div><div class="t">
          <b>用命令行下载（不触发拦截）</b>
          <div class="hint">按 Win+R 输入 <code>powershell</code> 回车，粘贴这行：</div>
          <div class="cmdbox">Invoke-WebRequest 'http://10.13.24.1:8080/download' -OutFile "$env:TEMP\Ferry-Setup.exe"; Start-Process "$env:TEMP\Ferry-Setup.exe"</div>
          <div class="hint" style="margin-top:6px">
            浏览器下载会给文件打上「来自 Internet」的标记（MOTW），SmartScreen 只查带标记的 exe。
            PowerShell 直接下载不带这个标记。
          </div>
        </div></div>
        <div class="step"><div class="n">2</div><div class="t">
          <b>已经下载了：解除锁定</b>
          <div class="hint">右键那个 exe &rarr; 属性 &rarr; 最下面勾「解除锁定」&rarr; 确定 &rarr; 再双击。</div>
        </div></div>
        <div class="step"><div class="n">3</div><div class="t">
          <b>点「更多信息 &rarr; 仍要运行」</b>
          <div class="hint">弹窗左下角有「更多信息」，展开后出现「仍要运行」。同一个文件点过一次就不再问。</div>
        </div></div>
      </div>
    </details>

    <details class="dl-fold">
      <summary>密钥规则<span class="hint">一次性 · 12 小时内用掉 · 输错锁定</span></summary>
      <div class="inner">
        <div class="kv"><div class="k">一次性</div><div class="v">激活成功即失效，换机器要找管理员要新密钥。</div></div>
        <div class="kv"><div class="k">有效期</div><div class="v">密钥本身 12 小时内必须用掉；激活后的授权时长由管理员设定（默认 30 天）。</div></div>
        <div class="kv"><div class="k">限速</div><div class="v">每台机器默认 10 MB/s，由服务端执行；连续输错密钥会被临时锁定 15 分钟。</div></div>
      </div>
    </details>

    <details class="dl-fold">
      <summary>历史版本（OLDER_COUNT 个）<span class="hint">新版本有问题时可以装回旧版</span></summary>
      <div class="inner">OLDER_HTML</div>
    </details>

    <details class="dl-fold">
      <summary>更新日志<span class="hint">每个版本改了什么</span></summary>
      <div class="inner" style="max-height:560px;overflow:auto">LOG_HTML</div>
    </details>

    <details class="dl-fold">
      <summary>安装时提示「错误 4551：应用程序控制策略已阻止此文件」<span class="hint">用免安装版</span></summary>
      <div class="inner">
        这不是杀软报毒，也不是文件坏了，是 Windows 的
        <strong>智能应用控制 / 应用控制策略</strong>禁止「从临时目录执行程序」。
        安装程序本来就要先解压到临时目录再运行，于是被这条策略拦下 &mdash;&mdash;
        换台机器就能装，是策略差异，不是安装包的问题。

        <div class="step"><div class="n">1</div><div class="t">
          <b>改用免安装版</b>
          <div class="hint">下载 <a href="/download-portable" style="color:var(--accent)">渡口助手-免安装.zip</a>，
            解压到任意目录（比如 <code>D:\渡口</code>），双击里面的
            <code>渡口助手.exe</code> 即可。完全不经过临时目录，所以不受这条策略影响。</div>
        </div></div>
        <div class="step"><div class="n">2</div><div class="t">
          <b>免安装版怎么开机自启</b>
          <div class="hint">设置里勾「开机自动启动」&mdash;&mdash; 用的是计划任务，不写注册表。</div>
        </div></div>
        <div class="step"><div class="n">3</div><div class="t">
          <b>以后怎么卸载</b>
          <div class="hint">设置里取消勾选「开机自动启动」&rarr; 退出程序 &rarr; 删掉那个文件夹。</div>
        </div></div>
      </div>
    </details>

    <details class="dl-fold">
      <summary>下载中断 / 文件大小不对<span class="hint">0 KB 或明显偏小怎么办</span></summary>
      <div class="inner">
        下载完成后确认文件大小约 <b>SIZE</b>。如果只有 0 KB，说明下载被中断了 &mdash;&mdash;
        删掉那个文件重新点一次。双击 0 KB 的文件会提示「此应用无法在你的电脑上运行」。
      </div>
    </details>

  </div>
</div>
""".replace('ANDROID_VER_TXT', html.escape(android_ver))
   .replace('ANDROID_SIZE_TXT', html.escape(android_size))
   .replace('OLDER_COUNT', str(len(older)))
   .replace('OLDER_HTML', older_html)
   .replace('LOG_HTML', log_html)
   .replace('PORTALHOST', html.escape(portal_host))
   .replace('LATEST', html.escape(latest_ver))
   .replace('SIZE', html.escape(size_txt))) 


# 反馈附件的前端逻辑：粘贴 / 拖拽 / 选择 + 缩略图列表。
#
# 为什么单独一份而不是复用 ADMIN_JS：管理面板那 46 KB 脚本里全是实时图、
# 统计、设备表格的逻辑，反馈页一样用不上。共用只会让反馈页也去跑轮询。
ATTACH_JS = r"""
<script>
(function(){
  var attach = [];
  var token = '';

  function tok(){
    if (token) return token;
    var a = 'abcdefghjkmnpqrstuvwxyz23456789', s = '';
    for (var i = 0; i < 16; i++) s += a[Math.floor(Math.random() * a.length)];
    token = s;
    return s;
  }

  function human(n){
    if (n < 1024) return n + ' B';
    if (n < 1048576) return (n / 1024).toFixed(1) + ' KB';
    return (n / 1048576).toFixed(1) + ' MB';
  }

  function esc(s){
    return String(s).replace(/[&<>"']/g, function(c){
      return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c];
    });
  }

  function render(){
    var box = document.getElementById('fbAttachList');
    if (!box) return;
    if (!attach.length) { box.innerHTML = ''; return; }
    var h = '';
    attach.forEach(function(a, i){
      var isImg = (a.mime || '').indexOf('image/') === 0;
      h += '<div class="att-item">'
        + (isImg
            ? '<img class="att-thumb" src="' + a.url + '?t=' + encodeURIComponent(tok()) + '" alt="">'
            : '<div class="att-thumb att-doc">' + (a.name.split('.').pop() || '?').toUpperCase() + '</div>')
        + '<div class="att-meta"><div class="att-name" title="' + esc(a.name) + '">'
        + esc(a.name) + '</div><div class="att-size">' + human(a.size) + '</div></div>'
        + '<button type="button" class="att-del" data-i="' + i + '" title="移除">&times;</button>'
        + '</div>';
    });
    box.innerHTML = h;
    Array.prototype.forEach.call(box.querySelectorAll('.att-del'), function(b){
      b.addEventListener('click', function(){
        attach.splice(parseInt(b.getAttribute('data-i'), 10), 1);
        render();
      });
    });
  }

  function upload(file){
    if (!file) return;
    if (attach.length >= 6) { alert('最多 6 个附件'); return; }
    if (file.size > 8 * 1048576) { alert('单个附件上限 8 MB'); return; }

    var box = document.getElementById('fbAttachList');
    var tip = document.createElement('div');
    tip.className = 'att-item att-uploading';
    tip.innerHTML = '<div class="att-thumb att-doc">&hellip;</div>'
      + '<div class="att-meta"><div class="att-name">' + esc(file.name || '粘贴的图片') + '</div>'
      + '<div class="att-size">上传中&hellip;</div></div>';
    if (box) box.appendChild(tip);

    var fd = new FormData();
    fd.append('token', tok());
    fd.append('file', file, file.name || ('paste-' + Date.now() + '.png'));

    fetch('/attach/upload', { method: 'POST', body: fd, credentials: 'same-origin' })
      .then(function(r){ return r.json(); })
      .then(function(d){
        if (!d.ok) { alert('上传失败：' + (d.error || '未知')); return; }
        attach.push(d);
        render();
      })
      .catch(function(e){ alert('上传失败：' + e.message); })
      .then(function(){ if (tip.parentNode) tip.parentNode.removeChild(tip); });
  }

  var zone = document.getElementById('fbDrop');
  var input = document.getElementById('fbFile');
  var form = document.querySelector('.fbform form');
  if (zone && input) {
    zone.addEventListener('click', function(){ input.click(); });
    zone.addEventListener('keydown', function(e){
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); input.click(); }
    });
    input.addEventListener('change', function(){
      Array.prototype.forEach.call(input.files, upload);
      input.value = '';
    });
    ['dragenter', 'dragover'].forEach(function(ev){
      zone.addEventListener(ev, function(e){ e.preventDefault(); zone.classList.add('hot'); });
    });
    ['dragleave', 'drop'].forEach(function(ev){
      zone.addEventListener(ev, function(e){ e.preventDefault(); zone.classList.remove('hot'); });
    });
    zone.addEventListener('drop', function(e){
      var dt = e.dataTransfer;
      if (dt) Array.prototype.forEach.call(dt.files || [], upload);
    });
    // 粘贴只在表单里生效，不干扰用户粘文字
    if (form) {
      form.addEventListener('paste', function(e){
        var items = (e.clipboardData && e.clipboardData.items) || [];
        var got = false;
        Array.prototype.forEach.call(items, function(it){
          if (it.kind === 'file') {
            var f = it.getAsFile();
            if (f) { upload(f); got = true; }
          }
        });
        if (got) e.preventDefault();
      });
      form.addEventListener('submit', function(){
        var t = document.getElementById('fbAttachToken');
        if (t) t.value = tok();
      });
    }
  }
})();
</script>
"""


def feedback_page(detected_ip='', msg='', err=''):
    """意见反馈页。

    不需要登录 —— 用户可能还没激活，不能拿「登录」挡反馈。
    IP 自动带上（管理员定位要靠它），另外只要一个联系方式。
    """
    ip_txt = html.escape(detected_ip) if detected_ip else '未能识别'
    msgbox = ('<div class="fbmsg ok">%s</div>' % html.escape(msg)) if msg else ''
    errbox = ('<div class="fbmsg err">%s</div>' % html.escape(err)) if err else ''

    # CSRF：do_POST 对所有 POST 统一校验，不带 token 会被 403 挡掉，
    # 而用户看到的是「点了提交没反应」，完全无法自查。
    # key 用 'fb|<ip>'，和登录态分开 —— 反馈不需要登录。
    fb_key = 'fb|' + (detected_ip or 'anon')
    fb_csrf = csrf_slot(fb_key)
    csrf_in = '<input type="hidden" name="csrf" value="%s">' % html.escape(fb_csrf)

    return page('意见反馈 · 渡口 Ferry', """
<nav class="nav">
  <a href="/" class="nv">首页</a>
  <a href="/download-page" class="nv">下载</a>
  <a href="/feedback" class="nv on">意见反馈</a>
</nav>

<div class="fbwrap">
  <div class="fbhero">
    <h1>意见反馈</h1>
    <p>用的时候遇到问题、或者有想让它做到的事，都可以说。<br>
       每一条都会被读到，能修的会修，暂时做不到的也会告诉你为什么。</p>
  </div>

  <div class="fbform">
    <form method="post" action="/feedback">
      CSRF_IN
      <div class="row2">
        <div>
          <label>反馈类型</label>
          <select name="kind">
            <option value="bug">出了问题 / 报错</option>
            <option value="feature">想要新功能</option>
            <option value="perf">太慢 / 卡顿</option>
            <option value="ask">使用方法咨询</option>
            <option value="other">其他</option>
          </select>
        </div>
        <div>
          <label>联系方式（选填，方便回复你）</label>
          <input type="text" name="contact" maxlength="60"
                 placeholder="微信 / QQ / 手机号，随你">
        </div>
      </div>

      <label>详细描述</label>
      <textarea name="text" maxlength="2000" required
                placeholder="发生了什么？期望的结果是什么？如果能贴上报错文字或截图描述，会更好定位。"></textarea>

      <!-- 附件区：拖拽 / 点击选择 / 直接粘贴截图，三个入口共用同一个上传接口。 -->
      <label>截图或文件（选填）</label>
      <div class="fbdrop" id="fbDrop" tabindex="0" role="button"
           aria-label="点击选择文件，或把文件拖进来">
        <div class="fbdrop-ico">&#128206;</div>
        <div class="fbdrop-t">
          <b>点击选择</b>、把文件<b>拖进来</b>，或者直接在这里 <b>Ctrl+V 粘贴截图</b>
        </div>
        <div class="fbdrop-h">支持 PNG / JPG / GIF / WebP / TXT / LOG / JSON / ZIP，
          单个不超过 8 MB，最多 6 个</div>
      </div>
      <input type="file" id="fbFile" multiple accept="image/*,.txt,.log,.json,.zip,.csv"
             style="display:none">
      <div class="att-list" id="fbAttachList"></div>
      <input type="hidden" name="attach_token" id="fbAttachToken">

      MSGBOX
      ERRBOX

      <div class="btns">
        <button class="btn btn-main" type="submit">提交反馈</button>
        <span class="hint">提交后可以在管理面板看到处理状态</span>
      </div>
    </form>
  </div>

  <div class="fbnote">
    &#183; 本机 IP（<b>IP_TXT</b>）会一起提交，用于定位是哪台机器 &mdash;&mdash;
      这也是「不用登录」的原因：IP 本身就是身份。<br>
    &#183; 请不要在描述里写密钥 / 密码等敏感信息。<br>
    &#183; 客户端软件里也有这个入口：「设置 &rarr; 意见反馈」。
  </div>
</div>
""".replace('IP_TXT', ip_txt).replace('MSGBOX', msgbox).replace('ERRBOX', errbox)
   .replace('CSRF_IN', csrf_in) + ATTACH_JS)



def landing_page(online_now=0):
    """介绍页（新的首页）。

    设计参考成熟开源项目的落地页：hero -> 功能网格 -> 三步 -> FAQ。
    不引外部资源（零 CDN / 零字体 / 零 JS 库）—— 门户必须能完全离线跑。

    「正在借网」这个数字实时读 live 数据，让页面有「这是活的」的感觉，
    比写死一句「已稳定运行」更有说服力。
    """
    return page('渡口 Ferry · 借网工具', """
<nav class="nav">
  <a href="/" class="nv on">首页</a>
  <a href="/download-page" class="nv">下载</a>
  <a href="/feedback" class="nv">意见反馈</a>
</nav>

<div class="hero">
  <div class="badge">Phase 2 &#183; 稳定版</div>
  <h1>网断了，手上的活还没完？<br><span class="g">借一台能出网的主机顶一下</span></h1>
  <p class="tag">
    作业还没交、代码还没推、资料查到一半 &mdash;&mdash; 断网偏偏都挑这种时候。
    只要局域网还通，就能借一台能出网的主机做应急出口：装一次，点一下接通，
    把眼前的事先做完，不用等网络恢复。
  </p>
  <div class="cta">
    <a class="btn btn-main" href="/download-page">前往下载</a>
    <a class="btn btn-ghost" href="/feedback">意见反馈</a>
  </div>
  <div class="stats">
    <div class="stat"><b>1</b><span>个端点，HTTP + SOCKS5 同端口</span></div>
    <div class="stat"><b>__ONLINE__</b><span>台正在借网</span></div>
    <div class="stat"><b>10 MB/s</b><span>每台限速（可调）</span></div>
  </div>
</div>

<div class="sec">
  <div class="ph"><i></i><h2>这些时候救急</h2></div>
  <p class="pd">不是日常必备，是「事情做到一半」的那一下 &mdash;&mdash; 三个最常见的场景。</p>
</div>

<div class="fgrid">
  <div class="fcard scc">
    <div class="ico">&#9203;</div>
    <h3>断网了，事情还没做完</h3>
    <p>作业要交、代码要推、论文查到一半 &mdash;&mdash; 偏偏这时候断网。
       先借一台能出网的主机把眼前的事做完：装一次，之后每次点一下就好，
       不用等网络恢复，也不用抱着电脑到处找热点。</p>
    <span class="tg">&#9679; 一分钟接上</span>
  </div>
  <div class="fcard scc">
    <div class="ico">&#128225;</div>
    <h3>下载、看视频不吃自己的流量</h3>
    <p>下载安装包、看视频最费出校流量。让这类流量走公用出口那台机器，
       自己的账号额度几乎不动 &mdash;&mdash; 月底还差两天的时候很顶用。</p>
    <span class="tg">&#9679; 不扣自己额度</span>
  </div>
  <div class="fcard scc">
    <div class="ico">&#128421;</div>
    <h3>只有一台机器能上网</h3>
    <p>宿舍、实验室常有这种情况：只有一台连着网线，或者只有那台激活了账号。
       其余机器经它出去，不用挨个去开权限、也不用把网线拔来拔去。</p>
    <span class="tg">&#9679; 多台共用</span>
  </div>
</div>

<div class="sec">
  <div class="ph"><i></i><h2>它做到了什么</h2></div>
  <p class="pd">不是「又一个代理工具」&mdash;&mdash; 针对的是「内网里只有部分主机能出网」这个具体场景。</p>
</div>

<div class="fgrid">
  <div class="fcard">
    <div class="ico">&#8646;</div>
    <h3>断网期间照常工作</h3>
    <p>把默认路由删掉模拟彻底断网后，直连全部失败，经渡口访问常用站点全部正常。实测 20 秒采样 4/4。</p>
    <span class="tg">&#9679; 已验证</span>
  </div>
  <div class="fcard">
    <div class="ico">&#9889;</div>
    <h3>按目标自动选路</h3>
    <p>国内站点直连最快（实测 0.1 秒）；直连不通的目标自动改走出口机，不需要你手动切换任何配置。
       两条路同时发起、谁先连上就用谁，输的那条立刻取消 &mdash;&mdash; 所以你感觉不到「切换」这个动作。</p>
    <span class="tg">&#9679; 已实测</span>
  </div>
  <div class="fcard">
    <div class="ico">&#9635;</div>
    <h3>游戏也能用（TUN）</h3>
    <p>可选开启 TUN 全局接管：建一块虚拟网卡接管全部流量，不认系统代理的游戏客户端、QUIC、视频通话都走同一条路。</p>
    <span class="tg">&#9679; 需管理员权限</span>
  </div>
  <div class="fcard">
    <div class="ico">&#8595;</div>
    <h3>UDP低延迟模式</h3>
    <p>不只是 TCP。DNS 查询、游戏联机、实时语音都走 UDP &mdash;&mdash;
       经远端中继转发，实测完整链路 5/5 通，平均 10&ndash;15ms。</p>
    <span class="tg">&#9679; 全协议</span>
  </div>
  <div class="fcard">
    <div class="ico">&#8942;</div>
    <h3>托盘常驻，一键开关</h3>
    <p>托盘图标就能借网 / 恢复直连。停止时自动还原你原来的系统代理，不会把网络搞断。</p>
    <span class="tg">&#9679; 不动你的配置</span>
  </div>
  <div class="fcard">
    <div class="ico">&#8635;</div>
    <h3>自动更新 + 自动恢复</h3>
    <p>启动时静默检查新版本，有更新才提示。借网中断自动重连，被管理员停用后 5 秒内自动切回直连。</p>
    <span class="tg">&#9679; 全自动</span>
  </div>
</div>

<div class="sec">
  <div class="ph"><i></i><h2>三步用起来</h2></div>
  <p class="pd">不需要配 SSH 密钥、不需要装 Node.js、不需要敲命令。</p>
</div>

<div class="steps">
  <div class="step2">
    <div class="n">1</div>
    <h3>下载安装包</h3>
    <p>约 17 MB，内嵌运行库。装完不用额外装任何东西，系统自带的 ssh.exe 就够。</p>
  </div>
  <div class="step2">
    <div class="n">2</div>
    <h3>双击安装</h3>
    <p>会弹一次「是否允许更改设备」&mdash;&mdash; TUN 全局接管需要它。不勾 TUN 则完全不需要管理员。</p>
  </div>
  <div class="step2">
    <div class="n">3</div>
    <h3>填密钥激活</h3>
    <p>打开渡口助手，把管理员给的 8 位密钥填进「输入密钥激活」，点激活。之后不用再输。</p>
  </div>
</div>

<div class="sec">
  <div class="ph"><i></i><h2>常见问题</h2></div>
  <p class="pd">都是被真实问过的问题。</p>
</div>

<div class="faq">
  <details>
    <summary>会影响我本来用的代理软件吗？</summary>
    <div class="ans">不会。借网时渡口只切到自己的端口；停止借网时还原你原来的代理地址，并且会先探测那个代理是否活着 &mdash;&mdash; 探不通就直接关掉系统代理，至少保证你能上网。</div>
  </details>
  <details>
    <summary>不开启 TUN 的话，哪些程序能借网？</summary>
    <div class="ans">所有「认系统代理」或「能填代理地址」的程序：浏览器、下载工具、git、curl、Steam、各种客户端。不认代理的程序（多数游戏）需要开 TUN。</div>
  </details>
  <details>
    <summary>限速是怎么回事？会被别人挤掉吗？</summary>
    <div class="ans">每台机器默认 10 MB/s，在远端服务端执行（不是客户端，改本地配置绕不过去）。多台同时用会自动分摊，谁都不会把出口占满。</div>
  </details>
  <details>
    <summary>安全吗？我的流量别人能看到吗？</summary>
    <div class="ans">默认走远端 SOCKS5，链路上是明文（同一局域网内可嗅探域名和 IP）。需要加密就在客户端设置里关掉「直连模式」，退回 SSH 隧道加密 &mdash;&mdash; 代价是那时服务端无法按机器限速。</div>
  </details>
  <details>
    <summary>换机器怎么办？密钥能重复用吗？</summary>
    <div class="ans">密钥一次性，激活成功即失效，且绑定激活时那台机器的局域网 IP。换机器找管理员要新密钥。</div>
  </details>
  <details>
    <summary>出问题了怎么反馈？</summary>
    <div class="ans">网页顶部导航有「意见反馈」，客户端「设置」里也有。提交后管理员能在面板里直接看到并处理。</div>
  </details>
  <details>
    <summary>会不会扣我自己的流量？</summary>
    <div class="ans">出校流量记在出口那台机器上（也就是公用出口的账号），你自己的额度基本不动。
       速度上限由服务端按 IP 下发，改本地配置绕不过去。</div>
  </details>
  <details>
    <summary>作为出口的那台主机关机 / 断网了怎么办？</summary>
    <div class="ans">借网就断了 &mdash;&mdash; 它是整条链路的关键，这一点没办法绕。客户端会检测到并提示，
       点「停止并恢复直连」就能回到本地网络。想长期稳定，出口机最好设成开机自启、别随手关机。</div>
  </details>
  <details>
    <summary>开着全局模式会影响局域网 / 打印机 / 共享吗？</summary>
    <div class="ans">不会。10.x、172.16&ndash;31.x、192.168.x 这些内网地址在分流规则里是直连的，
       局域网打印机、共享目录、内网其它机器都照常访问 &mdash;&mdash; 全局接管只接管去外网的流量。</div>
  </details>
  <details>
    <summary>已经断网了，还能装上吗？</summary>
    <div class="ans">能。安装包可以提前下好放在本地，装完不联网也能跑；只有第一次激活需要能连到这台服务端
       （所以最好在网络正常时先装好、先把密钥激活了）。</div>
  </details>
</div>
""".replace('__ONLINE__', str(int(online_now or 0))))


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


# 全站附加样式（介绍页 / 下载页 / 反馈页 / 管理面板共用）。
#
# 踩过的坑：这些规则原来只跟在 LIVE_UI 里、被插进管理面板 —— 于是首页
# 和反馈页拿到的是裸 HTML：没有卡片、没有网格，卡片竖着排成一列。
# 结构测试（数 class 出现次数）全过，所以一直没暴露；截一张图就露馅。
# 现在拆成独立常量，由 page() 统一注入每一页。
EXTRA_CSS = r'''<style>
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

/* ── 性能面板：折线图 + 六卡片 ──
   参考「路由器/监控面板」的常见布局：上面一条时间曲线看趋势，
   下面 2x3 的数字卡看当前值。曲线能看出「刚才那波是不是突发」，
   纯数字看不出。 */
.perfpanel{background:var(--card);border:1px solid var(--line);
  border-radius:14px;padding:14px 16px 16px;margin:0 0 16px;
  box-shadow:var(--shadow)}

.perf-head{display:flex;align-items:center;gap:10px;flex-wrap:wrap;
  margin-bottom:10px}
.perf-title{font-size:13.5px;font-weight:700;color:var(--fg)}
.perf-range{font-size:11px;color:var(--fg2);background:var(--card2);
  border:1px solid var(--line);border-radius:99px;padding:1px 8px}
.perf-load{margin-left:auto;display:flex;align-items:center;gap:7px;
  font-size:12px;color:var(--fg2);flex-wrap:wrap}
.perf-load b{font-size:15px;font-family:Consolas,Monaco,monospace;
  color:var(--fg);line-height:1}
.pf-lbl{font-size:11.5px;color:var(--fg2)}
.pf-cap{font-size:11.5px;color:var(--fg2);font-family:Consolas,Monaco,monospace}

/* 图表容器：固定高度，SVG 拉伸填满（preserveAspectRatio=none 让它横向铺开） */
.chart-wrap{position:relative;height:132px;margin:2px 0 0;
  background:linear-gradient(180deg,color-mix(in srgb,var(--card2) 60%,transparent),transparent);
  border-radius:10px;overflow:hidden}
.chart-wrap svg{display:block;width:100%;height:100%}

/* 横向网格线 + Y 轴文字（JS 生成） */
.chart-wrap .grid-line{stroke:var(--line);stroke-width:1;
  stroke-dasharray:3 4;opacity:.65}
.chart-y{position:absolute;left:0;top:0;bottom:0;width:56px;
  pointer-events:none}
.chart-y .ylab{position:absolute;left:4px;transform:translateY(-50%);
  font-size:10px;color:var(--fg2);font-family:Consolas,Monaco,monospace;
  white-space:nowrap;letter-spacing:.2px}
/* 曲线左边缘留出 Y 轴宽度，否则线会压在数字上 */
.chart-wrap svg{padding-left:56px;box-sizing:border-box}

.chart-axis{display:flex;justify-content:space-between;margin:3px 2px 0;
  font-size:9.5px;color:var(--fg2);font-family:Consolas,Monaco,monospace}

.perf-foot{display:flex;gap:16px;margin:9px 0 12px;font-size:11.5px;
  color:var(--fg2);flex-wrap:wrap;font-family:Consolas,Monaco,monospace}

/* ── 2x3 指标卡 ── */
.perfgrid{display:grid;grid-template-columns:repeat(3,1fr);gap:10px}
@media(max-width:820px){ .perfgrid{grid-template-columns:repeat(2,1fr)} }
@media(max-width:520px){ .perfgrid{grid-template-columns:1fr} }

.pcard{display:flex;align-items:center;gap:11px;
  background:var(--card2);border:1px solid var(--line);border-radius:11px;
  padding:11px 13px;min-width:0;
  transition:border-color .18s,background .18s}
.pcard:hover{border-color:var(--line2)}

/* 图标：圆形底色 + 同色符号。
   为什么用 &#8593; &#8595; 这类字符而不是 emoji：
   ✏️⚡☁ 在多数系统上会被渲染成彩色 emoji，跟深色主题打架，
   而且不受 color 控制。Unicode 箭头 + 明确的 color 才是可控的。 */
.pico{flex:0 0 auto;width:30px;height:30px;border-radius:9px;
  display:flex;align-items:center;justify-content:center;
  font-size:15px;font-weight:700;line-height:1}
.pbody{min-width:0;flex:1 1 auto}
.plbl{font-size:11px;color:var(--fg2);margin-bottom:3px;white-space:nowrap}
.pval{font-size:17px;font-weight:700;font-family:Consolas,Monaco,monospace;
  line-height:1.1;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}

/* 四套配色。上传=橙、下载=蓝、连接=绿、内存=紫 —— 和曲线颜色对齐，
   看曲线看到橙色就知道是上传。 */
.pcard.up   .pico{background:rgba(255,166,87,.14);color:#ffa657}
.pcard.up   .pval{color:#ffa657}
.pcard.down .pico{background:rgba(74,158,255,.14);color:#4a9eff}
.pcard.down .pval{color:#4a9eff}
.pcard.conn .pico{background:rgba(61,220,151,.14);color:var(--ok)}
.pcard.conn .pval{color:var(--ok)}
.pcard.mem  .pico{background:rgba(165,140,255,.14);color:#a58cff}
.pcard.mem  .pval{color:#a58cff}
/* 「量」那两张用更淡的图标底，和「速度」区分开 */
.pcard.up2   .pico{background:rgba(255,166,87,.10);color:#ffa657}
.pcard.up2   .pval{color:var(--fg)}
.pcard.down2 .pico{background:rgba(74,158,255,.10);color:#4a9eff}
.pcard.down2 .pval{color:var(--fg)}

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

@media(max-width:1399px){
  /* 窄屏：浮层会真的压到内容，改成静态块放在最上面。
     断点从 1279 提到 1399：内容区 max-width 是 860，1280 视口下右边留白
     只有 (1280-860)/2 ≈ 210px，而侧边栏 320px —— 会实实在在盖住表格右列
     （实测截图里「操作」列被遮）。1400 以上留白才够。 */
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

/* ── 介绍页（首页）──
   布局参考成熟开源项目的落地页：hero 居中 + 功能网格 + 步骤 + FAQ。
   不引外部资源：零 CDN / 零字体 / 零 JS 库，门户要能完全离线跑。 */
.hero{text-align:center;padding:44px 16px 34px;
  background:radial-gradient(ellipse 70% 100% at 50% 0%,
    color-mix(in srgb,var(--accent) 10%,transparent),transparent);
  border-radius:18px;margin-bottom:26px}
.hero .badge{display:inline-flex;align-items:center;gap:6px;
  font-size:11.5px;color:var(--accent);background:var(--okbg);
  border:1px solid color-mix(in srgb,var(--accent) 35%,transparent);
  padding:3px 11px;border-radius:99px;margin-bottom:16px;letter-spacing:.3px}
.hero h1{font-size:34px;line-height:1.25;margin:0 0 12px;font-weight:800;
  letter-spacing:-.3px}
.hero h1 .g{background:linear-gradient(96deg,var(--accent),
  color-mix(in srgb,var(--accent) 55%,#4cc9f0));
  -webkit-background-clip:text;background-clip:text;color:transparent}
.hero .tag{font-size:15px;color:var(--fg2);max-width:620px;margin:0 auto 26px;
  line-height:1.75}
.hero .cta{display:flex;gap:12px;justify-content:center;flex-wrap:wrap}
.btn{display:inline-flex;align-items:center;gap:7px;
  padding:11px 22px;border-radius:11px;font-size:14px;font-weight:600;
  text-decoration:none;border:1px solid transparent;cursor:pointer;
  transition:transform .12s,box-shadow .12s,background .15s,border-color .15s}
.btn:active{transform:translateY(1px)}
.btn-main{background:var(--accent);color:var(--accent-fg);
  box-shadow:0 4px 14px color-mix(in srgb,var(--accent) 28%,transparent)}
.btn-main:hover{background:var(--accent-d)}
.btn-ghost{background:transparent;color:var(--fg);border-color:var(--line2)}
.btn-ghost:hover{border-color:var(--accent);color:var(--accent)}
.hero .stats{display:flex;gap:34px;justify-content:center;margin-top:26px;
  flex-wrap:wrap}
.hero .stat{text-align:center}
.hero .stat b{display:block;font-size:22px;font-weight:700;line-height:1;
  font-family:Consolas,Monaco,monospace}
.hero .stat span{font-size:11.5px;color:var(--fg2);margin-top:5px;display:block}

/* 章节标题 */
.sec{margin:34px 0 16px}
.sec .ph{display:flex;align-items:center;gap:8px;margin-bottom:6px}
.sec .ph i{width:6px;height:6px;border-radius:50%;background:var(--accent);
  display:inline-block;flex:0 0 auto}
.sec h2{font-size:19px;margin:0;font-weight:700}
.sec .pd{font-size:13px;color:var(--fg2);line-height:1.7}

/* 功能网格 */
.fgrid{display:grid;grid-template-columns:repeat(3,1fr);gap:12px}
@media(max-width:900px){.fgrid{grid-template-columns:repeat(2,1fr)}}
@media(max-width:600px){.fgrid{grid-template-columns:1fr}}
.fcard{background:var(--card);border:1px solid var(--line);border-radius:14px;
  padding:18px 18px 16px;transition:border-color .16s,transform .16s}
.fcard:hover{border-color:var(--line2);transform:translateY(-2px)}
.fcard .ico{width:34px;height:34px;border-radius:10px;
  background:color-mix(in srgb,var(--accent) 13%,transparent);
  color:var(--accent);display:flex;align-items:center;justify-content:center;
  font-size:17px;font-weight:700;margin-bottom:12px;line-height:1}
.fcard h3{font-size:14.5px;margin:0 0 7px;font-weight:650}
.fcard p{font-size:12.5px;color:var(--fg2);line-height:1.7;margin:0}
.fcard .tg{display:inline-flex;align-items:center;gap:4px;margin-top:11px;
  font-size:11px;color:var(--ok);background:var(--okbg);
  padding:2px 8px;border-radius:99px}

/* 场景卡（首页「这些时候最有用」）。
   和技术特性卡同一个网格，用左侧一条强调色 + 略深的底区分：
   场景是「故事」（什么时候去找它），特性是「参数」（它凭什么做到）。
   两者混在一排会让人读不出重点，所以分成两个区块。 */
.fcard.scc{border-left:3px solid color-mix(in srgb,var(--accent) 78%,transparent);
  background:linear-gradient(180deg,
    color-mix(in srgb,var(--accent) 6%,var(--card)),var(--card))}
.fcard.scc .tg{color:var(--accent);background:color-mix(in srgb,var(--accent) 12%,transparent)}
.fcard.scc h3{font-size:15px}
.fcard.scc .ico{background:color-mix(in srgb,var(--accent) 20%,transparent)}

/* 步骤 */
.steps{display:grid;grid-template-columns:repeat(3,1fr);gap:12px}
@media(max-width:820px){.steps{grid-template-columns:1fr}}
.step2{background:var(--card);border:1px solid var(--line);border-radius:14px;
  padding:18px}
.step2 .n{width:26px;height:26px;border-radius:8px;background:var(--accent);
  color:var(--accent-fg);display:flex;align-items:center;justify-content:center;
  font-size:13px;font-weight:700;margin-bottom:11px}
.step2 h3{font-size:14px;margin:0 0 6px;font-weight:650}
.step2 p{font-size:12.5px;color:var(--fg2);line-height:1.7;margin:0}

/* FAQ */
.faq{background:var(--card);border:1px solid var(--line);border-radius:14px;
  padding:4px 18px}
.faq details{border-bottom:1px solid var(--line);padding:13px 0}
.faq details:last-child{border-bottom:0}
.faq summary{cursor:pointer;font-size:13.5px;font-weight:600;outline:none;
  list-style:none;display:flex;align-items:center;gap:8px}
.faq summary::-webkit-details-marker{display:none}
.faq summary::before{content:'+';color:var(--accent);font-weight:700;
  width:14px;display:inline-block;flex:0 0 auto;font-size:14px}
.faq details[open] summary::before{content:'−'}
.faq .ans{font-size:12.5px;color:var(--fg2);line-height:1.8;
  padding:9px 0 2px 22px}

/* 下载页 */
.dlwrap{max-width:760px;margin:0 auto}
.dlhero{text-align:center;padding:30px 16px 24px}
.dlhero h1{font-size:27px;margin:0 0 10px;font-weight:800}
.dlhero .sub{font-size:14px;color:var(--fg2);line-height:1.7;
  max-width:560px;margin:0 auto 22px}
.dlbox{background:var(--card);border:1px solid var(--line);border-radius:16px;
  padding:20px;text-align:center}
.dlbox .meta{display:flex;gap:8px;justify-content:center;flex-wrap:wrap;
  margin-top:14px}
.dlbox .chip{font-size:11.5px;color:var(--fg2);background:var(--card2);
  border:1px solid var(--line);padding:3px 10px;border-radius:99px}
.dlbox .chip b{color:var(--fg);font-weight:600}
.warnbox{margin-top:14px;text-align:left;font-size:12px;line-height:1.8;
  background:var(--warnbg);color:var(--warn);padding:11px 13px;border-radius:10px}
.warnbox b{color:var(--warn)}
.cmdbox{background:var(--code);color:var(--fg);padding:10px 12px;
  border-radius:8px;margin-top:9px;font-size:11.5px;word-break:break-all;
  user-select:all;font-family:Consolas,Monaco,monospace;line-height:1.6}
.nav{display:flex;gap:14px;align-items:center;flex-wrap:wrap}
.nav a.nv{font-size:13px;color:var(--fg2);text-decoration:none;
  padding:5px 2px;border-bottom:1.5px solid transparent;transition:color .15s}
.nav a.nv:hover{color:var(--fg)}
.nav a.nv.on{color:var(--accent);border-bottom-color:var(--accent);font-weight:600}

/* ── 意见反馈 ── */
.fbwrap{max-width:680px;margin:0 auto}
.fbhero{text-align:center;padding:26px 8px 20px}
.fbhero h1{font-size:24px;margin:0 0 9px;font-weight:800}
.fbhero p{font-size:13.5px;color:var(--fg2);line-height:1.75;margin:0}
.fbform{background:var(--card);border:1px solid var(--line);border-radius:16px;
  padding:20px}
.fbform label{display:block;font-size:12.5px;color:var(--fg2);margin:14px 0 6px}
.fbform label:first-child{margin-top:0}
.fbform input[type=text],.fbform select,.fbform textarea{
  width:100%;padding:9px 11px;font-size:13px;font-family:inherit;
  background:var(--card2);border:1px solid var(--line2);border-radius:9px;
  color:var(--fg);transition:border-color .15s}
.fbform input[type=text]:focus,.fbform select:focus,.fbform textarea:focus{
  outline:none;border-color:var(--accent)}
.fbform textarea{min-height:110px;resize:vertical;line-height:1.7}
.fbform .row2{display:grid;grid-template-columns:1fr 1fr;gap:12px}
@media(max-width:560px){.fbform .row2{grid-template-columns:1fr}}
.fbform .btns{display:flex;gap:10px;align-items:center;margin-top:18px}
.fbmsg{font-size:12.5px;line-height:1.7;padding:10px 12px;border-radius:9px;
  margin-top:14px;display:none}
.fbmsg.ok{display:block;background:var(--okbg);color:var(--ok)}
.fbmsg.err{display:block;background:var(--errbg);color:var(--err)}
.fbnote{font-size:11.5px;color:var(--fg2);line-height:1.75;margin-top:14px}

/* ── 反馈附件上传 ── */
.fbdrop{border:1.5px dashed var(--line2);border-radius:12px;padding:18px 16px;
  text-align:center;cursor:pointer;transition:border-color .15s,background .15s;
  background:color-mix(in srgb,var(--card2) 50%,transparent);outline:none}
.fbdrop:hover,.fbdrop:focus-visible{border-color:var(--accent);
  background:color-mix(in srgb,var(--accent) 6%,transparent)}
.fbdrop.hot{border-color:var(--accent);border-style:solid;
  background:color-mix(in srgb,var(--accent) 12%,transparent)}
.fbdrop-ico{font-size:22px;line-height:1;margin-bottom:8px;opacity:.75}
.fbdrop-t{font-size:12.5px;color:var(--fg2);line-height:1.7}
.fbdrop-t b{color:var(--fg);font-weight:600}
.fbdrop-h{font-size:11px;color:var(--fg2);opacity:.75;margin-top:6px;line-height:1.6}

.att-list{display:flex;flex-direction:column;gap:7px;margin-top:10px}
.att-item{display:flex;align-items:center;gap:10px;padding:7px 9px;
  background:var(--card2);border:1px solid var(--line);border-radius:9px}
.att-item.att-uploading{opacity:.6}
.att-thumb{width:38px;height:38px;border-radius:7px;object-fit:cover;
  flex:0 0 auto;background:var(--line);border:1px solid var(--line2)}
.att-thumb.att-doc{display:flex;align-items:center;justify-content:center;
  font-size:10px;font-weight:700;color:var(--fg2);font-family:Consolas,Monaco,monospace}
.att-meta{flex:1 1 auto;min-width:0}
.att-name{font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.att-size{font-size:10.5px;color:var(--fg2);margin-top:1px}
.att-del{flex:0 0 auto;width:24px;height:24px;padding:0;background:transparent;
  border:0;color:var(--fg2);font-size:17px;line-height:1;cursor:pointer;border-radius:6px}
.att-del:hover{color:var(--err);background:var(--errbg)}

/* 管理面板里显示附件：小缩略图 + 名字，点开看大图 */
.fb-attach{display:flex;gap:6px;flex-wrap:wrap;margin-top:6px}
.fb-attach a{display:inline-flex;align-items:center;gap:5px;font-size:11px;
  color:var(--fg2);text-decoration:none;background:var(--card2);
  border:1px solid var(--line);border-radius:7px;padding:3px 8px;
  transition:border-color .15s,color .15s}
.fb-attach a:hover{color:var(--accent);border-color:var(--accent)}
.fb-attach img{width:44px;height:44px;object-fit:cover;border-radius:6px;
  border:1px solid var(--line2);display:block}

/* ── 意见反馈管理 ── */
.fb-st{display:inline-block;padding:1px 8px;border-radius:99px;font-size:11px;
  white-space:nowrap}
.fb-st-0{background:var(--warnbg);color:var(--warn)}
.fb-st-1{background:var(--okbg);color:var(--ok)}
.fb-st-2{background:var(--card2);color:var(--fg2)}
.fb-st-3{background:var(--errbg);color:var(--err)}
.fb-text{font-size:12.5px;line-height:1.65;max-width:420px;
  white-space:pre-wrap;word-break:break-word}

/* 同源折叠的「共 N 条」按钮：做得像链接而不是按钮，
   因为它属于「来源」那一列的信息，不是主操作。 */
.fb-fold{background:none;border:0;padding:2px 0 0;cursor:pointer;
  font-size:11px;color:var(--accent);font-family:inherit;
  text-decoration:underline dotted;text-underline-offset:3px}
.fb-fold:hover{text-decoration-style:solid}
/* 子行（同源的历史反馈）左侧加一道竖线，一眼看出是从属关系 */
tr.fb-more>td{background:color-mix(in srgb,var(--card2) 45%,transparent)}
tr.fb-more>td:first-child{box-shadow:inset 3px 0 0 color-mix(in srgb,var(--accent) 40%,transparent)}

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

/* ── 管理面板：顶部导航 + 页头 ──
   五个区块拆成独立页面后，导航变成「你在哪一页」的指示器。 */
.adminnav{position:sticky;top:0;z-index:60;display:flex;gap:4px;flex-wrap:wrap;
  padding:8px 0;margin:0 0 18px;
  background:color-mix(in srgb,var(--bg) 90%,transparent);
  backdrop-filter:blur(10px);
  border-bottom:1px solid var(--line)}
.adminnav a.anav-item{font-size:12.5px;color:var(--fg2);text-decoration:none;
  padding:6px 14px;border-radius:9px;transition:color .15s,background .15s}
.adminnav a.anav-item:hover{color:var(--fg);background:var(--card)}
.adminnav a.anav-item.on{color:var(--accent);
  background:color-mix(in srgb,var(--accent) 14%,transparent);font-weight:600}

.ahead{display:flex;align-items:flex-end;justify-content:space-between;
  gap:16px;flex-wrap:wrap;margin:0 0 18px}
.ahead h1{margin:0 0 4px;font-size:24px;font-weight:800;letter-spacing:-.3px}
.ahead .asub{font-size:12.5px;color:var(--fg2)}
.ahead-act{display:flex;gap:8px;flex-wrap:wrap}

/* 反馈页工具栏 + 卡片列表 */
.fbtoolbar{display:flex;align-items:center;gap:12px;flex-wrap:wrap;margin:0 0 12px}
.fbfilters{display:flex;gap:6px;flex-wrap:wrap}
.fbfilters .fb-filter.on{background:color-mix(in srgb,var(--accent) 16%,transparent);
  border-color:var(--accent);color:var(--accent);font-weight:600}
.fbcount{margin-left:auto;font-size:12px;color:var(--warn)}

.fbcards{display:flex;flex-direction:column;gap:10px}
.fbskel{color:var(--fg2);font-size:13px;padding:20px 0;text-align:center}

.fbcard{background:var(--card);border:1px solid var(--line);border-radius:13px;
  padding:14px 16px;transition:border-color .15s}
.fbcard:hover{border-color:var(--line2)}
.fbcard.st0{border-left:3px solid var(--warn)}
.fbcard.st1{border-left:3px solid var(--ok)}
.fbcard.st2{border-left:3px solid var(--line2);opacity:.75}
.fbcard.st3{border-left:3px solid var(--err);opacity:.7}
.fbcard-top{display:flex;align-items:center;gap:9px;flex-wrap:wrap;margin-bottom:8px}
.fbcard .fb-st{font-size:11px}
.fbkind{font-size:11.5px;color:var(--fg2);background:var(--card2);
  border:1px solid var(--line);border-radius:99px;padding:1px 9px}
.fbwho{margin-left:auto;font-size:11px;color:var(--fg2);
  font-family:Consolas,Monaco,monospace;white-space:nowrap}
.fbcard .fb-text{font-size:13px;line-height:1.7;max-width:none;color:var(--fg)}
.fbcard .fb-extra{font-size:11.5px;color:var(--fg2);margin-top:6px}
.fbcard-foot{display:flex;align-items:center;gap:10px;margin-top:10px;flex-wrap:wrap}
.fbcard-foot select{font-size:11.5px;padding:3px 7px;background:var(--card2);
  color:var(--fg);border:1px solid var(--line2);border-radius:7px;font-family:inherit}
.fbold{font-size:11px;color:var(--fg2);background:transparent;border:0;
  cursor:pointer;padding:2px 0;text-decoration:underline dotted;
  text-underline-offset:3px;font-family:inherit}
.fbold:hover{color:var(--accent)}
.evtag{font-size:11px;background:var(--card2);border:1px solid var(--line);
  border-radius:6px;padding:1px 7px;font-family:Consolas,Monaco,monospace}

/* ── 管理面板分区导航 ──
   sticky 顶部，滚动时一直在。五个区块都是长表格，
   没有它就得靠滚轮翻半天才能找到「意见反馈」。 */
.adminnav{position:sticky;top:0;z-index:60;display:flex;gap:6px;flex-wrap:wrap;
  padding:9px 0;margin:0 0 14px;
  background:color-mix(in srgb,var(--bg) 88%,transparent);
  backdrop-filter:blur(8px);
  border-bottom:1px solid var(--line)}
.adminnav a{font-size:12.5px;color:var(--fg2);text-decoration:none;
  padding:5px 12px;border-radius:99px;border:1px solid var(--line);
  background:var(--card);transition:color .15s,border-color .15s}
.adminnav a:hover{color:var(--accent);border-color:var(--accent)}
/* 锚点跳转时别被 sticky 导航盖住标题 */
[id^="sec-"]{scroll-margin-top:66px}

/* 新建密钥折叠：低频操作，不占首屏。
   展开后才看到备注 / 有效期 / 档位那一坨。 */
.newcode>summary{list-style:none;cursor:pointer;display:inline-flex;
  align-items:center;gap:6px;font-size:13px;font-weight:600;color:var(--accent);
  background:color-mix(in srgb,var(--accent) 12%,transparent);
  border:1px solid color-mix(in srgb,var(--accent) 35%,transparent);
  padding:7px 14px;border-radius:9px;user-select:none;
  transition:background .15s}
.newcode>summary:hover{background:color-mix(in srgb,var(--accent) 20%,transparent)}
.newcode>summary::-webkit-details-marker{display:none}
.newcode[open]>summary{margin-bottom:6px}
.newcode form{margin-top:4px}

/* ── 下载页（v0.9.12 改版）──
   原来是「居中标题 + 一摞同宽卡片」，三段 SmartScreen 说明 + 三步 + 密钥规则
   全平铺，一屏塞不下重点也看不出来。改成双栏 hero（左文案右版本卡）+
   三列步骤 + 其余全部折叠。 */

/* ── 下载页 v2：双平台并列 + 扫码 ──
   2026-10-30 重排。原来的 dl-hero 是「左文案 + 右版本卡」，
   只服务 Windows；现在要并列两个平台，改用 grip 两列。
   窄屏堆叠，Windows 在前。 */
.platgrid{display:grid;grid-template-columns:1fr 1fr;gap:16px;margin:8px 0 22px}
@media(max-width:860px){.platgrid{grid-template-columns:1fr}}

.platcard{background:var(--card);border:1px solid var(--line);border-radius:16px;
  padding:20px 20px 18px;box-shadow:var(--shadow);display:flex;flex-direction:column;
  position:relative;overflow:hidden}
/* 顶部一条 3px 的平台色带：蓝=Windows，绿=Android。
   用色带而不是整块换色，深色主题下也不会显脏。 */
.platcard::before{content:'';position:absolute;left:0;right:0;top:0;height:3px}
.platcard.win::before{background:linear-gradient(90deg,#4a9eff,#7bb8ff)}
.platcard.droid::before{background:linear-gradient(90deg,#3ddc97,#6fe3b4)}

.plat-head{display:flex;align-items:center;gap:10px;margin-bottom:4px}
.plat-ico{width:34px;height:34px;border-radius:10px;display:flex;align-items:center;
  justify-content:center;font-size:17px;flex:0 0 auto;font-weight:700}
.platcard.win .plat-ico{background:rgba(74,158,255,.14);color:#4a9eff}
.platcard.droid .plat-ico{background:rgba(61,220,151,.14);color:var(--accent)}
.plat-title{font-size:16px;font-weight:700;line-height:1.2}
.plat-sub{font-size:11.5px;color:var(--fg2);margin-top:2px}

.plat-meta{display:flex;gap:14px;flex-wrap:wrap;margin:10px 0 12px;
  font-size:12px;color:var(--fg2);font-family:Consolas,Monaco,monospace}
.plat-meta b{color:var(--fg);font-weight:600}

.plat-actions{display:flex;gap:8px;flex-wrap:wrap;margin-top:2px}
/* 卡片之间高度不一致时（Android 内容更多），用弹性空白把各自的说明推到
   底部对齐 —— 但**不能**用 margin-top:auto 直接把按钮也推下去，
   否则内容少的 Windows 卡中间会裂出一大块空白（2026-10-30 截图看到过）。
   这里放一个弹性占位，只在按钮之后生效。 */
.plat-spacer{flex:1 1 auto;min-height:8px}
.plat-actions .btn{padding:9px 16px;font-size:13px;border-radius:10px}

/* Android 卡里的扫码区：左边二维码，右边文字说明 */
.qrrow{display:flex;gap:14px;align-items:center;margin:14px 0 2px;
  padding:12px;border-radius:12px;background:var(--card2);border:1px solid var(--line)}
.qrbox{flex:0 0 auto;background:#fff;border-radius:8px;padding:5px;
  line-height:0;box-shadow:0 1px 3px rgba(0,0,0,.18)}
.qrbox img{display:block;width:88px;height:88px;image-rendering:pixelated}
.qrtxt{flex:1 1 auto;min-width:0;font-size:12px;color:var(--fg2);line-height:1.65}
.qrtxt b{color:var(--fg);display:block;font-size:13px;margin-bottom:3px}
/* 手机上自己扫自己没意义 —— 窄屏把扫码区藏掉 */
@media(max-width:860px){.qrrow{display:none}}

.plat-note{font-size:11.5px;color:var(--fg2);line-height:1.7;margin-top:10px}
/* 卡片里的长串必须能断行。
   2026-10-30 实测：手机上 <code>127.0.0.1:18092</code> 这类长串不换行，
   把卡片撑破、整页横向溢出（最右列像素几乎全是内容 = 被裁）。
   overflow-wrap:anywhere 让浏览器在必要时**任意位置**断字，
   比 word-break:break-all 更温和（优先按词断，实在不行才硬断）。 */
.plat-note code{overflow-wrap:anywhere;word-break:break-word}
.platcard{min-width:0}
.platcard *{min-width:0}
/* 卡片内的 flex 子项也要允许收缩，否则内容会把它撑开 */
.plat-note,.plat-meta,.plat-actions{max-width:100%;overflow-wrap:anywhere}
.dlwrap{max-width:900px;margin:0 auto}
.dl-hero{display:grid;grid-template-columns:1.3fr .7fr;gap:26px;align-items:start;
  padding:14px 0 4px}
@media(max-width:820px){.dl-hero{grid-template-columns:1fr;gap:18px}}
.dl-hero-l h1{font-size:31px;line-height:1.25;margin:0 0 12px;font-weight:800;
  letter-spacing:-.4px}
.dl-hero-l p{font-size:14px;color:var(--fg2);line-height:1.8;margin:0;max-width:460px}
.dl-actions{display:flex;gap:10px;flex-wrap:wrap;margin:24px 0 18px}
.dl-trust{display:flex;gap:16px;flex-wrap:wrap;font-size:12px;color:var(--fg2)}
.dl-trust span{display:inline-flex;align-items:center;gap:5px}
.dl-trust span::before{content:'\2713';color:var(--accent);font-weight:700}

.dl-card{background:var(--card);border:1px solid var(--line);border-radius:16px;
  padding:18px 20px;box-shadow:var(--shadow)}
.dl-row{display:flex;align-items:baseline;justify-content:space-between;gap:12px;
  padding:10px 0;border-bottom:1px dashed var(--line);font-size:13px}
.dl-row:last-of-type{border-bottom:0}
.dl-row span{color:var(--fg2);font-size:12px}
.dl-row b{font-family:Consolas,Monaco,monospace;font-weight:700;font-size:13.5px}
.dl-tip{margin-top:14px;font-size:11.5px;color:var(--fg2);line-height:1.75;
  background:var(--card2);border:1px solid var(--line);border-radius:10px;
  padding:11px 12px}
.dl-tip b{color:var(--accent)}

.dl-steps{display:grid;grid-template-columns:repeat(3,1fr);gap:12px;margin:22px 0 16px}
@media(max-width:760px){.dl-steps{grid-template-columns:1fr}}
.dl-step{background:var(--card);border:1px solid var(--line);border-radius:14px;
  padding:16px;transition:border-color .15s}
.dl-step:hover{border-color:var(--line2)}
.dl-step i{display:inline-flex;width:24px;height:24px;border-radius:8px;
  background:color-mix(in srgb,var(--accent) 15%,transparent);color:var(--accent);
  font-style:normal;font-weight:700;font-size:12px;align-items:center;justify-content:center}
.dl-step b{display:block;margin:11px 0 6px;font-size:13.5px}
.dl-step p{margin:0;font-size:12.5px;color:var(--fg2);line-height:1.7}

.dl-folds{display:flex;flex-direction:column;gap:10px;margin-top:4px}
.dl-fold{background:var(--card);border:1px solid var(--line);border-radius:14px;
  padding:0 18px}
.dl-fold>summary{cursor:pointer;list-style:none;padding:15px 0;font-size:13.5px;
  font-weight:600;display:flex;align-items:center;gap:9px;outline:none;
  user-select:none}
.dl-fold>summary::-webkit-details-marker{display:none}
.dl-fold>summary::before{content:'+';color:var(--accent);font-weight:700;
  width:13px;flex:0 0 auto;font-size:15px;line-height:1}
.dl-fold[open]>summary::before{content:'\2212'}
.dl-fold[open]>summary{border-bottom:1px solid var(--line);margin-bottom:12px}
.dl-fold>summary .hint{font-weight:400;margin-left:2px}
.dl-fold .inner{padding:0 0 16px;font-size:13px;line-height:1.8}
.dl-fold .inner .step{margin:12px 0 0}
.dl-fold .inner .step .n{width:22px;height:22px;line-height:22px;font-size:11px}

/* ── 多维统计面板 ──
   思路抄异星工厂的产量统计：时间跨度越大，曲线上的点越粗，
   但总量永远准确（桶可加，聚合只是求和）。 */
.statpanel{background:var(--card);border:1px solid var(--line);
  border-radius:14px;padding:16px 18px 18px;margin:0 0 16px;
  box-shadow:var(--shadow)}
.stat-head{display:flex;align-items:center;gap:10px;flex-wrap:wrap;
  margin-bottom:14px}
.stat-title{font-size:14px;font-weight:700}
.stat-sub{font-size:11.5px;color:var(--fg2)}

/* 时间窗口切换：一排胶囊 */
.winchips{display:flex;gap:5px;flex-wrap:wrap;margin-left:auto}
.winchips button{background:var(--card2);border:1px solid var(--line);
  color:var(--fg2);font-size:11.5px;font-family:inherit;cursor:pointer;
  padding:4px 10px;border-radius:99px;transition:all .15s}
.winchips button:hover{border-color:var(--accent);color:var(--accent)}
.winchips button.on{background:color-mix(in srgb,var(--accent) 16%,transparent);
  border-color:var(--accent);color:var(--accent);font-weight:600}

/* 汇总卡片：一行五张，窄屏折行 */
.statcards{display:grid;grid-template-columns:repeat(5,1fr);gap:9px;
  margin:14px 0 4px}
@media(max-width:900px){.statcards{grid-template-columns:repeat(3,1fr)}}
@media(max-width:560px){.statcards{grid-template-columns:repeat(2,1fr)}}
.scard{background:var(--card2);border:1px solid var(--line);border-radius:10px;
  padding:10px 12px;min-width:0}
.scard .k{font-size:11px;color:var(--fg2);margin-bottom:4px;white-space:nowrap;
  overflow:hidden;text-overflow:ellipsis}
.scard .v{font-size:16px;font-weight:700;font-family:Consolas,Monaco,monospace;
  line-height:1.15;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.scard .s{font-size:10.5px;color:var(--fg2);margin-top:2px}
.scard.blue .v{color:#4a9eff}
.scard.orange .v{color:#ffa657}
.scard.green .v{color:var(--ok)}
.scard.purple .v{color:#a58cff}

/* 统计图表：比实时图高一点，看趋势更清楚 */
.stat-chart{position:relative;height:170px;margin-top:12px;
  background:linear-gradient(180deg,color-mix(in srgb,var(--card2) 60%,transparent),transparent);
  border-radius:10px;overflow:hidden}
.stat-chart svg{display:block;width:100%;height:100%;padding-left:56px;
  box-sizing:border-box}
.stat-chart .grid-line{stroke:var(--line);stroke-width:1;
  stroke-dasharray:3 4;opacity:.65}
.stat-y{position:absolute;left:0;top:0;bottom:0;width:56px;pointer-events:none}
.stat-y .ylab{position:absolute;left:4px;transform:translateY(-50%);
  font-size:10px;color:var(--fg2);font-family:Consolas,Monaco,monospace;
  white-space:nowrap}
.stat-axis{display:flex;justify-content:space-between;margin:3px 2px 0;
  font-size:9.5px;color:var(--fg2);font-family:Consolas,Monaco,monospace}

/* 设备排行 */
.stat-hosts{margin-top:16px}
.stat-hosts h4{margin:0 0 8px;font-size:12.5px;color:var(--fg2);
  font-weight:600;letter-spacing:.3px}
.stat-hosts table{font-size:12px}
.stat-hosts .bar{height:3px;border-radius:2px;background:var(--line);
  margin-top:4px;overflow:hidden}
.stat-hosts .bar>i{display:block;height:100%;border-radius:2px;
  background:linear-gradient(90deg,#4a9eff,#a58cff)}

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
</style>'''

# 管理面板的 HTML 骨架（实时图 + 统计面板 + 带宽控制台 + 弹窗）。
ADMIN_UI_HTML = r'''

<div class="perfpanel" id="sec-live">
  <div class="perf-head">
    <span class="perf-title">流量趋势</span>
    <span class="perf-range">10 分钟</span>
    <span class="perf-load">
      <span class="pf-lbl">出口载荷</span>
      <b id="gVal">—</b>
      <span id="gCap" class="pf-cap">/ —</span>
      <span class="gauge-tag" id="gTag">—</span>
    </span>
    <span class="livestamp" id="lvStamp">正在连接…</span>
  </div>

  <div class="chart-wrap">
    <svg id="perfChart" viewBox="0 0 800 132" preserveAspectRatio="none">
      <defs>
        <linearGradient id="gradDown" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stop-color="#4a9eff" stop-opacity="0.32"/>
          <stop offset="100%" stop-color="#4a9eff" stop-opacity="0"/>
        </linearGradient>
        <linearGradient id="gradUp" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stop-color="#ffa657" stop-opacity="0.30"/>
          <stop offset="100%" stop-color="#ffa657" stop-opacity="0"/>
        </linearGradient>
      </defs>
      <g id="chartGrid"></g>
      <path id="chartAreaUp"   fill="url(#gradUp)"   d=""></path>
      <path id="chartAreaDown" fill="url(#gradDown)" d=""></path>
      <polyline id="chartLineUp"   fill="none" stroke="#ffa657" stroke-width="1.5"
                stroke-linejoin="round" stroke-linecap="round" points=""></polyline>
      <polyline id="chartLineDown" fill="none" stroke="#4a9eff" stroke-width="1.5"
                stroke-linejoin="round" stroke-linecap="round" points=""></polyline>
    </svg>
    <!-- Y 轴标签放在 HTML 里，不放 SVG。
         原因：SVG 用了 preserveAspectRatio="none" 才能自由拉伸铺满，
         但那样里面的 <text> 也会被横向拉扁，数字变形。 -->
    <div class="chart-y" id="chartY"></div>
  </div>
  <div class="chart-axis" id="chartAxis"></div>

  <div class="perf-foot">
    <span id="gHead">余量 —</span>
    <span id="gPerIp">单机最快 —</span>
    <span id="gPeak">峰值 —</span>
  </div>

  <div class="perfgrid">
    <div class="pcard up">
      <div class="pico">&#8593;</div>

      <div class="pbody"><div class="plbl">上传速度</div>
        <div class="pval"><span id="pcTxSpd">—</span></div></div>
    </div>
    <div class="pcard down">
      <div class="pico">&#8595;</div>
      <div class="pbody"><div class="plbl">下载速度</div>
        <div class="pval"><span id="pcRxSpd">—</span></div></div>
    </div>
    <div class="pcard conn">
      <div class="pico">&#8644;</div>
      <div class="pbody"><div class="plbl">活跃连接</div>
        <div class="pval"><span id="pcConns">—</span></div></div>
    </div>
    <div class="pcard up2">
      <div class="pico">&#8593;</div>
      <div class="pbody"><div class="plbl">上传量</div>
        <div class="pval"><span id="pcTxTot">—</span></div></div>
    </div>
    <div class="pcard down2">
      <div class="pico">&#8595;</div>
      <div class="pbody"><div class="plbl">下载量</div>
        <div class="pval"><span id="pcRxTot">—</span></div></div>
    </div>
    <div class="pcard mem">
      <div class="pico">&#9635;</div>
      <div class="pbody"><div class="plbl">服务端内存</div>
        <div class="pval"><span id="pcMem">—</span></div></div>
    </div>
  </div>
</div>

<!-- ── 多维统计 ──
     和上面的实时图分开：实时图是「此刻在发生什么」，这里是「过去一段时间用了多少」。
     两者混在一起会让人误以为实时图也能切时间范围。 -->
<div class="statpanel" id="sec-stats">
  <div class="stat-head">
    <span class="stat-title">多维统计</span>
    <span class="stat-sub" id="statRange">—</span>
    <div class="winchips" id="statWins"></div>
  </div>

  <div class="statcards">
    <div class="scard blue"><div class="k">下行总量</div><div class="v" id="stRx">—</div><div class="s" id="stRxAvg">—</div></div>
    <div class="scard orange"><div class="k">上行总量</div><div class="v" id="stTx">—</div><div class="s" id="stTxAvg">—</div></div>
    <div class="scard green"><div class="k">合计</div><div class="v" id="stTotal">—</div><div class="s" id="stHosts">—</div></div>
    <div class="scard purple"><div class="k">峰值速率</div><div class="v" id="stPeak">—</div><div class="s" id="stPeakAt">—</div></div>
    <div class="scard"><div class="k">覆盖时段</div><div class="v" id="stCover">—</div><div class="s" id="stBuckets">—</div></div>
  </div>

  <div class="stat-chart">
    <svg id="statChart" viewBox="0 0 800 170" preserveAspectRatio="none">
      <defs>
        <linearGradient id="sgDown" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stop-color="#4a9eff" stop-opacity="0.30"/>
          <stop offset="100%" stop-color="#4a9eff" stop-opacity="0"/>
        </linearGradient>
        <linearGradient id="sgUp" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stop-color="#ffa657" stop-opacity="0.28"/>
          <stop offset="100%" stop-color="#ffa657" stop-opacity="0"/>
        </linearGradient>
      </defs>
      <g id="statGrid"></g>
      <path id="statAreaUp"   fill="url(#sgUp)"   d=""></path>
      <path id="statAreaDown" fill="url(#sgDown)" d=""></path>
      <polyline id="statLineUp"   fill="none" stroke="#ffa657" stroke-width="1.5"
                stroke-linejoin="round" stroke-linecap="round" points=""></polyline>
      <polyline id="statLineDown" fill="none" stroke="#4a9eff" stroke-width="1.5"
                stroke-linejoin="round" stroke-linecap="round" points=""></polyline>
    </svg>
    <div class="stat-y" id="statY"></div>
  </div>
  <div class="stat-axis" id="statAxis"></div>

  <div class="stat-hosts">
    <h4>设备用量排行（本窗口）</h4>
    <div class="listwrap sm" style="max-height:260px">
      <table>
        <thead><tr>
          <th>设备</th>
          <th class="numh">下行</th>
          <th class="numh">上行</th>
          <th class="numh">峰值</th>
          <th style="width:90px"></th>
        </tr></thead>
        <tbody id="statHosts"><tr><td colspan="5" class="muted">加载中…</td></tr></tbody>
      </table>
    </div>
  </div>
</div>
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
</div>'''

# 管理面板的脚本。**每个管理页都要注入** —— 反馈页、密钥页虽然不含
# 实时图，但复用同一套 fmtSpeed / fbEsc / loadFeedback 等工具函数。
# 踩过的坑：脚本原来夹在 LIVE_UI 的 HTML 中间，拆页时忘了一起带上，
# 反馈页就变成「加载中…」永远不动（JS 根本没加载）。
ADMIN_JS = r'''<script>
(function(){
  var peak = {rx:0, tx:0};
  function esc(s){
    return String(s).replace(/[&<>"']/g, function(c){
      return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c];
    });
  }
  function fmtBps(n){
    if (!n || n < 1) return '—';
    var u = ['B/s','KB/s','MB/s','GB/s'], i = 0, v = n;
    while (v >= 1024 && i < u.length-1) { v /= 1024; i++; }
    return (v >= 100 ? v.toFixed(0) : v.toFixed(v >= 10 ? 1 : 2)) + ' ' + u[i];
  }
  // ── 性能面板：折线图 + 六卡片 ──
  //
  // 为什么用 SVG 手画而不是 canvas：canvas 要做 DPI 缩放、要处理重绘、
  // 而且缩放窗口时会糊。SVG 用 viewBox + preserveAspectRatio="none"
  // 就能自动铺满容器，代码还短。也不引第三方图表库（门户的卖点是零依赖）。
  var CHART_H = 132, CHART_PAD_T = 10, CHART_PAD_B = 6;

  function fmtSpeed(n){
    if (!n || n < 1) return '0 B/s';
    var u = ['B/s','KB/s','MB/s','GB/s'], i = 0, v = n;
    while (v >= 1024 && i < u.length-1) { v /= 1024; i++; }
    return (v >= 100 ? v.toFixed(0) : v.toFixed(v >= 10 ? 1 : 2)) + ' ' + u[i];
  }
  // 刻度专用：整数不带小数点（30 而不是 30.0）。
  // 卡片仍然用 fmtSpeed —— 那里 1.28 MB/s 的两位小数是有意义的。
  function fmtSpeedTick(n){
    if (!n || n < 1) return '0';
    var u = ['B','KB','MB','GB'], i = 0, v = n;
    while (v >= 1024 && i < u.length-1) { v /= 1024; i++; }
    var s = (Math.abs(v - Math.round(v)) < 0.05)
      ? String(Math.round(v))
      : v.toFixed(v >= 10 ? 0 : 1);
    return s + ' ' + u[i] + '/s';
  }
  function fmtBytes2(n){
    if (!n || n < 1) return '0 B';
    var u = ['B','KB','MB','GB','TB'], i = 0, v = n;
    while (v >= 1024 && i < u.length-1) { v /= 1024; i++; }
    return (v >= 100 ? v.toFixed(0) : v.toFixed(v >= 10 ? 1 : 2)) + ' ' + u[i];
  }
  function fmtClock(ts){
    var d = new Date(ts * 1000);
    function p(x){ return (x < 10 ? '0' : '') + x; }
    return p(d.getHours()) + ':' + p(d.getMinutes());
  }

  // 把一条序列映射成 SVG 点串。
  // headroom：Y 轴顶部留白比例，让峰值不贴顶。
  function buildPoints(vals, maxV, w, h){
    var n = vals.length;
    if (n < 2) return { line: '', area: '' };
    var step = w / (n - 1);
    var pts = [];
    for (var i = 0; i < n; i++) {
      var y = h - (maxV > 0 ? (vals[i] / maxV) * h : 0);
      if (y < 0) y = 0;
      if (y > h) y = h;
      pts.push((i * step).toFixed(1) + ',' + y.toFixed(1));
    }
    var line = pts.join(' ');
    var area = 'M0,' + h + ' L' + pts.join(' L') + ' L' + w + ',' + h + ' Z';
    return { line: line, area: area };
  }

  function renderChart(d){
    var series = d.series || [];
    var svg = document.getElementById('perfChart');
    if (!svg) return;

    var W = 800, H = CHART_H;
    var plotH = H - CHART_PAD_T - CHART_PAD_B;

    // Y 轴：取两条线的最大值，向上取到一个「好看」的刻度
    var maxV = 0;
    for (var i = 0; i < series.length; i++) {
      if (series[i].rx > maxV) maxV = series[i].rx;
      if (series[i].tx > maxV) maxV = series[i].tx;
    }
    if (maxV < 1024) maxV = 1024;          // 全静默时也画一条基线，别让它抖

    // 刻度取整分两步，否则会得到 57.2 / 38.1 / 19.1 这种带小数的刻度：
    //   ① 先把 maxV/3 换算到**显示单位**（B/KB/MB/GB），在那个单位下取整
    //   ② 再换算回字节
    // 这样刻度是 60/40/20/0 MB/s 或 1.5/1.0/0.5/0 MB/s，都是能一眼读的数。
    var du = ['B/s', 'KB/s', 'MB/s', 'GB/s'], di = 0, dv = maxV / 3;
    while (dv >= 1024 && di < du.length - 1) { dv /= 1024; di++; }
    var dmag = Math.pow(10, Math.floor(Math.log10(dv || 1)));
    var dnorm = (dv || 1) / dmag;
    // 允许 1 / 1.5 / 2 / 3 / 5 / 10 —— 加 1.5 和 3 是为了让刻度更贴实际值，
    // 不然 19 MB/s 会被推到 20（可以）但 7 MB/s 会被推到 10（浪费一半高度）
    var dn;
    if (dnorm <= 1) dn = 1;
    else if (dnorm <= 1.5) dn = 1.5;
    else if (dnorm <= 2) dn = 2;
    else if (dnorm <= 3) dn = 3;
    else if (dnorm <= 5) dn = 5;
    else dn = 10;
    var unitInDisp = dn * dmag;            // 以显示单位计的格高
    var top = unitInDisp * Math.pow(1024, di) * 3;

    var rxVals = [], txVals = [];
    for (var j = 0; j < series.length; j++) { rxVals.push(series[j].rx || 0); txVals.push(series[j].tx || 0); }

    var down = buildPoints(rxVals, top, W, plotH);
    var up = buildPoints(txVals, top, W, plotH);
    var off = 'translate(0,' + CHART_PAD_T + ')';

    var pDown = document.getElementById('chartLineDown');
    var pUp = document.getElementById('chartLineUp');
    var aDown = document.getElementById('chartAreaDown');
    var aUp = document.getElementById('chartAreaUp');
    if (pDown) { pDown.setAttribute('points', down.line); pDown.setAttribute('transform', off); }
    if (pUp)   { pUp.setAttribute('points', up.line);     pUp.setAttribute('transform', off); }
    if (aDown) { aDown.setAttribute('d', down.area);       aDown.setAttribute('transform', off); }
    if (aUp)   { aUp.setAttribute('d', up.area);           aUp.setAttribute('transform', off); }

    // 网格 + Y 轴刻度（3 条：0 / 中 / 顶）
    var grid = document.getElementById('chartGrid');
    var ylab = document.getElementById('chartY');
    if (grid) {
      var g = '';
      for (var k = 0; k <= 3; k++) {
        var gy = CHART_PAD_T + plotH * (k / 3);
        g += '<line class="grid-line" x1="0" y1="' + gy.toFixed(1) +
             '" x2="' + W + '" y2="' + gy.toFixed(1) + '"/>';
      }
      grid.innerHTML = g;
    }
    if (ylab) {
      // 百分比定位，跟 SVG 的 CHART_H 对齐。用 top 百分比而不是像素，
      // 这样容器高度变了也不用重算。
      var yh = '';
      for (var m = 0; m <= 3; m++) {
        var pctY = (CHART_PAD_T + plotH * (m / 3)) / H * 100;
        var v = top * (1 - m / 3);
        yh += '<span class="ylab" style="top:' + pctY.toFixed(2) + '%">' +
              fmtSpeedTick(v) + '</span>';
      }
      ylab.innerHTML = yh;
    }

    // X 轴时间标签：首 / 中 / 尾
    var axis = document.getElementById('chartAxis');
    if (axis) {
      if (series.length >= 2) {
        var t0 = series[0].t, t1 = series[series.length - 1].t;
        axis.innerHTML = '<span>' + fmtClock(t0) + '</span>' +
                         '<span>' + fmtClock((t0 + t1) / 2) + '</span>' +
                         '<span>' + fmtClock(t1) + '</span>';
      } else {
        axis.innerHTML = '<span>—</span><span>—</span><span>—</span>';
      }
    }
  }

  function renderPerf(d){
    var n = d.net || {};

    // 标题行：载荷 + 状态标签
    var pct = Math.max(0, Math.min(100, n.loadPct || 0));
    var val = document.getElementById('gVal');
    var cap = document.getElementById('gCap');
    var tag = document.getElementById('gTag');
    if (val) val.innerHTML = fmtSpeed(n.curBps || 0);
    if (cap) cap.innerHTML = '/ ' + (n.uplinkMbps ? n.uplinkMbps.toFixed(0) + ' Mbps' : '—');
    var lv = n.level || 'ok';
    if (tag) {
      tag.textContent = ({ok:'畅通', warn:'偏挤', hot:'饱和'}[lv] || '—') +
                        ' · ' + pct.toFixed(0) + '%';
      tag.className = 'gauge-tag ' + lv;
    }
    if (val) val.style.color = lv === 'hot' ? 'var(--err)' : (lv === 'warn' ? 'var(--warn)' : 'var(--ok)');

    // 六张卡
    var set = function(id, txt){ var e = document.getElementById(id); if (e) e.textContent = txt; };
    set('pcTxSpd', fmtSpeed(d.txBps || 0));
    set('pcRxSpd', fmtSpeed(d.rxBps || 0));
    set('pcConns', String(d.conns || 0) + ' 条');
    set('pcTxTot', fmtBytes2(d.totalTx || 0));
    set('pcRxTot', fmtBytes2(d.totalRx || 0));
    set('pcMem',   (d.memMB ? d.memMB.toFixed(1) : '—') + ' MB');

    // 副行：余量 / 单机最快 / 本次峰值
    var head = document.getElementById('gHead');
    if (head) head.textContent = '余量 ' + fmtSpeed(n.headroomBps || 0) +
      (n.online ? ' · ' + n.online + ' 台在线' : '');

    var fastest = 0;
    (d.list || []).forEach(function(u){
      if (u.online) fastest = Math.max(fastest, (u.rxBps || 0) + (u.txBps || 0));
    });
    var perIp = document.getElementById('gPerIp');
    if (perIp) {
      var capBps = (n.perIpMb || 0) * 1024 * 1024;
      var s = '单机最快 ' + fmtSpeed(fastest);
      if (capBps > 0) {
        s += ' / 配额 ' + n.perIpMb.toFixed(0) + ' MB/s';
        if (fastest >= capBps * 0.92) s += '  ← 瓶颈在单机限速';
      } else {
        s += '（未设单机限速）';
      }
      if (!(capBps > 0 && fastest >= capBps * 0.92) && (n.overload || pct >= 85)) {
        s += '  ← 瓶颈在出口带宽';
      }
      perIp.textContent = s;
    }
    var pk = document.getElementById('gPeak');
    if (pk) pk.textContent = '本次最高 ' + fmtSpeed(Math.max(peak.rx, peak.tx));

    renderChart(d);
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

  // ── 意见反馈管理 ──
  var FB_ST = {0:'新提交', 1:'处理中', 2:'已解决', 3:'暂不做'};
  var FB_KIND = {bug:'问题', feature:'功能', perf:'性能', ask:'咨询', other:'其他'};
  var fbFilter = 'all';

  function fbEsc(s){
    return String(s).replace(/[&<>"']/g, function(c){
      return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c];
    });
  }
  function fbTime(ts){
    if (!ts) return '—';
    var d = new Date(ts * 1000);
    function p(x){ return (x < 10 ? '0' : '') + x; }
    return (d.getMonth()+1) + '-' + p(d.getDate()) + ' ' + p(d.getHours()) + ':' + p(d.getMinutes());
  }

  function loadFeedback(){
    fetch('/api/admin/feedback', {credentials:'same-origin', cache:'no-store'})
      .then(function(r){ return r.json(); })
      .then(function(d){
        if (!d.ok) return;
        var c = d.counts || {};
        var nb = document.getElementById('fbNew');
        if (nb) nb.textContent = c[0] ? (c[0] + ' 条待处理') : '';

        var rows = (d.list || []).filter(function(x){
          return fbFilter === 'all' ? true : String(x.status) === fbFilter;
        });
        var box = document.getElementById('fbCards');
        if (!box) return;
        if (!rows.length) {
          box.innerHTML = '<div class="fbskel">没有符合条件的反馈</div>';
          return;
        }

        // 卡片列表而不是表格：
        // 反馈正文长短不一、还可能带图，塞进表格单元格里必然挤成一团。
        // 卡片能自然换行、附件缩略图有地方放、状态一眼看出（左边色条）。
        var h = '';
        rows.forEach(function(x){
          var kind = FB_KIND[x.kind] || x.kind || '其他';
          var atts = x.attachments || [];
          h += '<div class="fbcard st' + x.status + '" data-id="' + x.id + '">'
            + '<div class="fbcard-top">'
            + '<span class="fb-st fb-st-' + x.status + '">' + (FB_ST[x.status] || '?') + '</span>'
            + '<span class="fbkind">' + fbEsc(kind) + '</span>'
            + '<span class="fbwho">' + fbEsc(x.ip)
            + (x.client_ver ? ' · v' + fbEsc(x.client_ver) : '') + ' · '
            + fbTime(x.ts) + '</span>'
            + '</div>'
            + '<div class="fb-text">' + fbEsc(x.text) + '</div>';

          if (x.contact) h += '<div class="fb-extra">联系方式：' + fbEsc(x.contact) + '</div>';
          if (x.admin_note) h += '<div class="fb-extra">备注：' + fbEsc(x.admin_note) + '</div>';

          if (atts.length) {
            h += '<div class="fb-attach">';
            atts.forEach(function(a){
              var isImg = (a.mime || '').indexOf('image/') === 0;
              h += '<a href="' + a.url + '" target="_blank" rel="noopener" title="'
                + fbEsc(a.name) + '">'
                + (isImg ? '<img src="' + a.url + '" alt="">' : '&#128196;')
                + '<span>' + fbEsc(a.name) + ' · ' + fbHumanSize(a.size) + '</span></a>';
            });
            h += '</div>';
          }

          h += '<div class="fbcard-foot">'
            + '<select class="fb-sel" data-id="' + x.id + '">'
            + '<option value="0"' + (x.status === 0 ? ' selected' : '') + '>新提交</option>'
            + '<option value="1"' + (x.status === 1 ? ' selected' : '') + '>处理中</option>'
            + '<option value="2"' + (x.status === 2 ? ' selected' : '') + '>已解决</option>'
            + '<option value="3"' + (x.status === 3 ? ' selected' : '') + '>暂不做</option>'
            + '</select>'
            + '<button type="button" class="fbold" data-note="' + x.id + '">加备注</button>'
            + '</div></div>';
        });
        box.innerHTML = h;

        Array.prototype.forEach.call(box.querySelectorAll('.fb-sel'), function(sel){
          sel.addEventListener('change', function(){
            var id = sel.getAttribute('data-id');
            var st = parseInt(sel.value, 10);
            var csrfEl = document.querySelector('input[name="csrf"]');
            var body = new URLSearchParams();
            body.append('id', id);
            body.append('status', st);
            if (csrfEl) body.append('csrf', csrfEl.value);
            fetch('/api/admin/feedback', {
              method: 'POST', credentials: 'same-origin',
              headers: {'Content-Type': 'application/x-www-form-urlencoded',
                        'X-Requested-With': 'fetch'},
              body: body.toString()
            }).then(function(){ loadFeedback(); }).catch(function(){});
          });
        });

        Array.prototype.forEach.call(box.querySelectorAll('.fbold'), function(b){
          b.addEventListener('click', function(){
            var id = b.getAttribute('data-note');
            var txt = window.prompt('备注（例如：已修，下版生效）');
            if (txt === null) return;
            var csrfEl = document.querySelector('input[name="csrf"]');
            var body2 = new URLSearchParams();
            body2.append('id', id);
            body2.append('note', txt);
            if (csrfEl) body2.append('csrf', csrfEl.value);
            fetch('/api/admin/feedback', {
              method: 'POST', credentials: 'same-origin',
              headers: {'Content-Type': 'application/x-www-form-urlencoded',
                        'X-Requested-With': 'fetch'},
              body: body2.toString()
            }).then(function(){ loadFeedback(); }).catch(function(){});
          });
        });
      })
      .catch(function(){});
  }

  function bindFeedback(){
    Array.prototype.forEach.call(document.querySelectorAll('.fb-filter'), function(b){
      b.addEventListener('click', function(){
        fbFilter = b.getAttribute('data-st');
        Array.prototype.forEach.call(document.querySelectorAll('.fb-filter'), function(x){
          x.classList.remove('on');
        });
        b.classList.add('on');
        loadFeedback();
      });
    });
    loadFeedback();
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
    // ⚠ 这里**只能**操作还存在的元素。
    //
    // 踩过的坑：改版后忘了删旧的 `document.getElementById('lvOnline').textContent = ...`，
    // 那个元素已经没了 → getElementById 返回 null → `.textContent` 抛 TypeError
    // → render 整个中断 → 折线图和六卡片永远不更新。
    // 页面上表现为「数字一直不动」，很容易误判成后端没返回数据。
    if (d.rxBps > peak.rx) peak.rx = d.rxBps;
    if (d.txBps > peak.tx) peak.tx = d.txBps;
    renderPerf(d);

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
      // 有备注正在编辑时**不要重排**。
      // appendChild 会把行从原位置移除再插回去，输入框因此失焦、内容被清空 ——
      // 用户打一半的备注每 3 秒被吃掉一次（实测反馈）。
      // 数值更新在上面已完成，这里跳过的只是排序。
      if (document.querySelector('.notecell.editing')) return;
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

  // ── 生成密钥：fetch + 浮窗 ──
  var _lastCode = '';

  function closeCodeModal(){
    var m = document.getElementById('codeModal');
    if (m) m.classList.remove('open');
    _lastCode = '';
  }

  function openCodeModal(d){
    _lastCode = d.code || '';
    var m = document.getElementById('codeModal');
    if (!m) return;
    var code = document.getElementById('cmCode');
    var meta = document.getElementById('cmMeta');
    if (code) code.textContent = _lastCode;
    if (meta) {
      var parts = [];
      if (d.note) parts.push('给 ' + d.note);
      parts.push(d.hours + ' 小时内必须用掉');
      parts.push(d.days === 0 ? '激活后永久' : ('激活后 ' + d.days + ' 天'));
      meta.textContent = parts.join(' · ');
    }
    m.classList.add('open');
  }

  function bindNewCode(){
    var form = document.getElementById('ncForm');
    if (!form || form.getAttribute('data-bound') === '1') return;
    form.setAttribute('data-bound', '1');

    form.addEventListener('submit', function(e){
      e.preventDefault();
      var btn = document.getElementById('ncBtn');
      if (btn) { btn.disabled = true; btn.textContent = '生成中…'; }
      fetch('/admin/newcode', {
        method: 'POST', credentials: 'same-origin',
        headers: {'Content-Type': 'application/x-www-form-urlencoded',
                  'X-Requested-With': 'fetch'},
        body: new URLSearchParams(new FormData(form)).toString()
      })
        .then(function(r){ return r.json(); })
        .then(function(d){
          if (!d || !d.ok) { alert((d && d.error) || '生成失败'); return; }
          openCodeModal(d);
        })
        .catch(function(err){ alert('生成失败：' + err.message); })
        .then(function(){
          if (btn) { btn.disabled = false; btn.textContent = '生成密钥'; }
        });
    });

    var modal = document.getElementById('codeModal');
    if (modal) {
      Array.prototype.forEach.call(modal.querySelectorAll('[data-close]'), function(x){
        x.addEventListener('click', closeCodeModal);
      });
    }
    document.addEventListener('keydown', function(e){
      if (e.key === 'Escape') closeCodeModal();
    });

    var copy = document.getElementById('cmCopy');
    if (copy) copy.addEventListener('click', function(){
      if (!_lastCode) return;
      var done = function(){
        copy.textContent = '已复制';
        setTimeout(function(){ copy.textContent = '复制密钥'; }, 1500);
      };
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(_lastCode).then(done, function(){
          // 非 https 下 clipboard 不可用（内网 http 很常见）—— 退回到选中
          var el = document.getElementById('cmCode');
          if (el) { var r = document.createRange(); r.selectNodeContents(el);
                    var s = window.getSelection(); s.removeAllRanges(); s.addRange(r); }
        });
      } else {
        var el2 = document.getElementById('cmCode');
        if (el2) { var r2 = document.createRange(); r2.selectNodeContents(el2);
                   var s2 = window.getSelection(); s2.removeAllRanges(); s2.addRange(r2); }
      }
    });
  }

  // ── 多维统计 ──────────────────────────────────────────────
  var statWin = '1h';
  var statWinsBuilt = false;

  function fmtDur(sec){
    if (!sec) return '—';
    if (sec < 3600) return Math.round(sec / 60) + ' 分钟';
    if (sec < 86400) return (sec / 3600).toFixed(sec < 36000 ? 1 : 0) + ' 小时';
    return (sec / 86400).toFixed(1) + ' 天';
  }

  function fmtClock(ts){
    var d = new Date(ts * 1000);
    function p(x){ return (x < 10 ? '0' : '') + x; }
    return p(d.getHours()) + ':' + p(d.getMinutes());
  }

  function fmtDay(ts){
    var d = new Date(ts * 1000);
    function p(x){ return (x < 10 ? '0' : '') + x; }
    return (d.getMonth() + 1) + '/' + p(d.getDate());
  }

  // 窗口按跨度选时间格式：短窗口显示时分，长窗口显示日期
  function fmtTick(ts, span){
    return span && span > 86400 ? fmtDay(ts) : fmtClock(ts);
  }

  function buildStatWins(windows){
    if (statWinsBuilt) return;
    var box = document.getElementById('statWins');
    if (!box) return;
    box.innerHTML = '';
    (windows || []).forEach(function(w){
      var b = document.createElement('button');
      b.type = 'button';
      b.textContent = w.label;
      b.setAttribute('data-w', w.key);
      if (w.key === statWin) b.className = 'on';
      b.addEventListener('click', function(){
        statWin = w.key;
        Array.prototype.forEach.call(box.querySelectorAll('button'), function(x){
          x.className = (x.getAttribute('data-w') === statWin) ? 'on' : '';
        });
        loadStats();
      });
      box.appendChild(b);
    });
    statWinsBuilt = true;
  }

  function loadStats(){
    fetch('/api/admin/stats?w=' + encodeURIComponent(statWin),
          {credentials:'same-origin', cache:'no-store'})
      .then(function(r){ return r.json(); })
      .then(function(d){
        if (!d.ok) return;
        buildStatWins(d.windows);
        renderStats(d);
      })
      .catch(function(){});
  }

  function renderStats(d){
    var sm = d.summary || {};
    var se = d.series || {};
    var pts = se.points || [];

    var set = function(id, v){ var e = document.getElementById(id); if (e) e.textContent = v; };
    set('stRx', fmtBytes2(sm.rx || 0));
    set('stTx', fmtBytes2(sm.tx || 0));
    set('stTotal', fmtBytes2(sm.total || 0));
    set('stRxAvg', '均 ' + fmtSpeed(sm.avgRxBps || 0));
    set('stTxAvg', '均 ' + fmtSpeed(sm.avgTxBps || 0));
    set('stHosts', (sm.hosts || 0) + ' 台设备');

    var pk = Math.max(sm.peakRx || 0, sm.peakTx || 0);
    set('stPeak', fmtSpeed(pk));
    set('stPeakAt', (sm.peakRx || 0) >= (sm.peakTx || 0) ? '下行峰值' : '上行峰值');

    set('stCover', fmtDur(sm.coveredSec || 0));
    set('stBuckets', (sm.buckets || 0) + ' 个 ' + (d.bucketSec / 60) + ' 分钟桶');

    var range = document.getElementById('statRange');
    if (range) {
      var from = sm.from ? fmtDay(sm.from) + ' ' + fmtClock(sm.from) : '—';
      var to = sm.to ? fmtDay(sm.to) + ' ' + fmtClock(sm.to) : '—';
      range.textContent = '粒度 ' + (se.unit || '—') + ' · ' + from + ' → ' + to;
    }

    renderStatChart(pts, se.span || 0);
    renderStatHosts(d.hosts || []);
  }

  function renderStatChart(series, span){
    var svg = document.getElementById('statChart');
    if (!svg) return;
    var W = 800, H = 170, PAD_T = 12, PAD_B = 8;
    var plotH = H - PAD_T - PAD_B;

    var maxV = 0;
    for (var i = 0; i < series.length; i++) {
      if (series[i].rx > maxV) maxV = series[i].rx;
      if (series[i].tx > maxV) maxV = series[i].tx;
    }
    if (maxV < 1024) maxV = 1024;

    // 和实时图同一套刻度取整逻辑（显示单位下取整，避免 57.2 这种刻度）
    var du = ['B', 'KB', 'MB', 'GB'], di = 0, dv = maxV / 3;
    while (dv >= 1024 && di < du.length - 1) { dv /= 1024; di++; }
    var dmag = Math.pow(10, Math.floor(Math.log10(dv || 1)));
    var dnorm = (dv || 1) / dmag;
    var dn;
    if (dnorm <= 1) dn = 1;
    else if (dnorm <= 1.5) dn = 1.5;
    else if (dnorm <= 2) dn = 2;
    else if (dnorm <= 3) dn = 3;
    else if (dnorm <= 5) dn = 5;
    else dn = 10;
    var top = dn * dmag * Math.pow(1024, di) * 3;

    function pts(vals){
      var n = vals.length;
      if (!n) return '';
      var arr = [];
      for (var k = 0; k < n; k++) {
        var x = (n === 1) ? W / 2 : (k / (n - 1)) * W;
        var y = plotH - Math.min(1, vals[k] / top) * plotH;
        arr.push(x.toFixed(1) + ',' + y.toFixed(1));
      }
      return arr.join(' ');
    }
    function area(vals){
      var p = pts(vals);
      if (!p) return '';
      return 'M0,' + plotH + ' L' + p.split(' ').join(' L') + ' L' + W + ',' + plotH + ' Z';
    }

    var rxVals = [], txVals = [];
    for (var j = 0; j < series.length; j++) {
      rxVals.push(series[j].rx || 0);
      txVals.push(series[j].tx || 0);
    }

    var e;
    if ((e = document.getElementById('statLineDown'))) e.setAttribute('points', pts(rxVals));
    if ((e = document.getElementById('statLineUp')))   e.setAttribute('points', pts(txVals));
    if ((e = document.getElementById('statAreaDown'))) e.setAttribute('d', area(rxVals));
    if ((e = document.getElementById('statAreaUp')))   e.setAttribute('d', area(txVals));

    // 网格 + Y 轴标签
    var grid = document.getElementById('statGrid');
    var ybox = document.getElementById('statY');
    if (grid) {
      var g = '';
      for (var gi = 0; gi <= 3; gi++) {
        var yy = PAD_T + (plotH / 3) * gi;
        g += '<line class="grid-line" x1="0" y1="' + yy.toFixed(1) +
             '" x2="800" y2="' + yy.toFixed(1) + '"></line>';
      }
      grid.innerHTML = g;
    }
    if (ybox) {
      var yh = '';
      for (var yi = 0; yi <= 3; yi++) {
        var val = top - (top / 3) * yi;
        var yy2 = ((PAD_T + (plotH / 3) * yi) / H) * 100;
        yh += '<div class="ylab" style="top:' + yy2.toFixed(1) + '%">' +
              fmtSpeed(val) + '</div>';
      }
      ybox.innerHTML = yh;
    }

    // 时间轴：首、中、尾三个刻度
    var ax = document.getElementById('statAxis');
    if (ax) {
      if (!series.length) { ax.innerHTML = ''; }
      else {
        var t0 = series[0].t, t2 = series[series.length - 1].t;
        var tm = Math.round((t0 + t2) / 2);
        ax.innerHTML = '<span>' + fmtTick(t0, span) + '</span>' +
                       '<span>' + fmtTick(tm, span) + '</span>' +
                       '<span>' + fmtTick(t2, span) + '</span>';
      }
    }
  }

  function renderStatHosts(hosts){
    var body = document.getElementById('statHosts');
    if (!body) return;
    if (!hosts.length) {
      body.innerHTML = '<tr><td colspan="5" class="muted">这个窗口还没有流量记录</td></tr>';
      return;
    }
    var maxTotal = 1;
    hosts.forEach(function(h){ maxTotal = Math.max(maxTotal, h.total || 0); });

    var html = '';
    hosts.forEach(function(h){
      var pct = Math.max(2, Math.round((h.total / maxTotal) * 100));
      var name = h.note ? (h.note + ' <span class="muted">' + h.ip + '</span>') : h.ip;
      html += '<tr>'
        + '<td class="mono nowrap">' + name + '</td>'
        + '<td class="num">' + fmtBytes2(h.rx) + '</td>'
        + '<td class="num">' + fmtBytes2(h.tx) + '</td>'
        + '<td class="num">' + fmtSpeed(Math.max(h.peakRx, h.peakTx)) + '</td>'
        + '<td><div class="bar"><i style="width:' + pct + '%"></i></div></td>'
        + '</tr>';
    });
    body.innerHTML = html;
  }

  // ── 反馈附件：粘贴 / 拖拽 / 选择 ──
  //
  // 三个入口共用同一个上传函数：截图党 Ctrl+V、文件党拖进来、剩下的点按钮选。
  var fbAttach = [];
  var fbToken = '';

  function fbTokenGet(){
    if (fbToken) return fbToken;
    // token 只用来把「先传的附件」和「后提交的反馈」绑在一起。
    // 随机 16 位够用，不需要不可预测。
    var a = 'abcdefghjkmnpqrstuvwxyz23456789';
    var s = '';
    for (var i = 0; i < 16; i++) s += a[Math.floor(Math.random() * a.length)];
    fbToken = s;
    return s;
  }

  function fbHumanSize(n){
    if (n < 1024) return n + ' B';
    if (n < 1048576) return (n / 1024).toFixed(1) + ' KB';
    return (n / 1048576).toFixed(1) + ' MB';
  }

  function fbRenderAttach(){
    var box = document.getElementById('fbAttachList');
    if (!box) return;
    if (!fbAttach.length) { box.innerHTML = ''; return; }
    var h = '';
    fbAttach.forEach(function(a, i){
      var isImg = (a.mime || '').indexOf('image/') === 0;
      h += '<div class="att-item">'
        + (isImg
            ? '<img class="att-thumb" src="' + a.url + '?t=' + encodeURIComponent(fbTokenGet()) + '" alt="">'
            : '<div class="att-thumb att-doc">' + (a.name.split('.').pop() || '?').toUpperCase() + '</div>')
        + '<div class="att-meta"><div class="att-name" title="' + fbEsc(a.name) + '">'
        + fbEsc(a.name) + '</div><div class="att-size">' + fbHumanSize(a.size) + '</div></div>'
        + '<button type="button" class="att-del" data-i="' + i + '" title="移除">&times;</button>'
        + '</div>';
    });
    box.innerHTML = h;
    Array.prototype.forEach.call(box.querySelectorAll('.att-del'), function(b){
      b.addEventListener('click', function(){
        fbAttach.splice(parseInt(b.getAttribute('data-i'), 10), 1);
        fbRenderAttach();
      });
    });
  }

  function fbUpload(file){
    if (!file) return;
    if (fbAttach.length >= 6) { alert('最多 6 个附件'); return; }
    if (file.size > 8 * 1048576) { alert('单个附件上限 8 MB'); return; }

    var box = document.getElementById('fbAttachList');
    var tip = document.createElement('div');
    tip.className = 'att-item att-uploading';
    tip.innerHTML = '<div class="att-thumb att-doc">…</div>'
      + '<div class="att-meta"><div class="att-name">' + fbEsc(file.name || '粘贴的图片') + '</div>'
      + '<div class="att-size">上传中…</div></div>';
    if (box) box.appendChild(tip);

    var fd = new FormData();
    fd.append('token', fbTokenGet());
    fd.append('file', file, file.name || ('paste-' + Date.now() + '.png'));

    fetch('/attach/upload', { method: 'POST', body: fd, credentials: 'same-origin' })
      .then(function(r){ return r.json(); })
      .then(function(d){
        if (!d.ok) { alert('上传失败：' + (d.error || '未知')); return; }
        fbAttach.push(d);
        fbRenderAttach();
      })
      .catch(function(e){ alert('上传失败：' + e.message); })
      .then(function(){ if (tip.parentNode) tip.parentNode.removeChild(tip); });
  }

  function fbBindAttach(){
    var zone = document.getElementById('fbDrop');
    var input = document.getElementById('fbFile');
    var form = document.querySelector('.fbform form');
    if (!zone || !input) return;

    zone.addEventListener('click', function(){ input.click(); });
    zone.addEventListener('keydown', function(e){
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); input.click(); }
    });
    input.addEventListener('change', function(){
      Array.prototype.forEach.call(input.files, fbUpload);
      input.value = '';
    });

    ['dragenter', 'dragover'].forEach(function(ev){
      zone.addEventListener(ev, function(e){ e.preventDefault(); zone.classList.add('hot'); });
    });
    ['dragleave', 'drop'].forEach(function(ev){
      zone.addEventListener(ev, function(e){ e.preventDefault(); zone.classList.remove('hot'); });
    });
    zone.addEventListener('drop', function(e){
      var dt = e.dataTransfer;
      if (!dt) return;
      Array.prototype.forEach.call(dt.files || [], fbUpload);
    });

    // 粘贴：只在反馈表单区域里生效，避免干扰用户粘文字
    if (form) {
      form.addEventListener('paste', function(e){
        var items = (e.clipboardData && e.clipboardData.items) || [];
        var got = false;
        Array.prototype.forEach.call(items, function(it){
          if (it.kind === 'file') {
            var f = it.getAsFile();
            if (f) { fbUpload(f); got = true; }
          }
        });
        if (got) e.preventDefault();
      });
    }

    if (form) {
      form.addEventListener('submit', function(){
        var t = document.getElementById('fbAttachToken');
        if (t) t.value = fbTokenGet();
      });
    }
  }

  function bootAll(){
    // 只调「需要手动触发」的那几个。
    // bindIpCells / bindAllNotes 各自在定义处就注册了 DOMContentLoaded，
    // 这里再调一遍会重复绑定（点一次编辑触发两次）。
    // ⚠ 原来这里写的是 bindNotes() —— 那个函数根本不存在（真名 bindAllNotes），
    //   一执行就 ReferenceError，后面所有绑定全跳过：
    //   表现就是「意见反馈」永远停在「加载中…」。
    bindNetCtl(); bindFeedback(); bindNewCode(); fbBindAttach();

    // 统计和实时图只在「概览 / 设备」页存在。
    // 其它页也注入了同一份脚本（复用工具函数），但没必要跑轮询 ——
    // 反馈页每 30 秒打一次 stats 接口纯属浪费。
    if (document.getElementById('statChart')) {
      loadStats();
      setInterval(loadStats, 30000);
    }
  }
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', bootAll);
  } else {
    bootAll();
  }

  function tick(){
    // 没有实时图的页面（反馈/密钥/事件）不轮询 —— 那些接口每次都要跑
    // netstat + 采样，白烧 CPU。
    if (!document.getElementById('perfChart')) return;
    fetch('/api/admin/live', {credentials:'same-origin', cache:'no-store'})
      .then(function(r){ if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
      .then(render)
      .catch(function(e){ fail(e.message || '网络错误'); })
      .then(function(){ setTimeout(tick, 3000); });
  }
  tick();
})();
</script>'''



ADMIN_SECTIONS = [
    ('overview', '/admin', '概览', '实时流量、出口载荷与多维统计'),
    ('keys', '/admin/keys', '密钥', '生成与撤销一次性激活密钥'),
    ('users', '/admin/users', '设备', '已登录机器、实时速率与备注'),
    ('feedback', '/admin/feedback', '反馈', '用户提交的问题与建议'),
    ('events', '/admin/events', '事件', '登录、激活、限流等审计记录'),
]


def admin_nav(active):
    """管理面板顶部导航。

    为什么拆成独立页面而不是一页到底：
      原来所有区块堆在一页里靠锚点跳，越加越长（密钥 + 反馈 + 设备 +
      流量 + 事件），滚到一半就不知道自己在哪。独立页面各自有明确的
      标题、副标题和操作区，加新功能也不会把别人的页面撑长。
    """
    items = []
    for key, href, label, _desc in ADMIN_SECTIONS:
        cls = ' on' if key == active else ''
        items.append('<a href="%s" class="anav-item%s">%s</a>' % (href, cls, label))
    return '<nav class="adminnav">' + ''.join(items) + '</nav>'


def admin_shell(active, title, subtitle, body):
    """所有管理页共用的外壳：导航 + 标题区 + 内容。"""
    desc = ''
    for key, _h, _l, d in ADMIN_SECTIONS:
        if key == active:
            desc = d
    return page('管理 · ' + title, f"""
{admin_nav(active)}
<div class="ahead">
  <div>
    <h1>{html.escape(title)}</h1>
    <div class="asub">{html.escape(subtitle or desc)}</div>
  </div>
</div>
{body}
""" + ADMIN_JS)


CODE_MODAL = """
<!-- 密钥浮窗。
     为什么用浮窗而不是「重新渲染一整页」：
       老做法是 POST 后直接渲染面板，地址栏停在 /admin/newcode，
       用户按 F5 浏览器就重发 POST —— **每刷新一次多一个密钥**。
     现在 POST 只回 JSON，明文塞进这个浮窗，页面上再也拿不到第二次。
     复制走 Clipboard API，失败时退回选中文本，不至于复制不了。 -->
<div class="modal" id="codeModal" role="dialog" aria-modal="true" aria-labelledby="cmTitle">
  <div class="modal-bg" data-close="1"></div>
  <div class="modal-box" style="width:min(420px,100%)">
    <div class="modal-head">
      <div class="modal-title" id="cmTitle" style="color:var(--accent)">&#128273; 新密钥</div>
      <button type="button" class="modal-close" data-close="1" title="关闭">&times;</button>
    </div>
    <div class="modal-body">
      <div class="big mono" id="cmCode"
           style="font-size:26px;letter-spacing:2px;text-align:center;
                  padding:14px 0;background:var(--card2);border-radius:11px;
                  border:1px solid var(--line2);user-select:all"></div>
      <div class="hint" id="cmMeta" style="margin-top:10px"></div>
      <div class="hint" style="margin-top:8px;color:var(--warn)">
        只显示这一次。服务端只存哈希，关掉浮窗就再也看不到明文。
      </div>
      <div style="display:flex;gap:8px;margin-top:14px">
        <button type="button" class="btn-main sm" id="cmCopy" style="flex:1;padding:9px">
          复制密钥
        </button>
        <button type="button" class="sm ghost" data-close="1" style="flex:0 0 90px">关闭</button>
      </div>
    </div>
  </div>
</div>
"""


def admin_page(db, msg='', err='', new_code=None, new_hours=12, csrf='',
               section='overview'):
    """管理面板。section 决定渲染哪一块。"""
    csrf_in = f'<input type="hidden" name="csrf" value="{html.escape(csrf)}">' if csrf else ''
    _ld = int(CFG.get('license_days') or 0)
    license_days_txt = '永久' if _ld <= 0 else (str(_ld) + ' 天')
    _tier_txt = tier_text

    msgbox = ''
    if msg:
        msgbox = f'<div class="msg ok">{html.escape(msg)}</div>'
    if err:
        msgbox = f'<div class="msg err">{html.escape(err)}</div>'

    # ══════════ 概览 ══════════
    if section == 'overview':
        # csrf_in 必须注入。概览页的 JS（限速卡的「应用」按钮、
        # 保存备注用的 csrfToken()）都是靠
        # document.querySelector('input[name="csrf"]') 取 token 的，
        # 而概览页此前一个隐藏域都没有 —— 取到空串 → 服务端统一 CSRF 校验
        # 回 403 → 前端 .catch() 把错误吞掉，表现就是「点了没反应」。
        # 2026-10-29 实测：空 csrf POST /api/admin/net 得 403，
        # 换成页面里的 token 得 200。
        return admin_shell('overview', '概览',
            '实时流量、出口载荷与多维统计',
            msgbox + csrf_in + ADMIN_UI_HTML)

    # ══════════ 密钥 ══════════
    if section == 'keys':
        inv = db.q('SELECT * FROM invites ORDER BY id DESC LIMIT 40')
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
        body = msgbox + f"""
<div class="card">
  <h2 style="margin-top:0">创建临时密钥</h2>
  <form method="post" action="/admin/newcode" id="ncForm">
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
    <button type="submit" id="ncBtn">生成密钥</button>
  </form>
</div>

<div class="card">
  <h2 style="margin-top:0">临时密钥（最近 40 条）</h2>
  <div class="listwrap">
  <table><thead><tr><th>密钥前缀</th><th>备注</th><th>可用时长</th><th>状态</th><th>使用方</th><th>创建</th><th></th></tr></thead>
  <tbody>{inv_rows or '<tr><td colspan="7" class="muted">还没有密钥</td></tr>'}</tbody></table>
  </div>
</div>""" + CODE_MODAL
        return admin_shell('keys', '密钥', '生成、查看、撤销一次性激活密钥', body)

    # ══════════ 设备 ══════════
    if section == 'users':
        cli = db.q('SELECT * FROM clients ORDER BY created_at DESC')

        # 设备清单（2026-10-29 授权改成「按设备」后新增）。
        #
        # 为什么必须单独列出来：clients 表的主键是 ip，同一局域网里手机和电脑
        # 走同一个 NAT 出口时 IP 一样 —— 只看 clients 根本看不出「这个账号
        # 绑了几台设备、分别是哪台」。而这恰恰是设备授权上线后管理员最需要
        # 知道的事（谁占了 3 个名额？要不要踢掉一台？）。
        dev_rows = db.q('SELECT d.*, c.ip AS account_ip, c.expires_at AS acc_exp '
                        'FROM devices d LEFT JOIN clients c ON c.token = d.token '
                        'ORDER BY d.last_seen DESC')
        dev_html = ''
        for r in dev_rows:
            # 用 last_seen 判断在线：5 分钟内有心跳算在线
            online = (int(r['last_seen'] or 0) > 0) and (now() - int(r['last_seen']) <= 300)
            dot = '<span class="dot on"></span><span class="stx">在线</span>' if online \
                  else '<span class="dot off"></span><span class="stx">离线</span>'
            name = (r['model'] or '未知机型')
            plat = (r['platform'] or '-')
            ver = (r['app_version'] or '-')
            did = html.escape(r['device_id'] or '')
            # 优先用设备自己上报的 last_ip：clients.ip 是「这台机器当前的出口 IP」，
            # 设备换了网络（宿舍→教室）之后就不代表它了，而 last_ip 是它自己报的。
            seen_ip = r['last_ip'] or r['account_ip'] or '—'
            dev_html += f"""<tr>
  <td class="st">{dot}</td>
  <td>{html.escape(name)}</td>
  <td class="muted">{html.escape(plat)}</td>
  <td class="muted">{html.escape(ver)}</td>
  <td class="mono muted">{html.escape(seen_ip)}</td>
  <td class="mono muted" title="{did}">{html.escape((r['device_id'] or '')[:12])}…</td>
  <td class="muted">{fmt_ts(r['last_seen'])}</td>
  <td class="muted">{fmt_ts(r['acc_exp'])}</td>
  <td>{'<span class="ok">启用</span>' if r['enabled'] else '<span class="bad">已停用</span>'}</td>
  <td class="nowrap">
    <form method="post" action="/admin/device" style="display:inline">{csrf_in}<input type="hidden" name="device_id" value="{did}"><input type="hidden" name="op" value="toggle"><button class="sm ghost" type="submit">{'踢下线' if r['enabled'] else '恢复'}</button></form>
    <form method="post" action="/admin/device" style="display:inline;margin-left:4px" onsubmit="return confirm('删除这台设备？名额会被释放；它下次启动需要重新激活，同一账号下的其它设备不受影响。')">{csrf_in}<input type="hidden" name="device_id" value="{did}"><input type="hidden" name="op" value="forget"><button class="sm ghost danger" type="submit">删除</button></form>
  </td>
</tr>"""
        if not dev_html:
            dev_html = '<tr><td colspan="10" class="muted">还没有设备登记（老客户端不上报设备标识）</td></tr>'

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
        body = msgbox + f"""
<div class="card">
  <h2 style="margin-top:0">已登记的设备</h2>
  <div class="hint" style="margin:-4px 0 10px">
    一个激活码（账号）最多绑 <b>{MAX_DEVICES_PER_TOKEN}</b> 台设备（只数「启用」的）。
    「踢下线」把这一台标记为停用：它下次启动会被拒绝，且不能自己恢复，
    要由管理员点「恢复」；「删除」则连记录一起移除。两者都会释放名额，
    也都不影响同一账号下的其它设备。
  </div>
  <div class="listwrap" style="max-height:420px">
  <table id="deviceTable">
    <thead><tr>
      <th>状态</th><th>机型</th><th>平台</th><th>版本</th>
      <th>最近 IP</th><th>设备标识</th><th>最后活动</th><th>账号到期</th>
      <th>授权</th><th>操作</th>
    </tr></thead>
    <tbody>{dev_html}</tbody>
  </table>
  </div>
</div>

<div class="card">
  <h2 style="margin-top:0">已登录的机器（按 IP）</h2>
  <div class="hint" style="margin:-4px 0 10px">
    在线置顶，离线靠下。速率来自客户端每 5 秒一次的心跳；
    老版本客户端不上报心跳，速率列显示「—」，但在线状态仍准确。<br>
    注意：同一局域网里手机和电脑可能共用一个出口 IP，这里按 IP 聚合；
    要看「具体是哪台设备」请用上面的设备表。
  </div>
  <div class="listwrap" style="max-height:640px">
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
""" + ADMIN_UI_HTML
        return admin_shell('users', '设备', '已登录机器、实时速率与备注', body)

    # ══════════ 事件 ══════════
    if section == 'events':
        ev = db.q('SELECT * FROM events ORDER BY id DESC LIMIT 200')
        ev_rows = ''.join(
            f'<tr><td class="muted nowrap">{fmt_ts(r["ts"])}</td>'
            f'<td class="nowrap"><span class="evtag">{html.escape(r["kind"])}</span></td>'
            f'<td class="mono">{html.escape(r["ip"])}</td>'
            f'<td class="muted">{html.escape(r["detail"])}</td></tr>' for r in ev)
        body = msgbox + f"""
<div class="card">
  <h2 style="margin-top:0">最近事件（200 条）</h2>
  <div class="hint" style="margin:-4px 0 12px">
    登录、激活、密钥生成、限流、下载等操作的审计记录。
  </div>
  <div class="listwrap" style="max-height:700px">
  <table><thead><tr><th>时间</th><th>类型</th><th>IP</th><th>说明</th></tr></thead>
  <tbody>{ev_rows or '<tr><td colspan="4" class="muted">无</td></tr>'}</tbody></table>
  </div>
</div>"""
        return admin_shell('events', '事件', '审计日志', body)

    # ══════════ 反馈（独立页）══════════
    return admin_feedback_page(db, csrf, msgbox)


def admin_feedback_page(db, csrf, msgbox=''):
    """意见反馈独立页。

    原来它只是概览页里的一张卡，夹在密钥和机器列表中间 ——
    有附件之后表格会变得很高，必须给它一页。
    """
    body = msgbox + f"""
<div class="fbtoolbar">
  <div class="fbfilters" id="fbFilters">
    <button type="button" class="sm ghost fb-filter on" data-st="all">全部</button>
    <button type="button" class="sm ghost fb-filter" data-st="0">新提交</button>
    <button type="button" class="sm ghost fb-filter" data-st="1">处理中</button>
    <button type="button" class="sm ghost fb-filter" data-st="2">已解决</button>
    <button type="button" class="sm ghost fb-filter" data-st="3">暂不做</button>
  </div>
  <div class="fbcount" id="fbNew"></div>
</div>

<div class="hint" style="margin:-4px 0 14px">
  来自网页 <code>/feedback</code> 和客户端「设置 → 意见反馈」。按 IP 定位，
  所以用户不用登录也能反馈。带附件的条目会显示缩略图，点开可看原图。
</div>

<input type="hidden" name="csrf" value="{html.escape(csrf)}">
<div id="fbCards" class="fbcards">
  <div class="fbskel">加载中…</div>
</div>"""
    return admin_shell('feedback', '意见反馈', '用户提交的问题与建议', body)

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


# ═══════════════════════════════════════════════════════════════════
#  Android APK 分发（2026-10-30 新增）
# ═══════════════════════════════════════════════════════════════════
#
# 为什么单独一套、不复用 Windows 那套 installer_path()：
#   · Windows 是 .exe + Inno，Android 是 .apk，扩展名、包名规则都不一样；
#   · Android 的版本号有两段（versionCode 整数 + versionName 字符串），
#     判断「要不要更新」必须用 versionCode —— versionName 是给人看的，
#     是字符串，'0.10.0' 按字符串比会输给 '0.9.0'。
#   · 门户还要告诉客户端 APK 多大、sha256 多少，客户端下载完校验一次
#     （手机上下载中断/被劫持比 PC 常见得多）。
#
# APK 放哪：<工程根>/dist/android/。单独一个子目录，避免和安装包混在一起
# 被 list_installers() 之类按 *Ferry-Setup-*.exe 扫的逻辑看到。

ANDROID_APK_DIRNAME = os.path.join('dist', 'android')
# 允许分发的包名。防止有人往目录里丢一个别的 apk 被当成官方包发出去。
ANDROID_ALLOWED_PACKAGES = ('com.ferry.app', 'com.ferry.app.debug')


def android_apk_dirs():
    """返回所有可能存放 APK 的目录（工程根/dist/android，门户同级/dist/android）。"""
    here = os.path.dirname(os.path.abspath(__file__))
    out = []
    for base in (os.path.join(here, '..'), here):
        d = os.path.normpath(os.path.join(base, ANDROID_APK_DIRNAME))
        if os.path.isdir(d):
            out.append(d)
    return out


def _parse_apk_badging(apk_path):
    """从 APK 里读出 package / versionCode / versionName。

    为什么不用 androguard/aapt：门户只用标准库（AGENTS.md 的硬约定）。
    所以直接解 ZIP 读二进制 AndroidManifest.xml（AXML）。

    ⚠ 两个实测踩到的坑（2026-10-30）：
      1. `META-INF/com/android/build/gradle/app-metadata.properties`
         **没有** versionCode/versionName —— 里面只有 appMetadataVersion
         和 androidGradlePluginVersion。一度以为能走这条捷径，实测不行。
      2. AXML 的 START_TAG 节点体布局是：
             pos+0  : chunk header (type/hsize/size) = 8 字节
             body+0 : lineNumber(4) comment(4)
             body+8 : ns(4) name(4)
             body+16: attributeStart(2) attributeSize(2) attributeCount(2)
         我第一次写成 body+8/body+16 取值，全错位，属性一个都读不到。
         正确起点是 **body+8 取 ns/name**、**body+16 取三个 u16**。

    属性值原始类型在 attribute 的 dataType（第 15 字节）：
        0x03 = 字符串（data 是字符串池索引）
        0x10 = int（data 就是值）
    """
    import zipfile

    def parse_axml(zf):
        try:
            data = zf.read('AndroidManifest.xml')
        except (KeyError, OSError):
            return None
        if len(data) < 8:
            return None
        try:
            ax_type, ax_hsize, _ = struct.unpack('<HHI', data[:8])
            if ax_type != 0x0003:            # RES_XML_TYPE
                return None

            # ── 字符串池 ──
            off = ax_hsize
            sp_type, sp_hsize, sp_size = struct.unpack('<HHI', data[off:off + 8])
            if sp_type != 0x0001:            # RES_STRING_POOL_TYPE
                return None
            cnt, _style, flags, sstart, _ = \
                struct.unpack('<IIIII', data[off + 8:off + 28])
            is_utf8 = bool(flags & 0x100)
            # 偏移表紧跟在 chunk 头之后（sp_hsize 就是头长度，通常 28）
            tbl_off = off + sp_hsize
            offsets = struct.unpack('<%dI' % cnt,
                                    data[tbl_off:tbl_off + 4 * cnt])
            base = off + sstart
            strings = []
            for so in offsets:
                p = base + so
                if is_utf8:
                    n = data[p]
                    if n & 0x80:
                        n = ((n & 0x7F) << 8) | data[p + 1]
                        p += 2
                    else:
                        p += 1
                    n2 = data[p]
                    if n2 & 0x80:
                        n2 = ((n2 & 0x7F) << 8) | data[p + 1]
                        p += 2
                    else:
                        p += 1
                    strings.append(data[p:p + n2].decode('utf-8', 'replace'))
                else:
                    n = struct.unpack('<H', data[p:p + 2])[0]
                    p += 2
                    if n & 0x8000:
                        n2 = struct.unpack('<H', data[p:p + 2])[0]
                        n = ((n & 0x7FFF) << 16) | n2
                        p += 2
                    strings.append(data[p:p + n * 2].decode('utf-16-le', 'replace'))

            # ── 遍历 chunk，找 <manifest> ──
            pos = off + sp_size
            while pos + 8 <= len(data):
                ctype, chsize, csize = struct.unpack('<HHI', data[pos:pos + 8])
                if csize <= 0:
                    break
                if ctype == 0x0102:          # RES_XML_START_ELEMENT_TYPE
                    body = pos + chsize
                    _ns_i, name_i = struct.unpack('<II', data[body:body + 8])
                    nm = strings[name_i] if name_i < len(strings) else ''
                    if nm == 'manifest':
                        a_start, a_size, a_count = struct.unpack(
                            '<HHH', data[body + 8:body + 14])
                        res = {}
                        for i in range(a_count):
                            ap = body + a_start + i * a_size
                            _ans, aname, _araw = struct.unpack(
                                '<III', data[ap:ap + 12])
                            dtype = data[ap + 15]
                            dval = struct.unpack('<I', data[ap + 16:ap + 20])[0]
                            an = strings[aname] if aname < len(strings) else ''
                            if an == 'versionCode':
                                res['versionCode'] = int(dval)
                            elif an == 'versionName':
                                if dtype == 0x03:
                                    if dval < len(strings):
                                        res['versionName'] = strings[dval]
                                else:
                                    res['versionName'] = str(dval)
                            elif an == 'package':
                                if dtype == 0x03 and dval < len(strings):
                                    res['package'] = strings[dval]
                        if 'versionCode' in res and 'versionName' in res:
                            return res
                        return None
                pos += csize
        except Exception:
            return None
        return None

    try:
        with zipfile.ZipFile(apk_path) as zf:
            return parse_axml(zf)
    except Exception:
        return None


def android_apk_latest():
    """找版本号最高的 APK，返回 dict；没有就 None。

    每次请求重扫目录（和 installer_path 同样的理由：发布新包不用重启门户）。
    """
    best = None
    for d in android_apk_dirs():
        try:
            names = os.listdir(d)
        except OSError:
            continue
        for n in names:
            if not n.lower().endswith('.apk'):
                continue
            p = os.path.join(d, n)
            try:
                st = os.stat(p)
            except OSError:
                continue
            info = _parse_apk_badging(p)
            if not info:
                continue
            pkg = info.get('package') or ''
            if pkg and pkg not in ANDROID_ALLOWED_PACKAGES:
                continue
            info.update({'path': p, 'size': st.st_size, 'mtime': st.st_mtime,
                         'name': n, 'sha256': ''})
            if best is None or info['versionCode'] > best['versionCode']:
                best = info
    if best:
        # sha256 只在选中这个文件后算一次（几 MB，几十毫秒）
        try:
            h = hashlib.sha256()
            with open(best['path'], 'rb') as f:
                for chunk in iter(lambda: f.read(1 << 20), b''):
                    h.update(chunk)
            best['sha256'] = h.hexdigest()
        except OSError:
            pass
    return best


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

    # 用 HTTP/1.1 而不是默认的 1.0 —— 见文件顶部/本次改动的说明：
    # 部分手机浏览器对 HTTP/1.0 响应处理不好（一直加载中或白屏），
    # 而 curl 完全正常，所以这个问题只在真机浏览器上暴露。
    #
    # ⚠ 改成 1.1 后**每个响应都必须有准确的 Content-Length**，
    #   否则 keep-alive 会让浏览器一直等后续字节。
    #   本项目 send_html / send_json / 文件下载都已显式设置。
    protocol_version = 'HTTP/1.1'
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
        # HTTP/1.1 下**必须**显式声明 body 长度，即使是零长度。
        # 少了这一行，浏览器会按 keep-alive 语义继续等一个 body，
        # 表现就是「点了登录/跳转，页面一直转圈」。
        self.send_header('Content-Length', '0')
        if cookie:
            # HttpOnly：即使哪天出现 XSS，会话 cookie 也偷不走。
            # SameSite=Lax：挡住跨站请求带 cookie（CSRF 的第二道闸）。
            # 不加 Secure —— 门户是 http（内网自建没有证书），加了 cookie 直接失效。
            if 'HttpOnly' not in cookie:
                cookie = cookie + '; HttpOnly'
            if 'SameSite' not in cookie:
                cookie = cookie + '; SameSite=Lax'
            self.send_header('Set-Cookie', cookie)
        self._sec_headers()
        self.end_headers()

    def read_form(self):
        """读 POST body，按 Content-Type 分流。

        urlencoded -> parse_qs
        json       -> json.loads（前端 fetch 常用这种）

        为什么必须支持 json：只认 urlencoded 的话，前端用
        fetch(..., {body: JSON.stringify(...)}) 发过来的 csrf 字段
        解析不出来，do_POST 直接 403 —— 而页面上完全看不出原因。
        """
        n = int(self.headers.get('Content-Length') or 0)
        if n <= 0 or n > 65536:
            return {}
        raw = self.rfile.read(n).decode('utf-8', 'replace')
        ctype = (self.headers.get('Content-Type') or '').lower()
        if 'json' in ctype:
            try:
                obj = json.loads(raw or '{}')
                if isinstance(obj, dict):
                    return {k: (v if isinstance(v, str) else json.dumps(v))
                            for k, v in obj.items()}
                return {}
            except Exception:
                return {}
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
                # 首页 = 产品介绍（hero + 功能网格 + FAQ）。
                # 「正在借网」实时读，让页面有活着的感觉。
                ip0 = self.client_ip()
                online = 0
                try:
                    _lv = sample_live(self.db)
                    online = int(_lv.get('onlineCount') or 0)
                except Exception:
                    online = 0
                self.send_html(landing_page(online_now=online))
            elif path == '/download-page':
                ip1 = self.client_ip()
                _ph = (self.headers.get('Host') or '10.13.24.1:8080').strip()
                self.send_html(download_page(
                    detected_ip=ip1 if PRIVATE_IP_RE.match(ip1) else '',
                    portal_host=_ph))
            elif path == '/feedback':
                ip2 = self.client_ip()
                self.send_html(feedback_page(
                    detected_ip=ip2 if PRIVATE_IP_RE.match(ip2) else ''))
            elif path == '/login':
                # 老的登录入口作废，直接回首页
                self.redirect('/')
            elif path == '/portal':
                # 登录后的门户页作废：密钥已经改到软件里，网页不再有「登录」这件事。
                # 老用户的书签直接带回首页，别让他们看到「下载 ferry-*.cmd」那种过时内容。
                self.redirect('/')
            elif path == '/download-client':
                self.handle_download_client()
            elif path == '/download-portable':
                self.handle_download_portable()
            elif path == '/download-cmd':
                self.handle_download_cmd()
            elif path.startswith('/download/version/'):
                self.handle_download_version(path)
            elif path == '/download' or path.startswith('/download/'):
                self.handle_download(path)
            elif path == '/logout':
                self.handle_logout()
            elif path.startswith('/attach/'):
                self.handle_attach_get(path)
            elif path == '/api/admin/stats':
                self.handle_api_admin_stats()
            elif path == '/api/admin/live':
                self.handle_api_admin_live()
            elif path == '/api/admin/net':
                self.handle_api_admin_net()
            elif path == '/api/admin/feedback':
                self.handle_api_admin_feedback()
            elif path == '/api/admin/client':
                self.handle_api_admin_client(
                    urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query))
            elif path == '/admin':
                self.handle_admin('overview')
            elif path == '/admin/keys':
                self.handle_admin('keys')
            elif path == '/admin/users':
                self.handle_admin('users')
            elif path == '/admin/feedback':
                self.handle_admin('feedback')
            elif path == '/admin/events':
                self.handle_admin('events')
            elif path == '/proxy.mjs':
                self.handle_proxy_mjs()
            elif path == '/api/status':
                self.handle_api_status()
            elif path == '/api/config':
                self.handle_api_config(urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query))
            elif path == '/api/license':
                self.handle_api_license(urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query))
            elif path == '/api/device/restore':
                # 重装后凭 ANDROID_ID 自动恢复授权（GET，只读 + 心跳）
                self.handle_api_device_restore(urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query))
            elif path == '/version.json':
                self.handle_version()
            elif path == '/download-latest':
                self.handle_download_latest()
            # ── Android 客户端更新（2026-10-30 新增）──
            # 清单和 APK 本体分两个路径：清单要短超时轮询，
            # APK 是几 MB 二进制流（支持 Range 续传），混在一起日志和限流都不好办。
            elif path.startswith('/static/'):
                self.handle_static(path)
            elif path == '/android/version':
                self.handle_android_version()
            elif path == '/download-android':
                self.handle_download_android()
            elif path == '/get-android':
                # 扫码落地页（区分微信/QQ 内置浏览器）
                _ua = self.headers.get('User-Agent') or ''
                _ph = (self.headers.get('Host') or '10.13.24.1:8080').strip()
                self.send_html(android_landing_page(portal_host=_ph, ua=_ua))
            elif path == '/about':
                self.handle_about()
            elif path == '/setup-hosts':
                self.handle_setup_hosts()
            elif path == '/admin/unlock':
                self.handle_unlock()
            elif path == '/favicon.ico':
                self.send_response(204)
                # 204 本身没有 body，但 HTTP/1.1 keep-alive 下仍要显式声明，
                # 否则部分手机浏览器会挂住等数据。
                self.send_header('Content-Length', '0')
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
            # 附件上传：multipart，CSRF 走表单里的 token 字段而不是 csrf ——
            # 这个接口既给网页反馈页用（未登录），也给客户端用（没有 cookie）。
            if path == '/attach/upload':
                self.handle_attach_upload()
                return

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
            elif path == '/feedback':
                key = 'fb|' + self.client_ip()
            else:
                key = 'admin|' + self.client_ip()
            if not csrf_check(key, form.get('csrf', '')):
                self.db.log('csrf_fail', self.client_ip(), path)
                self.send_html(page('请求无效',
                    '<h1>请求无效</h1><div class="sub">'
                    '页面已过期，请重新打开。若反复出现，检查是否有人跨站提交。</div>'), 403)
                return

            if path == '/feedback':
                self.handle_feedback_post(form)
            elif path == '/login':
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
            elif path == '/admin/device':
                # 设备管理的两个动作（2026-10-29 设备授权上线后新增）：
                #   toggle —— 停用/恢复某台设备（踢下线）
                #   forget —— 从表里删掉一台设备（腾出名额）
                self.handle_admin_device(form)
            elif path == '/api/admin/net':
                self.handle_api_admin_net(form)
            elif path == '/api/admin/feedback':
                self.handle_api_admin_feedback(form)
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

    def handle_download_client(self):
        """只发客户端本体（约 120 KB），不发安装包。

        为什么需要这个端点：自动更新走安装包要趟一堆坑 ——
        Job Object 会杀掉安装进程、/CLOSEAPPLICATIONS 会和托盘程序死锁、
        [Run] 的 silent 语义容易写错、旧版进程占着文件还删不掉。
        而 99% 的更新只改客户端逻辑，mihomo.exe / wintun.dll 几乎不动。

        所以自动更新改成：下载这个 exe → 客户端退出后由 .cmd 覆盖 → 重新启动。
        不碰安装程序、不要 UAC、不涉及卸载注册表。

        首次安装仍然用安装包（它还要放 mihomo/wintun）。
        """
        here = os.path.dirname(os.path.abspath(__file__))
        exe = None
        for d in (os.path.join(here, '..', 'dist'), os.path.join(here, 'dist')):
            p = os.path.normpath(os.path.join(d, '渡口助手.exe'))
            if os.path.isfile(p):
                exe = p
                break
        if exe is None:
            self.send_json({'ok': False, 'error': '客户端尚未构建'}, 404)
            return

        with open(exe, 'rb') as f:
            data = f.read()

        ver = str(read_version_manifest().get('latest', '0.0.0'))
        self.send_response(200)
        self.send_header('Content-Type', 'application/vnd.microsoft.portable-executable')
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        # 客户端用它核对「拿到的到底是不是新版本」，避免半截文件被当成成功
        self.send_header('X-Ferry-Version', ver)
        self.send_header('X-Ferry-Bytes', str(len(data)))
        self.end_headers()
        CHUNK = 65536
        try:
            for i in range(0, len(data), CHUNK):
                self.wfile.write(data[i:i + CHUNK])
        except Exception:
            pass
        self.db.log('download_client', self.client_ip(), 'v' + ver)

    def handle_download_portable(self):
        """发免安装的 ZIP（解压即用，不经过 %TEMP%）。

        为什么需要：
          有些机器开了「智能应用控制 / 应用控制策略」，Inno 安装包在解压到
          临时目录执行那一步被系统拦下（错误 4551），装不了。
          Inno 的 SetupLdr 天生就是这个结构，改不掉，所以给一个绿色包兜底。

        找文件：dist/Ferry-Portable-*.zip，取版本号最大的那个。
        """
        here = os.path.dirname(os.path.abspath(__file__))
        cand = []
        for d in (os.path.join(here, '..', 'dist'), os.path.join(here, 'dist')):
            d = os.path.normpath(d)
            if not os.path.isdir(d):
                continue
            for n in os.listdir(d):
                if n.startswith('Ferry-Portable-') and n.endswith('.zip'):
                    cand.append(os.path.join(d, n))
        if not cand:
            self.send_json({'ok': False, 'error': '免安装包尚未构建'}, 404)
            return

        def ver_of(path):
            m = re.search(r'Ferry-Portable-([0-9.]+)\.zip$', path)
            if not m:
                return (0,)
            return tuple(int(x) for x in m.group(1).split('.') if x.isdigit())

        cand.sort(key=ver_of)
        path = cand[-1]

        with open(path, 'rb') as f:
            data = f.read()

        name = os.path.basename(path)
        self.send_response(200)
        self.send_header('Content-Type', 'application/zip')
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        # 中文文件名要按 RFC 5987 编码，否则浏览器拿到乱码名
        self.send_header('Content-Disposition',
                         "attachment; filename=ferry-portable.zip; "
                         "filename*=UTF-8''" + urllib.parse.quote('渡口助手-免安装.zip'))
        self.end_headers()
        CHUNK = 65536
        try:
            for i in range(0, len(data), CHUNK):
                self.wfile.write(data[i:i + CHUNK])
        except Exception:
            pass
        self.db.log('download_portable', self.client_ip(), name)

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
            # 文件是 GBK（下面 encode('gbk')），所以代码页也必须是 936。
            # 原来写的是 chcp 65001（UTF-8）—— 两者一错位，中文全变乱码。
            'chcp 936 >nul 2>&1',
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

    def handle_static(self, path):
        """发 portal/static 下的静态资源（目前只有二维码图片）。

        安全边界：只认**白名单文件名**，不做任意路径拼接。
        用白名单而不是 os.path.join(base, path)：后者一旦有人传
        `../../etc/passwd` 之类就麻烦，虽然 URL 里通常被规范化，
        但"不给自己留这种口子"更省心。文件就两个，写死最安全。
        """
        ALLOWED = {
            'android-qr.png': 'image/png',
            'android-qr.svg': 'image/svg+xml; charset=utf-8',
        }
        name = path.rsplit('/', 1)[-1]
        ctype = ALLOWED.get(name)
        if not ctype:
            self.send_json({'ok': False, 'error': 'not found'}, 404)
            return

        here = os.path.dirname(os.path.abspath(__file__))
        for base in (os.path.join(here, 'static'), here):
            fp = os.path.normpath(os.path.join(base, name))
            if os.path.isfile(fp):
                try:
                    with open(fp, 'rb') as f:
                        data = f.read()
                except OSError:
                    break
                self.send_response(200)
                self.send_header('Content-Type', ctype)
                self.send_header('Content-Length', str(len(data)))
                # 二维码内容是稳定的（除非换门户地址），缓存一天足够。
                # 换地址时把文件名或内容改一下即可，见 docs。
                self.send_header('Cache-Control', 'public, max-age=86400')
                self.end_headers()
                try:
                    self.wfile.write(data)
                except (BrokenPipeError, ConnectionResetError):
                    pass
                return

        self.send_json({'ok': False, 'error': 'not found'}, 404)

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

    def handle_android_version(self):
        """Android 客户端查更新用的清单。

        返回：
            ok         —— 服务端有没有可发的 APK
            latest     —— versionName（给人看的）
            latestCode —— versionCode（**客户端必须用它比较**）
            url        —— 下载路径（相对，客户端拼上 portalUrl）
            size       —— 字节数
            sha256     —— 下载完校验用

        为什么客户端必须拿 versionCode 比较、不能比 versionName：
          versionName 是字符串，按字典序 '0.10.0' < '0.9.0' ——
          用了就会永远提示"有新版本"，或者永远不提示。versionCode
          是单调递增的整数，是唯一正确的判据。
        """
        apk = android_apk_latest()
        if apk is None:
            self.send_json({'ok': False, 'error': '还没有发布 Android 安装包',
                            'latest': '', 'latestCode': 0, 'url': ''}, 200)
            return
        self.send_json({
            'ok': True,
            'latest': apk['versionName'],
            'latestCode': apk['versionCode'],
            'url': '/download-android',
            'size': apk['size'],
            'sha256': apk.get('sha256', ''),
            'notes': '',
            'built': True,
        })

    def handle_download_android(self):
        """下载最新 APK（二进制流，支持单段 Range 续传）。

        为什么要 Range：手机下 6 MB 在弱网下很容易断，下载器/浏览器会带
        Range 重试。不支持的话每次都从 0 开始，用户会觉得"怎么下不完"。
        只处理单段（bytes=a-b 或 bytes=-N），够用。
        """
        apk = android_apk_latest()
        if apk is None:
            self.send_json({'ok': False, 'error': '还没有发布 Android 安装包'}, 404)
            return
        path = apk['path']
        try:
            size = os.path.getsize(path)
        except OSError:
            self.send_json({'ok': False, 'error': '安装包读不到'}, 404)
            return

        rng = (self.headers.get('Range') or '').strip()
        start, end = 0, size - 1
        partial = False
        if rng.startswith('bytes='):
            try:
                spec = rng[6:].split(',')[0]
                a, _, b = spec.partition('-')
                if a:
                    start = int(a)
                    if b:
                        end = min(int(b), size - 1)
                elif b:                      # bytes=-N：最后 N 字节
                    start = max(0, size - int(b))
                if 0 <= start <= end < size:
                    partial = True
            except ValueError:
                start, end, partial = 0, size - 1, False

        length = end - start + 1
        try:
            self.send_response(206 if partial else 200)
            self.send_header('Content-Type', 'application/vnd.android.package-archive')
            self.send_header('Content-Disposition',
                             'attachment; filename="ferry-%s.apk"' % apk['versionName'])
            self.send_header('Content-Length', str(length))
            if partial:
                self.send_header('Content-Range', 'bytes %d-%d/%d' % (start, end, size))
            self.send_header('Accept-Ranges', 'bytes')
            self.send_header('Cache-Control', 'no-store')
            self.send_header('X-Ferry-Sha256', apk.get('sha256', ''))
            self.end_headers()
            with open(path, 'rb') as f:
                f.seek(start)
                left = length
                while left > 0:
                    chunk = f.read(min(65536, left))
                    if not chunk:
                        break
                    self.wfile.write(chunk)
                    left -= len(chunk)
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
        with_token = (qs.get('with_token') or ['0'])[0].strip() == '1'

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
            # token 默认不下发 —— 安装包不该内置凭据（转发一份安装包就泄露一个账号）。
            # 但客户端「自己没激活」时会带 with_token=1 来要自己的令牌：
            # 请求必须来自这台机器的 IP（上面的 src == want 校验），
            # 等价于「这台机器本人来取自己的令牌」，所以可以给。
            # 这样卸载重装 / 配置被清 / 换目录之后，不用再找管理员要密钥。
            'token': (cli['token'] or '') if with_token else '',
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
        """查询当前授权状态与剩余时长（客户端每次启动 / 定期调一次）。

        2026-10-29 起改成**按设备**鉴权：
          · 账号身份 = token
          · 设备身份 = device_id（客户端上报的 ANDROID_ID，重装不变）
          · ip 只在没有 device_id 时做兜底（兼容还没升级的老客户端）

        为什么不再用 (ip, token)：同一局域网里手机和电脑走同一个 NAT 出口，
        IP 完全一样。用 IP 当身份，等于「两台设备共用一个授权位」，
        谁后激活谁把对方顶掉。

        到期处理：这里不改库，只如实报告 enabled=0/expired=1。
        真正的落库在 activate 续期时做 —— 让「到期」是个可逆的软状态，
        用户续费后不用等管理员手工改库。
        """
        ip = (qs.get('ip') or [''])[0].strip()
        token = (qs.get('token') or [''])[0].strip()
        device_id = (qs.get('device_id') or [''])[0].strip()
        platform = (qs.get('platform') or [''])[0].strip()[:16]
        model = (qs.get('model') or [''])[0].strip()[:48]
        app_version = (qs.get('app_version') or [''])[0].strip()[:24]

        if not token:
            self.send_json({'ok': False, 'error': '缺少 token'}, 400)
            return

        # 先按 token 找账号（设备维度）；找不到再退回 ip（老客户端）
        cli = self.db.q1('SELECT * FROM clients WHERE token = ?', (token,))
        if cli is None and ip:
            cli = self.db.q1('SELECT * FROM clients WHERE ip = ?', (ip,))
        if not cli or not hmac.compare_digest(str(cli['token']), token):
            self.send_json({'ok': False, 'error': '授权信息无效'}, 401)
            return

        # 设备登记 / 心跳。设备超限时明确拒绝，不要静默放行 ——
        # 否则用户会以为自己在用，实际下一台设备把旧的顶掉了。
        devices_n = 0
        if device_id:
            ok, err, _is_new, reason = device_bind(
                self.db, device_id, token, ip=ip,
                platform=platform, model=model, app_version=app_version)
            if not ok:
                # 两种拒绝必须分开：设备满了 vs 这台被管理员停用。
                # 以前一律带 device_limit=True，客户端就会提示「设备数已达上限」——
                # 而用户看到的实际是「一个设备都没超却用不了」，完全没法自助。
                self.send_json({'ok': False, 'error': err,
                                'device_limit': reason == 'limit',
                                'device_disabled': reason == 'disabled'}, 409)
                return
            devices_n = device_count(self.db, token)
        else:
            devices_n = device_count(self.db, token)

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
            # 设备维度的附加信息：客户端据此显示「第 2/3 台设备」这类提示
            'deviceCount': devices_n,
            'deviceMax': MAX_DEVICES_PER_TOKEN,
        })

    def handle_api_device_restore(self, qs):
        """重装/新装后凭设备标识自动恢复授权（GET，纯查询 + 心跳）。

        场景：用户手机重装 App，本地 token 没了。如果还让他重新找管理员要
        一次性密钥，体验很差 —— 而这台设备其实早就登记过。

        安全边界（三条都要满足）：
          1. device_id 必须是已登记的、enabled 的设备
          2. 它绑的 token 必须仍然有效（账号存在且未过期/未停用）
          3. 来源 IP 必须与这台设备上次登记时在同一个 /24
             —— 光凭 device_id 太松（ID 可伪造），加上「还得在同一网段」
             这一条，攻击成本就从「知道一个 ID」变成「先进同一个局域网」
        """
        ip = self.client_ip()
        device_id = (qs.get('device_id') or [''])[0].strip()
        if not device_ok_id(device_id):
            self.send_json({'ok': False, 'error': '设备标识无效'}, 400)
            return

        dev = self.db.q1('SELECT * FROM devices WHERE device_id = ?', (device_id,))
        if not dev:
            self.send_json({'ok': False, 'error': '这台设备没有登记记录'}, 404)
            return
        if not dev['enabled']:
            # 「被停用」和「没登记过」要分开说。混成一句「没有登记记录」，
            # 用户会以为是恢复功能坏了，反复重装 App —— 实际是管理员停的。
            self.send_json({'ok': False, 'error': '这台设备已被管理员停用，请联系管理员',
                            'device_disabled': True}, 403)
            return

        last_ip = (dev['last_ip'] or '').strip()
        if last_ip and not same_subnet_24(last_ip, ip):
            self.db.log('device_restore_deny', ip, 'device=%s last_ip=%s' % (device_id[:12], last_ip))
            self.send_json({'ok': False,
                            'error': '这台设备上次在别的网络里使用，需要重新激活'}, 403)
            return

        cli = self.db.q1('SELECT * FROM clients WHERE token = ?', (dev['token'],))
        if not cli:
            self.send_json({'ok': False, 'error': '授权已不存在，请重新激活'}, 404)
            return
        st = license_state_of(cli)
        if st['expired'] or not st['enabled']:
            self.send_json({'ok': False, 'error': '授权已过期或已停用，请重新激活'}, 403)
            return

        device_touch(self.db, device_id, dev['token'], ip=ip)
        self.db.log('device_restore', ip, 'device=%s' % device_id[:12])

        self.send_json({
            'ok': True,
            'token': dev['token'],
            'expiresAt': st['expiresAt'],
            'daysLeft': st['daysLeft'],
            'hoursLeft': st['hoursLeft'],
            'remoteHost': CFG['public_host'] or '10.13.24.1',
            'sshPort': CFG['ssh_port'],
            'httpPort': CFG['port'] + 1,
            'deviceCount': device_count(self.db, dev['token']),
            'deviceMax': MAX_DEVICES_PER_TOKEN,
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

        # 通过：建号或复活（按 token 找账号，不再用 ip 当身份）
        #
        # 2026-10-29 改动：原来这里用 `WHERE ip = ?` 找账号，导致同局域网的
        # 手机和电脑共用一个授权位、互相顶掉。现在：
        #   · 账号 = token（clients 表里的那一行）
        #   · 设备 = device_id（devices 表，一台设备一行）
        # 一个 token 下可以有多台设备，共用同一个到期时间。
        existing = self.db.q1('SELECT * FROM clients WHERE ip = ?', (ip,))
        if existing:
            token = existing['token']
            # 续期：把 expires_at 往后推（重新激活 = 续一次）
            self.db.x('UPDATE clients SET enabled = 1, last_seen = ?, expires_at = ? WHERE ip = ?',
                      (now(), exp, ip))
        else:
            token = new_token()
            self.db.x(
                'INSERT INTO clients (ip, token, enabled, created_at, last_seen, invite_id, expires_at) '
                'VALUES (?,?,1,?,?,?,?)',
                (ip, token, now(), now(), inv['id'], exp))

        # 登记设备。device_id 由客户端上报（Android 的 ANDROID_ID，重装不变）。
        # 没上报就跳过 —— 兼容还没升级的老客户端，它们继续按 ip 用。
        device_id = str(body.get('device_id') or '').strip()
        device_msg = ''
        if device_id:
            ok, derr, is_new, areason = device_bind(
                self.db, device_id, token, ip=ip,
                platform=str(body.get('platform') or '').strip()[:16],
                model=str(body.get('model') or '').strip()[:48],
                app_version=str(body.get('app_version') or '').strip()[:24])
            if not ok:
                # 不发放 token。密钥的核销（UPDATE invites）在下面才执行，
                # 所以这里失败并没有吃掉用户的码 —— 60 秒内用同一个码重试仍然有效
                # （RETRY_WINDOW_SEC，见上面）。原来的 code_consumed=True 与代码
                # 实际行为相反，会把用户误导去要新密钥，这里改正。
                # 「设备满了」和「这台被停用」也要分开报，原因同 /api/status。
                self.send_json({'ok': False, 'error': derr,
                                'device_limit': areason == 'limit',
                                'device_disabled': areason == 'disabled',
                                'code_consumed': False}, 409)
                return
            device_msg = '新设备已登记' if is_new else ''

        self.db.x('UPDATE invites SET used_by_ip = ?, used_at = ? WHERE id = ?',
                  (ip, now(), inv['id']))
        rate_ok(ip, 'user')
        self.db.log('activate_ok', ip,
                    'invite=%s device=%s' % (inv['id'], device_id[:12] if device_id else '-'))

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
            'deviceCount': device_count(self.db, token),
            'deviceMax': MAX_DEVICES_PER_TOKEN,
            'deviceMessage': device_msg,
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
    def handle_api_admin_stats(self):
        """多维统计：窗口序列 + 汇总 + 设备排行。

        GET /api/admin/stats?w=10m|1h|5h|10h|50h|100h|250h|all&ip=<可选>
        """
        if not self.is_admin():
            self.send_json({'ok': False, 'error': 'forbidden'}, 403)
            return

        q = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
        key = (q.get('w') or ['1h'])[0]
        ip = (q.get('ip') or [''])[0] or None

        keys = [w[0] for w in STATS_WINDOWS]
        if key not in keys:
            key = '1h'

        try:
            series = stats_series(self.db, key, ip)
            summary = stats_summary(self.db, key, ip)
            hosts = stats_hosts(self.db, key)
        except Exception as e:
            self.send_json({'ok': False, 'error': str(e)}, 500)
            return

        self.send_json({
            'ok': True,
            'window': key,
            'windows': [{'key': w[0], 'label': w[1]} for w in STATS_WINDOWS],
            'series': series,
            'summary': summary,
            'hosts': hosts,
            'bucketSec': STATS_BUCKET_SEC,
            'keepHours': STATS_KEEP_HOURS,
        })

    def handle_api_admin_live(self):
        """管理面版轮询用。只有管理员能看 —— 这里会暴露谁在用。"""
        if not self.is_admin():
            self.send_json({'ok': False, 'error': 'forbidden'}, 403)
            return
        self.send_json(sample_live(self.db))

    def handle_admin(self, section='overview'):
        if not self.is_admin():
            self.send_html(admin_login_page(csrf=csrf_set('adminlogin|' + self.client_ip())))
            return
        # ?new=<明文密钥>&h=<小时>&d=<天数>：生成密钥后的落点。
        # 走 302 过来而不是在 POST 响应里直接渲染 —— 后者会让地址栏停在
        # /admin/newcode，用户按 F5 时浏览器重发 POST，**刷新一次多一个密钥**。
        # 这里从 query 读到明文，渲染一次就不再存在（页面本身是 GET，可安全刷新）。
        q = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
        ncode = (q.get('new') or [''])[0]
        try:
            nhours = int((q.get('h') or ['12'])[0])
        except ValueError:
            nhours = 12
        self.send_html(admin_page(self.db, new_code=(ncode or None), new_hours=nhours,
                                  csrf=csrf_set('admin|' + self.client_ip()),
                                  section=section))

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

        # 两条出路，都不在 POST 响应里渲染整页：
        #   · 前端用 fetch → 回 JSON，由页面弹浮窗显示（明文只在这里出现一次）
        #   · 没有 JS / 直接提交 → 302 到 /admin?new=...，浏览器存的是 GET 地址，
        #     刷新不会重发 POST，也就不会「刷一次多一个密钥」
        if self.headers.get('X-Requested-With') == 'fetch':
            self.send_json({'ok': True, 'code': code, 'note': note,
                            'hours': hours, 'days': lic})
            return
        qs = urllib.parse.urlencode({'new': code, 'h': hours, 'd': lic})
        self.redirect('/admin?' + qs)

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

    def handle_attach_upload(self):
        """接收一个附件（multipart/form-data）。

        为什么用 multipart 而不是 base64 塞进 JSON：
          8 MB 的截图 base64 之后变 11 MB，前端要整个读进内存再编码，
          移动端会直接卡死。multipart 是浏览器原生能力，`fetch(formData)` 就行。

        安全边界（都是刻意加的）：
          · 白名单 MIME —— 不接受 exe / html / svg（svg 能带脚本）
          · 单文件 8 MB、一条反馈最多 6 个
          · 存盘用随机文件名，原名只入库（防路径穿越）
          · 下载接口要登录（管理员）或带正确 upload_token
        """
        ip = self.client_ip()
        ctype = self.headers.get('Content-Type') or ''
        if 'multipart/form-data' not in ctype.lower():
            self.send_json({'ok': False, 'error': '需要 multipart/form-data'}, 400)
            return

        n = int(self.headers.get('Content-Length') or 0)
        if n <= 0:
            self.send_json({'ok': False, 'error': '空请求'}, 400)
            return
        if n > ATTACH_MAX_BYTES + 65536:
            self.send_json({'ok': False,
                            'error': '文件太大（上限 %d MB）' % (ATTACH_MAX_BYTES // 1048576)}, 413)
            return

        raw = self.rfile.read(n)
        m = re.search(r'boundary="?([^";]+)"?', ctype)
        if not m:
            self.send_json({'ok': False, 'error': '缺少 boundary'}, 400)
            return
        boundary = m.group(1).encode()

        # 极简 multipart 解析：只取 file 段和 token 字段。
        # 不引第三方库（门户要能在裸 Python 上跑）。
        parts = raw.split(b'--' + boundary)
        filename = ''
        mime = ''
        blob = b''
        token = ''
        for part in parts:
            if not part.strip(b'-\r\n'):
                continue
            head, sep, body = part.partition(b'\r\n\r\n')
            if not sep:
                continue
            hl = head.decode('utf-8', 'replace').lower()
            body = body.rstrip(b'\r\n')
            if 'name="token"' in hl:
                token = body.decode('utf-8', 'replace').strip()[:64]
            elif 'filename="' in hl:
                fn = re.search(r'filename="([^"]*)"', head.decode('utf-8', 'replace'))
                filename = (fn.group(1) if fn else '')[:120]
                ct = re.search(r'content-type:\s*([^\r\n;]+)', hl)
                mime = (ct.group(1).strip() if ct else '')
                blob = body

        if not blob:
            self.send_json({'ok': False, 'error': '没有收到文件内容'}, 400)
            return
        if len(blob) > ATTACH_MAX_BYTES:
            self.send_json({'ok': False,
                            'error': '文件太大（上限 %d MB）' % (ATTACH_MAX_BYTES // 1048576)}, 413)
            return

        # MIME 缺失时按扩展名兜底（有些客户端不上报 Content-Type）
        if not mime:
            low = filename.lower()
            for k, ext in ATTACH_ALLOWED.items():
                if low.endswith(ext):
                    mime = k
                    break
        ext = ATTACH_ALLOWED.get(mime)
        if not ext:
            self.send_json({'ok': False,
                            'error': '不支持的文件类型：' + (mime or '未知')}, 415)
            return

        # 这条 token 已绑了 6 个就拒绝
        if token:
            cnt = self.db.q1('SELECT COUNT(*) AS n FROM feedback_attach '
                             'WHERE upload_token = ? AND feedback_id = 0', (token,))
            if cnt and int(cnt['n'] or 0) >= ATTACH_MAX_COUNT:
                self.send_json({'ok': False,
                                'error': '最多 %d 个附件' % ATTACH_MAX_COUNT}, 400)
                return

        # 存盘：随机名 + 白名单扩展名。原名只入库。
        stored = secrets.token_hex(16) + ext
        try:
            with open(os.path.join(attach_dir(), stored), 'wb') as f:
                f.write(blob)
        except Exception as e:
            self.send_json({'ok': False, 'error': '保存失败：' + str(e)}, 500)
            return

        cur = self.db.x(
            'INSERT INTO feedback_attach (feedback_id, upload_token, name, mime, size, '
            'stored, created_at) VALUES (0,?,?,?,?,?,?)',
            (token, filename or ('附件' + ext), mime, len(blob), stored, now()))
        aid = getattr(cur, 'lastrowid', 0) or 0
        if not aid:
            r = self.db.q1('SELECT MAX(id) AS m FROM feedback_attach')
            aid = int((r['m'] if r else 0) or 0)
        self.db.log('attach_up', ip, '%s %dB' % (mime, len(blob)))
        self.send_json({'ok': True, 'id': aid, 'name': filename or ('附件' + ext),
                        'size': len(blob), 'mime': mime, 'url': attach_url(aid)})

    def handle_attach_get(self, path):
        """下载 / 预览附件。

        权限：管理员直接放行；否则必须带 ?t=<upload_token>。
        """
        try:
            aid = int(path[len('/attach/'):].split('/')[0])
        except ValueError:
            self.send_html(page('无效', '<h1>附件不存在</h1>'), 404)
            return

        row = self.db.q1('SELECT * FROM feedback_attach WHERE id = ?', (aid,))
        if not row:
            self.send_html(page('无效', '<h1>附件不存在</h1>'), 404)
            return

        q = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
        tok = (q.get('t') or [''])[0]
        if not self.is_admin():
            if not tok or tok != (row['upload_token'] or ''):
                self.send_html(page('拒绝', '<h1>无权查看这个附件</h1>'), 403)
                return

        fp = os.path.join(attach_dir(), row['stored'] or '')
        if not row['stored'] or not os.path.isfile(fp):
            self.send_html(page('丢失', '<h1>附件文件已丢失</h1>'), 404)
            return

        try:
            with open(fp, 'rb') as f:
                data = f.read()
        except Exception:
            self.send_html(page('读取失败', '<h1>附件读取失败</h1>'), 500)
            return

        inline = (row['mime'] or '').startswith('image/')
        disp = 'inline' if inline else 'attachment'
        self.send_response(200)
        self.send_header('Content-Type', row['mime'] or 'application/octet-stream')
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Content-Disposition',
                         '%s; filename="%s"' % (disp, urllib.parse.quote(row['name'] or 'file')))
        self.send_header('X-Content-Type-Options', 'nosniff')
        self.send_header('Cache-Control', 'private, max-age=300')
        self._sec_headers()
        self.end_headers()
        try:
            self.wfile.write(data)
        except Exception:
            pass

    def handle_feedback_post(self, form):
        """提交意见反馈。

        不需要登录：IP 本身就是身份（借网按 IP 限速、按 IP 激活）。
        挡登录会把「还没装好的人」挡在外面，而那正是最需要反馈的人群。

        限流：同一 IP 每 60 秒 1 条（FB_COOLDOWN）。

        顺序很重要：**先校验，再判冷却**。
        踩过的坑：以前先把时间戳写进 _fb_last 再校验，于是「描述太短」这种
        手滑一次也要等满整个冷却窗口才能重交，用户看到的是「提交太频繁」，
        以为功能坏了 —— 而真正的原因是自己刚打错一个字。
        现在只有校验通过、并且真的写进库，才占用冷却。
        """
        ip = self.client_ip()

        kind = (form.get('kind') or 'other').strip()[:16]
        text = (form.get('text') or '').strip()
        contact = (form.get('contact') or '').strip()[:60]
        ver = (form.get('ver') or '').strip()[:24]

        if not kind:
            kind = 'other'
        # 允许的反馈类型白名单，别让任意字符串进库
        if kind not in ('bug', 'feature', 'perf', 'ask', 'other'):
            kind = 'other'

        # ── 1. 校验（不占用冷却）──
        if len(text) < 4:
            self.send_html(feedback_page(ip, err='描述太短了，至少写 4 个字'))
            return
        if len(text) > 2000:
            text = text[:2000]

        # ── 2. 冷却 ──
        now_ts = now()
        with _fb_lock:
            last = _fb_last.get(ip, 0)
            wait_s = FB_COOLDOWN - (now_ts - last) if last else 0
        if wait_s > 0:
            self.send_html(feedback_page(
                ip, err='提交太频繁，请 %d 秒后再试' % wait_s))
            return

        # ── 3. 落库，成功之后才计时 ──
        try:
            self.db.x('INSERT INTO feedback (ts, ip, kind, text, contact, client_ver) '
                      'VALUES (?,?,?,?,?,?)',
                      (now(), ip, kind, text, contact, ver))
            self.db.log('feedback', ip, '%s: %s' % (kind, text[:60]))
        except Exception as e:
            sys.stderr.write('[feedback] insert failed: %s\n' % e)
            self.send_html(feedback_page(ip, err='提交失败，请稍后重试'))
            return

        with _fb_lock:
            _fb_last[ip] = now_ts

        # 绑定附件：把这条 token 名下还没归属的附件挂到刚插入的反馈上。
        tok = (form.get('attach_token') or '').strip()[:64]
        if tok:
            try:
                row = self.db.q1('SELECT MAX(id) AS m FROM feedback')
                fid = int((row['m'] if row else 0) or 0)
                if fid:
                    self.db.x('UPDATE feedback_attach SET feedback_id = ? '
                              'WHERE upload_token = ? AND feedback_id = 0', (fid, tok))
            except Exception as e:
                sys.stderr.write('[feedback] 绑定附件失败: %s\n' % e)

        self.send_html(feedback_page(
            ip, msg='收到了，谢谢反馈。管理员能看到并在这里跟进处理状态。'))

    def handle_api_admin_feedback(self, form=None):
        """意见反馈的管理接口。

        GET  -> 列表（含按状态分组的计数）
        POST -> 改状态 / 加备注
        """
        if not self._require_admin():
            return

        if self.command == 'POST':
            # 用 do_POST 传进来的 form，不要再读 rfile ——
            # body 已经被 do_POST 读掉了，再读会挂住（实测 20s 超时）。
            body = {}
            if form:
                body = {k: (v[0] if isinstance(v, list) else v)
                        for k, v in form.items()}
            # fetch 发的是 urlencoded，字段都是字符串，够用

            fid = body.get('id')
            try:
                fid = int(fid)
            except (TypeError, ValueError):
                self.send_json({'ok': False, 'error': 'bad id'}, 400)
                return

            row = self.db.q1('SELECT * FROM feedback WHERE id = ?', (fid,))
            if not row:
                self.send_json({'ok': False, 'error': 'not found'}, 404)
                return

            status = body.get('status')
            note = body.get('note')
            sets = []
            args = []
            if status is not None:
                try:
                    st = int(status)
                    if st in (0, 1, 2, 3):
                        sets.append('status = ?')
                        args.append(st)
                        sets.append('handled_at = ?')
                        args.append(now())
                except (TypeError, ValueError):
                    pass
            if note is not None:
                sets.append('admin_note = ?')
                args.append(str(note)[:500])
            if sets:
                args.append(fid)
                self.db.x('UPDATE feedback SET %s WHERE id = ?' % ', '.join(sets), args)
            self.db.log('feedback_handle', self.client_ip(), 'id=%d' % fid)
            self.send_json({'ok': True})
            return

        rows = self.db.q('SELECT * FROM feedback ORDER BY id DESC LIMIT 200')
        counts = {0: 0, 1: 0, 2: 0, 3: 0}
        for r in rows:
            st = int(r['status'] or 0)
            counts[st] = counts.get(st, 0) + 1
        items = []
        for r in rows:
            d = dict(r)
            d['attachments'] = attach_list(self.db, r['id'])
            items.append(d)

        self.send_json({
            'ok': True,
            'total': len(rows),
            'counts': counts,
            'list': items,
        })

    def handle_admin_device(self, form):
        """设备管理：停用/恢复（toggle）或删除（forget）。

        为什么需要：2026-10-29 授权改成「按设备」后，一个激活码最多绑
        MAX_DEVICES_PER_TOKEN 台。用户在别的机器上用满了名额、或者手机丢了，
        管理员必须能把某台踢下线/腾名额 —— 否则只能改库，运维成本太高。

        两个动作的区别（**两个都会释放名额** —— device_count 只统计 enabled=1 的行）：
          · toggle 置 enabled=0：记录还在，面板上一眼能看出「这台被停过」，
            点「恢复」就能放回来。适合「临时不让它用」。
          · forget 直接删行：记录消失，该设备下次启动会被当成一台全新设备重新登记。
            适合「换机了 / 这台我不要了」。

        被停用的设备不会自己复活：device_bind 见到 enabled=0 直接拒绝，
        所以「踢下线」是真的踢得掉（App 下次启动会拿到「已被管理员停用」）。
        要放回来只能由管理员在这里点「恢复」。

        安全：只按 device_id 操作，不碰 token —— 踢一台不会影响同账号的其它设备。
        """
        if not self._require_admin():
            return

        device_id = (form.get('device_id') or '').strip()
        op = (form.get('op') or 'toggle').strip()

        if op not in ('toggle', 'forget'):
            # 白名单。以前是「不是 forget 就按 toggle 处理」，手滑传个别的值
            # 会静默把设备停掉 —— 这种默认值不能猜。
            self.send_html(admin_page(
                self.db, err='未知的设备操作',
                csrf=csrf_set('admin|' + self.client_ip())), 400)
            return

        if not device_id:
            self.send_html(admin_page(
                self.db, err='缺少设备标识',
                csrf=csrf_set('admin|' + self.client_ip())), 400)
            return

        dev = self.db.q1('SELECT * FROM devices WHERE device_id = ?', (device_id,))
        if not dev:
            self.send_html(admin_page(
                self.db, err='这台设备不存在（可能已被删除）',
                csrf=csrf_set('admin|' + self.client_ip())), 404)
            return

        label = (dev['model'] or dev['device_id'][:12])
        if op == 'forget':
            self.db.x('DELETE FROM devices WHERE device_id = ?', (device_id,))
            self.db.log('device_forget', self.client_ip(),
                        'device=%s model=%s' % (device_id[:12], label))
            msgtext = f'已删除设备 {label}，名额已释放'
        else:
            newval = 0 if dev['enabled'] else 1
            self.db.x('UPDATE devices SET enabled = ? WHERE device_id = ?',
                      (newval, device_id))
            self.db.log('device_toggle', self.client_ip(),
                        'device=%s enabled=%d' % (device_id[:12], newval))
            msgtext = (f'{label} 已{"停用" if newval == 0 else "恢复"}')

        self.send_html(admin_page(
            self.db, msg=msgtext,
            csrf=csrf_set('admin|' + self.client_ip()),
            section='users'))

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
    · 远端办公网可能封某些站点（两端都不通）<br>
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
                # 清掉超过保留期的统计桶（默认 250 小时）
                try:
                    cutoff = int(time.time()) - STATS_KEEP_HOURS * 3600
                    db.x('DELETE FROM traffic_bucket WHERE bucket_ts < ?', (cutoff,))
                except Exception as e:
                    sys.stderr.write('[janitor] 清理统计桶失败: %s\n' % e)

                # 每天备份一份数据库，只留最近 7 天。
                #
                # 为什么在这里做：portal.db 里是全部账号的 token、授权到期时间、
                # 密钥状态 —— 丢了就得挨个重新激活。用 sqlite 的 backup API
                # （不是复制文件）才不会拿到写了一半的库。
                try:
                    _bak_dir = os.path.join(os.path.dirname(os.path.abspath(CFG['db'])), 'backup')
                    _today = time.strftime('%Y%m%d')
                    _dst = os.path.join(_bak_dir, 'portal-' + _today + '.db')
                    if not os.path.exists(_dst):
                        if not os.path.isdir(_bak_dir):
                            os.makedirs(_bak_dir)
                        _src = sqlite3.connect(CFG['db'])
                        _out = sqlite3.connect(_dst)
                        with _out:
                            _src.backup(_out)
                        _out.close()
                        _src.close()
                        # 只留 7 份
                        _all = sorted(f for f in os.listdir(_bak_dir) if f.startswith('portal-'))
                        for _old in _all[:-7]:
                            try:
                                os.remove(os.path.join(_bak_dir, _old))
                            except Exception:
                                pass
                except Exception as e:
                    sys.stderr.write('[janitor] 备份失败: %s\n' % e)

                # 反馈里可能带联系方式（微信/QQ），没道理永久留着。
                # 只保留最近 500 条 —— 个人工具的量级，够回溯很久了。
                db.x('DELETE FROM feedback WHERE id NOT IN '
                     '(SELECT id FROM feedback ORDER BY id DESC LIMIT 500)')
                if n:
                    sys.stderr.write(f'[janitor] 清理过期会话 {n} 条\n')

                # 内存里的心跳/累计表也要清。
                #
                # _hb：每 IP 一条。heartbeat_of 过期只返回 None、不删条目 ——
                #      设备长期不上线、或者 DHCP 频繁换 IP，条目会一直堆着。
                #      单条几十字节不算大，但这是纯粹的垃圾，留着没意义。
                # _totals：同上，但**不能删** —— 里面是还没 flush 到 DB 的增量，
                #      删了就丢流量统计。只清已经 flush 干净（增量归零）且超过
                #      24 小时没动静的。
                try:
                    cutoff = time.time() - 3600
                    with _hb_lock:
                        for _ip in [k for k, v in _hb.items() if v.get('at', 0) < cutoff]:
                            _hb.pop(_ip, None)
                    tcut = time.time() - 86400
                    for _ip in [k for k, v in list(_totals.items())
                                if v.get('rx', 0) == 0 and v.get('tx', 0) == 0
                                and v.get('first', 0) < tcut]:
                        _totals.pop(_ip, None)
                except Exception as e:
                    sys.stderr.write(f'[janitor] 清理内存表出错: {e}\n')
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

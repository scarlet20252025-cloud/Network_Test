/**
 * proxy-users.mjs — 渡口代理的多用户与认证
 *
 * 设计目标（对应用户要求「便捷且容易管理，状态显示也要合格」）：
 *   1. **零依赖**：只用 Node 内置模块，不引第三方包。
 *   2. **一个文件存所有**：`users.json` 一处读写，可直接看、可直接改。
 *   3. **加人只要一条命令**：`node proxy-admin.mjs add 张三`，打印出令牌即可发人。
 *   4. **双协议同一套账号**：HTTP 用 Basic、SOCKS5 用 RFC 1929，
 *      同一个 name+token 两边都能用，不给使用者增加心智负担。
 *   5. **每用户可观测**：连接数、上下行字节、最后活跃、被拒次数。
 *
 * 认证格式（给要接进来的客户端看）：
 *   HTTP / HTTPS:  Proxy-Authorization: Basic base64("用户名:令牌")
 *                  → 例：curl -x http://127.0.0.1:10809 -U 张三:abc123 https://github.com
 *   SOCKS5:        RFC 1929 用户名/密码子协商（用户名=用户名，密码=令牌）
 *
 * 为什么不直接用 IP 白名单：局域网 IP 会变（DHCP），且同一台机器上
 * 多个人无法区分。令牌是唯一稳定的身份。
 */

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const DEFAULT_FILE = 'users.json';

/* ---------------- 默认结构 ---------------- */

const DEFAULT_SETTINGS = {
  // 是否要求认证。false 时任何人可直连（仅建议在完全可信环境用）
  requireAuth: true,
  // 单用户最大并发连接数，0 = 不限
  maxConnsPerUser: 40,
  // 单用户每天字节上限（上行+下行），0 = 不限
  dailyBytesPerUser: 0,
  // 单用户带宽上限（字节/秒）。默认 5 MB/s。
  // 为什么默认限速：出口是共享的，一个人拉大文件会占满，
  // 其他人的网页就打不开了。5 MB/s 够看 4K 视频，又不至于挤死别人。
  bytesPerSecPerUser: 5 * 1024 * 1024,
  // 拒绝时的提示语（会出现在 HTTP 407 / SOCKS5 认证失败里）
  authRealm: 'ferry',
};

/* ---------------- 工具 ---------------- */

function nowIso() {
  return new Date().toISOString();
}

/** 生成令牌：16 字节 base32 风格，肉眼可读、可手抄 */
export function newToken() {
  const alphabet = 'abcdefghjkmnpqrstuvwxyz23456789'; // 去掉易混的 i l o 0 1
  const buf = crypto.randomBytes(26);
  let out = '';
  for (let i = 0; i < 24; i++) out += alphabet[buf[i] % alphabet.length];
  return out.slice(0, 6) + '-' + out.slice(6, 12) + '-' + out.slice(12, 18) + '-' + out.slice(18, 24);
}

/** 定长比较，避免时序侧信道 */
function safeEqual(a, b) {
  const ab = Buffer.from(String(a));
  const bb = Buffer.from(String(b));
  if (ab.length !== bb.length) {
    // 仍走一次比较，保持耗时稳定
    crypto.timingSafeEqual(ab, ab);
    return false;
  }
  return crypto.timingSafeEqual(ab, bb);
}

/* ---------------- 用户存储 ---------------- */

export class UserStore {
  /**
   * @param {string} file  users.json 路径
   * @param {object} [opts] { autoSave=true }
   */
  constructor(file = DEFAULT_FILE, opts = {}) {
    this.file = path.resolve(file);
    this.autoSave = opts.autoSave !== false;
    this.data = { version: 1, settings: { ...DEFAULT_SETTINGS }, users: [] };
    this.load();
  }

  load() {
    try {
      if (fs.existsSync(this.file)) {
        // 显式用 Buffer + UTF8 解码，别依赖 readFileSync(path,'utf8') ——
        // 它在某些 Windows 环境下仍会走本机代码页，导致中文名变成
        // 「娴嬭瘯鐢?」这种乱码，而且乱码会被原样写回、永久固化。
        const buf = fs.readFileSync(this.file);
        const raw = new TextDecoder('utf-8', { fatal: false }).decode(buf);
        const parsed = JSON.parse(raw);
        this.data = {
          version: parsed.version || 1,
          settings: { ...DEFAULT_SETTINGS, ...(parsed.settings || {}) },
          users: Array.isArray(parsed.users) ? parsed.users : [],
        };
      } else {
        this.save();
      }
    } catch (e) {
      // 文件损坏时不要静默覆盖 —— 备份后重建，否则用户令牌全丢
      try {
        fs.copyFileSync(this.file, `${this.file}.corrupt-${Date.now()}`);
      } catch { /* ignore */ }
      this.data = { version: 1, settings: { ...DEFAULT_SETTINGS }, users: [] };
      this.save();
      console.error(`[proxy-users] users.json 解析失败（${e.message}），已备份并重建`);
    }
  }

  save() {
    if (!this.autoSave) return;
    fs.mkdirSync(path.dirname(this.file), { recursive: true });
    const tmp = `${this.file}.tmp`;
    // 显式用 Buffer 写 UTF-8 字节。
    // 踩坑（实测）：writeFileSync(tmp, str, 'utf8') 在本机仍会把中文用户名
    // 写成 GBK 字节（表现为「娴嬭瘯鐢?」），PS 5.1 的 ConvertFrom-Json 直接报
    // 「Invalid object passed in, ':' or '}' expected」。用 Buffer.from(str,'utf8')
    // 不经任何编码转换，写出去就是确定的 UTF-8。
    const buf = Buffer.from(JSON.stringify(this.data, null, 2), 'utf8');
    fs.writeFileSync(tmp, buf);
    fs.renameSync(tmp, this.file);
  }

  get settings() {
    return this.data.settings;
  }

  /**
   * 热加载：users.json 被 proxy-admin.mjs 改完后自动重新读取。
   *
   * 为什么必须做：users.json 是共享的 —— 你在机器 A 上用 CLI 加人/改配额，
   * 代理进程跑在机器 A 或另一台机器上。没有热加载的话，每次加人都要重启
   * 代理，「便捷管理」就没了。
   *
   * 实现要点：
   *   - 用 fs.watch 而不是轮询（省 CPU），但 fs.watch 在 Windows 上对
   *     rename 替换不可靠，所以再补一个 5 秒的兜底轮询。
   *   - save() 走的是「写 .tmp 再 rename」，rename 会触发事件，必须去抖。
   */
  watch(onChange) {
    if (this._watcher) return;
    let timer = null;
    const reload = (why) => {
      if (timer) return;
      timer = setTimeout(() => {
        timer = null;
        try {
          const before = JSON.stringify(this.data);
          this.load();
          if (JSON.stringify(this.data) !== before) {
            console.log(`[proxy-users] 用户库已热加载（${why}）：${this.data.users.length} 个用户`);
            onChange?.(this);
          }
        } catch (e) {
          console.error(`[proxy-users] 热加载失败：${e.message}`);
        }
      }, 120);
    };
    try {
      this._watcher = fs.watch(this.file, () => reload('fs.watch'));
    } catch (e) {
      console.error(`[proxy-users] 无法监听用户库：${e.message}，改用轮询`);
    }
    this._poll = setInterval(() => reload('poll'), 5000);
    if (this._poll.unref) this._poll.unref();
  }

  stopWatch() {
    if (this._watcher) { this._watcher.close(); this._watcher = null; }
    if (this._poll) { clearInterval(this._poll); this._poll = null; }
  }

  /** 按名字查（大小写不敏感，方便用户手输） */
  find(name) {
    const n = String(name || '').toLowerCase();
    return this.data.users.find((u) => String(u.name).toLowerCase() === n) || null;
  }

  /** 加一个用户，返回完整记录（含令牌） */
  add(name, opts = {}) {
    const trimmed = String(name || '').trim();
    if (!trimmed) throw new Error('用户名不能为空');
    if (!/^[\w\u4e00-\u9fa5.\-]{1,32}$/u.test(trimmed)) {
      throw new Error('用户名只允许 1–32 位字母/数字/下划线/中文/点/横线');
    }
    if (this.find(trimmed)) throw new Error(`用户「${trimmed}」已存在`);
    const user = {
      name: trimmed,
      token: opts.token || newToken(),
      enabled: opts.enabled !== false,
      note: opts.note || '',
      createdAt: nowIso(),
      // 运行期统计不落盘（重启清零），这里只留累计值
      stats: { totalConns: 0, bytesIn: 0, bytesOut: 0, denied: 0, lastSeen: null },
    };
    this.data.users.push(user);
    this.save();
    return user;
  }

  remove(name) {
    const i = this.data.users.findIndex((u) => String(u.name).toLowerCase() === String(name).toLowerCase());
    if (i < 0) return false;
    this.data.users.splice(i, 1);
    this.save();
    return true;
  }

  /** 改启用状态 / 备注 / 令牌 */
  update(name, patch = {}) {
    const u = this.find(name);
    if (!u) return null;
    if (typeof patch.enabled === 'boolean') u.enabled = patch.enabled;
    if (typeof patch.note === 'string') u.note = patch.note;
    if (typeof patch.token === 'string' && patch.token.length >= 8) u.token = patch.token;
    if (patch.resetToken === true) u.token = newToken();
    if (typeof patch.resetStats === 'boolean' && patch.resetStats) {
      u.stats = { totalConns: 0, bytesIn: 0, bytesOut: 0, denied: 0, lastSeen: null };
    }
    this.save();
    return u;
  }

  list() {
    return this.data.users.map((u) => ({ ...u }));
  }
}

/* ---------------- 认证器：双协议共用 ---------------- */

/**
 * Authenticator 把「认证」和「统计」绑在一起：
 * 认证通过后返回的 session 对象由调用方持有，用完调 session.close()。
 */
export class Authenticator {
  /**
   * @param {UserStore} store
   * @param {object} [opts] { logger }
   */
  constructor(store, opts = {}) {
    this.store = store;
    this.logger = opts.logger || console;
    // name -> { conns, bytesIn, bytesOut, lastSeen, since }
    this.sessions = new Map();
    // 排行榜/最近活动
    this.denials = [];
  }

  get requireAuth() {
    return this.store.settings.requireAuth !== false;
  }

  /**
   * 校验用户名 + 令牌。
   * @returns {{ ok: true, user: object } | { ok: false, reason: string }}
   */
  verify(name, token) {
    if (!this.requireAuth) {
      return { ok: true, user: { name: name || 'anonymous', token: '', anonymous: true } };
    }
    if (!name || !token) return { ok: false, reason: 'missing-credentials' };
    const user = this.store.find(name);
    if (!user) return { ok: false, reason: 'unknown-user' };
    if (!user.enabled) return { ok: false, reason: 'user-disabled' };
    if (!safeEqual(user.token, token)) return { ok: false, reason: 'bad-token' };
    return { ok: true, user };
  }

  /** 记录一次被拒，供面板显示「谁在试」 */
  noteDenied(reason, remote) {
    this.denials.unshift({ t: Date.now(), reason, remote });
    if (this.denials.length > 50) this.denials.length = 50;
    this.logger.warn?.(`[proxy-users] 拒绝 ${remote}: ${reason}`);
  }

  /** 建一个连接会话，返回带计账能力的对象 */
  open(userName) {
    const name = userName || 'anonymous';
    let s = this.sessions.get(name);
    if (!s) {
      s = { name, conns: 0, bytesIn: 0, bytesOut: 0, lastSeen: Date.now(), since: Date.now() };
      this.sessions.set(name, s);
    }
    s.conns++;
    s.lastSeen = Date.now();
    const user = this.store.find(name);
    if (user) user.stats.totalConns++;
    let closed = false;
    return {
      name,
      addIn(n) {
        s.bytesIn += n;
        if (user) user.stats.bytesIn += n;
      },
      addOut(n) {
        s.bytesOut += n;
        if (user) user.stats.bytesOut += n;
      },
      touch() {
        s.lastSeen = Date.now();
        if (user) user.stats.lastSeen = nowIso();
      },
      close() {
        if (closed) return;
        closed = true;
        s.conns = Math.max(0, s.conns - 1);
        s.lastSeen = Date.now();
      },
    };
  }

  /** 面板快照：每用户在线状态 + 累计量 */
  snapshot() {
    const users = this.store.list().map((u) => {
      const s = this.sessions.get(u.name);
      return {
        name: u.name,
        enabled: u.enabled,
        note: u.note,
        // 令牌不回传明文，只给前 6 位便于核对
        tokenHint: u.token ? `${u.token.slice(0, 6)}…` : '',
        createdAt: u.createdAt,
        live: s
          ? {
              conns: s.conns,
              onlineSec: Math.floor((Date.now() - s.since) / 1000),
              lastSeen: s.lastSeen,
              bytesIn: s.bytesIn,
              bytesOut: s.bytesOut,
            }
          : null,
        total: u.stats,
      };
    });
    const activeUsers = users.filter((x) => x.live && x.live.conns > 0).length;
    return {
      requireAuth: this.requireAuth,
      settings: this.store.settings,
      users,
      summary: {
        total: users.length,
        enabled: users.filter((x) => x.enabled).length,
        activeUsers,
        totalConns: users.reduce((a, x) => a + (x.live?.conns || 0), 0),
      },
      denials: this.denials.slice(0, 20),
    };
  }
}

/* ---------------- 协议层工具 ---------------- */

/** 解析 HTTP Proxy-Authorization: Basic xxx */
export function parseBasicAuth(headerValue) {
  if (!headerValue) return null;
  const m = /^Basic\s+(\S+)$/i.exec(String(headerValue).trim());
  if (!m) return null;
  let raw;
  try {
    raw = Buffer.from(m[1], 'base64').toString('utf8');
  } catch {
    return null;
  }
  const idx = raw.indexOf(':');
  if (idx < 0) return null;
  return { name: raw.slice(0, idx), token: raw.slice(idx + 1) };
}

/** 生成 HTTP 407 响应头 */
export function basicAuthChallenge(realm) {
  return {
    'Proxy-Authenticate': `Basic realm="${realm || 'ferry'}", charset="UTF-8"`,
    'Content-Type': 'text/plain; charset=utf-8',
  };
}

/** 按 RFC 1929 解析 SOCKS5 用户名/密码子协商包 */
export function parseSocks5Auth(buf) {
  // VER(1)=0x01 | ULEN(1) | UNAME | PLEN(1) | PASSWD
  if (buf.length < 3) return null;
  if (buf[0] !== 0x01) return null;
  const ulen = buf[1];
  if (buf.length < 2 + ulen + 1) return null;
  const name = buf.subarray(2, 2 + ulen).toString('utf8');
  const plen = buf[2 + ulen];
  if (buf.length < 2 + ulen + 1 + plen) return null;
  const token = buf.subarray(2 + ulen + 1, 2 + ulen + 1 + plen).toString('utf8');
  return { name, token, consumed: 2 + ulen + 1 + plen };
}

export { DEFAULT_SETTINGS };

#!/usr/bin/env node
/**
 * dual-proxy.mjs — 双协议代理（SOCKS5 + HTTP 单端口自动识别）
 *
 * 为什么需要「双协议单端口」：
 *   Windows 系统代理（HKCU Internet Settings 的 ProxyServer）**只支持 HTTP 代理**。
 *   把 ProxyServer 写成 SOCKS5 端口时，浏览器 / Electron / .NET / git 都会
 *   把 SOCKS5 当 HTTP 代理去连 —— 秒失败（实测 1.4ms 返回 000）。
 *
 *   单端口双协议：同一个端口先读首字节判断协议
 *     0x05      → SOCKS5
 *     'C'       → HTTP CONNECT 隧道（HTTPS）
 *     'G','P'…  → HTTP 普通请求
 *   这样系统代理填 http://127.0.0.1:10809 也能用，直接填 socks5 也能用，
 *   用户不用关心协议。
 *
 * 架构（参考 uu-remote-proxy 的思路，但上游是 ssh -D）：
 *   浏览器/系统代理 ──HTTP──▶ dual-proxy ──SOCKS5──▶ ssh -D ──▶ 远端 ──▶ Internet
 *
 * 零依赖：只用 Node 内置模块。
 *
 * 用法：
 *   node dual-proxy.mjs --listen 10809 --socks 10808
 *   node dual-proxy.mjs --listen 10809 --socks 10808 --allow-lan   # 局域网共享
 *   node dual-proxy.mjs --listen 10809 --socks 10808 --panel 10801 # 状态面板
 */

import net from 'node:net';
import http from 'node:http';
import { spawn } from 'node:child_process';
import { appendFileSync, mkdirSync, writeFileSync } from 'node:fs';
import { parseArgs } from 'node:util';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { connectBest, matchPool } from './smart-resolve.mjs';

const { values } = parseArgs({
  options: {
    listen: { type: 'string', default: '' },
    socks: { type: 'string', default: '' },
    host: { type: 'string', default: '127.0.0.1' },
    'allow-lan': { type: 'boolean', default: false },
    panel: { type: 'string', default: '0' },
    quiet: { type: 'boolean', default: false },
    // 隧道自愈：传 keeper 脚本路径即启用；不传则只告警不重启
    keeper: { type: 'string', default: '' },
    remote: { type: 'string', default: 'pcs' },
    // 日志文件：被 WMI / 计划任务以无控制台方式启动时 stdout 会被丢弃，
    // 没有日志文件就完全无法排查自愈为什么失败。
    log: { type: 'string', default: '' },
    // 用户库：传了 users.json 路径即启用认证（HTTP Basic + SOCKS5 RFC1929）
    users: { type: 'string', default: '' },
    // 'auto' 时端口自动探测（默认）。给具体数字则固定。
    port: { type: 'string', default: 'auto' },
  },
});

/**
 * 找一个可用端口。
 *
 * 为什么必须自动：
 *   固定 10808/10809 在「客户机环境」会撞车 —— 别人的 Clash、公司代理、
 *   v2rayN 都爱占这两个号。一旦被占，软件起不来，用户看到的就是「软件死了」。
 *   实际踩过：本机 Clash 占 7897，另一台机器上 10808 被占用导致隧道起不来。
 *
 * 做法：从 base 开始往上试，bind 成功就返回；试满 range 个就报错。
 * 用 bind('127.0.0.1', 0) 让内核分配也行，但那样端口随机、无法预测，
 * 面板和自启都要额外传参，反而不方便。固定起点 + 顺序探测最稳。
 */
async function findPort(base, range = 40, host = '127.0.0.1') {
  for (let p = base; p < base + range; p++) {
    // 必须 await：第一版忘了 await，isFree 返回的 Promise 恒为真值，
    // 于是每次都返回 base 端口。遇到该端口已被占用就直接 EADDRINUSE 崩掉，
    // 表现就是「端口探测出来了，但软件起不来」。
    if (await isFree(p, host)) return p;
  }
  throw new Error(`${base}–${base + range - 1} 全部被占用`);
}

function isFree(port, host) {
  return new Promise((resolve) => {
    const s = net.createServer();
    s.once('error', () => resolve(false));
    s.once('listening', () => s.close(() => resolve(true)));
    s.listen(port, host);
  });
}

const PORT_MODE = values.port === 'auto';
let LISTEN_PORT = values.listen ? Number(values.listen) : null;
let SOCKS_PORT = values.socks ? Number(values.socks) : null;
const BIND = values['allow-lan'] ? '0.0.0.0' : values.host;
const PANEL_PORT = Number(values.panel);
const QUIET = values.quiet;
const LOG_FILE = values.log || '';

/* ---------------- 端口解析 ----------------
 * auto 模式下：SOCKS 与 HTTP 端口都从 10800 起探测，SOCKS 先占、HTTP 后占，
 * 保证两者不冲突。探测结果写进 state 文件，GUI / CLI / 客户端配置都读它，
 * 这样任何一方都不需要硬编码端口。
 */

const STATE_DIR = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '.proxy-state');
const STATE_FILE = path.join(STATE_DIR, 'ports.json');

const RESOLVED = {
  socks: SOCKS_PORT,
  listen: LISTEN_PORT,
  panel: Number(values.panel) || 0,
};

function writePortState() {
  try {
    mkdirSync(STATE_DIR, { recursive: true });
    writeFileSync(STATE_FILE, JSON.stringify({
      socks: RESOLVED.socks,
      http: RESOLVED.listen,
      panel: RESOLVED.panel,
      mode: PORT_MODE ? 'auto' : 'fixed',
      updatedAt: new Date().toISOString(),
    }, null, 2), 'utf8');
  } catch (e) {
    log(`! 写端口状态失败（${e.message}），GUI/客户端可能读不到端口`);
  }
}

if (PORT_MODE) {
  // SOCKS 与面板都要避开，否则面板会和代理抢同一个端口。
  // 踩坑：第一版只避开了 SOCKS，结果面板端口 = HTTP 端口，
  // front 和 panel 同时 listen 同一个端口，表现为「端口状态文件里
  // http 和 panel 是同一个数」。
  RESOLVED.socks = await findPort(10800);
  RESOLVED.listen = await findPort(RESOLVED.socks + 1);
  if (RESOLVED.panel > 0) {
    let guard = 0;
    while ((RESOLVED.panel === RESOLVED.socks || RESOLVED.panel === RESOLVED.listen) && guard++ < 40) {
      RESOLVED.panel = await findPort(RESOLVED.listen + 1);
      RESOLVED.listen = await findPort(RESOLVED.socks + 1);
    }
    if (RESOLVED.panel === RESOLVED.socks || RESOLVED.panel === RESOLVED.listen) {
      log(`! 面板端口 ${values.panel} 与代理端口冲突，改为 0（关闭面板）`);
      RESOLVED.panel = 0;
    }
  }
  log(`端口自动探测：SOCKS=${RESOLVED.socks}  HTTP=${RESOLVED.listen}  面板=${RESOLVED.panel || '关'}`);
} else {
  if (RESOLVED.panel === RESOLVED.socks || RESOLVED.panel === RESOLVED.listen) {
    log(`! 面板端口 ${values.panel} 与代理端口冲突，改为 0（关闭面板）`);
    RESOLVED.panel = 0;
  }
  log(`端口固定：SOCKS=${RESOLVED.socks}  HTTP=${RESOLVED.listen}`);
}

// 生效：写回模块级常量，后续代码继续用 SOCKS_PORT / LISTEN_PORT
SOCKS_PORT = RESOLVED.socks;
LISTEN_PORT = RESOLVED.listen;
writePortState();

const stats = {
  startedAt: Date.now(),
  total: 0,
  active: 0,
  socks5: 0,
  http: 0,
  connect: 0,
  ok: 0,
  fail: 0,
  recent: [], // {t, kind, target, ms, ok}
  errors: {},
};

function log(...a) {
  const line = `${new Date().toISOString().slice(11, 23)} ${a.join(' ')}`;
  if (!QUIET) console.log(line);
  if (LOG_FILE) {
    try {
      appendFileSync(LOG_FILE, `${new Date().toISOString()} ${a.join(' ')}\n`);
    } catch {
      /* 日志写不进去不能影响代理本身 */
    }
  }
}

// 每用户累计（面板「谁在用、用了多少」的原始数据）
const perUser = new Map(); // name -> { conns, ok, fail, bytesIn, bytesOut }

function record(kind, target, ms, ok, err, user) {
  stats.total++;
  if (ok) stats.ok++;
  else {
    stats.fail++;
    const k = err || 'unknown';
    stats.errors[k] = (stats.errors[k] || 0) + 1;
  }
  stats.recent.unshift({ t: Date.now(), kind, target, ms, ok, user: user || '' });
  if (stats.recent.length > 200) stats.recent.length = 200;

  if (user) {
    const u = perUser.get(user) || { conns: 0, ok: 0, fail: 0 };
    u.conns++;
    if (ok) u.ok++;
    else u.fail++;
    perUser.set(user, u);
  }
}

/* ---------------- SOCKS5 客户端 ---------------- */

/**
 * 择优建连包装：对已知易故障域名（github 系）走候选 IP 池，
 * 其余域名照旧让 ssh -D 自己解析。
 *
 * 背景：远端办公网到 github.com 的 DNS 只给 20.205.243.166，
 * 该 IP 实测 40% 可达；候选池里 7 个 IP 都是 100% 可达。
 */
async function smartConnect(host, port, timeoutMs = 8000) {
  if (matchPool(host)) {
    try {
      // deferConfirm：先不在 TCP 层记成功。
      // 实测 raw.githubusercontent.com / github.com 都有「SOCKS5 建连成功但
      // TLS 握手被 RST」的 IP。若这里直接 markOk，坏 IP 会被永久当成好 IP，
      // 表现为「择优成功但浏览器连接被重置」。
      const r = await connectBest(host, port, {
        socksPort: SOCKS_PORT,
        perTryTimeout: 3500,
        totalTimeoutMs: 8000,
        concurrency: 4,
        deferConfirm: true,
      });
      return { sock: r.sock, ip: r.ip, confirm: r.confirm };
    } catch (e) {
      // 候选池全失败则退回原路径，至少还有一次机会（但要短，别让用户干等）
      log(`  ! 择优失败 ${host}:${port} ${e.message.slice(0, 120)}，退回域名解析`);
    }
  }
  const r = await socksConnect(host, port, Math.min(timeoutMs, 8000));
  return { sock: r.sock, ip: host, confirm: () => {} };
}

/**
 * 带确认的双向管道。
 *
 * 为什么不用 attachConfirm + pipe：给 sock 加 'data' 监听会让流进入 flowing
 * 模式，早到的字节被确认逻辑吃掉、不会转发给客户端 —— 实测导致 TLS 握手
 * 卡住、请求 20s 超时。必须由我们自己转发，一个字节都不能丢。
 *
 * 确认规则：上游只要回吐 ≥1 字节即认为该 IP 可用；若在上游首字节前就 close，
 * 或 6 秒内零字节，则记一次失败（下次排序自动降权）。
 */
function pipeWithConfirm(src, dst, confirm, label) {
  let bytes = 0;
  let settled = false;
  const settle = (ok, detail) => {
    if (settled) return;
    settled = true;
    clearTimeout(timer);
    confirm(ok, detail);
  };
  const timer = setTimeout(() => settle(false, `${label} 6s 无上游字节`), 6000);

  src.on('data', (chunk) => {
    if (bytes === 0) settle(true);
    bytes += chunk.length;
    if (!dst.write(chunk)) src.pause();
  });
  dst.on('drain', () => src.resume());

  src.on('end', () => {
    if (bytes === 0) settle(false, `${label} 上游零字节即结束`);
    if (dst.writable) dst.end();
  });
  src.on('close', () => {
    if (bytes === 0) settle(false, `${label} 上游零字节即关闭`);
    else settle(true);
    if (dst.writable) dst.end();
  });
  src.on('error', () => {
    if (bytes === 0) settle(false, `${label} 上游 error`);
    dst.destroy();
  });
  dst.on('error', () => src.destroy());
  dst.on('close', () => src.destroy());
}

function socksConnect(host, port, timeoutMs = 20000) {
  return new Promise((resolve, reject) => {
    const t0 = Date.now();
    const sock = net.connect({ host: '127.0.0.1', port: SOCKS_PORT });
    let stage = 'greeting';
    let buf = Buffer.alloc(0);
    let settled = false;

    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      sock.destroy();
      reject(new Error(`timeout@${stage}`));
    }, timeoutMs);

    const fail = (m) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      sock.destroy();
      reject(new Error(`${m}@${stage}`));
    };

    sock.on('error', (e) => fail(e.code || e.message));
    sock.on('close', () => fail('closed'));
    sock.on('connect', () => sock.write(Buffer.from([0x05, 0x01, 0x00])));

    sock.on('data', (chunk) => {
      buf = Buffer.concat([buf, chunk]);

      if (stage === 'greeting') {
        if (buf.length < 2) return;
        if (buf[0] !== 0x05) return fail(`ver${buf[0]}`);
        if (buf[1] !== 0x00) return fail(`auth0x${buf[1].toString(16)}`);
        buf = buf.subarray(2);
        stage = 'request';
        const h = Buffer.from(host, 'utf8');
        const p = Buffer.alloc(2);
        p.writeUInt16BE(port, 0);
        sock.write(Buffer.concat([Buffer.from([5, 1, 0, 3, h.length]), h, p]));
        return;
      }

      if (stage === 'request') {
        if (buf.length < 4) return;
        if (buf[1] !== 0x00) {
          const names = {
            1: 'general', 2: 'notallowed', 3: 'netunreach', 4: 'hostunreach',
            5: 'refused', 6: 'ttlexpired', 7: 'cmdnosupport', 8: 'atypnosupport',
          };
          return fail(names[buf[1]] || `reply0x${buf[1].toString(16)}`);
        }
        const atyp = buf[3];
        const need = atyp === 1 ? 10 : atyp === 4 ? 22 : 4 + 1 + buf[4] + 2;
        if (buf.length < need) return;
        settled = true;
        clearTimeout(timer);
        sock.removeAllListeners('data');
        sock.removeAllListeners('error');
        sock.removeAllListeners('close');
        const rest = buf.subarray(need);
        if (rest.length) sock.unshift(rest);
        resolve({ sock, ms: Date.now() - t0 });
      }
    });
  });
}

/* ---------------- 协议分流 ---------------- */

/**
 * 判断首字节属于哪个协议。
 * 0x05 → SOCKS5；其余按 HTTP 处理（'C'=CONNECT, 'G'=GET, 'P'=POST/PUT...）
 */
function detectProtocol(firstByte) {
  if (firstByte === 0x05) return 'socks5';
  return 'http';
}

/* ---------------- 用户认证（可选，--users 指定用户库后启用） ---------------- */

let auth = null;
let authHelpers = null;
if (values.users) {
  try {
    const mod = await import('./proxy-users.mjs');
    authHelpers = mod;
    const store = new mod.UserStore(values.users);
    auth = new mod.Authenticator(store, { logger: console });
    // 热加载：proxy-admin.mjs 加人 / 改配额后，这里 120ms 内生效，不用重启代理。
    // 否则「便捷管理」不成立 —— 每加一个人都要重启一次 bridge。
    store.watch(() => {
      log(`用户库已更新：${store.list().length} 人，认证=${auth.requireAuth}，连接上限=${store.settings.maxConnsPerUser || '不限'}`);
    });
    log(`用户认证已启用  库=${values.users}  用户数=${store.list().length}  要求认证=${auth.requireAuth}`);
  } catch (e) {
    log(`! 加载用户库失败（${e.message}），本进程将不启用认证`);
    auth = null;
  }
}

/**
 * 认证 + 配额检查。返回 session（成功）或 null（已回写拒绝响应）。
 * @param {object} req  http.IncomingMessage（CONNECT 与普通请求都用它）
 * @param {string} remote  客户端地址，仅用于日志
 * @param {function} deny  回调：收到 (statusCode, headers, bodyText)
 */
function checkAuthHttp(req, remote, deny) {
  if (!auth) return { session: null, user: null, anonymous: true };
  const { parseBasicAuth, basicAuthChallenge } = authHelpers;
  const cred = parseBasicAuth(req.headers['proxy-authorization']);
  const r = cred ? auth.verify(cred.name, cred.token) : { ok: false, reason: 'no-credentials' };
  if (!r.ok) {
    auth.noteDenied(r.reason, remote);
    deny(407, basicAuthChallenge(auth.store.settings.authRealm), `需要认证：${r.reason}`);
    return null;
  }
  // 配额与并发
  const snap = auth.sessions.get(r.user.name);
  const conns = snap ? snap.conns : 0;
  const maxC = auth.store.settings.maxConnsPerUser || 0;
  if (maxC > 0 && conns >= maxC) {
    auth.noteDenied('too-many-connections', remote);
    deny(429, { 'content-type': 'text/plain; charset=utf-8' }, `连接数超限（上限 ${maxC}）`);
    return null;
  }
  const session = auth.open(r.user.name);
  return { session, user: r.user, anonymous: false };
}

/* ---------------- HTTP 代理实现（复用 http.Server） ---------------- */

const httpServer = http.createServer((req, res) => {
  const remote = `${req.socket.remoteAddress}:${req.socket.remotePort}`;
  const a = checkAuthHttp(req, remote, (code, headers, body) => {
    res.writeHead(code, headers);
    res.end(body);
  });
  if (a === null) return;                 // 已被拒
  const session = a.session;

  let target;
  try {
    target = new URL(req.url);
  } catch {
    res.writeHead(400, { 'content-type': 'text/plain; charset=utf-8' });
    res.end('这是代理服务器，需要绝对 URI（http://host/path）');
    session?.close();
    return;
  }
  if (target.protocol !== 'http:') {
    res.writeHead(400, { 'content-type': 'text/plain; charset=utf-8' });
    res.end(`不支持 ${target.protocol}，HTTPS 请走 CONNECT`);
    session?.close();
    return;
  }

  const host = target.hostname;
  const port = Number(target.port) || 80;
  const t0 = Date.now();
  log(`HTTP  ${req.method} ${host}:${port}${target.pathname}${a.anonymous ? '' : ` [${a.user.name}]`}`);

  smartConnect(host, port).then(({ sock, ip, confirm }) => {
      stats.active++;
      const path = target.pathname + target.search;
      const headers = { ...req.headers, host: target.host };
      delete headers['proxy-connection'];

      sock.write(
        `${req.method} ${path} HTTP/1.1\r\n` +
        Object.entries(headers).filter(([, v]) => v !== undefined).map(([k, v]) => `${k}: ${v}`).join('\r\n') +
        '\r\n\r\n'
      );
      req.pipe(sock);
      if (ip !== host) pipeWithConfirm(sock, res, confirm, `${host}@${ip}`);
      else sock.pipe(res);

      const done = (err) => {
        stats.active--;
        session?.close();
        record('http', `${host}:${port}`, Date.now() - t0, !err, err, a.user?.name);
      };
      sock.on('data', (c) => session?.addIn(c.length));
      req.on('data', (c) => session?.addOut(c.length));
      sock.on('error', (e) => {
        log(`  ! ${host}:${port} ${e.message}`);
        if (!res.headersSent) res.writeHead(502);
        res.end();
        done(e.code || e.message);
      });
      sock.on('close', () => {
        if (!res.writableEnded) res.end();
        done(null);
      });
    })
    .catch((e) => {
      log(`  ! ${host}:${port} ${e.message}`);
      session?.close();
      record('http', `${host}:${port}`, Date.now() - t0, false, e.message, a.user?.name);
      if (!res.headersSent) res.writeHead(502, { 'content-type': 'text/plain; charset=utf-8' });
      res.end(`代理错误: ${e.message}`);
    });
});

httpServer.on('connect', (req, clientSocket, head) => {
  const [host, portStr] = req.url.split(':');
  const port = Number(portStr) || 443;
  const t0 = Date.now();
  const remote = `${clientSocket.remoteAddress}:${clientSocket.remotePort}`;

  // 认证：CONNECT 走同一套 HTTP 头（Proxy-Authorization）
  const a = checkAuthHttp(req, remote, (code, headers, body) => {
    const hdr = Object.entries(headers).map(([k, v]) => `${k}: ${v}`).join('\r\n');
    clientSocket.write(`HTTP/1.1 ${code} ${code === 407 ? 'Proxy Authentication Required' : 'Too Many Requests'}\r\n${hdr}\r\nContent-Length: ${Buffer.byteLength(body)}\r\nConnection: close\r\n\r\n${body}`);
    clientSocket.end();
  });
  if (a === null) return;
  const session = a.session;

  log(`CONNECT ${host}:${port}${a.anonymous ? '' : ` [${a.user.name}]`}`);

  smartConnect(host, port).then(({ sock, ip, confirm }) => {
      stats.active++;
      stats.connect++;
      clientSocket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
      if (head && head.length) sock.write(head);
      // 用 pipeWithConfirm 代替 sock.pipe，才能在不丢字节的前提下降权坏 IP
      if (ip !== host) pipeWithConfirm(sock, clientSocket, confirm, `${host}@${ip}`);
      else sock.pipe(clientSocket);
      clientSocket.pipe(sock);

      let closed = false;
      const done = (err) => {
        if (closed) return;
        closed = true;
        stats.active--;
        session?.close();
        record('connect', `${host}:${port}`, Date.now() - t0, !err, err, a.user?.name);
        sock.destroy();
        clientSocket.destroy();
      };
      sock.on('data', (c) => session?.addIn(c.length));
      clientSocket.on('data', (c) => session?.addOut(c.length));
      sock.on('error', (e) => done(e.code || e.message));
      clientSocket.on('error', (e) => done(e.code || e.message));
      sock.on('close', () => done(null));
      clientSocket.on('close', () => done(null));
    })
    .catch((e) => {
      log(`  ! CONNECT ${host}:${port} ${e.message}`);
      session?.close();
      record('connect', `${host}:${port}`, Date.now() - t0, false, e.message, a.user?.name);
      clientSocket.end('HTTP/1.1 502 Bad Gateway\r\n\r\n');
    });
});

httpServer.on('clientError', (err, socket) => {
  if (socket.writable) socket.end('HTTP/1.1 400 Bad Request\r\n\r\n');
});

/* ---------------- SOCKS5 服务端（给能直接用 socks5 的客户端） ---------------- */

const socksServer = net.createServer((client) => {
  // stage: greeting → (auth) → request → relay
  let stage = 'greeting';
  let buf = Buffer.alloc(0);
  let targetStr = '';
  let session = null;
  let userName = '';
  const remote = `${client.remoteAddress}:${client.remotePort}`;

  client.on('error', () => client.destroy());

  client.on('data', (chunk) => {
    buf = Buffer.concat([buf, chunk]);

    if (stage === 'greeting') {
      if (buf.length < 2) return;
      if (buf[0] !== 0x05) return client.destroy();
      const nMethods = buf[1];
      if (buf.length < 2 + nMethods) return;
      const methods = buf.subarray(2, 2 + nMethods);
      const needAuth = auth !== null && auth.requireAuth;
      if (needAuth) {
        // 只接受 RFC 1929 用户名/密码（0x02）
        if (!methods.includes(0x02)) {
          client.write(Buffer.from([0x05, 0xFF]));  // 无可接受方法
          return client.destroy();
        }
        client.write(Buffer.from([0x05, 0x02]));
        stage = 'auth';
      } else {
        client.write(Buffer.from([0x05, 0x00]));
        stage = 'request';
      }
      buf = buf.subarray(2 + nMethods);
    }

    if (stage === 'auth') {
      const cred = authHelpers.parseSocks5Auth(buf);
      if (!cred) return;   // 数据不够
      buf = buf.subarray(cred.consumed);
      const r = auth.verify(cred.name, cred.token);
      if (!r.ok) {
        auth.noteDenied(r.reason, remote);
        client.write(Buffer.from([0x01, 0x01]));   // 版本 1，失败
        return client.destroy();
      }
      const snap = auth.sessions.get(r.user.name);
      const maxC = auth.store.settings.maxConnsPerUser || 0;
      if (maxC > 0 && snap && snap.conns >= maxC) {
        auth.noteDenied('too-many-connections', remote);
        client.write(Buffer.from([0x01, 0x01]));
        return client.destroy();
      }
      client.write(Buffer.from([0x01, 0x00]));     // 成功
      userName = r.user.name;
      session = auth.open(userName);
      stage = 'request';
    }

    if (stage === 'request') {
      if (buf.length < 4) return;
      const cmd = buf[1];
      const atyp = buf[3];
      let host, port, consumed;

      if (atyp === 0x01) {
        if (buf.length < 10) return;
        host = `${buf[4]}.${buf[5]}.${buf[6]}.${buf[7]}`;
        port = buf.readUInt16BE(8);
        consumed = 10;
      } else if (atyp === 0x03) {
        if (buf.length < 5 + buf[4] + 2) return;
        host = buf.subarray(5, 5 + buf[4]).toString('utf8');
        port = buf.readUInt16BE(5 + buf[4]);
        consumed = 5 + buf[4] + 2;
      } else if (atyp === 0x04) {
        if (buf.length < 22) return;
        const seg = [];
        for (let i = 0; i < 8; i++) seg.push(buf.readUInt16BE(4 + i * 2).toString(16));
        host = seg.join(':');
        port = buf.readUInt16BE(20);
        consumed = 22;
      } else {
        client.write(Buffer.from([5, 8, 0, 1, 0, 0, 0, 0, 0, 0]));
        return client.destroy();
      }

      if (cmd !== 0x01) {
        // 不支持 BIND / UDP ASSOCIATE
        client.write(Buffer.from([5, 7, 0, 1, 0, 0, 0, 0, 0, 0]));
        return client.destroy();
      }

      const rest = buf.subarray(consumed);
      targetStr = `${host}:${port}`;
      stage = 'relay';
      stats.socks5++;
      log(`SOCKS5 ${targetStr}${userName ? ` [${userName}]` : ''}`);

      // 关键：必须摘掉 'data' 监听器。
      // 之前没摘，客户端在等 SOCKS 回复期间发的数据（TLS ClientHello 等）
      // 会继续进入本处理器，而 stage 已是 'relay' 走不到任何分支 → 数据被吞
      // → 表现为「拿到 200 但 body 卡到 max-time」（实测 github.com 15s）。
      client.removeAllListeners('data');
      client.pause();

      const t0 = Date.now();
      smartConnect(host, port)
        .then(({ sock, ip, confirm }) => {
          stats.active++;
          // 成功回复：VER=5 REP=0 RSV=0 ATYP=1 BND.ADDR=0.0.0.0 BND.PORT=0
          client.write(Buffer.from([5, 0, 0, 1, 0, 0, 0, 0, 0, 0]));
          if (rest.length) sock.write(rest);
          if (ip !== host) pipeWithConfirm(sock, client, confirm, `${host}@${ip}`);
          else sock.pipe(client);
          client.pipe(sock);
          client.resume();

          let closed = false;
          const done = (err) => {
            if (closed) return;
            closed = true;
            stats.active--;
            session?.close();
            record('socks5', targetStr, Date.now() - t0, !err, err, userName);
            sock.destroy();
            client.destroy();
          };
          sock.on('data', (c) => session?.addIn(c.length));
          client.on('data', (c) => session?.addOut(c.length));
          sock.on('error', (e) => done(e.code || e.message));
          client.on('error', (e) => done(e.code || e.message));
          sock.on('close', () => done(null));
          client.on('close', () => done(null));
        })
        .catch((e) => {
          log(`  ! SOCKS5 ${targetStr} ${e.message}`);
          session?.close();
          record('socks5', targetStr, Date.now() - t0, false, e.message, userName);
          client.write(Buffer.from([5, 1, 0, 1, 0, 0, 0, 0, 0, 0]));
          client.destroy();
        });
    }
  });
});

/* ---------------- 隧道守护（自愈） ---------------- */

/**
 * 为什么需要这一层：
 *   ssh -D 隧道会因远端重启 / 网络抖动 / 休眠恢复而死掉。keeper 只在
 *   「ssh 自己退出」时重启 ssh；一旦 keeper 进程本身被杀（注销、任务管理器、
 *   崩溃），就没有任何人会再拉起它 —— 但 dual-proxy 还在跑、系统代理还指着
 *   10809，于是所有请求都被转发到一个没人监听的 10808，表现为「能开网页
 *   但 GitHub 全挂」。
 *
 * 策略：每 TUNNEL_CHECK_MS 探一次上游 SOCKS 端口；连续 TUNNEL_FAIL_MAX 次
 * 不通就按顺序尝试拉起 keeper 脚本。拉起前做冷却，避免风暴。
 */

const TUNNEL_CHECK_MS = 5000;
const TUNNEL_FAIL_MAX = 2;
const TUNNEL_REVIVE_COOLDOWN_MS = 20000;

let tunnelFailStreak = 0;
let lastReviveAt = 0;
let reviveAttempts = 0;

const tunnelHealth = {
  alive: true,
  lastOkAt: Date.now(),
  lastFailAt: 0,
  failStreak: 0,
  reviveAttempts: 0,
  lastReviveAt: 0,
};

function probeSocksPort(timeoutMs = 800) {
  return new Promise((resolve) => {
    const s = net.connect({ host: '127.0.0.1', port: SOCKS_PORT });
    let done = false;
    const finish = (ok) => {
      if (done) return;
      done = true;
      s.destroy();
      resolve(ok);
    };
    s.setTimeout(timeoutMs, () => finish(false));
    s.on('connect', () => finish(true));
    s.on('error', () => finish(false));
  });
}

function reviveTunnel() {
  const now = Date.now();
  if (now - lastReviveAt < TUNNEL_REVIVE_COOLDOWN_MS) return;
  lastReviveAt = now;
  reviveAttempts++;
  tunnelHealth.reviveAttempts = reviveAttempts;
  tunnelHealth.lastReviveAt = now;

  const script = values['keeper'];
  if (!script) {
    log('  ! 隧道已断，但未配置 --keeper 脚本，无法自动恢复');
    return;
  }
  log(`  ! 隧道已断（连续 ${tunnelFailStreak} 次探测失败），尝试拉起 keeper: ${script}`);
  try {
    // 坑（实测）：Windows 上 spawn(..., {detached:true}) 拉 powershell.exe，
    // 子进程会在 ~300ms 内以 code=0 退出，脚本一行都没执行 —— 连它自己的
    // 日志文件都不会生成。必须用**非 detached**，keeper 才能正常常驻。
    // 也不再 unref：dual-proxy 靠监听端口维持事件循环，不会因为持有子进程引用而
    // 提前退出；而 unref 会让 stdio 管道在父进程空闲时被回收。
    const child = spawn(
      'powershell.exe',
      ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script,
       '-RemoteHost', values['remote'] || 'pcs',
       '-SocksPort', String(SOCKS_PORT)],
      { stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true },
    );
    const startedAtChild = Date.now();
    const relay = (tag) => (buf) => {
      const s = buf.toString().trimEnd();
      if (!s) return;
      for (const line of s.split(/\r?\n/)) log(`  [keeper ${tag}] ${line}`);
    };
    child.stdout.on('data', relay('out'));
    child.stderr.on('data', relay('err'));
    child.on('exit', (code, sig) => {
      log(`  ! keeper 退出 code=${code} sig=${sig ?? '-'}（存活 ${Math.round((Date.now() - startedAtChild) / 1000)}s）`);
    });
    child.on('error', (e) => log(`  ! keeper spawn error: ${e.message}`));
    log(`  → keeper 已启动 (pid ${child.pid})`);
  } catch (e) {
    log(`  ! keeper 启动失败: ${e.message}`);
  }
}

async function tunnelWatchdog() {
  const ok = await probeSocksPort();
  if (ok) {
    if (tunnelFailStreak > 0) log(`  ✓ 隧道已恢复（SOCKS ${SOCKS_PORT} 可连）`);
    tunnelFailStreak = 0;
    tunnelHealth.alive = true;
    tunnelHealth.lastOkAt = Date.now();
    tunnelHealth.failStreak = 0;
    return;
  }
  tunnelFailStreak++;
  tunnelHealth.alive = false;
  tunnelHealth.lastFailAt = Date.now();
  tunnelHealth.failStreak = tunnelFailStreak;
  if (tunnelFailStreak === 1) {
    log(`  ! SOCKS ${SOCKS_PORT} 探测失败（第 ${tunnelFailStreak} 次）`);
  }
  if (tunnelFailStreak >= TUNNEL_FAIL_MAX) {
    reviveTunnel();
  }
}

/* ---------------- 单端口分流 ---------------- */

const front = net.createServer((socket) => {
  socket.once('data', (first) => {
    const proto = detectProtocol(first[0]);
    socket.pause();

    if (proto === 'socks5') {
      // 把已读的首包塞回
      socket.unshift(first);
      socksServer.emit('connection', socket);
    } else {
      socket.unshift(first);
      httpServer.emit('connection', socket);
    }
    socket.resume();
  });
  socket.on('error', () => socket.destroy());
});

front.listen(LISTEN_PORT, BIND, () => {
  log(`双协议代理已就绪  ${BIND}:${LISTEN_PORT}  (HTTP + SOCKS5 自动识别)`);
  log(`  上游 SOCKS5: 127.0.0.1:${SOCKS_PORT}`);
  if (values['allow-lan']) {
    log(`  ⚠️ 已开启局域网共享，同网段可用 本机IP:${LISTEN_PORT}`);
  }
  // 启动即探一次，别等 5 秒
  tunnelWatchdog();
  setInterval(tunnelWatchdog, TUNNEL_CHECK_MS).unref();
});

/* ---------------- 状态面板（可选） ---------------- */

if (PANEL_PORT > 0) {
  const panel = http.createServer((req, res) => {
    if (req.url === '/api/status') {
      res.writeHead(200, { 'content-type': 'application/json; charset=utf-8' });
      // 用户数据：auth 未启用时给 null，前端据此隐藏整个区块
      const userPayload = auth
        ? { ...auth.snapshot(), perUser: Object.fromEntries(perUser) }
        : null;
      res.end(JSON.stringify({
        ...stats,
        tunnel: tunnelHealth,
        users: userPayload,
        uptimeSec: Math.floor((Date.now() - stats.startedAt) / 1000),
        listen: `${BIND}:${LISTEN_PORT}`,
        upstream: `127.0.0.1:${SOCKS_PORT}`,
      }));
      return;
    }
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
    res.end(`<!doctype html><meta charset="utf-8"><title>借网代理状态</title>
<style>
body{font:14px/1.6 system-ui,"Microsoft YaHei";margin:0;padding:24px;background:#0f1115;color:#e6e6e6}
h1{font-size:18px;margin:0 0 16px}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin-bottom:20px}
.card{background:#181b21;border:1px solid #262b33;border-radius:10px;padding:14px}
.card .k{color:#8b95a5;font-size:12px}
.card .v{font-size:22px;font-weight:600;margin-top:4px}
.ok{color:#3ecf8e}.bad{color:#e5534b}
table{width:100%;border-collapse:collapse;font-size:12px}
th,td{text-align:left;padding:6px 8px;border-bottom:1px solid #262b33}
th{color:#8b95a5;font-weight:500}
tr.bad td{color:#e5534b}
</style>
<h1>借网代理状态 <span id="up" style="font-size:12px;color:#8b95a5"></span></h1>
<div class="grid">
  <div class="card"><div class="k">累计连接</div><div class="v" id="total">-</div></div>
  <div class="card"><div class="k">当前活跃</div><div class="v" id="active">-</div></div>
  <div class="card"><div class="k">成功</div><div class="v ok" id="ok">-</div></div>
  <div class="card"><div class="k">失败</div><div class="v bad" id="fail">-</div></div>
  <div class="card"><div class="k">SOCKS5 / HTTP</div><div class="v" id="split">-</div></div>
</div>
 <h1 style="font-size:14px">最近连接</h1>
 <table><thead><tr><th>时间</th><th>协议</th><th>目标</th><th>用户</th><th>耗时</th></tr></thead><tbody id="rows"></tbody></table>
 <div id="usersec"></div>
 <h1 style="font-size:14px">被拒绝的访问</h1>
 <table><thead><tr><th>时间</th><th>来源</th><th>原因</th></tr></thead><tbody id="denials"></tbody></table>
<script>
function fmtB(n){ if(!n) return '0 B'; const u=['B','KB','MB','GB']; let i=0,v=Number(n);
  while(v>=1024&&i<u.length-1){v/=1024;i++;} return v.toFixed(v>=100?0:1)+' '+u[i]; }
function agoT(t){ if(!t) return '—'; const s=Math.floor((Date.now()-t)/1000);
  if(s<60) return s+'s 前'; if(s<3600) return Math.floor(s/60)+'m 前';
  if(s<86400) return Math.floor(s/3600)+'h 前'; return Math.floor(s/86400)+'d 前'; }
async function tick(){
  const r = await fetch('/api/status'); const s = await r.json();
  document.getElementById('up').textContent = '已运行 ' + s.uptimeSec + 's · ' + s.listen + ' → ' + s.upstream;
  document.getElementById('total').textContent = s.total;
  document.getElementById('active').textContent = s.active;
  document.getElementById('ok').textContent = s.ok;
  document.getElementById('fail').textContent = s.fail;
  document.getElementById('split').textContent = s.socks5 + ' / ' + (s.http + s.connect);
  document.getElementById('rows').innerHTML = s.recent.slice(0,30).map(x =>
    '<tr class="' + (x.ok?'':'bad') + '"><td>' + new Date(x.t).toLocaleTimeString() +
    '</td><td>' + x.kind + '</td><td>' + x.target + '</td><td>' + (x.user||'<span style="color:#8b95a5">匿名</span>') +
    '</td><td>' + x.ms + 'ms</td></tr>').join('');

  const box = document.getElementById('usersec');
  if (s.users) {
    const u = s.users;
    const auth = u.requireAuth
      ? '<span class="ok">已启用</span>'
      : '<span style="color:#e5534b">未启用（任何人都能用）</span>';
    const rows = u.users.map(x => {
      const live = x.live;
      const dot = (live && live.conns > 0) ? '<span class="ok">●</span>' : '<span style="color:#8b95a5">○</span>';
      const st = x.enabled ? '' : ' <span style="color:#e5534b">[停用]</span>';
      const note = x.note ? '<div style="color:#8b95a5;font-size:11px">' + x.note + '</div>' : '';
      return '<tr><td>' + dot + ' <b>' + x.name + '</b>' + st + note + '</td>' +
        '<td>' + x.tokenHint + '</td>' +
        '<td>' + (live ? live.conns : 0) + '</td>' +
        '<td>' + fmtB(live ? live.bytesIn : 0) + ' ↓</td>' +
        '<td>' + fmtB(live ? live.bytesOut : 0) + ' ↑</td>' +
        '<td>' + x.total.totalConns + '</td>' +
        '<td>' + agoT(x.total.lastSeen ? new Date(x.total.lastSeen).getTime() : 0) + '</td></tr>';
    }).join('');
    box.innerHTML =
      '<h1 style="font-size:14px">用户 <span style="font-size:12px;color:#8b95a5">认证 ' + auth +
      ' · 共 ' + u.summary.total + ' 人（启用 ' + u.summary.enabled + '）· 当前在线 ' +
      u.summary.activeUsers + ' 人 · ' + u.summary.totalConns + ' 个活跃连接</span></h1>' +
      '<table><thead><tr><th>用户</th><th>令牌</th><th>连接</th><th>下行</th><th>上行</th><th>累计连接</th><th>最后活跃</th></tr></thead>' +
      '<tbody>' + (rows || '<tr><td colspan="7" style="color:#8b95a5">还没有用户，用 proxy-admin.mjs add 添加</td></tr>') + '</tbody></table>';
  } else {
    box.innerHTML = '<h1 style="font-size:14px">用户 <span style="font-size:12px;color:#8b95a5">未启用认证（未传 --users 参数）</span></h1>';
  }

  document.getElementById('denials').innerHTML = ((s.users && s.users.denials) || []).slice(0,10).map(x =>
    '<tr class="bad"><td>' + new Date(x.t).toLocaleTimeString() + '</td><td>' + x.remote +
    '</td><td>' + x.reason + '</td></tr>').join('') ||
    '<tr><td colspan="3" style="color:#8b95a5">无</td></tr>';
}
tick(); setInterval(tick, 1500);
</script>
`);
  });
  panel.listen(PANEL_PORT, '127.0.0.1', () => {
    log(`状态面板: http://127.0.0.1:${PANEL_PORT}/`);
  });
}

/* ---------------- 退出 ---------------- */

function shutdown() {
  log('退出');
  front.close();
  httpServer.close();
  socksServer.close();
  process.exit(0);
}
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);

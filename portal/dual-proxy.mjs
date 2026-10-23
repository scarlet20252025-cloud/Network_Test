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
import { spawn, execSync } from 'node:child_process';
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

/* ---------------- 每用户限速（GCRA） ----------------
 *
 * 为什么不用令牌桶（实测踩过）：
 *   标准令牌桶初始给满一桶（= 1 秒配额），前 1 秒的流量完全免费。
 *   实测 5MB @ 2MB/s 只花 1.03s（应 2.5s）、4.86 MB/s（超 2.4 倍）；
 *   20MB @ 5MB/s 实测 8.08 MB/s。桶越大偏差越大。
 *
 * GCRA 只维护「下一个包最早可发时间」，每个包消耗 n/rate 秒。
 * 没有桶、没有初始免费额度，实测 20MB@5MB/s 正好 4.00s / 5.00 MB/s。
 *
 * 为什么按「用户」而不是按「连接」限速：
 *   一个人能开 40 条连接，按连接限速等于把总配额乘 40。
 */
const gcraNext = new Map();   // name -> 下一个包最早可发时间（ms）
const GCRA_TOLERANCE_MS = 50; // 允许 50ms 突发，避免 TCP 窗口塌陷

function takeTokens(name, n, rate) {
  if (!rate || rate <= 0) return 0;   // 不限速
  const nowMs = Date.now();
  const costMs = (n / rate) * 1000;
  let t = gcraNext.get(name);
  if (t === undefined) t = nowMs;
  const earliest = Math.max(nowMs - GCRA_TOLERANCE_MS, t);
  gcraNext.set(name, earliest + costMs);
  const waitMs = earliest - nowMs;
  return waitMs > 0 ? Math.ceil(waitMs) : 0;
}

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
 * 限速转发：替代所有 `sock.pipe(dst)`。
 *
 * 为什么不能只用 sock.pipe()：pipe 无法插队限速。
 * 为什么不能只 setTimeout 不 pause：数据会继续涌进内存，
 *   最后一次性冲出去，限速形同虚设。
 *
 * limiter(chunkLen) 返回「需要等多少毫秒」，0 表示不用等。
 */
/**
 * 给某个用户造一个 limiter。返回 null 表示不限速。
 *
 * 为什么按用户而不是按连接：一个人能开 40 条连接，
 * 按连接限速等于把总带宽乘 40，限了跟没限一样。
 */
function makeLimiter(userName) {
  if (!auth || !userName) return null;
  const rate = auth.store.settings.bytesPerSecPerUser || 0;
  if (rate <= 0) return null;
  return (n) => takeTokens(userName, n, rate);
}

function pipeLimited(src, dst, limiter) {
  // 队列式：'data' 只入队，单泵按节奏出队。
  //
  // 为什么不用「pause + setTimeout」那版（实测踩过）：
  //   在 'data' 里 pause 后 setTimeout，Node 在 pause 生效前可能已 emit
  //   多个缓冲 chunk，于是**多个 setTimeout 并发排队**、顺序错乱；
  //   src 'close' 又可能先于尾部 setTimeout 触发 → 实测 5MB 丢 64KB。
  //
  // 队列式的关键：单泵串行、队列有界、只有队列空且 src 结束才 dst.end()。
  const queue = [];
  let pumping = false;
  let srcEnded = false;
  let dstClosed = false;
  let queued = 0;
  const HIGH_WATER = 1024 * 1024;   // 1MB 高水位，够吸收抖动又不吃内存

  const finish = () => {
    if (dstClosed) return;
    dstClosed = true;
    if (dst.writable) dst.end();
  };

  const pump = () => {
    if (pumping) return;
    pumping = true;
    const step = () => {
      if (dstClosed) { pumping = false; return; }
      if (queue.length === 0) {
        pumping = false;
        if (srcEnded) finish();
        return;
      }
      const chunk = queue.shift();
      queued -= chunk.length;
      if (queued < HIGH_WATER / 2) src.resume();

      const waitMs = limiter ? limiter(chunk.length) : 0;
      const write = () => {
        if (dstClosed) { pumping = false; return; }
        if (!dst.writable) { finish(); pumping = false; return; }
        if (dst.write(chunk)) step();
        else dst.once('drain', step);   // 等下游消化，不丢数据
      };
      if (waitMs > 0) setTimeout(write, waitMs);
      else write();
    };
    step();
  };

  src.on('data', (chunk) => {
    queue.push(chunk);
    queued += chunk.length;
    if (queued >= HIGH_WATER) src.pause();
    pump();
  });
  src.on('end', () => { srcEnded = true; pump(); });
  src.on('close', () => { srcEnded = true; pump(); });
  src.on('error', () => { if (dst.writable) dst.destroy(); });
  dst.on('error', () => src.destroy());
  dst.on('close', () => { dstClosed = true; });
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
function pipeWithConfirm(src, dst, confirm, label, limiter) {
  let bytes = 0;
  let settled = false;
  const settle = (ok, detail) => {
    if (settled) return;
    settled = true;
    clearTimeout(timer);
    confirm(ok, detail);
  };
  const timer = setTimeout(() => settle(false, `${label} 6s 无上游字节`), 6000);

  // 复用 pipeLimited，只是额外观察「有没有拿到首字节」。
  // 不要在这里再挂 src.on('data') —— 会和 pipeLimited 抢数据（踩过的坑）。
  const origLimiter = limiter;
  const wrapped = (n) => {
    if (bytes === 0) settle(true);
    bytes += n;
    return origLimiter ? origLimiter(n) : 0;
  };

  src.once('close', () => {
    if (bytes === 0) settle(false, `${label} 上游零字节即关闭`);
    else settle(true);
  });

  pipeLimited(src, dst, wrapped);
}

/**
 * 把上游的 HTTP 响应转成 res。必须自己解析响应头。
 *
 * 为什么不能直接 pipe：
 *   res 是 Node 的 ServerResponse，它会在第一次 write 时自己补状态行和头。
 *   把上游的 "HTTP/1.1 200 OK\r\nContent-Length: 1234\r\n\r\n<body>"
 *   整个写进去，Node 会把状态行当成 body 的第一个 chunk，
 *   同时按自己的规则补 Content-Length: 0 —— 客户端拿到 0 字节。
 *   实测：baidu 200 但 size_download=0，github 走 CONNECT 却正常。
 *
 * 处理要点：
 *   · 头部可能分多个 TCP 段到达，要缓存到 \r\n\r\n 为止
 *   · Content-Length / Transfer-Encoding 必须原样搬（不能重算）
 *   · 1xx / 204 / 304 没有 body，直接结束
 *   · 剩余字节（已读到的 body 部分）要先写出去
 */
function relayHttpResponse(sock, res, limiter, onDone, onError) {
  let buf = Buffer.alloc(0);
  let headersParsed = false;
  let finished = false;

  const finish = (err) => {
    if (finished) return;
    finished = true;
    if (err) onError(err);
    else onDone();
  };

  // 换掉 pipeLimited，改用自己控制的队列式转发（因为要先剥头）
  const writeBody = (chunk) => {
    const waitMs = limiter ? limiter(chunk.length) : 0;
    if (waitMs > 0) {
      sock.pause();
      setTimeout(() => {
        if (!res.writable) return;
        if (res.write(chunk)) sock.resume();
        else res.once('drain', () => sock.resume());
      }, waitMs);
    } else {
      if (!res.write(chunk)) {
        sock.pause();
        res.once('drain', () => sock.resume());
      }
    }
  };

  const onData = (chunk) => {
    if (finished) return;

    if (!headersParsed) {
      buf = Buffer.concat([buf, chunk]);
      const idx = buf.indexOf('\r\n\r\n');
      if (idx < 0) {
        // 头部太大？给个上限，避免恶意服务器把内存吃满
        if (buf.length > 64 * 1024) finish('响应头超过 64KB');
        return;
      }
      const headText = buf.subarray(0, idx).toString('latin1');
      const body = buf.subarray(idx + 4);
      buf = Buffer.alloc(0);

      const lines = headText.split('\r\n');
      const statusLine = lines.shift() || '';
      const m = /^HTTP\/\d\.\d\s+(\d{3})\s*(.*)$/.exec(statusLine);
      if (!m) return finish(`无法解析状态行: ${statusLine.slice(0, 60)}`);
      const statusCode = Number(m[1]);

      const headers = {};
      for (const line of lines) {
        const c = line.indexOf(':');
        if (c <= 0) continue;
        const k = line.slice(0, c).trim();
        const v = line.slice(c + 1).trim();
        // 逐跳头不能转发
        const lower = k.toLowerCase();
        if (lower === 'connection' || lower === 'keep-alive' ||
            lower === 'proxy-authenticate' || lower === 'proxy-authorization' ||
            lower === 'transfer-encoding' && false) {
          // 注意：transfer-encoding 要保留（chunked 必须原样传）
          if (lower !== 'transfer-encoding') continue;
        }
        headers[k] = v;
      }

      headersParsed = true;
      sock.removeListener('data', onData);

      try {
        res.writeHead(statusCode, headers);
      } catch (e) {
        return finish(`writeHead 失败: ${e.message}`);
      }

      // 无 body 的状态码
      if (statusCode === 204 || statusCode === 304 ||
          (statusCode >= 100 && statusCode < 200)) {
        res.end();
        sock.destroy();
        return finish(null);
      }

      if (body.length) writeBody(body);
      // 剩下的字节继续走流式
      sock.on('data', (c) => writeBody(c));
      sock.on('end', () => { if (res.writable) res.end(); finish(null); });
      sock.on('close', () => { if (res.writable) res.end(); finish(null); });
      return;
    }
  };

  sock.on('data', onData);
  sock.on('error', (e) => finish(e.code || e.message));
  // 只在「还没解析出头」时才把 close 当错误。
  // 踩过的坑：这里原本无条件 finish('上游未返回完整响应头就关闭')，
  // 但 headersParsed 已经为 true 时（正常结束）也会走到，
  // 于是每个正常响应都变成 502。必须判断 headersParsed。
  sock.on('close', () => {
    if (headersParsed) {
      if (res.writable) res.end();
      finish(null);
    } else {
      finish('上游未返回完整响应头就关闭');
    }
  });

  // 超时保护：30 秒没拿到完整响应头就报错
  setTimeout(() => {
    if (!headersParsed) finish('等待响应头超时（30s）');
  }, 30000).unref();
}

function socksConnect(host, port, timeoutMs = 20000) {  return new Promise((resolve, reject) => {
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

      // ⚠️ HTTP 明文代理不能把上游的**原始响应**直接写进 res。
      // 踩过的坑：`pipeLimited(sock, res)` 会让 Node 的 ServerResponse
      // 把 "HTTP/1.1 200 OK\r\n..." 当成 body，同时自己补一套头
      // （实测 Content-Length: 0），结果响应体全部丢失、下载 0 字节。
      // CONNECT 路径没这问题，因为那条走的是裸 socket。
      //
      // 正解：先解析上游响应头，把状态码和头搬到 res，再转发 body。
      const lim = makeLimiter(a.user?.name);
      relayHttpResponse(sock, res, lim, () => {
        stats.active--;
        session?.close();
        record('http', `${host}:${port}`, Date.now() - t0, true, null, a.user?.name);
      }, (err) => {
        log(`  ! ${host}:${port} ${err}`);
        session?.close();
        record('http', `${host}:${port}`, Date.now() - t0, false, err, a.user?.name);
        if (!res.headersSent) res.writeHead(502, { 'content-type': 'text/plain; charset=utf-8' });
        res.end(`代理错误: ${err}`);
      });

      sock.on('data', (c) => session?.addIn(c.length));
      req.on('data', (c) => session?.addOut(c.length));
      sock.on('error', (e) => {
        log(`  ! ${host}:${port} ${e.message}`);
        if (!res.headersSent) res.writeHead(502);
        res.end();
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
      // 统一走 pipeLimited：既能降权坏 IP（走 confirm），又能限速。
      // 不能混用 sock.pipe() —— 那条路径不限速，而绝大多数域名都不在择优池里。
      const lim = makeLimiter(a.user?.name);
      if (ip !== host) pipeWithConfirm(sock, clientSocket, confirm, `${host}@${ip}`, lim);
      else pipeLimited(sock, clientSocket, lim);
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
          const lim = makeLimiter(userName);
          if (ip !== host) pipeWithConfirm(sock, client, confirm, `${host}@${ip}`, lim);
          else pipeLimited(sock, client, lim);
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
      // keeper 走了，它拉起的 ssh 会变成孤儿继续占着端口。
      // 实测踩过：反复重启 dual-proxy 后堆了 4 个 ssh 进程，
      // 只有第一个能监听，其余全在空转，而隧道端口早就变了。
      killOrphanSsh();
    });
    child.on('error', (e) => log(`  ! keeper spawn error: ${e.message}`));
    log(`  → keeper 已启动 (pid ${child.pid})`);
  } catch (e) {
    log(`  ! keeper 启动失败: ${e.message}`);
  }
}

/**
 * 杀掉「占着某个 -D 端口但没人管」的 ssh。
 *
 * 为什么要：keeper 被强杀时，它前台跑的 ssh 不会跟着死（Windows 没有
 * 进程组的强杀语义），于是 ssh 变成孤儿，继续监听原来的端口。
 * dual-proxy 的端口是自动探测的，重启后端口会变，旧 ssh 占的旧端口
 * 就再也用不上了 —— 但它们还连着远端，白占资源。
 *
 * 做法：找出所有 `ssh -D 127.0.0.1:<port>` 进程，凡是端口不是当前
 * SOCKS_PORT 的一律杀掉；是当前端口的留下（那可能正是活的隧道）。
 */
function killOrphanSsh() {
  try {
    // 用 WMIC 而不是 powershell 嵌套引号 —— 实测后者在 node→cmd→powershell
    // 三层转义下必坏（报「不是内部或外部命令」）。WMIC 是独立 exe，参数干净。
    // CSV 输出形如：Node,CommandLine,ProcessId
    // 注意 CommandLine 里可能含逗号，所以用 first/last comma 定位，不能 split(',')。
    const out = execSync(
      'wmic process where "name=\'ssh.exe\'" get ProcessId,CommandLine /format:csv',
      { encoding: 'utf8', windowsHide: true, timeout: 10000 },
    );
    const lines = out.split(/\r?\n/).map((s) => s.trim()).filter(Boolean);
    const orphans = [];
    for (const line of lines) {
      const firstComma = line.indexOf(',');
      if (firstComma < 0) continue;
      const rest = line.slice(firstComma + 1);
      const lastComma = rest.lastIndexOf(',');
      if (lastComma < 0) continue;
      const cmd = rest.slice(0, lastComma);
      const pid = Number(rest.slice(lastComma + 1));
      const m = /-D\s+127\.0\.0\.1:(\d+)/.exec(cmd);
      if (m && Number.isInteger(pid)) orphans.push({ pid, port: Number(m[1]) });
    }

    let killed = 0;
    for (const o of orphans) {
      if (o.port === SOCKS_PORT) continue;   // 当前隧道的端口，别动
      try {
        process.kill(o.pid, 'SIGKILL');
        killed++;
        log(`  → 清理孤儿 ssh pid=${o.pid}（占着旧端口 ${o.port}）`);
      } catch {
        /* 进程可能已经没了 */
      }
    }
    if (killed > 0) log(`  → 共清理 ${killed} 个孤儿 ssh`);
  } catch (e) {
    log(`  ! 清理孤儿 ssh 失败: ${e.message}`);
  }
}

async function tunnelWatchdog() {
  const ok = await probeSocksPort();
  if (ok) {
    if (tunnelFailStreak > 0) {
      log(`  ✓ 隧道已恢复（SOCKS ${SOCKS_PORT} 可连）`);
      // 隧道恢复后扫一次孤儿。放在这里而不是只在 keeper exit 里调，
      // 是因为旧 keeper 常常是在新 keeper 成功之后才退的，那时再扫已经晚了。
      killOrphanSsh();
    }
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
    res.end(`<!doctype html><meta charset="utf-8"><title>渡口代理状态</title>
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
<h1>渡口代理状态 <span id="up" style="font-size:12px;color:#8b95a5"></span></h1>
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

#!/usr/bin/env node
/**
 * smart-resolve.mjs — 目标 IP 择优解析器
 *
 * 问题（实测确认）：
 *   远端办公网到 github.com 的 DNS 只返回 20.205.243.166，
 *   而该 IP 在远端 5 次采样只通 2 次（40%）。
 *   同一时刻另外 7 个 github IP 全部 5/5 通过。
 *   → 不是网络不通，是「DNS 给的那个 IP 恰好不可达」。
 *
 * 解法：
 *   对已知易故障的域名维护一份候选 IP 池，
 *   建连时并发/串行尝试，第一个成功即用。
 *   不依赖修改 hosts（需要管理员），也不需要远端配合。
 *
 * 候选池来源：
 *   1. 远端实测可用的固定 IP（写死在 POOL 里，带采样记录）
 *   2. 系统 DNS 解析结果（作为补充）
 *   3. DoH 查询（可选，Cloudflare/Google，若可达）
 *
 * 用法（作为库）：
 *   import { resolveBest, connectBest } from './smart-resolve.mjs';
 *   const sock = await connectBest('github.com', 443, { socksPort: 10808 });
 */

import dns from 'node:dns';
import net from 'node:net';

/**
 * 实测可用的候选 IP 池。
 * 采样环境：2026-10-23 03:20 远端直连 github.com:443
 * 每项 5 次采样，记录通过次数。
 */
const POOL = {
  'github.com': [
    { ip: '140.82.121.4', ok: '5/5' },
    { ip: '20.27.177.113', ok: '5/5' },
    { ip: '20.200.245.247', ok: '5/5' },
    { ip: '20.26.156.215', ok: '5/5' },
    { ip: '4.237.22.38', ok: '5/5' },
    { ip: '20.87.245.0', ok: '5/5' },
    { ip: '140.82.113.4', ok: '5/5' },
    { ip: '20.205.243.166', ok: '2/5' }, // DNS 默认返回，最差，放最后
  ],
  'www.github.com': [
    { ip: '140.82.121.4', ok: '5/5' },
    { ip: '20.27.177.113', ok: '5/5' },
  ],
  'gist.github.com': [
    // 注意：gist 的 A 记录和 github.com 不是同一批，别复用 github.com 的池
    { ip: '78.16.49.15', ok: '3/3' },
  ],
  'api.github.com': [
    { ip: '20.205.243.168', ok: '5/5' },
    { ip: '140.82.121.6', ok: '5/5' },
  ],
  'codeload.github.com': [
    { ip: '140.82.121.9', ok: '5/5' },
    { ip: '20.205.243.165', ok: '5/5' },
  ],
  'raw.githubusercontent.com': [
    // 2026-10-23 04:15 远端实测：108.133 = 0/3（黑洞），其余全通
    { ip: '185.199.110.133', ok: '3/3' },
    { ip: '185.199.111.133', ok: '3/3' },
    { ip: '185.199.108.153', ok: '3/3' },
    { ip: '185.199.109.153', ok: '3/3' },
    { ip: '185.199.110.153', ok: '3/3' },
    { ip: '185.199.111.153', ok: '3/3' },
    { ip: '185.199.109.133', ok: '2/3' },
    { ip: '185.199.108.133', ok: '0/3' }, // 排最后
  ],
  'objects.githubusercontent.com': [
    { ip: '185.199.110.133', ok: '3/3' },
    { ip: '185.199.111.133', ok: '3/3' },
    { ip: '185.199.108.153', ok: '3/3' },
    { ip: '185.199.109.153', ok: '3/3' },
    { ip: '185.199.110.153', ok: '3/3' },
    { ip: '185.199.111.153', ok: '3/3' },
    { ip: '185.199.109.133', ok: '2/3' },
    { ip: '185.199.108.133', ok: '0/3' },
  ],
};

/**
 * 通配后缀 → 实际使用的候选池键。
 *
 * ⚠️ 绝不能 fallback 到 github.com 的池。
 * 事故：`gist.githubusercontent.com` 后缀匹配到 `githubusercontent.com`，
 * 旧代码退回 `POOL['github.com']` → 拿 github.com 的 IP（140.82.x）去连
 * gist.githubusercontent.com（真实 IP 185.199.x），SNI 和 IP 都不对，
 * 全部超时（实测 25s 失败）。
 *
 * 正确映射（远端 DNS 实测确认同一批 IP）：
 *   *.githubusercontent.com  → raw.githubusercontent.com 的池（185.199.x）
 *   *.github.com             → github.com 的池（140.82.x / 20.x）
 *   其他后缀无池 → null，让 ssh -D 用远端 DNS 正常解析
 */
const SUFFIX_POOL = {
  'github.com': 'github.com',
  'githubusercontent.com': 'raw.githubusercontent.com',
  'githubassets.com': null,
  'github.io': null,
};

const WILDCARD_SUFFIX = Object.keys(SUFFIX_POOL);

function matchPool(host) {
  const h = host.toLowerCase();
  if (POOL[h]) return POOL[h];
  for (const suf of WILDCARD_SUFFIX) {
    if (h === suf || h.endsWith('.' + suf)) {
      const key = SUFFIX_POOL[suf];
      return key ? (POOL[key] || null) : null;
    }
  }
  return null;
}

/** 系统 DNS 解析（异步） */
function dnsLookup(host) {
  return new Promise((resolve) => {
    dns.lookup(host, { all: true, family: 4 }, (err, addrs) => {
      if (err || !addrs) return resolve([]);
      resolve(addrs.map((a) => a.address));
    });
  });
}

/**
 * 返回按优先级排序的 IP 列表。
 * 顺序：实测可用池（按 ok 排序）→ DNS 结果（去重追加）
 */
export async function resolveBest(host) {
  const pool = matchPool(host);
  const out = [];
  const seen = new Set();

  if (pool) {
    // 按通过率降序
    const sorted = [...pool].sort((a, b) => {
      const ra = parseInt(a.ok, 10);
      const rb = parseInt(b.ok, 10);
      return rb - ra;
    });
    for (const { ip } of sorted) {
      if (!seen.has(ip)) { seen.add(ip); out.push(ip); }
    }
  }

  const dnsAddrs = await dnsLookup(host);
  for (const ip of dnsAddrs) {
    if (!seen.has(ip)) { seen.add(ip); out.push(ip); }
  }

  return out;
}

/**
 * 经 SOCKS5 连接到「域名」——但用指定 IP 建连，SNI 仍是域名。
 *
 * 注意：ssh -D 的 SOCKS5 支持 ATYP=0x01（IPv4 字面量），
 * 所以我们直接给 IP，远端就不会再去解析域名。
 * TLS 的 SNI 由调用方保证（servername=host）。
 */
export function socksConnectToIp(ip, port, socksPort, timeoutMs = 15000) {
  return new Promise((resolve, reject) => {
    const t0 = Date.now();
    const sock = net.connect({ host: '127.0.0.1', port: socksPort });
    let stage = 'greeting';
    let buf = Buffer.alloc(0);
    let settled = false;

    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      sock.destroy();
      reject(new Error(`timeout@${stage} (${ip}:${port})`));
    }, timeoutMs);

    const fail = (m) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      sock.destroy();
      reject(new Error(`${m}@${stage} (${ip}:${port})`));
    };

    sock.on('error', (e) => fail(e.code || e.message));
    sock.on('close', () => fail('closed'));
    sock.on('connect', () => sock.write(Buffer.from([0x05, 0x01, 0x00])));

    sock.on('data', (chunk) => {
      buf = Buffer.concat([buf, chunk]);

      if (stage === 'greeting') {
        if (buf.length < 2) return;
        if (buf[1] !== 0x00) return fail(`auth0x${buf[1].toString(16)}`);
        buf = buf.subarray(2);
        stage = 'request';

        // ATYP=0x01 IPv4 字面量（4 字节）+ port
        const parts = ip.split('.').map(Number);
        if (parts.length !== 4 || parts.some((n) => !Number.isInteger(n) || n < 0 || n > 255)) {
          return fail(`bad ipv4 ${ip}`);
        }
        const p = Buffer.alloc(2);
        p.writeUInt16BE(port, 0);
        sock.write(Buffer.concat([Buffer.from([5, 1, 0, 1, ...parts]), p]));
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
        resolve({ sock, ip, ms: Date.now() - t0 });
      }
    });
  });
}

/**
 * 进程内 IP 健康分缓存（带时间窗口衰减）。
 *
 * 为什么需要：POOL 里的 ok 值是「某一次采样」的快照，会过期。
 * 实测 2026-10-23 04:00 远端直连采样：
 *   140.82.121.4   0/2   ← 而池里排第 1
 *   20.205.243.166 0/2   ← DNS 默认返回
 *   其余 6 个       2/2
 * 串行逐个试会先各浪费一次超时。
 *
 * ⚠️ 教训：第一版用累加计数，dual-proxy 是常驻进程，
 * 跑几百次测试后 fail 累计到几百，所有 IP 的 score 都变负、顺序全乱，
 * 表现为「前 9 次全失败，后 6 次全成功」这种诡异模式。
 * 现在改为**只统计最近 WINDOW_MS 内的事件**，旧数据自动过期。
 */
const WINDOW_MS = 60000;
const HEALTH = new Map(); // ip -> { events: [{ok, t}], lastFail }

function recent(ip) {
  const h = HEALTH.get(ip);
  if (!h) return { ok: 0, fail: 0, lastFail: 0 };
  const cutoff = Date.now() - WINDOW_MS;
  const ev = h.events.filter((e) => e.t >= cutoff);
  if (ev.length !== h.events.length) h.events = ev;
  let ok = 0;
  let fail = 0;
  for (const e of ev) { if (e.ok) ok++; else fail++; }
  const lastFail = ev.filter((e) => !e.ok).reduce((m, e) => Math.max(m, e.t), 0);
  return { ok, fail, lastFail };
}

function score(ip, staticOk) {
  const h = recent(ip);
  if (h.ok === 0 && h.fail === 0) return staticOk;
  // 成功加分、失败扣分；最近 15s 内失败过的额外扣（避免立刻又去试）
  let s = staticOk + h.ok * 12 - h.fail * 18;
  if (h.lastFail && Date.now() - h.lastFail < 15000) s -= 30;
  return s;
}

function markOk(ip) {
  const h = HEALTH.get(ip) || { events: [], lastFail: 0 };
  h.events.push({ ok: true, t: Date.now() });
  if (h.events.length > 20) h.events = h.events.slice(-20);
  HEALTH.set(ip, h);
}

function markFail(ip) {
  const h = HEALTH.get(ip) || { events: [], lastFail: 0 };
  h.events.push({ ok: false, t: Date.now() });
  if (h.events.length > 20) h.events = h.events.slice(-20);
  h.lastFail = Date.now();
  HEALTH.set(ip, h);
}

/**
 * 择优建连：并行竞速（Happy Eyeballs 思路）。
 *
 * 为什么不是串行：串行遇到一个黑洞 IP 就要等满 perTryTimeout。
 * 实测 github.com 池里 140.82.121.4 和 20.205.243.166 都是黑洞，
 * 串行会先浪费 12s 才开始试第三个。
 *
 * 策略：
 *   - 按健康分排序，先发第 1 个；
 *   - 每隔 staggerMs 还没成功就再发下一个，最多 concurrency 个同时在飞；
 *   - 任何一个成功立刻返回，其余销毁；
 *   - **有总预算 totalTimeoutMs**（默认 8s）：到点全部判失败，不再无限等。
 *     教训：第一版没有总预算，8 个 IP 各 6s + dual-proxy 再 fallback 20s，
 *     最坏 38s，浏览器直接判超时，用户看到的就是「打不开」。
 *
 * @param {string} host  域名（用于查候选池）
 * @param {number} port
 * @param {object} opts  { socksPort, perTryTimeout, maxTries, concurrency, staggerMs, totalTimeoutMs }
 * @returns {Promise<{sock, ip, ms, tried}>}
 */
export async function connectBest(host, port, opts = {}) {
  const socksPort = opts.socksPort || 10808;
  const perTryTimeout = opts.perTryTimeout || 3500;
  const maxTries = opts.maxTries || 8;
  const concurrency = opts.concurrency || 4;
  const staggerMs = opts.staggerMs || 400;
  const totalTimeoutMs = opts.totalTimeoutMs || 8000;

  const ips = await resolveBest(host);
  if (!ips.length) throw new Error(`无法解析 ${host}`);

  const pool = matchPool(host) || [];
  const staticMap = new Map(pool.map((p) => [p.ip, parseInt(p.ok, 10) || 0]));

  // 按健康分排序（动态），取前 maxTries 个
  const ranked = ips
    .map((ip) => ({ ip, s: score(ip, staticMap.get(ip) || 0) }))
    .sort((a, b) => b.s - a.s)
    .slice(0, maxTries)
    .map((x) => x.ip);

  return new Promise((resolve, reject) => {
    const tried = [];
    let inflight = 0;
    let nextIdx = 0;
    let done = false;
    let pump = null;
    const t0 = Date.now();

    // 总预算：到点无论还剩多少都判失败（避免用户侧超时）
    const budget = setTimeout(() => failAll('总预算耗尽'), totalTimeoutMs);

    const cleanup = () => {
      if (pump) { clearInterval(pump); pump = null; }
      clearTimeout(budget);
    };

    const failAll = (why = '') => {
      if (done) return;
      done = true;
      cleanup();
      reject(new Error(
        `${host} 所有候选 IP 均失败${why ? `(${why})` : ''}: ` +
        tried.map((t) => `${t.ip}(${t.err})`).join('; ')
      ));
    };

    // 泵：每隔 staggerMs 补充发射，直到候选用完
    const launchOne = () => {
      if (done || nextIdx >= ranked.length) return false;
      const ip = ranked[nextIdx++];
      inflight++;
      socksConnectToIp(ip, port, socksPort, perTryTimeout)
        .then((r) => {
          if (done) { try { r.sock.destroy(); } catch {} return; }
          done = true;
          markOk(ip);
          cleanup();
          resolve({ ...r, tried, elapsed: Date.now() - t0 });
        })
        .catch((e) => {
          markFail(ip);
          tried.push({ ip, err: e.message });
          inflight--;
          if (done) return;
          if (nextIdx >= ranked.length && inflight === 0) failAll();
          // 失败立刻补位（但不超过 concurrency），不等下一个节拍
          else if (inflight < concurrency) launchOne();
        });
      return true;
    };

    launchOne(); // 立即发第一个
    pump = setInterval(() => {
      if (done) { clearInterval(pump); return; }
      if (inflight < concurrency) launchOne();
      if (nextIdx >= ranked.length && inflight === 0) failAll();
    }, staggerMs);
  });
}

export { POOL, matchPool, HEALTH };

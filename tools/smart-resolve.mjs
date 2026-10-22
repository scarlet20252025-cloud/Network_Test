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
 * 采样环境：2026-10-23 03:20 远端 10.13.24.1 直连 github.com:443
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
    { ip: '140.82.121.4', ok: '5/5' },
    { ip: '20.27.177.113', ok: '5/5' },
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
    { ip: '185.199.108.133', ok: '3/5' },
    { ip: '185.199.109.133', ok: '3/5' },
    { ip: '185.199.110.133', ok: '3/5' },
    { ip: '185.199.111.133', ok: '3/5' },
  ],
  'objects.githubusercontent.com': [
    { ip: '185.199.108.133', ok: '3/5' },
    { ip: '185.199.109.133', ok: '3/5' },
    { ip: '185.199.110.133', ok: '3/5' },
    { ip: '185.199.111.133', ok: '3/5' },
  ],
};

/** 通配：任何 *.github.com / githubusercontent.com 都走候选池逻辑 */
const WILDCARD_SUFFIX = ['github.com', 'githubusercontent.com', 'githubassets.com', 'github.io'];

function matchPool(host) {
  const h = host.toLowerCase();
  if (POOL[h]) return POOL[h];
  // 通配：xxx.github.com 用 github.com 的池
  for (const suf of WILDCARD_SUFFIX) {
    if (h === suf || h.endsWith('.' + suf)) {
      return POOL[suf] || POOL['github.com'];
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
 * 择优建连：按候选池顺序逐个尝试，第一个成功即返回。
 * @param {string} host  域名（用于查候选池）
 * @param {number} port
 * @param {object} opts  { socksPort, perTryTimeout, maxTries }
 * @returns {Promise<{sock, ip, ms, tried}>}
 */
export async function connectBest(host, port, opts = {}) {
  const socksPort = opts.socksPort || 10808;
  const perTryTimeout = opts.perTryTimeout || 6000;
  const maxTries = opts.maxTries || 8;

  const ips = await resolveBest(host);
  if (!ips.length) throw new Error(`无法解析 ${host}`);

  const tried = [];
  for (const ip of ips.slice(0, maxTries)) {
    try {
      const r = await socksConnectToIp(ip, port, socksPort, perTryTimeout);
      return { ...r, tried };
    } catch (e) {
      tried.push({ ip, err: e.message });
    }
  }

  throw new Error(`${host} 所有候选 IP 均失败: ${tried.map((t) => `${t.ip}(${t.err})`).join('; ')}`);
}

export { POOL, matchPool };

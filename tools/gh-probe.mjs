#!/usr/bin/env node
/**
 * gh-probe.mjs — github 访问稳定性统计采样
 *
 * 目的：区分「完全不通」和「间歇性超时」。
 * 逐次记录 CONNECT 阶段耗时、TLS 耗时、是否成功，算出失败率与失败分布。
 *
 * 用法：
 *   node gh-probe.mjs --rounds 30
 *   node gh-probe.mjs --rounds 30 --targets github.com,api.github.com
 */

import net from 'node:net';
import tls from 'node:tls';
import { parseArgs } from 'node:util';

const { values } = parseArgs({
  options: {
    socks: { type: 'string', default: '10808' },
    rounds: { type: 'string', default: '20' },
    targets: { type: 'string', default: 'github.com,api.github.com,codeload.github.com' },
    timeout: { type: 'string', default: '12000' },
  },
});

const SOCKS_PORT = Number(values.socks);
const ROUNDS = Number(values.rounds);
const TIMEOUT = Number(values.timeout);
const TARGETS = values.targets.split(',').map((s) => s.trim()).filter(Boolean);

function socksConnect(host, port, timeoutMs) {
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
      reject(new Error(`timeout ${timeoutMs}ms @ ${stage}`));
    }, timeoutMs);

    const fail = (m) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      sock.destroy();
      reject(new Error(`${m} @ ${stage}`));
    };

    sock.on('error', (e) => fail(e.code || e.message));
    sock.on('close', () => fail('closed'));
    sock.on('connect', () => sock.write(Buffer.from([0x05, 0x01, 0x00])));

    sock.on('data', (chunk) => {
      buf = Buffer.concat([buf, chunk]);
      if (stage === 'greeting') {
        if (buf.length < 2) return;
        if (buf[1] !== 0x00) return fail(`auth 0x${buf[1].toString(16)}`);
        buf = buf.subarray(2);
        stage = 'request';
        const h = Buffer.from(host, 'utf8');
        const p = Buffer.alloc(2);
        p.writeUInt16BE(port, 0);
        sock.write(Buffer.concat([Buffer.from([5, 1, 0, 3, h.length]), h, p]));
      }
      if (stage === 'request') {
        if (buf.length < 4) return;
        if (buf[1] !== 0) return fail(`reply 0x${buf[1].toString(16)}`);
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
        resolve({ sock, connectMs: Date.now() - t0 });
      }
    });
  });
}

function tlsHandshake(sock, host, timeoutMs) {
  return new Promise((resolve, reject) => {
    const t0 = Date.now();
    const t = tls.connect({ socket: sock, servername: host, rejectUnauthorized: false });
    const timer = setTimeout(() => {
      t.destroy();
      reject(new Error('tls timeout'));
    }, timeoutMs);
    t.once('secureConnect', () => {
      clearTimeout(timer);
      resolve({ tlsMs: Date.now() - t0, socket: t });
    });
    t.once('error', (e) => {
      clearTimeout(timer);
      reject(new Error(`tls ${e.code || e.message}`));
    });
  });
}

const results = new Map();
for (const t of TARGETS) results.set(t, { ok: 0, fail: 0, errors: [], connect: [], tls: [] });

for (let r = 1; r <= ROUNDS; r++) {
  for (const host of TARGETS) {
    const st = results.get(host);
    try {
      const { sock, connectMs } = await socksConnect(host, 443, TIMEOUT);
      const { tlsMs, socket } = await tlsHandshake(sock, host, TIMEOUT);
      socket.destroy();
      st.ok++;
      st.connect.push(connectMs);
      st.tls.push(tlsMs);
    } catch (e) {
      st.fail++;
      st.errors.push(e.message);
    }
  }
  if (r % 5 === 0) {
    const s = [...results.entries()].map(([h, v]) => `${h.split('.')[0]}:${v.ok}/${v.ok + v.fail}`).join(' ');
    process.stdout.write(`  第 ${r}/${ROUNDS} 轮  ${s}\n`);
  }
}

function pct(arr, p) {
  if (!arr.length) return null;
  const s = [...arr].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.floor(s.length * p))];
}

console.log('\n=== 结果 ===');
for (const [host, st] of results) {
  const total = st.ok + st.fail;
  const rate = total ? ((st.fail / total) * 100).toFixed(1) : '0.0';
  console.log(`\n${host}`);
  console.log(`  成功 ${st.ok}/${total}  失败 ${st.fail}  失败率 ${rate}%`);
  if (st.connect.length) {
    console.log(`  CONNECT p50=${pct(st.connect, 0.5)}ms p95=${pct(st.connect, 0.95)}ms`);
    console.log(`  TLS     p50=${pct(st.tls, 0.5)}ms p95=${pct(st.tls, 0.95)}ms`);
  }
  if (st.errors.length) {
    const m = new Map();
    for (const e of st.errors) m.set(e, (m.get(e) || 0) + 1);
    console.log('  失败原因:');
    for (const [e, n] of [...m.entries()].sort((a, b) => b[1] - a[1])) {
      console.log(`    ${n}x  ${e}`);
    }
  }
}

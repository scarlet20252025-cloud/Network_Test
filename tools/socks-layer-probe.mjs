#!/usr/bin/env node
/**
 * socks-layer-probe.mjs — 分层定位 SSH 隧道的失败层
 *
 * 对同一目标重复建连，记录失败发生在 SOCKS 握手哪一阶段：
 *   greeting  → 本地到 ssh -D 的连接有问题
 *   request   → ssh 到远端的转发有问题（最常见）
 *   tls       → 数据通了但 TLS 握手失败
 *
 * 用法：node socks-layer-probe.mjs --target github.com --rounds 20
 */

import net from 'node:net';
import tls from 'node:tls';
import { parseArgs } from 'node:util';

const { values } = parseArgs({
  options: {
    socks: { type: 'string', default: '10808' },
    target: { type: 'string', default: 'github.com' },
    port: { type: 'string', default: '443' },
    rounds: { type: 'string', default: '20' },
    timeout: { type: 'string', default: '8000' },
  },
});

const SOCKS_PORT = Number(values.socks);
const TARGET = values.target;
const PORT = Number(values.port);
const ROUNDS = Number(values.rounds);
const TIMEOUT = Number(values.timeout);

function probe(timeoutMs) {
  return new Promise((resolve) => {
    const t0 = Date.now();
    const marks = { tcp: null, greet: null, req: null, tls: null };
    let stage = 'tcp';
    let settled = false;
    let buf = Buffer.alloc(0);

    const sock = net.connect({ host: '127.0.0.1', port: SOCKS_PORT });

    const done = (ok, err) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      sock.destroy();
      resolve({ ok, err, stage, marks, totalMs: Date.now() - t0 });
    };

    const timer = setTimeout(() => done(false, `timeout@${stage}`), timeoutMs);

    sock.on('error', (e) => done(false, `${e.code || e.message}@${stage}`));
    sock.on('close', () => { if (!settled) done(false, `closed@${stage}`); });

    sock.on('connect', () => {
      marks.tcp = Date.now() - t0;
      stage = 'greeting';
      sock.write(Buffer.from([0x05, 0x01, 0x00]));
    });

    sock.on('data', (chunk) => {
      buf = Buffer.concat([buf, chunk]);
      if (stage === 'greeting') {
        if (buf.length < 2) return;
        if (buf[1] !== 0x00) return done(false, `auth 0x${buf[1].toString(16)}@greeting`);
        marks.greet = Date.now() - t0;
        buf = buf.subarray(2);
        stage = 'request';
        const h = Buffer.from(TARGET, 'utf8');
        const p = Buffer.alloc(2);
        p.writeUInt16BE(PORT, 0);
        sock.write(Buffer.concat([Buffer.from([5, 1, 0, 3, h.length]), h, p]));
        return;
      }
      if (stage === 'request') {
        if (buf.length < 4) return;
        if (buf[1] !== 0) return done(false, `reply 0x${buf[1].toString(16)}@request`);
        const atyp = buf[3];
        const need = atyp === 1 ? 10 : atyp === 4 ? 22 : 4 + 1 + buf[4] + 2;
        if (buf.length < need) return;
        marks.req = Date.now() - t0;
        const rest = buf.subarray(need);
        if (rest.length) sock.unshift(rest);
        sock.removeAllListeners('data');

        // TLS 阶段
        stage = 'tls';
        const t = tls.connect({ socket: sock, servername: TARGET, rejectUnauthorized: false });
        t.once('secureConnect', () => {
          marks.tls = Date.now() - t0;
          t.destroy();
          done(true, null);
        });
        t.once('error', (e) => done(false, `tls ${e.code || e.message}`));
      }
    });
  });
}

const stats = { ok: 0, fail: 0, stages: {}, errors: {}, tcp: [], req: [], tls: [] };

for (let i = 1; i <= ROUNDS; i++) {
  const r = await probe(TIMEOUT);
  if (r.ok) {
    stats.ok++;
    if (r.marks.tcp) stats.tcp.push(r.marks.tcp);
    if (r.marks.req) stats.req.push(r.marks.req);
    if (r.marks.tls) stats.tls.push(r.marks.tls);
  } else {
    stats.fail++;
    stats.stages[r.stage] = (stats.stages[r.stage] || 0) + 1;
    stats.errors[r.err] = (stats.errors[r.err] || 0) + 1;
  }
  process.stdout.write(r.ok ? '.' : 'X');
}
process.stdout.write('\n');

const pct = (a, p) => (a.length ? [...a].sort((x, y) => x - y)[Math.min(a.length - 1, Math.floor(a.length * p))] : null);

console.log(`\n=== ${TARGET}:${PORT} 经 SOCKS ${SOCKS_PORT} ===`);
console.log(`成功 ${stats.ok}/${ROUNDS}  失败 ${stats.fail}  失败率 ${((stats.fail / ROUNDS) * 100).toFixed(1)}%`);
console.log(`\n失败阶段分布:`);
for (const [s, n] of Object.entries(stats.stages)) console.log(`  ${s}: ${n}`);
console.log(`\n失败原因:`);
for (const [e, n] of Object.entries(stats.errors).sort((a, b) => b[1] - a[1])) console.log(`  ${n}x  ${e}`);
if (stats.tcp.length) {
  console.log(`\n各阶段耗时（成功样本）:`);
  console.log(`  本地→ssh  TCP:    p50=${pct(stats.tcp, 0.5)}ms`);
  console.log(`  SOCKS 请求(含远端建连): p50=${pct(stats.req, 0.5)}ms p95=${pct(stats.req, 0.95)}ms`);
  console.log(`  TLS 握手:         p50=${pct(stats.tls, 0.5)}ms`);
}

#!/usr/bin/env node
/**
 * concurrency-probe.mjs — 测 SSH -D 的并发承载上限
 *
 * 假设：ssh -D 单条 SSH 连接复用所有转发，并发一高就排队/超时。
 * 验证：不同并发度下建连成功率和耗时。
 *
 * 用法：node concurrency-probe.mjs
 */

import net from 'node:net';
import { parseArgs } from 'node:util';

const { values } = parseArgs({
  options: {
    socks: { type: 'string', default: '10808' },
    target: { type: 'string', default: 'github.com' },
    timeout: { type: 'string', default: '10000' },
  },
});

const SOCKS_PORT = Number(values.socks);
const TARGET = values.target;
const TIMEOUT = Number(values.timeout);

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
    sock.on('connect', () => sock.write(Buffer.from([5, 1, 0])));
    sock.on('data', (chunk) => {
      buf = Buffer.concat([buf, chunk]);
      if (stage === 'greeting') {
        if (buf.length < 2) return;
        if (buf[1] !== 0) return fail(`auth`);
        buf = buf.subarray(2);
        stage = 'request';
        const h = Buffer.from(host, 'utf8');
        const p = Buffer.alloc(2);
        p.writeUInt16BE(port, 0);
        sock.write(Buffer.concat([Buffer.from([5, 1, 0, 3, h.length]), h, p]));
      }
      if (stage === 'request') {
        if (buf.length < 4) return;
        if (buf[1] !== 0) return fail(`reply0x${buf[1].toString(16)}`);
        const atyp = buf[3];
        const need = atyp === 1 ? 10 : atyp === 4 ? 22 : 4 + 1 + buf[4] + 2;
        if (buf.length < need) return;
        settled = true;
        clearTimeout(timer);
        sock.removeAllListeners('data');
        sock.removeAllListeners('error');
        sock.removeAllListeners('close');
        sock.destroy();
        resolve(Date.now() - t0);
      }
    });
  });
}

async function testConcurrency(n, hold) {
  const t0 = Date.now();
  const tasks = [];
  for (let i = 0; i < n; i++) tasks.push(socksConnect(TARGET, 443, TIMEOUT));
  const rs = await Promise.allSettled(tasks);
  const ok = rs.filter((r) => r.status === 'fulfilled');
  const bad = rs.filter((r) => r.status === 'rejected');
  const times = ok.map((r) => r.value);
  const p50 = times.length ? [...times].sort((a, b) => a - b)[Math.floor(times.length / 2)] : null;
  const errs = {};
  for (const b of bad) errs[b.reason.message] = (errs[b.reason.message] || 0) + 1;
  console.log(
    `  并发 ${String(n).padStart(2)}: 成功 ${String(ok.length).padStart(2)}/${n}` +
    `  p50=${String(p50).padStart(5)}ms  总耗时=${Date.now() - t0}ms` +
    (Object.keys(errs).length ? `  错误: ${JSON.stringify(errs)}` : '')
  );
  return ok.length === n;
}

console.log(`=== SSH -D 并发承载测试（目标 ${TARGET}:443，超时 ${TIMEOUT}ms）===\n`);
for (const n of [1, 2, 4, 8, 16, 32]) {
  await testConcurrency(n);
  await new Promise((r) => setTimeout(r, 800));
}

console.log('\n=== 串行 vs 并发对照 ===');
let t = Date.now();
for (let i = 0; i < 8; i++) await socksConnect(TARGET, 443, TIMEOUT);
console.log(`  串行 8 次: ${Date.now() - t}ms`);
t = Date.now();
await testConcurrency(8);
console.log(`  并发 8 次: ${Date.now() - t}ms`);

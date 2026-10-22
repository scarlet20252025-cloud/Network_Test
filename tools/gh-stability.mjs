#!/usr/bin/env node
/**
 * gh-stability.mjs — 对比「远端本地直连」vs「经 SSH 隧道」访问 github 的稳定性
 *
 * 目的：区分「远端到 github 波动」和「隧道本身有问题」。
 *
 * 用法：node gh-stability.mjs [轮数]
 */

import { execFileSync } from 'node:child_process';
import { connectBest } from './smart-resolve.mjs';

const ROUNDS = Number(process.argv[2] || 20);
const GOOD_IPS = ['20.27.177.113', '20.26.156.215', '4.237.22.38', '20.87.245.0', '140.82.121.4'];

function tcpProbe(ip, port, timeoutMs = 3000) {
  return new Promise((resolve) => {
    const t0 = Date.now();
    import('node:net').then(({ default: net }) => {
      const s = net.connect({ host: ip, port });
      let done = false;
      const finish = (ok, err) => {
        if (done) return;
        done = true;
        s.destroy();
        resolve({ ok, ms: Date.now() - t0, err });
      };
      s.setTimeout(timeoutMs);
      s.on('connect', () => finish(true));
      s.on('timeout', () => finish(false, 'timeout'));
      s.on('error', (e) => finish(false, e.code || e.message));
    });
  });
}

console.log(`=== 本地直连 TCP（不经隧道，测本机自己到 github）===`);
for (const ip of GOOD_IPS.slice(0, 3)) {
  const r = await tcpProbe(ip, 443);
  console.log(`  ${ip.padEnd(16)} ${r.ok ? 'OK' : 'FAIL'} ${r.ms}ms ${r.err || ''}`);
}

console.log(`\n=== 经 SSH 隧道（smart-resolve 择优）${ROUNDS} 轮 ===`);
const results = [];
for (let i = 1; i <= ROUNDS; i++) {
  const t0 = Date.now();
  try {
    const r = await connectBest('github.com', 443, { socksPort: 10808, perTryTimeout: 4000, maxTries: 8 });
    const total = Date.now() - t0;
    results.push({ ok: true, ms: total, ip: r.ip });
    console.log(`  #${String(i).padStart(2)} OK   ip=${r.ip.padEnd(16)} ${String(total).padStart(5)}ms  tried=${r.tried.length}`);
    r.sock.destroy();
  } catch (e) {
    const total = Date.now() - t0;
    results.push({ ok: false, ms: total });
    console.log(`  #${String(i).padStart(2)} FAIL ${String(total).padStart(5)}ms`);
  }
  await new Promise((r) => setTimeout(r, 500));
}

const ok = results.filter((r) => r.ok);
const times = ok.map((r) => r.ms).sort((a, b) => a - b);
console.log(`\n=== 汇总 ===`);
console.log(`  成功 ${ok.length}/${ROUNDS} (${((ok.length / ROUNDS) * 100).toFixed(1)}%)`);
if (times.length) {
  console.log(`  延迟 min=${times[0]}ms p50=${times[Math.floor(times.length * 0.5)]}ms max=${times[times.length - 1]}ms`);
}
const byIp = {};
for (const r of ok) byIp[r.ip] = (byIp[r.ip] || 0) + 1;
console.log(`  按 IP: ${JSON.stringify(byIp)}`);

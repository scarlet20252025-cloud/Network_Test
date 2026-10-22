#!/usr/bin/env node
/**
 * 测试 smart-resolve 的择优效果
 * 用法：node test-smart-resolve.mjs
 */

import { resolveBest, connectBest } from './smart-resolve.mjs';

const HOSTS = ['github.com', 'api.github.com', 'codeload.github.com', 'raw.githubusercontent.com'];

console.log('=== 1. 候选池排序 ===');
for (const h of HOSTS) {
  const ips = await resolveBest(h);
  console.log(`\n${h}:`);
  ips.forEach((ip, i) => console.log(`  ${i + 1}. ${ip}`));
}

console.log('\n\n=== 2. 择优建连（20 轮，每轮试 github.com:443）===');
let ok = 0;
let fail = 0;
const tries = [];

for (let i = 1; i <= 20; i++) {
  try {
    const r = await connectBest('github.com', 443, { socksPort: 10808, perTryTimeout: 5000 });
    ok++;
    tries.push(r.tried.length);
    process.stdout.write(r.tried.length === 0 ? '1' : String(r.tried.length + 1));
  } catch (e) {
    fail++;
    process.stdout.write('X');
  }
}
process.stdout.write('\n');
console.log(`成功 ${ok}/20  失败 ${fail}  成功率 ${((ok / 20) * 100).toFixed(0)}%`);
if (tries.length) {
  const avg = (tries.reduce((a, b) => a + b, 0) / tries.length).toFixed(2);
  console.log(`平均尝试次数: ${avg}（0 = 首选 IP 就成功）`);
}

console.log('\n=== 3. 对照：不择优（让 ssh -D 自己解析域名）===');
import net from 'node:net';
function naive() {
  return new Promise((resolve, reject) => {
    const sock = net.connect({ host: '127.0.0.1', port: 10808 });
    let buf = Buffer.alloc(0);
    let stage = 'g';
    const timer = setTimeout(() => { sock.destroy(); reject(new Error('timeout')); }, 6000);
    sock.on('error', reject);
    sock.on('connect', () => sock.write(Buffer.from([5, 1, 0])));
    sock.on('data', (c) => {
      buf = Buffer.concat([buf, c]);
      if (stage === 'g') {
        if (buf.length < 2) return;
        buf = buf.subarray(2); stage = 'r';
        const h = Buffer.from('github.com');
        const p = Buffer.alloc(2); p.writeUInt16BE(443);
        sock.write(Buffer.concat([Buffer.from([5, 1, 0, 3, h.length]), h, p]));
      }
      if (stage === 'r') {
        if (buf.length < 4) return;
        if (buf[1] !== 0) { clearTimeout(timer); sock.destroy(); reject(new Error(`reply0x${buf[1].toString(16)}`)); return; }
        const atyp = buf[3];
        const need = atyp === 1 ? 10 : atyp === 4 ? 22 : 4 + 1 + buf[4] + 2;
        if (buf.length < need) return;
        clearTimeout(timer);
        sock.destroy();
        resolve();
      }
    });
  });
}

let nok = 0;
for (let i = 0; i < 20; i++) {
  try { await naive(); nok++; process.stdout.write('.'); }
  catch { process.stdout.write('X'); }
}
process.stdout.write('\n');
console.log(`不择优: 成功 ${nok}/20  成功率 ${((nok / 20) * 100).toFixed(0)}%`);

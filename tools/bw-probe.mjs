#!/usr/bin/env node
/**
 * bw-probe.mjs — 判断带宽瓶颈是「物理带宽」还是「单连接窗口限制」
 *
 * 原理：
 *   单 TCP 连接吞吐上限 ≈ 窗口大小 / RTT。
 *   如果多连接并发总吞吐 >> 单连接吞吐，说明瓶颈是窗口（可优化）。
 *   如果多连接并发总吞吐 ≈ 单连接吞吐，说明瓶颈是物理带宽（不可优化）。
 *
 * 用法：node bw-probe.mjs [--url URL] [--threads 1,2,4,8] [--seconds 10]
 */

import https from 'node:https';
import http from 'node:http';
import { parseArgs } from 'node:util';

const { values } = parseArgs({
  options: {
    url: { type: 'string', default: 'https://mirrors.tuna.tsinghua.edu.cn/ubuntu-releases/22.04/ubuntu-22.04.5-desktop-amd64.iso' },
    threads: { type: 'string', default: '1,2,4,8' },
    seconds: { type: 'string', default: '8' },
  },
});

const URL_ = values.url;
const SECONDS = Number(values.seconds);
const THREADS = values.threads.split(',').map(Number);

function downloadOnce(signal, onBytes) {
  return new Promise((resolve, reject) => {
    const lib = URL_.startsWith('https') ? https : http;
    const req = lib.get(URL_, { signal }, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
        // 跟随重定向
        const lib2 = res.headers.location.startsWith('https') ? https : http;
        const req2 = lib2.get(res.headers.location, { signal }, (r2) => {
          r2.on('data', (c) => onBytes(c.length));
          r2.on('end', resolve);
          r2.on('error', reject);
        });
        req2.on('error', reject);
        return;
      }
      res.on('data', (c) => onBytes(c.length));
      res.on('end', resolve);
      res.on('error', reject);
    });
    req.on('error', reject);
  });
}

async function testConcurrency(n) {
  const ac = new AbortController();
  let bytes = 0;
  const t0 = Date.now();

  const tasks = [];
  for (let i = 0; i < n; i++) {
    tasks.push(
      downloadOnce(ac.signal, (len) => { bytes += len; }).catch((e) => {
        if (e.name !== 'AbortError') throw e;
      })
    );
  }

  await new Promise((r) => setTimeout(r, SECONDS * 1000));
  ac.abort();
  await Promise.allSettled(tasks);

  const sec = (Date.now() - t0) / 1000;
  const mbps = (bytes / 1024 / 1024) / sec;
  console.log(`  并发 ${String(n).padStart(2)}: ${bytes.toString().padStart(10)} 字节 / ${sec.toFixed(1)}s = ${mbps.toFixed(2)} MB/s`);
  return mbps;
}

console.log(`=== 并发带宽测试 ===`);
console.log(`目标: ${URL_}`);
console.log(`时长: 每档 ${SECONDS}s\n`);

const results = [];
for (const n of THREADS) {
  const r = await testConcurrency(n);
  results.push({ n, mbps: r });
  await new Promise((r) => setTimeout(r, 1500));
}

console.log('\n=== 判定 ===');
const single = results.find((r) => r.n === 1)?.mbps || 0;
const max = Math.max(...results.map((r) => r.mbps));
const gain = single > 0 ? max / single : 0;

console.log(`单连接: ${single.toFixed(2)} MB/s`);
console.log(`最高并发: ${max.toFixed(2)} MB/s（${results.find((r) => r.mbps === max).n} 线程）`);
console.log(`加速比: ${gain.toFixed(2)}x`);

if (gain >= 2) {
  console.log('\n结论：瓶颈是【单连接 TCP 窗口】，多连接可显著提速 → 可优化');
} else if (gain >= 1.3) {
  console.log('\n结论：多连接有小幅提升，窗口和带宽都有影响');
} else {
  console.log('\n结论：瓶颈是【物理带宽上限】，加线程无用 → 不可优化');
}

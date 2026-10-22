#!/usr/bin/env node
/**
 * longrun.mjs — 长时间稳定性测试（分阶段统计，不一次性跑完）
 *
 * 用法：node longrun.mjs [轮数] [间隔ms]
 * 默认 60 轮、间隔 2s ≈ 2 分钟
 */
import net from 'node:net';
import tls from 'node:tls';

const ROUNDS = Number(process.argv[2] || 60);
const GAP = Number(process.argv[3] || 2000);

function once(host, port, proxyPort, timeoutMs = 15000) {
  return new Promise((resolve) => {
    const t0 = Date.now();
    const s = net.connect({ host: '127.0.0.1', port: proxyPort });
    let buf = '';
    let stage = 'connect';
    const timer = setTimeout(() => { s.destroy(); resolve({ ok: false, err: `timeout@${stage}`, ms: Date.now() - t0 }); }, timeoutMs);

    s.on('connect', () => s.write(`CONNECT ${host}:${port} HTTP/1.1\r\nHost: ${host}:${port}\r\n\r\n`));
    s.on('data', (chunk) => {
      if (stage !== 'connect') return;
      buf += chunk.toString('latin1');
      const idx = buf.indexOf('\r\n\r\n');
      if (idx < 0) return;
      const statusLine = buf.slice(0, buf.indexOf('\r\n'));
      if (!/ 200 /.test(statusLine)) {
        clearTimeout(timer); s.destroy();
        return resolve({ ok: false, err: `proxy ${statusLine}`, ms: Date.now() - t0 });
      }
      stage = 'tls';
      const rest = Buffer.from(buf.slice(idx + 4), 'latin1');
      s.removeAllListeners('data');
      if (rest.length) s.unshift(rest);
      const t = tls.connect({ socket: s, servername: host, rejectUnauthorized: false }, () => {
        clearTimeout(timer);
        resolve({ ok: true, ms: Date.now() - t0 });
        t.destroy();
      });
      t.on('error', (e) => { clearTimeout(timer); resolve({ ok: false, err: `tls:${e.code || e.message}`, ms: Date.now() - t0 }); });
    });
    s.on('error', (e) => { clearTimeout(timer); resolve({ ok: false, err: `sock:${e.code || e.message}`, ms: Date.now() - t0 }); });
  });
}

const targets = [
  ['github.com', 443],
  ['www.baidu.com', 443],
  ['api.github.com', 443],
];

console.log(`=== 长跑稳定性 ${ROUNDS} 轮 / 间隔 ${GAP}ms ===\n`);

const stats = {};
for (const [h] of targets) stats[h] = { ok: 0, fail: 0, times: [], errs: {} };

const CHUNK = 10;
for (let i = 1; i <= ROUNDS; i++) {
  for (const [h, p] of targets) {
    const r = await once(h, p, 10809);
    const st = stats[h];
    if (r.ok) { st.ok++; st.times.push(r.ms); }
    else { st.fail++; st.errs[r.err] = (st.errs[r.err] || 0) + 1; }
  }
  if (i % CHUNK === 0) {
    const parts = targets.map(([h]) => {
      const s = stats[h];
      const tot = s.ok + s.fail;
      const p50 = s.times.length ? s.times.slice().sort((a, b) => a - b)[Math.floor(s.times.length * 0.5)] : 0;
      return `${h}: ${s.ok}/${tot} p50=${p50}ms`;
    });
    console.log(`  [${String(i).padStart(3)}/${ROUNDS}] ${parts.join('  |  ')}`);
  }
  await new Promise((r) => setTimeout(r, GAP));
}

console.log('\n=== 最终统计 ===');
for (const [h] of targets) {
  const s = stats[h];
  const tot = s.ok + s.fail;
  const t = s.times.slice().sort((a, b) => a - b);
  console.log(`  ${h.padEnd(20)} ${s.ok}/${tot} (${((s.ok / tot) * 100).toFixed(1)}%)  ` +
    (t.length ? `min=${t[0]} p50=${t[Math.floor(t.length * 0.5)]} p95=${t[Math.floor(t.length * 0.95)]} max=${t[t.length - 1]}ms` : '') +
    (Object.keys(s.errs).length ? `  错误: ${JSON.stringify(s.errs)}` : ''));
}

// 验证队列式 pipeLimited + GCRA 限速器
import net from 'node:net';
import { PassThrough } from 'node:stream';
import { makePipeLimited, makeGcraLimiter } from './pipe-limited.mjs';

const take = makeGcraLimiter();
const pipeLimited = makePipeLimited(take);

async function run(targetBytes, rate, label) {
  const upstream = net.createServer((s) => {
    const chunk = Buffer.alloc(64 * 1024, 0x41);
    let sent = 0;
    const push = () => {
      while (sent < targetBytes) {
        sent += chunk.length;
        if (!s.write(chunk)) { s.once('drain', push); return; }
      }
      s.end();
    };
    push();
  });
  await new Promise((r) => upstream.listen(0, '127.0.0.1', r));
  const port = upstream.address().port;

  const t0 = Date.now();
  let received = 0;
  const src = net.connect({ host: '127.0.0.1', port });
  const dst = new PassThrough();
  dst.on('data', (c) => { received += c.length; });
  pipeLimited(src, dst, (n) => take('u' + label, n, rate));
  await new Promise((r) => dst.on('end', r));
  upstream.close();

  const sec = (Date.now() - t0) / 1000;
  const mb = received / 1024 / 1024;
  const speed = mb / sec;

  console.log(`\n[${label}] 目标 ${(targetBytes / 1024 / 1024).toFixed(1)} MB, 限速 ${(rate / 1024 / 1024).toFixed(1)} MB/s`);
  console.log(`  收到 ${mb.toFixed(2)} MB / ${sec.toFixed(2)}s = ${speed.toFixed(2)} MB/s`);
  console.log(`  丢字节: ${targetBytes - received} B`);
  return { received, targetBytes, sec, speed, rate };
}

let pass = 0, fail = 0;
const T = (n, c, e) => { console.log(`${c ? '✅' : '❌'} ${n}${e ? '  ' + e : ''}`); if (c) pass++; else fail++; };

let r = await run(1024 * 1024, 0, '1MB-不限速');
T('1MB 数据完整', r.received === r.targetBytes);
T('1MB 不限速够快', r.sec < 0.5, `${r.sec.toFixed(2)}s`);

r = await run(5 * 1024 * 1024, 2 * 1024 * 1024, '5MB@2');
T('5MB 数据完整', r.received === r.targetBytes, `差 ${r.targetBytes - r.received} B`);
T('5MB 耗时 2.2–3.2s（理论 2.5s）', r.sec >= 2.2 && r.sec <= 3.2, `${r.sec.toFixed(2)}s`);
T('5MB 速率 1.5–2.4 MB/s', r.speed >= 1.5 && r.speed <= 2.4, `${r.speed.toFixed(2)} MB/s`);

r = await run(20 * 1024 * 1024, 5 * 1024 * 1024, '20MB@5');
T('20MB 数据完整（背压正确）', r.received === r.targetBytes, `差 ${r.targetBytes - r.received} B`);
T('20MB 耗时 3.6–5.0s（理论 4.0s）', r.sec >= 3.6 && r.sec <= 5.0, `${r.sec.toFixed(2)}s`);
T('20MB 速率 4.0–5.6 MB/s', r.speed >= 4.0 && r.speed <= 5.6, `${r.speed.toFixed(2)} MB/s`);

r = await run(8 * 1024 * 1024, 8 * 1024 * 1024, '8MB@8');
T('8MB 耗时 0.8–1.6s（理论 1.0s）', r.sec >= 0.8 && r.sec <= 1.6, `${r.sec.toFixed(2)}s`);

console.log(`\n结果: ${pass} 通过 / ${fail} 失败`);
process.exit(fail === 0 ? 0 : 1);

#!/usr/bin/env node
/**
 * correlate.mjs — 同一时刻对比「远端直连」和「经隧道」，判断波动源
 *
 * 做法：本地经 SOCKS 连 github IP，同时通过 ssh 让远端直连同一 IP。
 * 如果两者同时失败 → 远端到 github 波动（办公网出口问题）。
 * 如果只有隧道失败 → 隧道/本机问题。
 */
import { execFileSync } from 'node:child_process';
import { socksConnectToIp } from './smart-resolve.mjs';

const IPS = ['20.27.177.113', '140.82.121.4', '20.26.156.215'];
const ROUNDS = Number(process.argv[2] || 6);

function tunnelProbe(ip) {
  return new Promise(async (resolve) => {
    const t0 = Date.now();
    try {
      const r = await socksConnectToIp(ip, 443, 10808, 4000);
      r.sock.destroy();
      resolve({ ok: true, ms: Date.now() - t0 });
    } catch (e) {
      resolve({ ok: false, ms: Date.now() - t0, err: e.message.slice(0, 40) });
    }
  });
}

function remoteProbe(ips) {
  const ps = `
$ips = @(${ips.map((i) => `'${i}'`).join(',')})
$out = @()
foreach ($ip in $ips) {
  $ok = 0
  for ($i=0; $i -lt 1; $i++) {
    $c = New-Object Net.Sockets.TcpClient
    try { $ar = $c.BeginConnect($ip, 443, $null, $null); if ($ar.AsyncWaitHandle.WaitOne(3000) -and $c.Connected) { $ok++ } } catch {}
    $c.Close()
  }
  $out += "$ip=$ok"
}
$out -join ' '
`;
  const b64 = Buffer.from(ps, 'utf16le').toString('base64');
  try {
    const out = execFileSync('ssh', ['-o', 'ConnectTimeout=8', 'pcs', `powershell -NoProfile -EncodedCommand ${b64}`], {
      timeout: 30000,
      encoding: 'utf8',
    });
    return out.trim();
  } catch (e) {
    return `远端探测失败: ${e.message.slice(0, 60)}`;
  }
}

console.log(`=== 同时刻对比 ${ROUNDS} 轮 ===`);
console.log(`IP 顺序: ${IPS.join(', ')}\n`);

let tOk = 0;
let rOk = 0;
let both = 0;
let total = 0;

for (let i = 1; i <= ROUNDS; i++) {
  // 先远端（阻塞），再立刻隧道
  const remote = remoteProbe(IPS);
  const rMap = {};
  for (const part of remote.split(/\s+/)) {
    const [k, v] = part.split('=');
    if (k && v !== undefined) rMap[k] = Number(v);
  }

  const tunnelResults = await Promise.all(IPS.map((ip) => tunnelProbe(ip)));

  const line = IPS.map((ip, idx) => {
    const t = tunnelResults[idx];
    const r = rMap[ip] === 1 ? 'R+': 'R-';
    const tn = t.ok ? 'T+' : 'T-';
    total++;
    if (t.ok) tOk++;
    if (rMap[ip] === 1) rOk++;
    if (t.ok && rMap[ip] === 1) both++;
    return `${ip}:${r}${tn}`;
  }).join('  ');

  console.log(`  #${i}  ${line}`);
  await new Promise((r) => setTimeout(r, 800));
}

console.log(`\n=== 汇总 ===`);
console.log(`  远端直连成功: ${rOk}/${total}`);
console.log(`  隧道成功:     ${tOk}/${total}`);
console.log(`  两者都成功:   ${both}/${total}`);
console.log(`\n判读：`);
console.log(`  - 远端成功率低、隧道同样低 → 远端到 github 波动（办公网出口）`);
console.log(`  - 远端成功率高、隧道低     → 隧道/本机问题`);

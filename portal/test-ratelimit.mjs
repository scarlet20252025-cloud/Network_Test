// 限速实测：下载一个大文件，看实际速率是否被压到设定值
// 用 Cloudflare 的测速端点，稳定且不需要额外鉴权
import http from 'node:http';
import net from 'node:net';

const SOCKS_HOST = '127.0.0.1';
const SOCKS_PORT = Number(process.env.SOCKS_PORT || 10800);
const PROXY_PORT = Number(process.env.PROXY_PORT || 10801);
const USER = process.env.PROXY_USER || '';
const TOKEN = process.env.PROXY_TOKEN || '';
const DURATION = Number(process.env.DURATION || 8);   // 秒

/** 经 SOCKS5（带认证）下载，返回 {bytes, seconds, mbps} */
function socksDownload(host, port, path, durationMs) {
  return new Promise((resolve, reject) => {
    const sock = net.connect({ host: SOCKS_HOST, port: SOCKS_PORT });
    let stage = 'greeting';
    let buf = Buffer.alloc(0);
    let bytes = 0;
    const t0 = Date.now();
    let done = false;

    const finish = (err) => {
      if (done) return;
      done = true;
      sock.destroy();
      const sec = (Date.now() - t0) / 1000;
      if (err) reject(new Error(`${err}（已下载 ${bytes} B / ${sec.toFixed(1)}s）`));
      else resolve({ bytes, seconds: sec, mbps: bytes / 1024 / 1024 / sec });
    };

    const timer = setTimeout(() => finish(null), durationMs);

    sock.on('error', (e) => finish(e.code || e.message));
    sock.on('connect', () => {
      // greeting：只报 0x02（用户名密码）
      sock.write(Buffer.from([5, 1, 2]));
    });
    sock.on('data', (d) => {
      buf = Buffer.concat([buf, d]);
      if (stage === 'greeting') {
        if (buf.length < 2) return;
        if (buf[1] !== 2) return finish('服务端不接受用户名密码认证');
        buf = buf.subarray(2);
        stage = 'auth';
        const u = Buffer.from(USER, 'utf8');
        const t = Buffer.from(TOKEN, 'utf8');
        sock.write(Buffer.concat([Buffer.from([1, u.length]), u, Buffer.from([t.length]), t]));
        return;
      }
      if (stage === 'auth') {
        if (buf.length < 2) return;
        if (buf[1] !== 0) return finish('认证失败');
        buf = buf.subarray(2);
        stage = 'request';
        const h = Buffer.from(host, 'utf8');
        sock.write(Buffer.concat([
          Buffer.from([5, 1, 0, 3, h.length]), h,
          Buffer.from([port >> 8, port & 0xff]),
        ]));
        return;
      }
      if (stage === 'request') {
        if (buf.length < 10) return;
        if (buf[1] !== 0) return finish(`SOCKS 连接失败 code=${buf[1]}`);
        buf = buf.subarray(10);
        stage = 'relay';
        sock.write(`GET ${path} HTTP/1.1\r\nHost: ${host}\r\nConnection: close\r\nUser-Agent: rate-test\r\n\r\n`);
        // 已读到的残留计入
        bytes += buf.length;
        buf = Buffer.alloc(0);
        return;
      }
      bytes += d.length;
    });
    sock.on('close', () => finish(null));
  });
}

// 先试 HTTP 代理（更常用）
function httpDownload(host, port, path, durationMs) {
  return new Promise((resolve, reject) => {
    const t0 = Date.now();
    let bytes = 0;
    let done = false;
    const auth = Buffer.from(`${USER}:${TOKEN}`).toString('base64');
    const req = http.request({
      host: '127.0.0.1', port: PROXY_PORT, method: 'GET',
      path: `http://${host}${path}`,
      headers: { Host: host, 'Proxy-Authorization': `Basic ${auth}`, 'User-Agent': 'rate-test' },
    }, (res) => {
      if (res.statusCode !== 200) {
        return reject(new Error(`HTTP ${res.statusCode}`));
      }
      res.on('data', (c) => { bytes += c.length; });
      res.on('end', () => finish(null));
      res.on('error', (e) => finish(e.message));
    });
    const finish = (err) => {
      if (done) return;
      done = true;
      try { req.destroy(); } catch {}
      const sec = (Date.now() - t0) / 1000;
      if (err) reject(new Error(`${err}（已下载 ${bytes} B / ${sec.toFixed(1)}s）`));
      else resolve({ bytes, seconds: sec, mbps: bytes / 1024 / 1024 / sec });
    };
    const timer = setTimeout(() => finish(null), durationMs);
    req.on('error', (e) => finish(e.code || e.message));
    req.end();
  });
}

// 用 speed.cloudflare.com 的 100MB 测试文件
const TARGET = { host: 'speed.cloudflare.com', port: 443, path: '/__down?bytes=100000000' };
const TARGET_HTTP = { host: 'speed.cloudflare.com', port: 80, path: '/__down?bytes=100000000' };

console.log(`测试目标: ${TARGET_HTTP.host}${TARGET_HTTP.path}`);
console.log(`时长: ${DURATION}s\n`);

let ok = 0, ng = 0;
const T = (n, c, e) => { console.log(`${c ? '✅' : '❌'} ${n}${e ? '  ' + e : ''}`); if (c) ok++; else ng++; };

try {
  const r = await httpDownload(TARGET_HTTP.host, TARGET_HTTP.port, TARGET_HTTP.path, DURATION * 1000);
  const mbps = r.mbps;
  console.log(`  下载 ${(r.bytes / 1024 / 1024).toFixed(2)} MB / ${r.seconds.toFixed(1)}s = ${mbps.toFixed(2)} MB/s`);
  // 限速 5MB/s，允许 ±30% 误差（TCP 慢启动 + 令牌桶突发）
  T('速率在 5MB/s 附近（3.5–7）', mbps >= 3.5 && mbps <= 7, `${mbps.toFixed(2)} MB/s`);
} catch (e) {
  console.log(`  HTTP 代理下载失败: ${e.message}`);
  ng++;
}

console.log(`\n结果: ${ok} 通过 / ${ng} 失败`);
process.exit(ng === 0 ? 0 : 1);

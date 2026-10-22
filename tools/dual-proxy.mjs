#!/usr/bin/env node
/**
 * dual-proxy.mjs — 双协议代理（SOCKS5 + HTTP 单端口自动识别）
 *
 * 为什么需要「双协议单端口」：
 *   Windows 系统代理（HKCU Internet Settings 的 ProxyServer）**只支持 HTTP 代理**。
 *   把 ProxyServer 写成 SOCKS5 端口时，浏览器 / Electron / .NET / git 都会
 *   把 SOCKS5 当 HTTP 代理去连 —— 秒失败（实测 1.4ms 返回 000）。
 *
 *   单端口双协议：同一个端口先读首字节判断协议
 *     0x05      → SOCKS5
 *     'C'       → HTTP CONNECT 隧道（HTTPS）
 *     'G','P'…  → HTTP 普通请求
 *   这样系统代理填 http://127.0.0.1:10809 也能用，直接填 socks5 也能用，
 *   用户不用关心协议。
 *
 * 架构（参考 uu-remote-proxy 的思路，但上游是 ssh -D）：
 *   浏览器/系统代理 ──HTTP──▶ dual-proxy ──SOCKS5──▶ ssh -D ──▶ 远端 ──▶ Internet
 *
 * 零依赖：只用 Node 内置模块。
 *
 * 用法：
 *   node dual-proxy.mjs --listen 10809 --socks 10808
 *   node dual-proxy.mjs --listen 10809 --socks 10808 --allow-lan   # 局域网共享
 *   node dual-proxy.mjs --listen 10809 --socks 10808 --panel 10801 # 状态面板
 */

import net from 'node:net';
import http from 'node:http';
import { parseArgs } from 'node:util';
import { connectBest, matchPool } from './smart-resolve.mjs';

const { values } = parseArgs({
  options: {
    listen: { type: 'string', default: '10809' },
    socks: { type: 'string', default: '10808' },
    host: { type: 'string', default: '127.0.0.1' },
    'allow-lan': { type: 'boolean', default: false },
    panel: { type: 'string', default: '0' },
    quiet: { type: 'boolean', default: false },
  },
});

const LISTEN_PORT = Number(values.listen);
const SOCKS_PORT = Number(values.socks);
const BIND = values['allow-lan'] ? '0.0.0.0' : values.host;
const PANEL_PORT = Number(values.panel);
const QUIET = values.quiet;

/* ---------------- 统计（给面板用） ---------------- */

const stats = {
  startedAt: Date.now(),
  total: 0,
  active: 0,
  socks5: 0,
  http: 0,
  connect: 0,
  ok: 0,
  fail: 0,
  recent: [], // {t, kind, target, ms, ok}
  errors: {},
};

function log(...a) {
  if (!QUIET) console.log(new Date().toISOString().slice(11, 23), ...a);
}

function record(kind, target, ms, ok, err) {
  stats.total++;
  if (ok) stats.ok++;
  else {
    stats.fail++;
    const k = err || 'unknown';
    stats.errors[k] = (stats.errors[k] || 0) + 1;
  }
  stats.recent.unshift({ t: Date.now(), kind, target, ms, ok });
  if (stats.recent.length > 200) stats.recent.length = 200;
}

/* ---------------- SOCKS5 客户端 ---------------- */

/**
 * 择优建连包装：对已知易故障域名（github 系）走候选 IP 池，
 * 其余域名照旧让 ssh -D 自己解析。
 *
 * 背景：远端办公网到 github.com 的 DNS 只给 20.205.243.166，
 * 该 IP 实测 40% 可达；候选池里 7 个 IP 都是 100% 可达。
 */
async function smartConnect(host, port, timeoutMs = 20000) {
  if (matchPool(host)) {
    try {
      const r = await connectBest(host, port, {
        socksPort: SOCKS_PORT,
        perTryTimeout: Math.min(timeoutMs, 6000),
      });
      return r.sock;
    } catch (e) {
      // 候选池全失败则退回原路径，至少还有一次机会
      log(`  ! 择优失败 ${host}:${port} ${e.message}，退回域名解析`);
    }
  }
  const r = await socksConnect(host, port, timeoutMs);
  return r.sock;
}

function socksConnect(host, port, timeoutMs = 20000) {
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
    sock.on('connect', () => sock.write(Buffer.from([0x05, 0x01, 0x00])));

    sock.on('data', (chunk) => {
      buf = Buffer.concat([buf, chunk]);

      if (stage === 'greeting') {
        if (buf.length < 2) return;
        if (buf[0] !== 0x05) return fail(`ver${buf[0]}`);
        if (buf[1] !== 0x00) return fail(`auth0x${buf[1].toString(16)}`);
        buf = buf.subarray(2);
        stage = 'request';
        const h = Buffer.from(host, 'utf8');
        const p = Buffer.alloc(2);
        p.writeUInt16BE(port, 0);
        sock.write(Buffer.concat([Buffer.from([5, 1, 0, 3, h.length]), h, p]));
        return;
      }

      if (stage === 'request') {
        if (buf.length < 4) return;
        if (buf[1] !== 0x00) {
          const names = {
            1: 'general', 2: 'notallowed', 3: 'netunreach', 4: 'hostunreach',
            5: 'refused', 6: 'ttlexpired', 7: 'cmdnosupport', 8: 'atypnosupport',
          };
          return fail(names[buf[1]] || `reply0x${buf[1].toString(16)}`);
        }
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
        resolve({ sock, ms: Date.now() - t0 });
      }
    });
  });
}

/* ---------------- 协议分流 ---------------- */

/**
 * 判断首字节属于哪个协议。
 * 0x05 → SOCKS5；其余按 HTTP 处理（'C'=CONNECT, 'G'=GET, 'P'=POST/PUT...）
 */
function detectProtocol(firstByte) {
  if (firstByte === 0x05) return 'socks5';
  return 'http';
}

/* ---------------- HTTP 代理实现（复用 http.Server） ---------------- */

const httpServer = http.createServer((req, res) => {
  let target;
  try {
    target = new URL(req.url);
  } catch {
    res.writeHead(400, { 'content-type': 'text/plain; charset=utf-8' });
    res.end('这是代理服务器，需要绝对 URI（http://host/path）');
    return;
  }
  if (target.protocol !== 'http:') {
    res.writeHead(400, { 'content-type': 'text/plain; charset=utf-8' });
    res.end(`不支持 ${target.protocol}，HTTPS 请走 CONNECT`);
    return;
  }

  const host = target.hostname;
  const port = Number(target.port) || 80;
  const t0 = Date.now();
  log(`HTTP  ${req.method} ${host}:${port}${target.pathname}`);

  smartConnect(host, port)
    .then((sock) => {
      stats.active++;
      const path = target.pathname + target.search;
      const headers = { ...req.headers, host: target.host };
      delete headers['proxy-connection'];

      sock.write(
        `${req.method} ${path} HTTP/1.1\r\n` +
        Object.entries(headers).filter(([, v]) => v !== undefined).map(([k, v]) => `${k}: ${v}`).join('\r\n') +
        '\r\n\r\n'
      );
      req.pipe(sock);
      sock.pipe(res);

      const done = (err) => {
        stats.active--;
        record('http', `${host}:${port}`, Date.now() - t0, !err, err);
      };
      sock.on('error', (e) => {
        log(`  ! ${host}:${port} ${e.message}`);
        if (!res.headersSent) res.writeHead(502);
        res.end();
        done(e.code || e.message);
      });
      sock.on('close', () => {
        if (!res.writableEnded) res.end();
        done(null);
      });
    })
    .catch((e) => {
      log(`  ! ${host}:${port} ${e.message}`);
      record('http', `${host}:${port}`, Date.now() - t0, false, e.message);
      if (!res.headersSent) res.writeHead(502, { 'content-type': 'text/plain; charset=utf-8' });
      res.end(`代理错误: ${e.message}`);
    });
});

httpServer.on('connect', (req, clientSocket, head) => {
  const [host, portStr] = req.url.split(':');
  const port = Number(portStr) || 443;
  const t0 = Date.now();
  log(`CONNECT ${host}:${port}`);

  smartConnect(host, port)
    .then((sock) => {
      stats.active++;
      stats.connect++;
      clientSocket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
      if (head && head.length) sock.write(head);
      sock.pipe(clientSocket);
      clientSocket.pipe(sock);

      let closed = false;
      const done = (err) => {
        if (closed) return;
        closed = true;
        stats.active--;
        record('connect', `${host}:${port}`, Date.now() - t0, !err, err);
        sock.destroy();
        clientSocket.destroy();
      };
      sock.on('error', (e) => done(e.code || e.message));
      clientSocket.on('error', (e) => done(e.code || e.message));
      sock.on('close', () => done(null));
      clientSocket.on('close', () => done(null));
    })
    .catch((e) => {
      log(`  ! CONNECT ${host}:${port} ${e.message}`);
      record('connect', `${host}:${port}`, Date.now() - t0, false, e.message);
      clientSocket.end('HTTP/1.1 502 Bad Gateway\r\n\r\n');
    });
});

httpServer.on('clientError', (err, socket) => {
  if (socket.writable) socket.end('HTTP/1.1 400 Bad Request\r\n\r\n');
});

/* ---------------- SOCKS5 服务端（给能直接用 socks5 的客户端） ---------------- */

const socksServer = net.createServer((client) => {
  let stage = 'greeting';
  let buf = Buffer.alloc(0);
  let upstream = null;
  let targetStr = '';

  client.on('error', () => client.destroy());

  client.on('data', (chunk) => {
    buf = Buffer.concat([buf, chunk]);

    if (stage === 'greeting') {
      if (buf.length < 2) return;
      if (buf[0] !== 0x05) return client.destroy();
      const nMethods = buf[1];
      if (buf.length < 2 + nMethods) return;
      // 只支持无认证
      client.write(Buffer.from([0x05, 0x00]));
      buf = buf.subarray(2 + nMethods);
      stage = 'request';
    }

    if (stage === 'request') {
      if (buf.length < 4) return;
      const cmd = buf[1];
      const atyp = buf[3];
      let host, port, consumed;

      if (atyp === 0x01) {
        if (buf.length < 10) return;
        host = `${buf[4]}.${buf[5]}.${buf[6]}.${buf[7]}`;
        port = buf.readUInt16BE(8);
        consumed = 10;
      } else if (atyp === 0x03) {
        if (buf.length < 5 + buf[4] + 2) return;
        host = buf.subarray(5, 5 + buf[4]).toString('utf8');
        port = buf.readUInt16BE(5 + buf[4]);
        consumed = 5 + buf[4] + 2;
      } else if (atyp === 0x04) {
        if (buf.length < 22) return;
        const seg = [];
        for (let i = 0; i < 8; i++) seg.push(buf.readUInt16BE(4 + i * 2).toString(16));
        host = seg.join(':');
        port = buf.readUInt16BE(20);
        consumed = 22;
      } else {
        client.write(Buffer.from([5, 8, 0, 1, 0, 0, 0, 0, 0, 0]));
        return client.destroy();
      }

      if (cmd !== 0x01) {
        // 不支持 BIND / UDP ASSOCIATE
        client.write(Buffer.from([5, 7, 0, 1, 0, 0, 0, 0, 0, 0]));
        return client.destroy();
      }

      const rest = buf.subarray(consumed);
      targetStr = `${host}:${port}`;
      stage = 'relay';
      stats.socks5++;
      log(`SOCKS5 ${targetStr}`);

      // 关键：必须摘掉 'data' 监听器。
      // 之前没摘，客户端在等 SOCKS 回复期间发的数据（TLS ClientHello 等）
      // 会继续进入本处理器，而 stage 已是 'relay' 走不到任何分支 → 数据被吞
      // → 表现为「拿到 200 但 body 卡到 max-time」（实测 github.com 15s）。
      client.removeAllListeners('data');
      client.pause();

      const t0 = Date.now();
      smartConnect(host, port)
        .then((sock) => {
          stats.active++;
          // 成功回复：VER=5 REP=0 RSV=0 ATYP=1 BND.ADDR=0.0.0.0 BND.PORT=0
          client.write(Buffer.from([5, 0, 0, 1, 0, 0, 0, 0, 0, 0]));
          if (rest.length) sock.write(rest);
          sock.pipe(client);
          client.pipe(sock);
          client.resume();

          let closed = false;
          const done = (err) => {
            if (closed) return;
            closed = true;
            stats.active--;
            record('socks5', targetStr, Date.now() - t0, !err, err);
            sock.destroy();
            client.destroy();
          };
          sock.on('error', (e) => done(e.code || e.message));
          client.on('error', (e) => done(e.code || e.message));
          sock.on('close', () => done(null));
          client.on('close', () => done(null));
        })
        .catch((e) => {
          log(`  ! SOCKS5 ${targetStr} ${e.message}`);
          record('socks5', targetStr, Date.now() - t0, false, e.message);
          client.write(Buffer.from([5, 1, 0, 1, 0, 0, 0, 0, 0, 0]));
          client.destroy();
        });
    }
  });
});

/* ---------------- 单端口分流 ---------------- */

const front = net.createServer((socket) => {
  socket.once('data', (first) => {
    const proto = detectProtocol(first[0]);
    socket.pause();

    if (proto === 'socks5') {
      // 把已读的首包塞回
      socket.unshift(first);
      socksServer.emit('connection', socket);
    } else {
      socket.unshift(first);
      httpServer.emit('connection', socket);
    }
    socket.resume();
  });
  socket.on('error', () => socket.destroy());
});

front.listen(LISTEN_PORT, BIND, () => {
  log(`双协议代理已就绪  ${BIND}:${LISTEN_PORT}  (HTTP + SOCKS5 自动识别)`);
  log(`  上游 SOCKS5: 127.0.0.1:${SOCKS_PORT}`);
  if (values['allow-lan']) {
    log(`  ⚠️ 已开启局域网共享，同网段可用 本机IP:${LISTEN_PORT}`);
  }
});

/* ---------------- 状态面板（可选） ---------------- */

if (PANEL_PORT > 0) {
  const panel = http.createServer((req, res) => {
    if (req.url === '/api/status') {
      res.writeHead(200, { 'content-type': 'application/json; charset=utf-8' });
      res.end(JSON.stringify({
        ...stats,
        uptimeSec: Math.floor((Date.now() - stats.startedAt) / 1000),
        listen: `${BIND}:${LISTEN_PORT}`,
        upstream: `127.0.0.1:${SOCKS_PORT}`,
      }));
      return;
    }
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
    res.end(`<!doctype html><meta charset="utf-8"><title>借网代理状态</title>
<style>
body{font:14px/1.6 system-ui,"Microsoft YaHei";margin:0;padding:24px;background:#0f1115;color:#e6e6e6}
h1{font-size:18px;margin:0 0 16px}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin-bottom:20px}
.card{background:#181b21;border:1px solid #262b33;border-radius:10px;padding:14px}
.card .k{color:#8b95a5;font-size:12px}
.card .v{font-size:22px;font-weight:600;margin-top:4px}
.ok{color:#3ecf8e}.bad{color:#e5534b}
table{width:100%;border-collapse:collapse;font-size:12px}
th,td{text-align:left;padding:6px 8px;border-bottom:1px solid #262b33}
th{color:#8b95a5;font-weight:500}
tr.bad td{color:#e5534b}
</style>
<h1>借网代理状态 <span id="up" style="font-size:12px;color:#8b95a5"></span></h1>
<div class="grid">
  <div class="card"><div class="k">累计连接</div><div class="v" id="total">-</div></div>
  <div class="card"><div class="k">当前活跃</div><div class="v" id="active">-</div></div>
  <div class="card"><div class="k">成功</div><div class="v ok" id="ok">-</div></div>
  <div class="card"><div class="k">失败</div><div class="v bad" id="fail">-</div></div>
  <div class="card"><div class="k">SOCKS5 / HTTP</div><div class="v" id="split">-</div></div>
</div>
<h1 style="font-size:14px">最近连接</h1>
<table><thead><tr><th>时间</th><th>协议</th><th>目标</th><th>耗时</th></tr></thead><tbody id="rows"></tbody></table>
<script>
async function tick(){
  const r = await fetch('/api/status'); const s = await r.json();
  document.getElementById('up').textContent = '已运行 ' + s.uptimeSec + 's · ' + s.listen + ' → ' + s.upstream;
  document.getElementById('total').textContent = s.total;
  document.getElementById('active').textContent = s.active;
  document.getElementById('ok').textContent = s.ok;
  document.getElementById('fail').textContent = s.fail;
  document.getElementById('split').textContent = s.socks5 + ' / ' + (s.http + s.connect);
  document.getElementById('rows').innerHTML = s.recent.slice(0,30).map(x =>
    '<tr class="' + (x.ok?'':'bad') + '"><td>' + new Date(x.t).toLocaleTimeString() +
    '</td><td>' + x.kind + '</td><td>' + x.target + '</td><td>' + x.ms + 'ms</td></tr>').join('');
}
tick(); setInterval(tick, 1500);
</script>`);
  });
  panel.listen(PANEL_PORT, '127.0.0.1', () => {
    log(`状态面板: http://127.0.0.1:${PANEL_PORT}/`);
  });
}

/* ---------------- 退出 ---------------- */

function shutdown() {
  log('退出');
  front.close();
  httpServer.close();
  socksServer.close();
  process.exit(0);
}
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);

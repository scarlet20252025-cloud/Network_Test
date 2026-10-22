#!/usr/bin/env node
/**
 * socks-http-bridge.mjs — 把 SOCKS5 端口桥接成 HTTP 代理
 *
 * 为什么需要它：
 *   ssh -D 只提供 SOCKS5。Windows 系统代理（WinINET / HKCU Internet Settings）
 *   只支持 HTTP 代理。把 ProxyServer 直接写成 127.0.0.1:10808(SOCKS5) 时，
 *   所有读系统代理的程序（浏览器 / Electron / .NET / git for windows）
 *   会把 SOCKS5 端口当 HTTP 代理去连 —— 秒失败（实测 1.4ms 返回 000）。
 *
 *   本脚本监听一个 HTTP 代理端口，内部把请求转成 SOCKS5 打给 ssh -D。
 *
 * 零依赖：只用 Node 内置 net / http / url。
 *
 * 用法：
 *   node socks-http-bridge.mjs --listen 10809 --socks 10808
 *   node socks-http-bridge.mjs --listen 10809 --socks 10808 --allow-lan
 *
 * 支持：
 *   - CONNECT 隧道（HTTPS 必需）
 *   - 普通 HTTP 绝对 URI 请求（GET/POST/PUT/...）
 *   - 上游 SOCKS5 无认证（ssh -D 默认）
 *   - 域名交给远端解析（SOCKS5 ATYP=0x03），本机不做 DNS
 */

import net from 'node:net';
import http from 'node:http';
import { parseArgs } from 'node:util';

const { values } = parseArgs({
  options: {
    listen: { type: 'string', default: '10809' },
    socks: { type: 'string', default: '10808' },
    host: { type: 'string', default: '127.0.0.1' },
    'allow-lan': { type: 'boolean', default: false },
    quiet: { type: 'boolean', default: false },
  },
});

const LISTEN_PORT = Number(values.listen);
const SOCKS_PORT = Number(values.socks);
const BIND = values['allow-lan'] ? '0.0.0.0' : values.host;
const QUIET = values.quiet;

function log(...a) {
  if (!QUIET) console.log(new Date().toISOString().slice(11, 23), ...a);
}

/* ---------------- SOCKS5 客户端（最小实现） ---------------- */

/**
 * 通过上游 SOCKS5 建立一个到 host:port 的 TCP 连接。
 * @returns {Promise<net.Socket>}
 */
function socksConnect(host, port, timeoutMs = 15000) {
  return new Promise((resolve, reject) => {
    const sock = net.connect({ host: '127.0.0.1', port: SOCKS_PORT });
    let stage = 'greeting';
    let buf = Buffer.alloc(0);
    let settled = false;

    const timer = setTimeout(() => {
      if (!settled) {
        settled = true;
        sock.destroy();
        reject(new Error(`SOCKS connect timeout ${timeoutMs}ms -> ${host}:${port}`));
      }
    }, timeoutMs);

    const fail = (msg) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      sock.destroy();
      reject(new Error(msg));
    };

    sock.on('error', (e) => fail(`SOCKS socket error: ${e.message}`));
    sock.on('close', () => fail('SOCKS closed before ready'));

    sock.on('connect', () => {
      // 1) 问候：VER=5, NMETHODS=1, METHODS=[0x00 无认证]
      sock.write(Buffer.from([0x05, 0x01, 0x00]));
    });

    sock.on('data', (chunk) => {
      buf = Buffer.concat([buf, chunk]);

      if (stage === 'greeting') {
        if (buf.length < 2) return;
        if (buf[0] !== 0x05) return fail(`bad SOCKS version ${buf[0]}`);
        if (buf[1] !== 0x00) return fail(`SOCKS auth required (0x${buf[1].toString(16)}), ssh -D 应无需认证`);
        buf = buf.subarray(2);
        stage = 'request';

        // 2) 请求：VER=5 CMD=1(CONNECT) RSV=0
        //    ATYP=0x03 域名（交给远端解析，本机不做 DNS）
        const hostBuf = Buffer.from(host, 'utf8');
        const portBuf = Buffer.alloc(2);
        portBuf.writeUInt16BE(port, 0);
        const req = Buffer.concat([
          Buffer.from([0x05, 0x01, 0x00, 0x03, hostBuf.length]),
          hostBuf,
          portBuf,
        ]);
        sock.write(req);
      }

      if (stage === 'request') {
        if (buf.length < 4) return;
        const rep = buf[1];
        if (rep !== 0x00) {
          const names = {
            1: 'general failure', 2: 'not allowed', 3: 'network unreachable',
            4: 'host unreachable', 5: 'connection refused', 6: 'TTL expired',
            7: 'command not supported', 8: 'address type not supported',
          };
          return fail(`SOCKS reply 0x${rep.toString(16)} (${names[rep] || 'unknown'}) -> ${host}:${port}`);
        }
        const atyp = buf[3];
        let need;
        if (atyp === 0x01) need = 4 + 4 + 2;
        else if (atyp === 0x04) need = 4 + 16 + 2;
        else if (atyp === 0x03) need = 4 + 1 + buf[4] + 2;
        else return fail(`bad ATYP 0x${atyp.toString(16)}`);

        if (buf.length < need) return;

        // 握手完成，把剩余数据交给调用方
        const rest = buf.subarray(need);
        settled = true;
        clearTimeout(timer);
        sock.removeAllListeners('data');
        sock.removeAllListeners('error');
        sock.removeAllListeners('close');
        if (rest.length) sock.unshift(rest);
        resolve(sock);
        return;
      }
    });
  });
}

/* ---------------- HTTP 代理服务 ---------------- */

const server = http.createServer((req, res) => {
  // 普通 HTTP 请求：req.url 是绝对 URI（http://host/path）
  let target;
  try {
    target = new URL(req.url);
  } catch {
    res.writeHead(400, { 'content-type': 'text/plain; charset=utf-8' });
    res.end('需要绝对 URI（这是代理，不是源服务器）');
    return;
  }
  if (target.protocol !== 'http:') {
    res.writeHead(400, { 'content-type': 'text/plain; charset=utf-8' });
    res.end(`不支持 ${target.protocol}，HTTPS 请用 CONNECT`);
    return;
  }

  const host = target.hostname;
  const port = Number(target.port) || 80;
  log(`HTTP  ${req.method} ${host}:${port}${target.pathname}`);

  socksConnect(host, port)
    .then((sock) => {
      const path = target.pathname + target.search;
      const headers = { ...req.headers };
      headers.host = target.host;
      delete headers['proxy-connection'];

      const head =
        `${req.method} ${path} HTTP/1.1\r\n` +
        Object.entries(headers)
          .filter(([, v]) => v !== undefined)
          .map(([k, v]) => `${k}: ${v}`)
          .join('\r\n') +
        '\r\n\r\n';

      sock.write(head);
      req.pipe(sock);
      sock.pipe(res);

      sock.on('error', (e) => {
        log(`  ! 上游错误 ${host}:${port} ${e.message}`);
        if (!res.headersSent) res.writeHead(502);
        res.end();
      });
      sock.on('close', () => { if (!res.writableEnded) res.end(); });
    })
    .catch((e) => {
      log(`  ! ${host}:${port} ${e.message}`);
      if (!res.headersSent) {
        res.writeHead(502, { 'content-type': 'text/plain; charset=utf-8' });
      }
      res.end(`借网代理错误: ${e.message}`);
    });
});

// CONNECT 隧道（HTTPS）
server.on('connect', (req, clientSocket, head) => {
  const [host, portStr] = req.url.split(':');
  const port = Number(portStr) || 443;
  log(`CONNECT ${host}:${port}`);

  socksConnect(host, port)
    .then((sock) => {
      clientSocket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
      if (head && head.length) sock.write(head);
      sock.pipe(clientSocket);
      clientSocket.pipe(sock);

      const closeBoth = () => {
        sock.destroy();
        clientSocket.destroy();
      };
      sock.on('error', closeBoth);
      clientSocket.on('error', closeBoth);
      sock.on('close', closeBoth);
      clientSocket.on('close', closeBoth);
    })
    .catch((e) => {
      log(`  ! CONNECT ${host}:${port} ${e.message}`);
      clientSocket.end('HTTP/1.1 502 Bad Gateway\r\n\r\n');
    });
});

server.on('clientError', (err, socket) => {
  if (socket.writable) socket.end('HTTP/1.1 400 Bad Request\r\n\r\n');
});

server.listen(LISTEN_PORT, BIND, () => {
  log(`HTTP 代理已就绪  http://${BIND}:${LISTEN_PORT}  ->  SOCKS5 127.0.0.1:${SOCKS_PORT}`);
  if (values['allow-lan']) {
    log(`⚠️  已开启局域网共享（--allow-lan），同网段其他人可用 本机IP:${LISTEN_PORT}`);
  }
});

process.on('SIGINT', () => { log('退出'); server.close(); process.exit(0); });
process.on('SIGTERM', () => { server.close(); process.exit(0); });

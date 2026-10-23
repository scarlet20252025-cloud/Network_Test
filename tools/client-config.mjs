#!/usr/bin/env node
/**
 * client-config.mjs — 生成「给使用者的一键配置」
 *
 * 解决的问题：
 *   局域网里其他机器要用渡口，得知道三件事：**代理地址、用户名、令牌**。
 *   口头传递容易错（尤其是 IP 和那一长串令牌），而且机器一换 IP 就得重发。
 *
 * 这个脚本做一件事：把某个用户的接入信息生成一个**自包含**的文件，
 *   1. `--format cmd`  → 双击就设好系统代理（Windows，普通用户可改）
 *   2. `--format sh`   → Linux/macOS 用
 *   3. `--format md`   → 直接贴微信/邮件，人看的
 *
 * 关键是**自动发现本机 IP**：不写死，用户在哪个网段都能用。
 * 也会顺手做一次连通性自检，避免发出去了才发现连不上。
 *
 * 用法：
 *   node client-config.mjs 张三                        # 生成给张三的配置（默认 cmd）
 *   node client-config.mjs 张三 --format md            # 生成说明文档
 *   node client-config.mjs --list                      # 列出所有用户
 */

import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import net from 'node:net';
import { parseArgs } from 'node:util';
import { fileURLToPath } from 'node:url';
import { UserStore } from './proxy-users.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.join(HERE, '..');
const USERS_FILE = process.env.FERRY_USERS || path.join(ROOT, 'users.json');
const PORTS_FILE = path.join(ROOT, '.proxy-state', 'ports.json');

const { values, positionals } = parseArgs({
  allowPositionals: true,
  options: {
    format: { type: 'string', default: 'cmd' },
    out: { type: 'string', default: '' },
    list: { type: 'boolean', default: false },
    lan: { type: 'boolean', default: false },
  },
});

/** 列出所有非内部网卡的 IPv4 地址 */
function localAddresses() {
  const out = [];
  const ifaces = os.networkInterfaces();
  for (const [name, addrs] of Object.entries(ifaces)) {
    for (const a of addrs || []) {
      if (a.family !== 'IPv4' || a.internal) continue;
      out.push({ name, address: a.address, cidr: a.cidr });
    }
  }
  return out;
}

/** 挑一个最可能被局域网访问的地址 */
function pickLanAddress() {
  const addrs = localAddresses();
  if (!addrs.length) return null;
  // 排除虚拟网卡 / VPN：Radmin、easytier、Tailscale、Hyper-V、WSL 等
  // 踩坑：本机同时有 Radmin VPN(26.224.13.134/8) 和 WLAN(10.141.59.235/22)，
  // 按前缀长度排序会选中 /8 的 Radmin —— 但那是 VPN 虚拟地址，
  // 局域网里其他机器根本路由不到，生成的配置直接废掉。
  const skip = /Radmin|easytier|Tailscale|ZeroTier|OpenVPN|WireGuard|Hamachi|TAP|TUN|vEthernet|VMware|VirtualBox|Hyper-V|WSL|Loopback|Pseudo/i;
  const prefer = addrs.filter((a) => !skip.test(a.name));
  const pool = prefer.length ? prefer : addrs;
  // 在真实网卡里再挑前缀最短的（/24 优先于 /16，通常就是实际局域网段）
  pool.sort((a, b) => {
    const ap = Number(a.cidr?.split('/')[1] || 24);
    const bp = Number(b.cidr?.split('/')[1] || 24);
    return ap - bp;
  });
  return { ...pool[0], skipped: addrs.length - pool.length };
}

function readPorts() {
  try {
    const j = JSON.parse(fs.readFileSync(PORTS_FILE, 'utf8'));
    return { http: j.http || 10809, socks: j.socks || 10808, mode: j.mode || 'auto' };
  } catch {
    return { http: 10809, socks: 10808, mode: 'default' };
  }
}

/** 探测某端口在给定地址上是否真的可连 */
function probe(host, port, timeout = 1500) {
  return new Promise((resolve) => {
    const s = net.connect({ host, port });
    const t = setTimeout(() => { s.destroy(); resolve(false); }, timeout);
    s.on('connect', () => { clearTimeout(t); s.destroy(); resolve(true); });
    s.on('error', () => { clearTimeout(t); resolve(false); });
  });
}

/* ---------------- 主流程 ---------------- */

const store = new UserStore(USERS_FILE, { autoSave: false });

if (values.list) {
  const users = store.list();
  if (!users.length) { console.log('还没有用户。用 proxy-admin.mjs add <名字> 添加。'); process.exit(0); }
  console.log(`共 ${users.length} 个用户：`);
  for (const u of users) {
    console.log(`  ${u.enabled ? '✅' : '⛔'} ${u.name}  ${u.token}`);
  }
  process.exit(0);
}

const name = positionals[0];
if (!name) {
  console.error('用法：node client-config.mjs <用户名> [--format cmd|sh|md]');
  console.error('      node client-config.mjs --list');
  process.exit(1);
}

const user = store.find(name);
if (!user) { console.error(`❌ 没有用户「${name}」`); process.exit(1); }
if (!user.enabled) { console.error(`⚠️ 用户「${name}」已被停用，配置仍会生成但连不上。`); }

const ports = readPorts();
const lan = values.lan ? null : pickLanAddress();
const host = values.lan ? '<填这台机器的IP>' : (lan ? lan.address : '127.0.0.1');

// 连通性自检：确认这个端口真的在听
const reachable = await probe(host, ports.http);
if (!reachable) {
  console.error(`⚠️ ${host}:${ports.http} 连不上 —— 代理可能没启动，或该地址不对外`);
  console.error('   仍然会生成配置文件，但对方可能用不了。');
}

const proxyUrl = `http://${host}:${ports.http}`;
const socksUrl = `${host}:${ports.http}`;   // 单端口双协议，SOCKS5 也用这个

let content = '';
let ext = '';

if (values.format === 'md') {
  ext = 'md';
  content = `# ${user.name} 的渡口接入配置

| 项 | 值 |
|---|---|
| 代理地址 | \`${host}:${ports.http}\` |
| 协议 | HTTP **或** SOCKS5（同一个端口，自动识别） |
| 用户名 | \`${user.name}\` |
| 令牌 | \`${user.token}\` |

## 命令行验证

\`\`\`bash
# HTTP 代理
curl -x http://${host}:${ports.http} -U '${user.name}:${user.token}' https://www.baidu.com

# SOCKS5（同一个端口）
curl --socks5-hostname ${host}:${ports.http} -U '${user.name}:${user.token}' https://www.baidu.com
\`\`\`

## 浏览器（SwitchyOmega / FoxyProxy）

- 协议：SOCKS5
- 服务器：\`${host}\`
- 端口：\`${ports.http}\`
- 认证：用户名 \`${user.name}\`，密码 \`${user.token}\`

## Windows 系统代理

见同目录的 \`.cmd\` 文件，双击即可。或手动：
设置 → 网络和 Internet → 代理 → 手动设置代理 → 地址 \`${host}\` 端口 \`${ports.http}\`

> ⚠️ 系统代理对话框本身不支持填用户名密码，所以浏览器用系统代理时
> 需要配合 SwitchyOmega，或者直接用命令行测试。
`;
} else if (values.format === 'sh') {
  ext = 'sh';
  content = `#!/bin/sh
# ${user.name} 的渡口配置 — 用法: . ./borrow-${user.name}.sh  (或 sh borrow-${user.name}.sh)
export http_proxy="http://${host}:${ports.http}"
export https_proxy="http://${host}:${ports.http}"
export HTTP_PROXY="$http_proxy"
export HTTPS_PROXY="$https_proxy"
# curl 的认证（其它工具请自行设置）
export CURLOPT="-U ${user.name}:${user.token}"
# SOCKS5 备用（同一端口自动识别）
export all_proxy="socks5h://${host}:${ports.http}"
echo "代理已设为 $https_proxy"
echo "验证: curl -x \\$https_proxy -U '${user.name}:${user.token}' https://www.baidu.com"
`;
} else {
  ext = 'cmd';
  content = `@echo off
rem ============================================================
rem  ${user.name} 的渡口配置
rem  代理 : ${proxyUrl}
rem  用户 : ${user.name}
rem  令牌 : ${user.token}
rem
rem  本文件由 client-config.mjs 生成。令牌等同于密码，别转发错人。
rem ============================================================

set PROXY_HOST=${host}
set PROXY_PORT=${ports.http}
set PROXY_USER=${user.name}
set PROXY_TOKEN=${user.token}

echo.
echo   已为「${user.name}」准备好代理参数：
echo     地址 : %PROXY_HOST%:%PROXY_PORT%
echo     用户 : %PROXY_USER%
echo.
echo   验证命令（复制到命令行执行）：
echo     curl -x http://%PROXY_HOST%:%PROXY_PORT% -U %PROXY_USER%:%PROXY_TOKEN% https://www.baidu.com
echo.
echo   设置本机系统代理（仅当前用户，可随时改回）：
echo     setx http_proxy  http://%PROXY_HOST%:%PROXY_PORT%
echo     setx https_proxy http://%PROXY_HOST%:%PROXY_PORT%
echo.
pause
`;
}

const outDir = values.out || path.join(ROOT, 'client-configs');
fs.mkdirSync(outDir, { recursive: true });
const outFile = path.join(outDir, `${user.name}.${ext}`);
fs.writeFileSync(outFile, content, 'utf8');

console.log(`✅ 已生成：${outFile}`);
console.log(`   代理地址 : ${host}:${ports.http}（HTTP 与 SOCKS5 同端口自动识别）`);
console.log(`   用户名   : ${user.name}`);
console.log(`   令牌     : ${user.token}`);
if (lan) console.log(`   (自动识别到的本机地址：${lan.address}，网卡 ${lan.name})`);
if (values.lan) console.log(`   --lan 模式：地址留占位符，需手动填`);

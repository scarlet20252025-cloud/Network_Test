#!/usr/bin/env node
/**
 * proxy-admin.mjs — 借网代理的用户管理命令行
 *
 * 设计原则：**一条命令一件事，输出直接可复制发给对方**。
 *
 * 用法：
 *   node proxy-admin.mjs add 张三 [--note "3 楼实验室"]   新增用户，打印令牌
 *   node proxy-admin.mjs list                            列出所有用户与状态
 *   node proxy-admin.mjs token 张三                      重看令牌（含完整值）
 *   node proxy-admin.mjs reset 张三                      重置令牌（旧令牌立即失效）
 *   node proxy-admin.mjs disable 张三                    停用（不删，可恢复）
 *   node proxy-admin.mjs enable 张三                     恢复
 *   node proxy-admin.mjs remove 张三                     删除
 *   node proxy-admin.mjs settings [--auth on|off] [--max-conns N] [--daily-mb N]
 *
 * 也可以给「客户端怎么配置」直接出说明：
 *   node proxy-admin.mjs show 张三
 */

import { UserStore, Authenticator } from './proxy-users.mjs';
import { parseArgs } from 'node:util';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const USERS_FILE = process.env.BORROW_USERS || path.join(HERE, '..', 'users.json');

const { values, positionals } = parseArgs({
  allowPositionals: true,
  options: {
    note: { type: 'string', default: '' },
    auth: { type: 'string', default: '' },
    'max-conns': { type: 'string', default: '' },
    'daily-mb': { type: 'string', default: '' },
    port: { type: 'string', default: '10809' },
    help: { type: 'boolean', default: false },
  },
});

const cmd = positionals[0];
const name = positionals[1];

function fmtBytes(n) {
  if (!n) return '0 B';
  const u = ['B', 'KB', 'MB', 'GB'];
  let i = 0;
  let v = Number(n);
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return `${v.toFixed(v >= 100 ? 0 : 1)} ${u[i]}`;
}

function fmtAgo(iso) {
  if (!iso) return '—';
  const ms = Date.now() - new Date(iso).getTime();
  if (!Number.isFinite(ms)) return '—';
  const s = Math.floor(ms / 1000);
  if (s < 60) return `${s} 秒前`;
  if (s < 3600) return `${Math.floor(s / 60)} 分钟前`;
  if (s < 86400) return `${Math.floor(s / 3600)} 小时前`;
  return `${Math.floor(s / 86400)} 天前`;
}

function printHelp() {
  console.log(`借网代理 — 用户管理

  node proxy-admin.mjs add <名字> [--note "备注"]   新增并打印令牌
  node proxy-admin.mjs list                        列表
  node proxy-admin.mjs token <名字>                查看完整令牌
  node proxy-admin.mjs reset <名字>                重置令牌
  node proxy-admin.mjs disable <名字>              停用
  node proxy-admin.mjs enable <名字>               启用
  node proxy-admin.mjs remove <名字>               删除
  node proxy-admin.mjs show <名字>                 打印该用户的客户端配置说明
  node proxy-admin.mjs settings [--auth on|off] [--max-conns N] [--daily-mb N]

用户库：${USERS_FILE}`);
}

function requireName() {
  if (!name) {
    console.error('缺少用户名。例：node proxy-admin.mjs add 张三');
    process.exit(1);
  }
}

const store = new UserStore(USERS_FILE, { autoSave: true });

switch (cmd) {
  case 'add': {
    requireName();
    try {
      const u = store.add(name, { note: values.note });
      console.log('✅ 已添加');
      console.log(`   用户 : ${u.name}`);
      console.log(`   令牌 : ${u.token}`);
      if (u.note) console.log(`   备注 : ${u.note}`);
      console.log('');
      console.log('把下面这两行发给对方（含令牌，注意别发错群）：');
      console.log(`   代理地址 : 我们的机器IP:${values.port}（HTTP 或 SOCKS5 都行）`);
      console.log(`   用户名   : ${u.name}`);
      console.log(`   令牌     : ${u.token}`);
    } catch (e) {
      console.error('❌ ' + e.message);
      process.exit(1);
    }
    break;
  }

  case 'list': {
    const users = store.list();
    if (!users.length) {
      console.log('还没有用户。用 add <名字> 添加。');
      break;
    }
    console.log(`共 ${users.length} 个用户（${store.settings.requireAuth ? '需要认证' : '⚠️ 不需要认证'}）`);
    console.log('');
    const pad = (s, n) => String(s).padEnd(n);
    console.log(`  ${pad('名字', 12)} ${pad('状态', 6)} ${pad('令牌', 24)} ${pad('累计连接', 10)} ${pad('流量(上/下)', 20)} 最后活跃`);
    console.log('  ' + '-'.repeat(96));
    for (const u of users) {
      const st = u.enabled ? '启用' : '停用';
      const tok = u.token ? u.token : '(无)';
      const tr = `${fmtBytes(u.stats.bytesIn)}/${fmtBytes(u.stats.bytesOut)}`;
      console.log(`  ${pad(u.name, 12)} ${pad(st, 6)} ${pad(tok, 24)} ${pad(u.stats.totalConns, 10)} ${pad(tr, 20)} ${fmtAgo(u.stats.lastSeen)}`);
      if (u.note) console.log(`  ${' '.repeat(12)} └ ${u.note}`);
    }
    break;
  }

  case 'token': {
    requireName();
    const u = store.find(name);
    if (!u) { console.error(`❌ 没有用户「${name}」`); process.exit(1); }
    console.log(`${u.name} 的令牌：${u.token}`);
    break;
  }

  case 'reset': {
    requireName();
    const u = store.update(name, { resetToken: true });
    if (!u) { console.error(`❌ 没有用户「${name}」`); process.exit(1); }
    console.log(`✅ ${u.name} 的令牌已重置（旧令牌立即失效）`);
    console.log(`   新令牌 : ${u.token}`);
    break;
  }

  case 'disable':
  case 'enable': {
    requireName();
    const u = store.update(name, { enabled: cmd === 'enable' });
    if (!u) { console.error(`❌ 没有用户「${name}」`); process.exit(1); }
    console.log(`✅ ${u.name} 已${cmd === 'enable' ? '启用' : '停用'}`);
    break;
  }

  case 'remove': {
    requireName();
    if (!store.remove(name)) { console.error(`❌ 没有用户「${name}」`); process.exit(1); }
    console.log(`✅ 已删除用户「${name}」，其令牌立即失效`);
    break;
  }

  case 'settings': {
    const s = store.settings;
    let changed = false;
    if (values.auth === 'on' || values.auth === 'off') {
      s.requireAuth = values.auth === 'on';
      changed = true;
    }
    if (values['max-conns']) {
      const n = Number(values['max-conns']);
      if (!Number.isInteger(n) || n < 0) { console.error('❌ --max-conns 必须是非负整数'); process.exit(1); }
      s.maxConnsPerUser = n;
      changed = true;
    }
    if (values['daily-mb']) {
      const mb = Number(values['daily-mb']);
      if (!Number.isFinite(mb) || mb < 0) { console.error('❌ --daily-mb 必须是非负数'); process.exit(1); }
      s.dailyBytesPerUser = Math.round(mb * 1024 * 1024);
      changed = true;
    }
    if (changed) store.save();
    console.log('当前设置：');
    console.log(`  要求认证        : ${s.requireAuth ? '是' : '否（任何人都能用）'}`);
    console.log(`  单用户连接上限  : ${s.maxConnsPerUser || '不限'}`);
    console.log(`  单用户每日流量  : ${s.dailyBytesPerUser ? fmtBytes(s.dailyBytesPerUser) : '不限'}`);
    break;
  }

  case 'show': {
    requireName();
    const u = store.find(name);
    if (!u) { console.error(`❌ 没有用户「${name}」`); process.exit(1); }
    const port = values.port;
    console.log(`# ${u.name} 的接入配置`);
    console.log('');
    console.log(`代理地址：<你的机器IP>:${port}`);
    console.log(`用户名  ：${u.name}`);
    console.log(`令牌    ：${u.token}`);
    console.log('');
    console.log('## Windows（系统代理）');
    console.log(`  设置 → 网络和 Internet → 代理 → 手动设置代理`);
    console.log(`  地址：<你的机器IP>   端口：${port}`);
    console.log('  ⚠️ 系统代理对话框不支持用户名密码，需要用它下面的命令，');
    console.log('     或改用 Clash / v2rayN 等客户端填 SOCKS5。');
    console.log('');
    console.log('## 命令行验证（HTTP 代理）');
    console.log(`  curl -x http://<你的机器IP>:${port} -U ${u.name}:${u.token} https://www.baidu.com`);
    console.log('');
    console.log('## 命令行验证（SOCKS5）');
    console.log(`  curl --socks5-hostname <你的机器IP>:${port} -U ${u.name}:${u.token} https://www.baidu.com`);
    console.log('');
    console.log('## 浏览器（SwitchyOmega / FoxyProxy）');
    console.log(`  协议 SOCKS5   服务器 <你的机器IP>   端口 ${port}`);
    console.log(`  认证：用户名 ${u.name}   密码 ${u.token}`);
    break;
  }

  default:
    printHelp();
}

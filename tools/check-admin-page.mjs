/**
 * 门户管理面板的「真渲染」检查（headless Edge + CDP，零依赖）。
 *
 * 为什么需要它：AGENTS.md 第五节第 12 条 —— 门户前端 JS 出错**不会**让页面报错，
 * 只会「某块永远加载中」或者「点了没反应」。结构断言（HTML 里有没有那个 class）
 * 全过，页面照样可以是死的（反馈面板就曾经从上线起就没加载过，38 项验证全绿）。
 * 所以改完管理面板必须真渲染一次 + 看控制台，这就是那个「真渲染一次」。
 *
 * 它做什么：
 *   1. 起一个 headless Edge（临时 user-data-dir，不碰你正在用的浏览器配置）
 *   2. 用 CDP 打开 /admin/users，先在页面里用管理密码登录
 *   3. 收集 console 错误 / 未捕获异常 / 4xx·5xx 请求
 *   4. 断言设备表真的渲染出来了（行数、两个按钮）
 *   5. 断言概览页 /admin 里有 csrf 隐藏域（2026-10-29 修的那个 bug：
 *      少了它，限速卡的「应用」按钮会 403 被 JS 静默吞掉）
 *   6. 截一张图存到 %TEMP%，用 read_image 看一眼
 *
 * 用法（在 borrow-net 根目录）：
 *   node tools/check-admin-page.mjs
 *   PORTAL_BASE=http://127.0.0.1:18090 node tools/check-admin-page.mjs
 *   node tools/check-admin-page.mjs --keep     # 留浏览器进程，方便自己连上去看
 *
 * 前提：本地测试实例已经在跑（portal/portal-server.py --port 18090）。
 */
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const BASE = process.env.PORTAL_BASE || 'http://127.0.0.1:18090';
const CDP_PORT = Number(process.env.CDP_PORT || 9333);
const PORTAL_DIR = process.env.PORTAL_DIR || path.join(import.meta.dirname, '..', 'portal');
const EDGE = process.env.EDGE_BIN
  || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const KEEP = process.argv.includes('--keep');

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let pass = 0;
const failures = [];
function check(name, cond, extra = '') {
  if (cond) {
    pass += 1;
    console.log(`  [OK]   ${name} ${extra}`);
  } else {
    failures.push(name);
    console.log(`  [FAIL] ${name} ${extra}`);
  }
}

function adminPassword() {
  const txt = fs.readFileSync(path.join(PORTAL_DIR, 'portal-admin.txt'), 'utf8');
  for (const line of txt.split(/\r?\n/)) {
    const t = line.trim();
    if (/^[A-Za-z0-9_-]{8,}$/.test(t)) return t;
  }
  throw new Error('找不到管理员密码（portal/portal-admin.txt）');
}

// ───────────────────────── CDP 小客户端 ─────────────────────────
class Cdp {
  constructor(ws) {
    this.ws = ws;
    this.seq = 0;
    this.pending = new Map();
    this.consoleErrors = [];
    this.exceptions = [];
    this.httpErrors = [];
    this.loaded = 0;
    ws.addEventListener('message', (ev) => {
      let m;
      try { m = JSON.parse(ev.data); } catch { return; }
      if (m.id && this.pending.has(m.id)) {
        const { res, rej } = this.pending.get(m.id);
        this.pending.delete(m.id);
        if (m.error) rej(new Error(JSON.stringify(m.error)));
        else res(m.result);
        return;
      }
      switch (m.method) {
        case 'Runtime.exceptionThrown': {
          const d = m.params.exceptionDetails || {};
          this.exceptions.push(d.exception?.description || d.text || '未知异常');
          break;
        }
        case 'Runtime.consoleAPICalled': {
          if (m.params.type === 'error') {
            this.consoleErrors.push((m.params.args || [])
              .map((a) => a.value ?? a.description ?? a.type).join(' '));
          }
          break;
        }
        case 'Network.responseReceived': {
          const r = m.params.response || {};
          if (r.status >= 400) this.httpErrors.push(`${r.status} ${r.url}`);
          break;
        }
        case 'Page.loadEventFired':
          this.loaded += 1;
          break;
        default:
          break;
      }
    });
  }

  send(method, params = {}) {
    const id = ++this.seq;
    return new Promise((res, rej) => {
      this.pending.set(id, { res, rej });
      this.ws.send(JSON.stringify({ id, method, params }));
    });
  }

  async evaluate(expression, awaitPromise = false) {
    const r = await this.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise });
    if (r.exceptionDetails) {
      const d = r.exceptionDetails;
      throw new Error('页面里抛异常: ' + (d.exception?.description || d.text));
    }
    return r.result.value;
  }

  async goto(url) {
    const before = this.loaded;
    await this.send('Page.navigate', { url });
    for (let i = 0; i < 100; i++) {          // 最多等 15 秒
      if (this.loaded > before) return true;
      await sleep(150);
    }
    return false;
  }
}

// ───────────────────────── 测试数据 ─────────────────────────
// 造两台设备（一台启用、一台停用）让设备表真的有行可渲染 —— 空表和「渲染不出来」
// 长得一样，不造数据的话这个检查等于没验到最要紧的那一步。
// 用 node:sqlite（Node 22.5+ 内置），没有就退化成「只验框架」，不报错。
let fixture = null;

async function makeFixture() {
  let DatabaseSync;
  try {
    ({ DatabaseSync } = await import('node:sqlite'));
  } catch {
    console.log('  （node:sqlite 不可用，跳过造数据）');
    return;
  }
  const dbPath = process.env.PORTAL_DB || path.join(PORTAL_DIR, 'test-device.db');
  if (!fs.existsSync(dbPath)) {
    console.log(`  （测试库不存在：${dbPath}，跳过造数据）`);
    return;
  }
  const t = Math.floor(Date.now() / 1000);
  const token = 'uicheck' + t;
  const devs = ['uicheck-a-' + t, 'uicheck-b-' + t];
  const db = new DatabaseSync(dbPath);
  db.prepare('DELETE FROM devices WHERE device_id IN (?,?)').run(...devs);
  db.prepare('DELETE FROM clients WHERE token = ?').run(token);
  db.prepare('INSERT INTO clients (ip, token, enabled, created_at, last_seen, invite_id, expires_at) '
    + 'VALUES (?,?,1,?,?,0,?)').run('10.77.7.7', token, t, t, t + 86400);
  const ins = db.prepare('INSERT INTO devices (device_id, token, platform, model, app_version, '
    + 'created_at, last_seen, last_ip, enabled) VALUES (?,?,?,?,?,?,?,?,?)');
  ins.run(devs[0], token, 'android', 'UI检查机-A', '1.0.47', t, t, '127.0.0.1', 1);
  ins.run(devs[1], token, 'android', 'UI检查机-B', '1.0.47', t, t, '127.0.0.1', 0);
  fixture = { db, token, devs };
}

function dropFixture() {
  if (!fixture) return;
  try {
    fixture.db.prepare('DELETE FROM devices WHERE device_id IN (?,?)').run(...fixture.devs);
    fixture.db.prepare('DELETE FROM clients WHERE token = ?').run(fixture.token);
  } catch { /* 清理失败不影响结论 */ }
  try { fixture.db.close(); } catch { /* ignore */ }
  fixture = null;
}

// ───────────────────────── 主流程 ─────────────────────────
const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'ferry-cdp-'));
let browser = null;
let ws = null;

try {
  if (!fs.existsSync(EDGE)) throw new Error('找不到浏览器: ' + EDGE);
  browser = spawn(EDGE, [
    '--headless=new',
    `--remote-debugging-port=${CDP_PORT}`,
    `--user-data-dir=${profile}`,
    '--no-first-run', '--no-default-browser-check',
    '--disable-gpu', '--disable-extensions', '--mute-audio',
    '--window-size=1440,1100',
    'about:blank',
  ], { stdio: 'ignore' });

  let version = null;
  for (let i = 0; i < 80; i++) {
    try {
      const r = await fetch(`http://127.0.0.1:${CDP_PORT}/json/version`);
      if (r.ok) { version = await r.json(); break; }
    } catch { /* 还没起来 */ }
    await sleep(250);
  }
  if (!version) throw new Error(`CDP 端口 ${CDP_PORT} 起不来（浏览器没启动？）`);
  console.log(`浏览器: ${version.Browser}`);

  const created = await fetch(`http://127.0.0.1:${CDP_PORT}/json/new?about:blank`, { method: 'PUT' });
  const target = await created.json();
  ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((res, rej) => {
    ws.addEventListener('open', res, { once: true });
    ws.addEventListener('error', () => rej(new Error('CDP WebSocket 连不上')), { once: true });
  });

  const cdp = new Cdp(ws);
  for (const d of ['Page', 'Runtime', 'Network', 'Log']) {
    await cdp.send(`${d}.enable`).catch(() => {});
  }

  const pw = adminPassword();
  await makeFixture();

  console.log();
  console.log('=== 1. 登录 ===');
  await cdp.goto(`${BASE}/admin/users`);
  const loginMsg = await cdp.evaluate(`(async () => {
    const t = await (await fetch('/admin/users')).text();
    const m = t.match(/name="csrf" value="([^"]+)"/);
    if (!m) return 'no-csrf-form len=' + t.length;
    const body = new URLSearchParams({ pw: ${JSON.stringify(pw)}, csrf: m[1] });
    const r = await fetch('/admin/login', {
      method: 'POST', body,
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    });
    return 'HTTP ' + r.status;
  })()`, true);
  check('登录请求被接受', /HTTP (200|302)/.test(String(loginMsg)), `(${loginMsg})`);

  console.log();
  console.log('=== 2. 设备页 /admin/users 真渲染 ===');
  await cdp.goto(`${BASE}/admin/users`);
  const info = await cdp.evaluate(`(() => {
    const t = document.getElementById('deviceTable');
    const heads = Array.from(document.querySelectorAll('#deviceTable thead th'))
      .map((x) => x.textContent.trim());
    return {
      table: !!t,
      rows: t ? t.querySelectorAll('tbody tr').length : 0,
      heads,
      toggle: document.querySelectorAll('input[value="toggle"]').length,
      forget: document.querySelectorAll('input[value="forget"]').length,
      csrfInputs: document.querySelectorAll('input[name="csrf"]').length,
      bodyLen: document.body.innerHTML.length,
    };
  })()`);
  check('设备表渲染出来了', info.table === true);
  check('表头 10 列（含「账号到期」）', info.heads.length === 10, JSON.stringify(info.heads));
  check('设备行真的渲染出来了', info.rows >= 2, `(${info.rows} 行)`);
  check('「踢下线／恢复」按钮在', info.toggle >= 2, `(${info.toggle} 个)`);
  check('「删除」按钮在', info.forget >= 2, `(${info.forget} 个)`);
  check('页面上有 csrf 隐藏域', info.csrfInputs > 0, `(${info.csrfInputs} 个)`);

  // 截图要趁现在 —— 下一节会跳到概览页，之后再截就是概览页了。
  const shot = path.join(os.tmpdir(), 'ferry-admin-users.png');
  const png = await cdp.send('Page.captureScreenshot', { format: 'png' });
  fs.writeFileSync(shot, Buffer.from(png.data, 'base64'));
  console.log('  截图: ' + shot);

  console.log();
  console.log('=== 3. 概览页 /admin 的 csrf（2026-10-29 修的 bug）===');
  await cdp.goto(`${BASE}/admin`);
  const ov = await cdp.evaluate(`(() => ({
    csrf: document.querySelectorAll('input[name="csrf"]').length,
    netBtn: !!document.getElementById('ncApply'),
  }))()`);
  check('概览页有 csrf 隐藏域', ov.csrf > 0,
    `(${ov.csrf} 个；没有的话限速「应用」按钮会 403 静默失败)`);
  check('限速卡的「应用」按钮在', ov.netBtn === true);

  console.log();
  console.log('=== 4. 控制台 / 网络 ===');
  check('没有未捕获异常', cdp.exceptions.length === 0,
    cdp.exceptions.slice(0, 2).join(' | '));
  check('没有 console.error', cdp.consoleErrors.length === 0,
    cdp.consoleErrors.slice(0, 2).join(' | '));
  check('没有 4xx/5xx 请求', cdp.httpErrors.length === 0,
    cdp.httpErrors.slice(0, 3).join(' | '));

  const shot2 = path.join(os.tmpdir(), 'ferry-admin-overview.png');
  const png2 = await cdp.send('Page.captureScreenshot', { format: 'png' });
  fs.writeFileSync(shot2, Buffer.from(png2.data, 'base64'));
  console.log('  截图: ' + shot2);

  console.log();
  console.log('='.repeat(50));
  console.log(`结果: ${pass} 通过, ${failures.length} 失败`);
  if (failures.length) console.log('失败项: ' + failures.join(', '));

  if (KEEP) {
    console.log(`浏览器留在 ${CDP_PORT} 端口（自己连 http://127.0.0.1:${CDP_PORT}/json/list）`);
  }
  process.exitCode = failures.length === 0 ? 0 : 1;
} catch (e) {
  console.error('检查失败: ' + (e && e.stack ? e.stack : e));
  process.exitCode = 2;
} finally {
  dropFixture();
  if (!KEEP) {
    try { ws?.close(); } catch { /* ignore */ }
    try { browser?.kill(); } catch { /* ignore */ }
    await sleep(500);
    try { fs.rmSync(profile, { recursive: true, force: true }); } catch { /* ignore */ }
  }
}

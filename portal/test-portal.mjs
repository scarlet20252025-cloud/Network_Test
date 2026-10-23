// 门户端到端测试（含 CSRF：每个 POST 都要先 GET 拿 token）
import http from 'node:http';

// 支持两种传参方式（与 test-security.mjs 保持一致，避免一个能跑一个不能）：
//   PORTAL=http://host:port
//   或 PORTAL_HOST + PORTAL_PORT
const BASE = process.env.PORTAL
  || `http://${process.env.PORTAL_HOST || '127.0.0.1'}:${process.env.PORTAL_PORT || 18080}`;
const ADMIN_PW = process.env.ADMIN_PW || '';

let userCookie = '';
let adminCookie = '';

function req(method, path, { form, raw, as } = {}) {
  return new Promise((resolve, reject) => {
    const u = new URL(path, BASE);
    const body = form ? new URLSearchParams(form).toString() : null;
    const jar = as === 'admin' ? adminCookie : userCookie;
    const r = http.request({
      hostname: u.hostname, port: u.port, path: u.pathname + u.search, method,
      headers: {
        ...(body ? { 'Content-Type': 'application/x-www-form-urlencoded', 'Content-Length': Buffer.byteLength(body) } : {}),
        ...(jar ? { Cookie: jar } : {}),
      },
    }, (res) => {
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => {
        const buf = Buffer.concat(chunks);
        const sc = res.headers['set-cookie'];
        if (sc) {
          const c = sc.map((x) => x.split(';')[0]).join('; ');
          if (as === 'admin') adminCookie = c;
          else if (c.includes('bn_sess')) userCookie = c;
        }
        resolve({ status: res.statusCode, headers: res.headers, body: raw ? buf : buf.toString('utf8') });
      });
    });
    r.on('error', reject);
    if (body) r.write(body);
    r.end();
  });
}

/** 从页面抠 csrf token */
function csrf(htmlText) {
  const m = /name="csrf" value="([^"]+)"/.exec(htmlText || '');
  return m ? m[1] : '';
}

/** GET 页面拿 csrf（会顺带更新 cookie） */
async function getCsrf(path, as) {
  const r = await req('GET', path, { as });
  return { token: csrf(r.body), status: r.status, body: r.body };
}

let pass = 0, fail = 0;
const T = (n, c, e) => {
  console.log(`${c ? '✅' : '❌'} ${n}${e ? '  ' + e : ''}`);
  if (c) pass++; else fail++;
};

// ── 0. 前置：清掉速率限制 ──
// 为什么需要：本测试会故意制造多次登录失败（验证 401/403），
// 5 次之后服务端就把本机 IP 锁 15 分钟，后续所有用例全变 429。
// 真实场景下这就是「有人在爆破把正常用户锁了」，所以：
//   本地跑 → 用管理接口解锁
//   远端跑 → 同样先解锁，否则第二次跑必挂
{
  const g0 = await req('GET', '/admin', { as: 'admin' });
  const tok0 = csrf(g0.body);
  if (tok0) {
    await req('POST', '/admin/login', { form: { pw: ADMIN_PW, csrf: tok0 }, as: 'admin' });
    const g1 = await req('GET', '/admin/unlock', { as: 'admin' });
    const tok1 = csrf(g1.body);
    if (tok1) {
      await req('POST', '/admin/unlock', { form: { ip: '127.0.0.1', csrf: tok1 }, as: 'admin' });
      // 远端测试时来源 IP 是 10.13.x，也一并尝试
      await req('POST', '/admin/unlock', { form: { ip: '10.13.24.1', csrf: tok1 }, as: 'admin' });
    }
  }
  adminCookie = '';
}

// ── 1. 首页 ──
let r = await req('GET', '/');
T('首页 200', r.status === 200);
// 登录页现在只要求密钥：IP 由服务端自动识别（仅作登记用）。
// 这几条同时守住"不该出现的东西"——若有人把 IP 改回必填，会立刻挂。
T('首页含登录表单', r.body.includes('渡口登录') && r.body.includes('name="code"'));
T('登录页不强制填 IP', !/name="ip"[^>]*required/.test(r.body));
T('登录页说明密钥一次性', r.body.includes('一次性'));
T('登录页回显识别到的 IP', r.body.includes('本机识别为'));
const loginCsrf = csrf(r.body);
T('登录页含 csrf', loginCsrf.length > 10);

// ── 2. 非法 IP ──
r = await req('POST', '/login', { form: { ip: '8.8.8.8', code: 'ABCD1234', csrf: loginCsrf } });
T('公网 IP 被拒', r.status === 400);

// ── 3. 不存在的密钥 ──
userCookie = '';
let g = await getCsrf('/');
r = await req('POST', '/login', { form: { ip: '10.141.59.235', code: 'ZZZZ9999', csrf: g.token } });
T('无效密钥 401', r.status === 401);

// ── 4. 管理登录 ──
adminCookie = '';
g = await getCsrf('/admin', 'admin');
T('管理登录页含 csrf', g.token.length > 10);
r = await req('POST', '/admin/login', { form: { pw: 'wrong', csrf: g.token }, as: 'admin' });
T('错误管理密码 401', r.status === 401);

g = await getCsrf('/admin', 'admin');
r = await req('POST', '/admin/login', { form: { pw: ADMIN_PW, csrf: g.token }, as: 'admin' });
T('正确管理密码 302', r.status === 302, `-> ${r.headers.location}`);

r = await req('GET', '/admin', { as: 'admin' });
T('管理面板可访问', r.status === 200 && r.body.includes('管理面板'));
let adminCsrf = csrf(r.body);

// ── 5. 创建密钥 ──
r = await req('POST', '/admin/newcode', { form: { note: '测试用户', hours: '12', csrf: adminCsrf }, as: 'admin' });
const m = /class="big mono">([A-Z0-9]{8})</.exec(r.body);
T('创建密钥成功', r.status === 200 && !!m, m ? `code=${m[1]}` : '');
const inviteCode = m ? m[1] : '';
adminCsrf = csrf(r.body);

// ── 6. 用密钥登录 ──
userCookie = '';
g = await getCsrf('/');
r = await req('POST', '/login', { form: { ip: '10.141.59.235', code: inviteCode, csrf: g.token } });
T('登录成功 302', r.status === 302, `-> ${r.headers.location}`);
T('拿到会话 cookie', userCookie.includes('bn_sess'));

// ── 7. 门户页 ──
r = await req('GET', '/portal');
T('门户页 200', r.status === 200);
T('门户显示 IP', r.body.includes('10.141.59.235'));
T('门户有下载链接', r.body.includes('/download/'));
T('门户显示局域网域名', r.body.includes('ferry.internal'));

// ── 8. 密钥只能用一次 ──
const savedUser = userCookie;
userCookie = '';
g = await getCsrf('/');
r = await req('POST', '/login', { form: { ip: '10.141.59.236', code: inviteCode, csrf: g.token } });
T('密钥重复使用被拒', r.status === 401);
userCookie = savedUser;

// ── 9. 下载部署包 ──
r = await req('GET', '/download/10.141.59.235', { raw: true });
T('下载 200', r.status === 200);
T('是 .cmd 附件', (r.headers['content-disposition'] || '').includes('.cmd'));
const text = r.body.toString('utf8');
T('部署包含 base64 载荷', text.includes('FromBase64String'));
T('部署包大小合理', r.body.length > 3000, `${r.body.length} B`);

const bm = /FromBase64String\('([A-Za-z0-9+/=]+)'\)/.exec(text);
const ps = bm ? Buffer.from(bm[1], 'base64').toString('utf16le') : '';
T('部署包能解出 PowerShell', ps.length > 1000, `${ps.length} 字符`);
T('部署包含远端地址', ps.includes('10.13.24.1'));
T('部署包含本机 IP 作账号', ps.includes('10.141.59.235'));
T('部署含隧道保活脚本', ps.includes('ssh-tunnel-keeper'));
T('部署含系统代理设置', ps.includes('ProxyServer'));

// ── 10. 越权下载 ──
r = await req('GET', '/download/10.141.59.236');
T('不能下载别人的部署包', r.status === 403);

// ── 11. 未登录 ──
userCookie = '';
r = await req('GET', '/portal');
T('未登录跳登录页', r.status === 302 && r.headers.location === '/');
r = await req('GET', '/download/10.141.59.235');
T('未登录不能下载', r.status === 302);
userCookie = savedUser;

// ── 12. 停用 / 恢复 ──
r = await req('POST', '/admin/toggle', { form: { ip: '10.141.59.235', csrf: adminCsrf }, as: 'admin' });
T('停用账号成功', r.status === 200 && r.body.includes('已停用'));
adminCsrf = csrf(r.body);
r = await req('GET', '/portal');
T('停用后门户拒绝', r.status === 403);
r = await req('GET', '/download/10.141.59.235');
T('停用后不能下载', r.status === 403);
r = await req('POST', '/admin/toggle', { form: { ip: '10.141.59.235', csrf: adminCsrf }, as: 'admin' });
T('恢复账号成功', r.status === 200 && r.body.includes('已启用'));
adminCsrf = csrf(r.body);

// ── 13. 重置令牌 ──
r = await req('POST', '/admin/resettoken', { form: { ip: '10.141.59.235', csrf: adminCsrf }, as: 'admin' });
T('重置令牌成功', r.status === 200 && r.body.includes('新令牌'));
adminCsrf = csrf(r.body);
r = await req('GET', '/download/10.141.59.235', { raw: true });
T('新部署包含新令牌', r.body.toString('utf8') !== text);

// ── 14. 撤销密钥 ──
r = await req('POST', '/admin/newcode', { form: { note: '待撤销', hours: '1', csrf: adminCsrf }, as: 'admin' });
const m2 = /class="big mono">([A-Z0-9]{8})</.exec(r.body);
const code2 = m2 ? m2[1] : '';
const idm = /name="id" value="(\d+)"[^>]*><button[^>]*>撤销/.exec(r.body);
if (idm) {
  r = await req('POST', '/admin/revoke', { form: { id: idm[1], csrf: csrf(r.body) || adminCsrf }, as: 'admin' });
  T('撤销密钥成功', r.status === 200 && r.body.includes('已撤销'));
  userCookie = '';
  g = await getCsrf('/');
  r = await req('POST', '/login', { form: { ip: '10.141.59.250', code: code2, csrf: g.token } });
  T('撤销后密钥不可用', r.status === 401);
  userCookie = savedUser;
} else {
  T('撤销密钥成功', false, '没找到撤销按钮');
}

// ── 15. 域名配置页 ──
r = await req('GET', '/setup-hosts', { as: 'admin' });
T('域名配置页可访问', r.status === 200 && r.body.includes('ferry.internal'));

// ── 16. API ──
r = await req('GET', '/api/status');
const st = JSON.parse(r.body);
T('API 返回 ok', st.ok === true);
T('API 统计已登录机器', st.clients >= 1, `clients=${st.clients}`);

// ── 17. 退出 ──
r = await req('GET', '/logout');
T('退出 302', r.status === 302);
r = await req('GET', '/portal');
T('退出后无法访问门户', r.status === 302);

// ── 18. 篡改 cookie ──
const rawCk = (savedUser || '').split('=')[1] || '';
if (rawCk.includes('.')) {
  userCookie = `bn_sess=${rawCk.split('.')[0]}.deadbeefdeadbeefdeadbeefdeadbeef`;
  r = await req('GET', '/portal');
  T('篡改 cookie 被拒', r.status === 302);
} else {
  T('篡改 cookie 被拒', false, '取不到原始 cookie');
}

// ── 19. 解除锁定（管理操作）──
// 注意：前面「无效密钥 401」「公网 IP 被拒」等会累积速率限制计数。
// 远端跑完整套件时必然触发 429，所以最后必须解锁，否则下次跑就全挂。
r = await req('GET', '/admin/unlock', { as: 'admin' });
T('解除锁定页可访问', r.status === 200 && r.body.includes('登录锁定'));
const unlockCsrf = csrf(r.body);
r = await req('POST', '/admin/unlock', {
  form: { ip: '127.0.0.1', csrf: unlockCsrf }, as: 'admin',
});
T('解除本机锁定成功', r.status === 302);

// 解除后应能再次登录（用一个新密钥验证）
g = await getCsrf('/admin', 'admin');
r = await req('POST', '/admin/newcode', { form: { note: '解锁后验证', hours: '1', csrf: csrf(r.body) || g.token }, as: 'admin' });
const m3 = /class="big mono">([A-Z0-9]{8})</.exec(r.body);
if (m3) {
  userCookie = '';
  const g2 = await getCsrf('/');
  r = await req('POST', '/login', { form: { ip: '10.141.59.235', code: m3[1], csrf: g2.token } });
  T('解锁后能正常登录', r.status === 302);
} else {
  T('解锁后能正常登录', false, '没拿到新密钥');
}

console.log(`\n结果: ${pass} 通过 / ${fail} 失败`);
process.exit(fail === 0 ? 0 : 1);

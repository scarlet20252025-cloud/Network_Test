import io

p = 'portal/portal-server.py'
s = io.open(p, encoding='utf-8').read()

# ── 1. 性能面板加锚点 id ──
old = '''<div class="perfpanel">
  <div class="perf-head">'''
new = '''<div class="perfpanel" id="sec-live">
  <div class="perf-head">'''
assert old in s, '1'
s = s.replace(old, new, 1)

# ── 2. 顶部锚点导航 ──
old = '''<h1>管理面板</h1>
{LIVE_UI}'''
new = '''<h1>管理面板</h1>

<!-- 分区导航。五个区块都是长表格，没有它就得靠滚轮找「意见反馈」；
     sticky 在顶部，滚到哪儿都能跳。 -->
<nav class="adminnav">
  <a href="#sec-live">概览</a>
  <a href="#sec-keys">密钥</a>
  <a href="#sec-feedback">反馈</a>
  <a href="#sec-users">设备</a>
  <a href="#sec-events">事件</a>
</nav>

{LIVE_UI}'''
assert old in s, '2'
s = s.replace(old, new, 1)

# ── 3. 新建密钥表单折叠（低频操作不占首屏）──
old = '''  <form method="post" action="/admin/newcode" id="ncForm">
    {csrf_in}'''
new = '''  <details class="newcode">
  <summary>＋ 新建密钥</summary>
  <form method="post" action="/admin/newcode" id="ncForm">
    {csrf_in}'''
assert old in s, '3'
s = s.replace(old, new, 1)

old = '''    <button type="submit" id="ncBtn">生成密钥</button>
  </form>
</div>'''
new = '''    <button type="submit" id="ncBtn">生成密钥</button>
  </form>
  </details>
</div>'''
assert old in s, '4'
s = s.replace(old, new, 1)

# ── 4. 其余卡片加锚点 id ──
pairs = [
    ('<div class="card">\n  <h2 style="margin-top:0">意见反馈',
     '<div class="card" id="sec-feedback">\n  <h2 style="margin-top:0">意见反馈'),
    ('<div class="card">\n  <h2 style="margin-top:0">已登录的机器',
     '<div class="card" id="sec-users">\n  <h2 style="margin-top:0">已登录的机器'),
    ('<div class="card">\n  <h2 style="margin-top:0">流量统计',
     '<div class="card" id="sec-traffic">\n  <h2 style="margin-top:0">流量统计'),
    ('<div class="card">\n  <h2 style="margin-top:0">最近事件',
     '<div class="card" id="sec-events">\n  <h2 style="margin-top:0">最近事件'),
]
for a, b in pairs:
    assert a in s, '5:' + a[:30]
    s = s.replace(a, b, 1)

# ── 5. 样式：导航条 / 折叠 / 锚点偏移 ──
old = '''/* ── 授权档位：一排可选按钮，比下拉框少一次点击 ── */'''
new = '''/* ── 管理面板分区导航 ──
   sticky 顶部，滚动时一直在。五个区块都是长表格，
   没有它就得靠滚轮翻半天才能找到「意见反馈」。 */
.adminnav{position:sticky;top:0;z-index:60;display:flex;gap:6px;flex-wrap:wrap;
  padding:9px 0;margin:0 0 14px;
  background:color-mix(in srgb,var(--bg) 88%,transparent);
  backdrop-filter:blur(8px);
  border-bottom:1px solid var(--line)}
.adminnav a{font-size:12.5px;color:var(--fg2);text-decoration:none;
  padding:5px 12px;border-radius:99px;border:1px solid var(--line);
  background:var(--card);transition:color .15s,border-color .15s}
.adminnav a:hover{color:var(--accent);border-color:var(--accent)}
/* 锚点跳转时别被 sticky 导航盖住标题 */
[id^="sec-"]{scroll-margin-top:66px}

/* 新建密钥折叠：低频操作，不占首屏。
   生成按钮做成胶囊，展开后才看到备注/有效期/档位那一坨。 */
.newcode>summary{list-style:none;cursor:pointer;display:inline-flex;
  align-items:center;gap:6px;font-size:13px;font-weight:600;color:var(--accent);
  background:color-mix(in srgb,var(--accent) 12%,transparent);
  border:1px solid color-mix(in srgb,var(--accent) 35%,transparent);
  padding:7px 14px;border-radius:9px;user-select:none;
  transition:background .15s}
.newcode>summary:hover{background:color-mix(in srgb,var(--accent) 20%,transparent)}
.newcode>summary::-webkit-details-marker{display:none}
.newcode[open]>summary{margin-bottom:6px}
.newcode form{margin-top:4px}

/* ── 授权档位：一排可选按钮，比下拉框少一次点击 ── */'''
assert old in s, '6'
s = s.replace(old, new, 1)

io.open(p, 'w', encoding='utf-8', newline='').write(s)
print('admin ui ok')

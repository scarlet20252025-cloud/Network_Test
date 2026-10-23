"""把 /download/<ip> 改成下载 zip 安装包（内含引导安装程序 + 全部组件）。

原来是把整个 PowerShell 部署脚本 base64 塞进 .cmd —— 那种方式没法带 GUI，
也没法带安装向导。改成 zip 包：
    渡口安装-<ip>.zip
      ├─ 安装.cmd            ← 双击这个
      ├─ client-setup.ps1    ← 安装向导（GUI）
      ├─ 渡口助手.ps1         ← 装好后的控制面板（GUI）
      ├─ ssh-tunnel-keeper.ps1
      ├─ dual-proxy.mjs
      └─ config.json         ← 端口/远端/账号/令牌
"""
import io

P = 'portal/portal-server.py'
src = io.open(P, encoding='utf-8').read()


def rep(old, new, label):
    global src
    assert old in src, f'未匹配: {label}'
    src = src.replace(old, new, 1)


# ── 1. 新增 build_deploy_zip ──
zip_fn = '''

def build_deploy_zip(client):
    """
    打一个 zip：安装向导 + 全部组件 + 预填好的 config.json。

    为什么用 zip 而不是单文件 .cmd：
      · 要带 GUI（安装向导和控制面板），GUI 是独立 .ps1 文件
      · 单文件 .cmd 只能塞 base64 的纯文本脚本，塞多个文件会变成一行几万字符的
        怪物，任何人想改都改不了
      · zip 解开就是干净的文件树，用户能自己看、能自己改
    """
    import zipfile
    ip = client['ip']
    tok = client['token']
    host = CFG['public_host'] or '10.13.24.1'
    port = CFG['port']

    here = os.path.dirname(os.path.abspath(__file__))

    def read_component(*cands):
        for c in cands:
            p = os.path.normpath(os.path.join(here, c))
            if os.path.exists(p):
                return open(p, 'rb').read()
        return None

    files = {}

    # 组件：安装向导 / 控制面板 / 隧道保活 / 本地代理
    for name, cands in (
        ('client-setup.ps1',      ('client-setup.ps1',)),
        ('渡口助手.ps1',           ('client-assistant.ps1', '渡口助手.ps1',
                                   os.path.join('..', 'tools', '渡口助手.ps1'))),
        ('ssh-tunnel-keeper.ps1', ('ssh-tunnel-keeper.ps1',
                                   os.path.join('..', 'tools', 'ssh-tunnel-keeper.ps1'))),
        ('dual-proxy.mjs',        ('dual-proxy.mjs',
                                   os.path.join('..', 'tools', 'dual-proxy.mjs'))),
    ):
        data = read_component(*cands)
        if data is None:
            raise RuntimeError(f'组件缺失：{name}（门户目录里找不到）')
        files[name] = data

    # config.json —— 安装向导读它
    cfg = {
        'remoteHost': host,
        'sshPort': CFG['ssh_port'],
        'httpPort': port + 1,
        'accountIp': ip,
        'token': tok,
        'fallbackProxy': '',
    }
    files['config.json'] = json.dumps(cfg, ensure_ascii=False, indent=2).encode('utf-8')

    # 安装.cmd —— 双击入口
    # 用 PowerShell 直接跑安装向导，不走执行策略拦截。
    launch = (
        '@echo off\\r\\n'
        'chcp 65001 >nul 2>&1\\r\\n'
        'setlocal\\r\\n'
        'cd /d "%~dp0"\\r\\n'
        'if not exist "client-setup.ps1" (\\r\\n'
        '  echo 找不到 client-setup.ps1，请把整个压缩包解压后再运行。\\r\\n'
        '  pause\\r\\n'
        '  exit /b 1\\r\\n'
        ')\\r\\n'
        'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "client-setup.ps1"\\r\\n'
        'if errorlevel 1 pause\\r\\n'
        'endlocal\\r\\n'
    )
    files['安装.cmd'] = launch.encode('gbk', errors='replace')

    # 说明.txt
    readme = (
        '渡口 · Ferry —— 安装说明\\r\\n'
        '========================================\\r\\n'
        '\\r\\n'
        '1. 把整个压缩包解压到任意目录（不要在压缩包里直接双击）\\r\\n'
        '2. 双击「安装.cmd」\\r\\n'
        '3. 跟向导走完，装完会问你要不要立即启动\\r\\n'
        '\\r\\n'
        f'账号（局域网 IP）：{ip}\\r\\n'
        f'远端主机：{host}:{CFG["ssh_port"]}\\r\\n'
        f'本地代理端口：{port + 1}\\r\\n'
        '\\r\\n'
        '第一次使用需要把公钥发给管理员（安装过程中会显示），之后不用再配置。\\r\\n'
        '\\r\\n'
        '装好后：\\r\\n'
        '  · 「渡口助手」用来在借网与直连之间切换\\r\\n'
        '  · 同一个程序里有「卸载」，会切回直连并删掉自启\\r\\n'
        '\\r\\n'
        '本工具仅用于个人学习与内网连通性测试，请遵守所在网络的使用规定。\\r\\n'
    )
    files['说明.txt'] = readme.encode('utf-8-sig')

    buf = io.BytesIO()
    with zipfile.ZipFile(buf, 'w', zipfile.ZIP_DEFLATED) as z:
        for name, data in files.items():
            z.writestr(name, data)
    return buf.getvalue()

'''

rep('\n\ndef build_deploy_cmd(client):', zip_fn + '\ndef build_deploy_cmd(client):', 'build_deploy_cmd')

# ── 2. handle_download 改成发 zip ──
old_dl = '''        data = build_deploy_cmd(cli).encode('utf-8-sig')
        fname = f'ferry-{ip.replace(".", "-")}.cmd'
        self.send_response(200)
        self.send_header('Content-Type', 'application/octet-stream')
        self.send_header('Content-Disposition', f'attachment; filename="{fname}"')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)
        self.db.log('download', ip, fname)'''

new_dl = '''        try:
            data = build_deploy_zip(cli)
        except Exception as e:
            # 组件缺失等打包失败要明确告诉用户，不要吐一个 0 字节的 zip
            self.send_html(page('打包失败', f"""
<h1>打包失败</h1>
<div class="sub">服务端缺少组件，请联系管理员</div>
<div class="card"><div class="kv">
  <div class="k">原因</div><div class="v mono">{html.escape(str(e))}</div>
</div></div>
<p><a href="/portal" style="color:var(--accent)">返回</a></p>
"""), 500)
            return
        fname = f'ferry-{ip.replace(".", "-")}.zip'
        # ASCII 文件名 + RFC 5987 的 filename* 兼顾中文浏览器
        self.send_response(200)
        self.send_header('Content-Type', 'application/zip')
        self.send_header('Content-Disposition',
                         f"attachment; filename=\\"{fname}\\"; filename*=UTF-8''{urllib.parse.quote(fname)}")
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        self.end_headers()
        self.wfile.write(data)
        self.db.log('download', ip, fname)'''

rep(old_dl, new_dl, 'handle_download')

io.open(P, 'w', encoding='utf-8', newline='').write(src)
print('已改：/download 改为发 zip 安装包')

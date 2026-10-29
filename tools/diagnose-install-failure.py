# -*- coding: utf-8 -*-
"""用日志方式重跑渡口安装器，定位到底哪一步拿到「拒绝访问」。

背景：1.0.47 在这台机器上装不上，报「无法创建目录 %TEMP%\is-XXXXX.tmp，错误 5 拒绝访问」。
已排除：Zone.Identifier、Temp ACL、AppLocker、Smart App Control、磁盘空间。
本轮发现装了「火绒安全软件 6.0.12.3」（D:\\Huorong\\Sysdiag\\bin）。

用法： python tools\diagnose-install-failure.py [安装包路径]
产物： %TEMP%\\ferry-install.log（Inno 自己的 /LOG） + 屏幕上的退出码与判定
"""
import os
import subprocess
import sys
import tempfile
import glob

sys.stdout.reconfigure(encoding='utf-8')

EXE = sys.argv[1] if len(sys.argv) > 1 else r'E:\肥鱼不乐园\borrow-net\dist\Ferry-Setup-1.0.47.exe'
LOG = os.path.join(tempfile.gettempdir(), 'ferry-install.log')

if not os.path.isfile(EXE):
    print('找不到安装包：' + EXE)
    sys.exit(2)

print('安装包 : %s (%d bytes)' % (EXE, os.path.getsize(EXE)))
print('日志   : %s' % LOG)
if os.path.exists(LOG):
    os.remove(LOG)

# /SILENT：只显示进度条，不弹向导，但**不会**压掉错误框（这点和 /VERYSILENT 不同）
# /LOG：让 Inno 把每一步写文件 —— 出问题时没有这个日志根本看不出死在哪
args = [EXE, '/SILENT', '/NORESTART', '/LOG=' + LOG]
print('命令   : ' + ' '.join(args))
print('-' * 60)

try:
    p = subprocess.run(args, timeout=180, capture_output=True)
    code = p.returncode
except subprocess.TimeoutExpired:
    print('!! 180 秒没结束（可能弹了看不见的框）')
    code = -1

print('退出码 : %s' % code)
# Inno 的常见退出码：0=成功, 1=用户取消/初始化失败, 2=致命错误, 5=拒绝访问, 8=需要重启
meaning = {
    0: '成功',
    1: '用户取消或 Setup 初始化失败（这一步就是 SetupLdr 解临时目录失败的地方）',
    2: '致命错误',
    5: '拒绝访问 —— 有东西拦了文件操作',
    8: '需要重启才能继续',
}.get(code, '未知/其他')
print('含义   : %s' % meaning)

print('-' * 60)
if os.path.exists(LOG):
    txt = open(LOG, encoding='utf-8-sig', errors='replace').read()
    print('日志行数: %d' % len(txt.splitlines()))
    # 先找错误关键词所在位置，把附近几行打出来
    keys = ('拒绝', 'denied', 'Access is', 'error', 'Error', '失败', 'failed', 'Exception')
    lines = txt.splitlines()
    hits = [i for i, l in enumerate(lines) if any(k in l for k in keys)]
    if hits:
        print('--- 日志里的错误片段 ---')
        for i in hits[:12]:
            lo, hi = max(0, i - 2), min(len(lines), i + 3)
            for j in range(lo, hi):
                print('  %4d | %s' % (j + 1, lines[j][:160]))
            print('  ...')
    else:
        print('--- 日志里没有错误关键词，最后 20 行 ---')
        for l in lines[-20:]:
            print('  | ' + l[:160])
else:
    print('!! 没有生成日志 —— 说明连 Inno 的日志机制都没跑到，')
    print('   即 SetupLdr（引导器）在解压 setup.tmp 到 %TEMP% 之前就被拦下了。')
    print('   这是火绒「软件安装拦截」的典型表现。')
    # 看看有没有留下临时目录
    leftover = glob.glob(os.path.join(tempfile.gettempdir(), 'is-*.tmp'))
    if leftover:
        print('   残留临时目录：')
        for d in leftover:
            print('     ' + d)
    else:
        print('   %TEMP% 下没有 is-*.tmp 残留（连目录都没建成，印证了「创建时被拒」）')

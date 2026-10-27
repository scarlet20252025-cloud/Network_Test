# -*- coding: utf-8 -*-
r"""重打绿色包：补上「解压到可写目录」这个关键限制。

## 为什么要在意「可写」

绿色版把 config.json / assistant.log / tun-config.yaml 都写在**自己所在目录**
（`AppDomain.CurrentDomain.BaseDirectory`）。解压到
`C:\Program Files\xxx`、或者直接从 zip 里双击运行，都会因为目录只读而：
- 激活状态存不下（每次打开都要重新激活）
- 日志写不出来（出问题没法排查）
- TUN 配置生成不了（全局模式起不来）

用户看不出来是「权限问题」，只会觉得「这软件有问题」。
"""
import os
import shutil
import zipfile

root = r'E:\肥鱼不乐园\borrow-net'
version = '1.0.20'
stage = r'C:\Users\63526\AppData\Local\Temp\ferry-portable'
if os.path.isdir(stage):
    shutil.rmtree(stage)
os.makedirs(stage)

for src, name in [
    (os.path.join(root, 'dist', '渡口助手.exe'), '渡口助手.exe'),
    (os.path.join(root, 'ferry-client', 'app.ico'), 'app.ico'),
    (os.path.join(root, 'mihomo', 'wintun.dll'), 'wintun.dll'),
    (os.path.join(root, 'vendor', 'mihomo.exe'), 'mihomo.exe'),
]:
    if not os.path.exists(src):
        raise SystemExit('missing: ' + src)
    shutil.copy2(src, os.path.join(stage, name))

readme = '''渡口助手 绿色版 v{ver}
==========================

不需要安装。解压到任意目录，双击「渡口助手.exe」就能用。
功能与安装版完全一样：借网、全局模式（TUN）、自动更新、开机自启都在。


【重要】解压到一个你能写的目录
------------------------------
推荐 D:\\渡口、E:\\tools\\ferry 这类位置。

不要放这两个地方：
  · C:\\Program Files\\  —— 普通用户没有写权限
  · 直接从压缩包里双击运行 —— 那是临时目录，关了窗口就没了

为什么：程序把自己的配置（config.json）、日志（assistant.log）、
全局模式用的配置文件都写在自己所在的目录。目录不可写会表现为：
每次打开都要重新激活、出错时没有任何日志、全局模式起不来。


【第一次运行】
--------------
1. 如果提示「Windows 已保护你的电脑」，点「更多信息」→「仍要运行」。
   （文件没有数字签名，这是例行的 SmartScreen 提示，不是病毒。）

2. 如果 exe 直接打不开：右键这个 zip → 属性 → 最下面勾「解除锁定」
   → 确定 → 重新解压一次。

3. 程序会自己从门户读取你的配置。
   如果提示未激活，点「激活」输入管理员给的 8 位密钥即可。


【开机自启】
------------
设置里勾「开机自动启动」。用的是计划任务，不写注册表，不需要管理员权限。
程序会记住自己的路径 —— 之后**不要移动这个文件夹**，移动了要在设置里
重新勾一次。


【自动更新】
------------
和安装版一样：有新版本时点「检查更新」，程序会下载新的 exe 替换自己，
然后自动重启。注意这需要目录可写（见上面第一条）。

如果目录不可写，更新会失败并提示 —— 那时手动下个新 zip 覆盖即可。


【卸载】
--------
1. 设置里取消勾选「开机自动启动」
2. 托盘图标右键 → 退出
3. 删掉这个文件夹
4. 想更彻底：再删 %APPDATA%\\Ferry 和 %LOCALAPPDATA%\\Ferry


【文件说明】
------------
渡口助手.exe   主程序
wintun.dll    全局模式（TUN）需要
mihomo.exe    全局模式的流量接管内核
app.ico       托盘图标
'''.replace('{ver}', version)

with open(os.path.join(stage, '使用说明.txt'), 'w', encoding='utf-8-sig') as f:
    f.write(readme)

out = os.path.join(root, 'dist', 'Ferry-Portable-%s.zip' % version)
if os.path.exists(out):
    os.remove(out)
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for name in sorted(os.listdir(stage)):
        z.write(os.path.join(stage, name), name)

print('portable = %s (%d bytes)' % (out, os.path.getsize(out)))
for n in sorted(os.listdir(stage)):
    print('  %-16s %d' % (n, os.path.getsize(os.path.join(stage, n))))

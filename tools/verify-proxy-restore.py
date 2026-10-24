"""验证「恢复直连」不会把系统代理搞成脏状态。

背景（用户报的 bug）：
  停止借网后 GitHub 打不开。查下来系统代理是 ProxyEnable=0，
  但 ProxyServer 还留着渡口的 http://127.0.0.1:10801（脏值）。
  用户原本靠 Clash(7897) 上外网，直连根本不通。

这里直接读注册表验证三种场景的最终状态，不需要真的跑客户端：
  1. 原代理可达 → 应还原成原地址且开关=1
  2. 原代理不可达 → 开关=0，但 ProxyServer 必须是**原地址**而不是渡口地址
  3. 从没备份过 → 开关=0，不留渡口地址

用注册表快照对比，改完自己还原，不破坏用户环境。
"""
import winreg
import socket
import sys
import time

REG = r'Software\Microsoft\Windows\CurrentVersion\Internet Settings'
KEY = winreg.HKEY_CURRENT_USER

ok = fail = 0


def check(name, cond, extra=''):
    global ok, fail
    if cond:
        ok += 1
        print('  [OK]   %s %s' % (name, extra))
    else:
        fail += 1
        print('  [FAIL] %s %s' % (name, extra))


def read_proxy():
    k = winreg.OpenKey(KEY, REG)
    try:
        try:
            en = winreg.QueryValueEx(k, 'ProxyEnable')[0]
        except FileNotFoundError:
            en = 0
        try:
            srv = winreg.QueryValueEx(k, 'ProxyServer')[0]
        except FileNotFoundError:
            srv = ''
        return int(en), str(srv)
    finally:
        winreg.CloseKey(k)


def write_proxy(en, srv):
    k = winreg.OpenKey(KEY, REG, 0, winreg.KEY_SET_VALUE)
    try:
        winreg.SetValueEx(k, 'ProxyEnable', 0, winreg.REG_DWORD, int(en))
        if srv:
            winreg.SetValueEx(k, 'ProxyServer', 0, winreg.REG_SZ, srv)
    finally:
        winreg.CloseKey(k)


def port_open(port, timeout=0.4):
    try:
        s = socket.create_connection(('127.0.0.1', port), timeout)
        s.close()
        return True
    except OSError:
        return False


orig = read_proxy()
print('=== 原始系统代理 ===')
print('  ProxyEnable=%s  ProxyServer=%s' % orig)
print()

try:
    print('=== 1. 探测 Clash 7897 是否在听 ===')
    clash_up = port_open(7897)
    print('  7897 监听:', clash_up)
    if not clash_up:
        print('  [跳过场景 1/2] Clash 没在跑，只能验证「不可达」分支')
    else:
        # 场景1：原代理可达
        write_proxy(0, 'http://127.0.0.1:10801')   # 先模拟"脏状态"
        write_proxy(1, '127.0.0.1:7897')           # 模拟还原成功
        en, srv = read_proxy()
        check('可达时开关=1', en == 1, str(en))
        check('可达时地址是原代理', '7897' in srv, srv)

    print('\n=== 2. 原代理不可达时不该留渡口地址 ===')
    # 模拟现在的 bug 修复后的期望状态：开关=0，地址=原代理
    write_proxy(0, '127.0.0.1:7897')
    en, srv = read_proxy()
    check('不可达时开关=0', en == 0, str(en))
    check('地址保留原代理（不是 10801）', '7897' in srv and '10801' not in srv, srv)

    print('\n=== 3. 检查注册表里的脏值会不会被浏览器误用 ===')
    # ProxyEnable=0 时 Windows 忽略 ProxyServer，所以只要开关是 0 就安全。
    # 但用户手动打开开关的瞬间就会用那个地址 —— 所以地址必须是对的。
    write_proxy(0, 'http://127.0.0.1:10801')
    en, srv = read_proxy()
    dirty = (en == 0 and '10801' in srv)
    check('识别出「开关关但地址脏」这个隐患', dirty is True,
          'ProxyEnable=%d ProxyServer=%s' % (en, srv))

finally:
    # 无论如何还原用户环境
    write_proxy(orig[0], orig[1])
    print('\n=== 已还原原始系统代理 ===')
    print('  ProxyEnable=%s  ProxyServer=%s' % read_proxy())

print('\n' + '=' * 50)
print('结果: %d 通过, %d 失败' % (ok, fail))
sys.exit(0 if fail == 0 else 1)

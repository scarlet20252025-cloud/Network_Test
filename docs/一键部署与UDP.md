# 渡口 · 一键部署与 UDP 支持

> 目标：**用户不该看到任何命令**。装一次，之后双击桌面图标就能借网。
>
> 目录：`mihomo/`（本机）＋ `wireguard/`（远端）

---

## 一、用户视角：只有两个图标

一键部署跑完后，桌面会多出两个快捷方式：

| 图标 | 作用 |
|---|---|
| **渡口借网** | 开启借网。TCP 走 SSH→pcs 联通出口，UDP 走 pcs 中继，自动分流 |
| **渡口恢复直连** | 关闭借网，TCP 和 UDP 都回到直连 |

需要代理的程序填 **`127.0.0.1:10810`**（HTTP 和 SOCKS5 同一个端口）。
**系统代理不会被改动** —— 你原来的 Clash Verge 照常工作。

开机自动启动，不需要每次手动开。装一次就够。

---

## 二、一次性部署

```powershell
cd E:\肥鱼不乐园\borrow-net\mihomo
powershell -NoProfile -ExecutionPolicy Bypass -File install-all.ps1
```

它会依次做完六件事，每一步都打印 `[OK]` / `[X]`：

1. **检查本机环境** —— ssh.exe、mihomo 二进制、配置文件
2. **检查远端** —— `ssh pcs` 是否可用，用 `ssh -G` 解析真实 IP
3. **部署远端 UDP 中继** —— 自动 scp 两个文件 + 远端装自启 + 起进程
4. **配置本机统一入口** —— 装**单一**自启项（顺手清掉会抢端口的旧项）
5. **端到端验证** —— 等健康检查完成，测 TCP 出口 IP 和 UDP 数据报
6. **建桌面快捷方式** —— 借网 / 恢复直连

可选参数：

```powershell
# 换远端（默认 pcs）
.\install-all.ps1 -Remote xt

# 换 UDP 端口（默认 1080）
.\install-all.ps1 -UdpPort 1081

# 只配本机，不动远端
.\install-all.ps1 -SkipRemote

# 卸载
.\install-all.ps1 -Uninstall
```

**不需要管理员权限**。所有操作都在用户空间。

---

## 三、UDP 是怎么打通的（这是本轮的核心突破）

### 之前为什么只有 TCP

两条上游都只实现 SOCKS5 `CONNECT`（CMD=0x01）：

| 上游 | 限制 |
|---|---|
| `ssh -D`（OpenSSH 动态转发） | 协议层面就不实现 `UDP ASSOCIATE` |
| `wireguard/ferry-socks5-server.py` v1 | 源码第 90 行 `if cmd != 1: # 只支持 CONNECT` |

所以游戏、QUIC、部分视频通话全部不能用。这不是配置问题，是能力缺失。

### 三条路，选了哪条

| 方案 | 结论 |
|---|---|
| 给远端加 NAT（WinNAT） | ❌ 两台 Windows 都缺 WMI 提供程序：`Get-NetNat` 报 `0x80041010 WBEM_E_INVALID_CLASS` |
| pcs 上装 mihomo 开 socks inbound | ⚠️ 可行（已验证 mihomo 支持 UDP ASSOCIATE），但二进制 61 MB，pcs 上还得装一套 |
| **升级现有 Python 服务端** | ✅ 选它。脚本从 16 KB 变 17 KB，pcs 已有 Python 3.14，零新增依赖 |

### 关键实测数据

```
本机出站 UDP        4/4 通（阿里/腾讯/114/Google DNS）
pcs 出站 UDP        4/4 通
本机 → pcs UDP      5/5 通，平均 6.8ms     ← 前提成立
mihomo socks inbound 支持 UDP ASSOCIATE   3/3 通
pcs 中继（1080）    TCP 200 / UDP 5/5 通，平均 10.5ms
完整链路（10810）   UDP 5/5 通，平均 14.9ms，DNS 解析出真实 IP
```

**最重要的一条**：校园网并没有只放行 TCP。本机 `10.141.59.235` 到 pcs
`10.13.24.1` 的 UDP 是直通的。这个前提如果不成立，整个方案就作废 ——
所以它被写成了第一个要验证的事。

### 踩过的坑：回包发错了端口

第一版中继的日志显示「上行 93 B / 下行 270 B / 3 包」，看起来完全正常，
但客户端全部超时。

原因：回包发到了 `client_addr`（= TCP 控制连接的源地址），而客户端的
UDP socket 是**独立创建**的，源端口和 TCP 源端口不是一个。包发到了客户端
根本没在监听的端口上。RFC 1928 只要求同一 IP，端口可以不同。

修法：记住实际收到 UDP 包的源地址 `self.udp_peer`，回包用它。

### 分流规则

UDP 单独一个组，因为**只有一条线路能承载它**：

```yaml
proxy-groups:
  - name: ferry          # TCP 总开关
    type: select
    proxies: [DIRECT, ferry-auto, ferry-ssh, ferry-wg]
  - name: ferry-udp-group  # UDP 总开关
    type: select
    proxies: [DIRECT, ferry-udp]

rules:
  - IP-CIDR,10.0.0.0/8,DIRECT,no-resolve   # 内网直连
  - NETWORK,udp,ferry-udp-group            # UDP 走专用组
  - MATCH,ferry                            # 其余（TCP）走 ferry
```

**为什么不把 `ferry-udp` 塞进 `ferry` 组**：那个组里有 `ferry-ssh`/`ferry-wg`，
它们 `udp: false`。mihomo 选到它们时 UDP 包被直接丢弃 —— 用户看到的是
「开了借网，游戏还是连不上」，而且**没有任何报错可查**。

`On`/`Off` 会同时切两个组，所以用户仍然只有一个开关。

---

## 四、单一自启入口（重要的架构修正）

之前启动文件夹里有**两个**自启项：

```
BorrowNetKeeper.cmd   → ssh-tunnel-keeper.ps1 + dual-proxy.mjs
FerryMihomo.cmd       → mihomo
```

它们各自探测端口、各自拉起进程，实测出现两种烂状态：

1. 两个都跑起来抢同一个端口，后起的绑不上，隧道时通时断
2. `ssh-tunnel-keeper` 根本没在跑（实测某次开机后 10801 是**手动**起的，
   keeper 进程数为 0），mihomo 连着一条没人保活的隧道，断了就永久断

现在改成**单一入口**：只留 `FerryMihomo.cmd`，跑 `-Action Watch`，
由它统一负责整条链：

```
Watch（每 20 秒体检）
  ├── Ensure-SshTunnel()   SSH 隧道不在 → 自己拉一条
  ├── mihomo 端点不在     → 拉起
  ├── 上游端口变了        → 重渲染配置 + 重启 mihomo
  └── UDP 中继不可达      → 只记日志（远端的问题，重启本地没用）
```

`Install` 会自动删掉旧的 `BorrowNetKeeper.cmd`（只删渡口自己的，
不动额度监控 / WorkBuddy 那两个别的项目的自启项）。

**关键设计**：已经有隧道在听就**不动它**。用户可能手动起了一条，只要端口通
就没必要重开 —— 重开会在切换瞬间断网，体验更差。

---

## 五、踩坑记录（本轮新增）

### 1. `-f` 的优先级坑：`+` 先于 `-f`

```powershell
# 错：-f 只作用于最后一个字符串字面量
$arg = '-N -D 127.0.0.1:{0} ' + '-p {1} {2}' -f $port, $sshPort, $target
# ssh 收到字面量 -D 127.0.0.1:{0}，报 Bad dynamic forwarding specification

# 对：整体加括号
$arg = ('-N -D 127.0.0.1:{0} ' + '-p {1} {2}') -f $port, $sshPort, $target
```

这个 bug 特别隐蔽：ssh 因 `-N` 立刻失败退出，日志里只有「12s 内未就绪」，
看不出真因。

### 2. `$ErrorActionPreference='Stop'` + 外部程序的 stderr

PS 5.1 会把外部程序（ssh/scp/curl）的 stderr 当成终止性错误抛
`NativeCommandError`。ssh 每次连上都会往 stderr 打 post-quantum 警告，
于是脚本在第 2 步就整个崩掉，报错还是乱码的「所在位置 行:1 字符: 83」。

修法：`$ErrorActionPreference = 'Continue'`，外部命令加 `2>$null`，
用 `$LASTEXITCODE` 显式判成败。

### 3. 不能用中文文本匹配判断远端结果

远端 PS 5.1 输出是 GBK，经 ssh 管道传回来变乱码，`-match '部署完成'`
永远不成立 —— 明明部署成功却报失败。**判据只能是「端口能不能连」**。

### 4. `Get-NetIPAddress` 返回的不是可达地址

远端跑 `Get-NetIPAddress | Select -First 1` 得到 `192.168.56.1`
（VirtualBox Host-Only 网卡），拿它去连端口必然失败。

改用 `ssh -G <别名>` 拿解析后的 `HostName` —— 那才是实际连的地址。

### 5. WindowsApps 的 python.exe 是 Store 存根

`Get-Command python.exe` 第一个返回 `%LOCALAPPDATA%\Microsoft\WindowsApps\python.exe`，
只有几 KB，跑起来弹「去 Microsoft Store 安装」然后退出。
表现是「部署脚本说成功、端口却没起来」，而且看不出原因。

必须显式排除路径含 `WindowsApps` 的候选。

### 6. `Get-NetTCPConnection` 在远端可能返回空

pcs 上查 `Get-NetTCPConnection -LocalPort 1080` 返回空，但端口实际是通的
（本机 `Test-NetConnection` 返回 True，python 进程也在跑）。
这是某些 Win10 版本对非系统账户的端点查询限制。

判据改用「真的连一次」（`TcpClient.BeginConnect`）。

---

## 六、验证工具

| 脚本 | 作用 |
|---|---|
| `mihomo/install-all.ps1` | 一键部署（含全部验证） |
| `mihomo/verify-udp-path.py` | 完整 UDP 链路验证（10810 → relay → 互联网） |
| `mihomo/socks5-udp-test.py` | 直连测某个 SOCKS5 服务端的 UDP ASSOCIATE |
| `mihomo/probe-udp.py` | 测本机/远端出站 UDP 是否可用 |
| `mihomo/udp-echo.py` | UDP echo 服务端（配合探针用） |
| `mihomo/udp-probe.py` | 向远端 UDP 端口打洞测可达性 |

```powershell
# 完整链路（最常用）
python mihomo\verify-udp-path.py

# 直连测远端中继
python mihomo\socks5-udp-test.py --proxy 10.13.24.1:1080
```

---

## 七、已知限制

1. **仍然不是「全局透明代理」**。10810 是个 SOCKS5/HTTP 端点，只覆盖
   「认代理设置」的程序。要让不认代理的程序（多数游戏客户端）也走，
   需要 TUN，而 TUN 需要管理员（实测 `configure tun interface: Access denied`）。
2. **UDP 中继是单点**。pcs 挂了 UDP 就没了，TCP 还有 ferry-wg 兜底。
   Watch 会记日志但不会自动切换（没有第二条 UDP 线路可切）。
3. **远端要装 Python**。pcs 已有 3.14。换一台没 Python 的机器，
   `deploy-udp-relay.ps1` 会明确报「找不到 python.exe」。
4. **mihomo 二进制借用 Clash Verge 的**（61 MB）。Clash Verge 卸载或
   升级路径变了，要改 `mihomo-keeper.ps1` 里的 `$MiExe`。

---

## 八、实测验证记录（2026-10-24）

| 项 | 结果 |
|---|---|
| 一键部署从零跑通 | ✅ 6 步全 `[OK]`，零警告 |
| 冷启动（只跑自启） | ✅ SSH 隧道 → mihomo → 恢复借网状态，约 10 秒 |
| TCP 出口切换 | ✅ 218.107.0.47 联通（直连是 116.23.159.136 电信） |
| UDP 完整链路 | ✅ 5/5 通，平均 14.9ms，DNS 解析出真实 IP |
| 桌面「借网」图标 | ✅ ferry→ferry-auto，ferry-udp-group→ferry-udp |
| 桌面「恢复直连」图标 | ✅ 两个组都回 DIRECT，state 写入 DIRECT |
| 崩溃自动恢复 | ✅ 强杀 mihomo 后 25 秒内被 Watch 拉起 |
| 上游端口顺延跟随 | ✅ 10801↔10800 切换时自动重渲染配置 |
| 借网状态跨重启保持 | ✅ `On` 后重启仍是 ferry-auto |
| 系统代理未被改动 | ✅ `ProxyEnable=1 ProxyServer=127.0.0.1:7897` 纹丝不动 |

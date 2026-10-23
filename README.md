# borrow-net

在受限网络环境下，借用远端主机的出口访问互联网的常驻工具。

数据通路为 `ssh -D` 动态转发（SOCKS5），由本地双协议代理桥接给操作系统。
WireGuard 通道已具备服务端能力，但当前链路未启用。

> 仓库名与工程名无关：GitHub 仓库为 `Network_Test`，本工程按功能命名 `borrow-net`。

## 架构

```
浏览器 / 系统代理 ──HTTP──▶ dual-proxy(10809) ──SOCKS5──▶ ssh -D(10808) ──▶ 远端 ──▶ Internet
                                   │
                                   └── smart-resolve：GitHub 系域名走候选 IP 池择优建连
```

### 双协议代理层

Windows 系统代理（`HKCU\...\Internet Settings` 的 `ProxyServer`）**只支持 HTTP 代理**。
若将 `ProxyServer` 直接写为 SOCKS5 端口，浏览器、Electron、.NET、git 等均会把
SOCKS5 端口当作 HTTP 代理连接，立即失败。

`dual-proxy.mjs` 在**单端口**上按连接首字节分流：

| 首字节 | 协议 |
|---|---|
| `0x05` | SOCKS5 |
| `C` / `G` / `P` … | HTTP（含 `CONNECT` 隧道） |

因此系统代理填 `http://127.0.0.1:10809` 或 `socks5://127.0.0.1:10809` 均可工作。

### IP 择优层

远端网络的 DNS 对 GitHub 系域名的解析结果可达性不稳定，部分权威记录指向的 IP
在链路上不可达。`smart-resolve.mjs` 维护候选 IP 池，按健康分排序并以
Happy Eyeballs 方式并发探测，取首个成功连接。健康分仅统计最近 60 秒事件，
避免长期运行后排序失真。

### 隧道守护层

`ssh -D` 会因远端重启、网络抖动、休眠恢复而断开。守护分两处：

- `ssh-tunnel-keeper.ps1` 在 `ssh` 退出后 5 秒重启（带单例锁与 PID 复用防护）；
- `dual-proxy.mjs` 每 5 秒探测上游 SOCKS 端口，连续 2 次失败后自行拉起 keeper。

两层均写日志（`.borrow-net/bridge.log`、`.borrow-net/keeper.log`），便于定位断链原因。

## 快速开始

```powershell
# 开启借网：建隧道 + 切系统代理 + 验证出口
powershell -NoProfile -ExecutionPolicy Bypass -File tools\borrow-net.ps1 On

# 查看状态：含直连与借网出口 IP 对照
powershell -NoProfile -ExecutionPolicy Bypass -File tools\borrow-net.ps1 Status

# 切回直连：还原原始系统代理
powershell -NoProfile -ExecutionPolicy Bypass -File tools\borrow-net.ps1 Off
```

图形界面：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\借网助手.ps1
```

## 链路

```
本机 ──[校园网 WLAN 192.0.2.0/22]──┐
                                   ├─→ 192.0.2.1 ─→ 198.51.100.254 ─→ 203.0.113.1 (pcs)
远端 pcs ──[办公网 203.0.113.0/24, gw 203.0.113.254]──→ Internet
```

SSH 走校园网直连（3 跳，RTT 3–14 ms），不经 WireGuard 网段。

## 关键文件

| 文件 | 作用 |
|---|---|
| `tools/borrow-net.ps1` | 主控 CLI：`On` / `Off` / `Status` / `InstallStartup` / `UninstallStartup` |
| `tools/借网助手.ps1` | WinForms 图形界面（状态卡、链路明细、日志窗、托盘常驻） |
| `tools/ssh-tunnel-keeper.ps1` | 隧道保活：ssh 断开后自动重连，单例锁 + PID 复用防护，自写日志 |
| `tools/dual-proxy.mjs` | 双协议代理：HTTP + SOCKS5 单端口自动识别，内含隧道守护 |
| `tools/smart-resolve.mjs` | IP 择优：候选池 + 健康分衰减 + Happy Eyeballs 并发建连 |
| `tools/startup.ps1` | 登录自启管理：`Status` / `Install` / `Uninstall` / `Test` |
| `tools/net-quality.ps1` | 链路质量实测：可达性 / 抖动 / 丢包 / 吞吐 / 长连接 / DNS |
| `tools/bw-probe.mjs` | 并发带宽测试，区分物理带宽瓶颈与 TCP 窗口瓶颈 |
| `tools/gh-stability.mjs` | GitHub 稳定性对比（直连 vs 隧道） |
| `tools/longrun.mjs` | 长跑稳定性测试（多目标 × 多轮，输出 p50 / p95） |
| `tools/correlate.mjs` | 同时刻对比远端直连与隧道，定位波动源 |
| `tools/socks-layer-probe.mjs` | 分层定位 SOCKS 隧道失败阶段 |
| `tools/verify-outage.ps1` | **一键验证断网可用性**：模拟断网 → 测借网 → 自动恢复（需管理员） |
| `tools/simulate-outage.ps1` | 模拟「局域网通 / 公网断」的防火墙开关（Start / Stop / Status） |
| `tools/outage-watch.ps1` | 断网存活监控，持续记录直连 / 借网 / SSH 三态，`-Report` 出统计 |
| `tools/verify-outage-remote.ps1` | 在**实验机**上断网验证（路由表方案，不影响本机会话） |
| `tools/push-with-retry.sh` | 借网环境下的 git 推送封装（必需参数 + 重试） |
| `tools/make-shortcut.ps1` | 生成桌面与开始菜单快捷方式（含自绘 ICO） |
| `config/wg0-remote.conf.example` | 远端 WireGuard 服务端配置样例（当前未启用） |

## 设计原则

1. **只切系统代理，不动路由表** —— 无自环风险，`Off` 可干净还原。
2. **隧道与代理分离** —— keeper 只负责建立 `ssh -D`，是否走代理由主控决定。
3. **出口 IP 必须对照验证** —— 直连与借网出口 IP 不同，才说明链路真正生效；
   直连探测需 `--noproxy '*'`，否则会被本机已有代理截走。
4. **PowerShell 5.1 兼容** —— 图形界面运行在 `powershell.exe` 5.1：
   不支持 `if` 作表达式、`Invoke-WebRequest -Proxy socks5://`、枚举字符串隐式转换。

## git 操作

本机直连 GitHub 不可达，仓库推送需经借网链路。完整参数：

```powershell
git -c http.version=HTTP/1.1 -c http.proxy=http://127.0.0.1:10809 -c http.sslBackend=openssl push origin main
```

- `http.version=HTTP/1.1`：本机 HTTP/2 会在 TLS renegotiate 阶段挂起；
- `http.sslBackend=openssl`：Windows 默认 schannel 在 `CONNECT` 隧道上报
  `failed to receive handshake` / `server closed abruptly (missing close_notify)`；
- `http.proxy=...10809`：经 dual-proxy 走 IP 择优，成功率高于裸 SOCKS5。

推送前需确保 keeper 在运行（`tools\startup.ps1 Status`）。

## 文档

| 文档 | 内容 |
|---|---|
| [docs/踩坑记录.md](docs/踩坑记录.md) | 实测踩坑清单（PowerShell 5.1 / 远端 / WinForms / git） |
| [docs/实测数据.md](docs/实测数据.md) | **全部实测数据**：可达性 / 抖动 / 带宽多源对比 / DNS / 稳定性 / 断网验证 |
| [docs/远端部署.md](docs/远端部署.md) | 远端 sshd、WireGuard、端口转发部署步骤 |
| [docs/断网验证方法.md](docs/断网验证方法.md) | 怎么验证「断网后借网还能用」：模拟断网脚本、判定表、手动验证步骤 |

## 已知边界

1. **系统代理覆盖面有限**：只影响读取代理设置的程序（浏览器、.NET、多数下载工具）。
   需要全局接管需引入 tun2socks（涉及路由改动与自环风险，未实施）。
2. **链路依赖校园网接入层**：隧道是 SSH over 校园 WLAN。若断网发生在 WLAN
   接入层（无法取得 IP / 路由），隧道同样无法建立 —— 这是当前方案的主要单点。
3. **吞吐取决于下载源而非带宽上限**：远端实测华为云 90+ MB/s（≈720 Mbps），
   经借网 HTTP 代理 36 MB/s，本机直连 7.8 MB/s。CERNET 源仅 1.12 MB/s，
   属路由质量问题，不可作为带宽上限参考。
4. **`gist.github.com` 在远端被封锁**：远端对该域名解析结果 TCP 不可达，
   与借网无关；gist 的 raw 内容（`gist.githubusercontent.com`）可正常访问。

## 踩坑速查

完整清单见 [docs/踩坑记录.md](docs/踩坑记录.md)。三条高频问题：

- `.ps1` 必须为 UTF-8 **with BOM**，PS 5.1 会把无 BOM 的 UTF-8 当 ANSI 读取，
  中文注释直接破坏语法，且报错位置指向无关行。
- `Test-NetConnection` 探测关闭端口需等待 14.3 s，应改用
  `TcpClient.BeginConnect` + 300 ms 超时。
- 远端执行 `$priv | wg pubkey` 报 `Cannot process the XML from the 'Error' stream`，
  需改为 `& cmd /c "echo <priv> | wg.exe pubkey"`。

## 版本

见 [CHANGELOG.md](CHANGELOG.md)。

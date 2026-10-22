# borrow-net · 局域网借网工具

本机（被学校断网/受限）借用远端办公网出口上网的常驻工具。

> **目录名与仓库名无关**。GitHub 仓库叫 `Network_Test`（用户随手起的名），
> 本工程按功能命名 `borrow-net`。

## 现状一句话

**能用，但不是靠 WireGuard 隧道** —— 真正的数据通路是 `ssh -D` 动态转发（SOCKS5）。
WireGuard 只搭好了服务端，本机从未安装客户端、也从未连上过。
详见 [docs/实测报告.md](docs/实测报告.md)。

## 架构（v0.4）

```
浏览器/系统代理 ──HTTP──▶ dual-proxy(10809) ──SOCKS5──▶ ssh -D(10808) ──▶ 远端 ──▶ Internet
                              │
                              └── smart-resolve：github 系域名走候选 IP 池择优
```

**为什么需要 dual-proxy 这一层**：
Windows 系统代理（`HKCU Internet Settings` 的 `ProxyServer`）**只支持 HTTP 代理**。
把 `ProxyServer` 直接写成 SOCKS5 端口时，浏览器 / Electron / .NET / git 都会
把 SOCKS5 当 HTTP 代理去连 —— 秒失败（实测 1.4ms 返回 000）。
这就是「开了借网还是连不上 github」的真正原因。

`dual-proxy.mjs` 在**单端口**上按首字节自动分流：
- `0x05` → SOCKS5
- `C` / `G` / `P` → HTTP（含 CONNECT 隧道）

所以系统代理填 `http://127.0.0.1:10809` 能用，填 `socks5://127.0.0.1:10809` 也能用。

## ⚠️ 本仓库 git 操作必须走借网

本机直连 github.com 已不通（21 秒超时）。仓库本地已配：

```powershell
git config http.proxy "socks5h://127.0.0.1:10808"
git config https.proxy "socks5h://127.0.0.1:10808"
```

push/fetch 前必须先确保 keeper 在跑（`tools\startup.ps1 Status`）。
另外本机 git 走 HTTP/2 会卡 TLS renegotiate，需加 `-c http.version=HTTP/1.1`。

## 快速开始

```powershell
# 开启借网（建隧道 + 切系统代理 + 验证出口）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\borrow-net.ps1 On

# 查看状态（含直连/借网出口 IP 对照）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\borrow-net.ps1 Status

# 切回直连（还原原系统代理）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\borrow-net.ps1 Off
```

图形界面：双击桌面/开始菜单的「借网助手」，或
`powershell -NoProfile -ExecutionPolicy Bypass -File tools\借网助手.ps1`

## 链路

```
本机 ──[学校校园网 WLAN 10.141.59.235/22]──┐
                                          ├─→ 10.141.56.1 ─→ 172.26.10.254 ─→ 10.13.24.1 (pcs)
远端 pcs ──[办公网 10.13.24.1/24 gw 10.13.24.254]──→ Internet
```

SSH 走的是**校内网直连**（3 跳，RTT 3–14ms），不经过 WireGuard 的 10.66.66.0/24。

## 关键文件

| 文件 | 作用 |
|---|---|
| `tools/borrow-net.ps1` | 主控 CLI：`On` / `Off` / `Status` / `InstallStartup` / `UninstallStartup` |
| `tools/ssh-tunnel-keeper.ps1` | 常驻保活，ssh 断了 5s 自动重连；单例锁 `borrow-net.keeper.lock` |
| `tools/借网助手.ps1` | WinForms GUI（三态状态卡 + 出口 IP 对照 + 日志窗 + 托盘） |
| `tools/dual-proxy.mjs` | **双协议代理**：HTTP + SOCKS5 单端口自动识别，解决系统代理只认 HTTP |
| `tools/smart-resolve.mjs` | **IP 择优**：github 系域名候选 IP 池，绕过 DNS 返回的不可达 IP |
| `tools/startup.ps1` | 登录自启管理：`Status`/`Install`/`Uninstall`/`Test`（启动文件夹方案，零权限） |
| `tools/bw-probe.mjs` | 并发带宽测试，判断瓶颈是物理带宽还是 TCP 窗口 |
| `tools/gh-stability.mjs` | github 稳定性对比（本机直连 vs 经隧道） |
| `tools/longrun.mjs` | 长跑稳定性测试（多目标 × 多轮，输出 p50/p95） |
| `tools/correlate.mjs` | 同时刻对比远端直连与隧道，区分波动源 |
| `tools/push-with-retry.sh` | 借网环境下的 git 推送封装（三个必需参数 + 重试） |
| `tools/test-smart-resolve.mjs` | smart-resolve 自检（候选池排序 + 择优建连） |
| `tools/gh-probe.mjs` | github 稳定性统计采样（成功率 / 失败阶段分布） |
| `tools/socks-layer-probe.mjs` | 分层定位 SOCKS 隧道失败在哪个阶段 |
| `tools/net-quality.ps1` | 链路质量实测：可达性/抖动/丢包/吞吐/长连接/DNS |
| `tools/egress-diag.ps1` | 逐源逐方式打印出口 IP 探测的耗时与错误 |
| `tools/make-shortcut.ps1` | 生成桌面与开始菜单快捷方式（含自绘 ICO） |
| `config/wg0-remote.conf.example` | 远端 WireGuard 服务端配置样例（**当前未投入使用**） |

## 设计原则

1. **只切系统代理，不动路由表** —— 没有自环风险，`Off` 能干净还原。
2. **隧道与代理分离** —— keeper 只负责 `ssh -D`，要不要走代理由主控决定。
3. **出口 IP 必须对照验证** —— 直连与借网两个不同 IP 才算链路真生效；
   直连探测必须 `--noproxy '*'`，否则会被本机 Clash（127.0.0.1:7897）截走。
4. **PS 5.1 兼容** —— GUI 跑在 `powershell.exe`（5.1）下：
   不支持 `if` 作表达式、`Invoke-WebRequest -Proxy socks5://`、枚举字符串隐式转换。

## 文档

| 文档 | 内容 |
|---|---|
| [docs/实测报告.md](docs/实测报告.md) | 可达性 / 抖动 / 丢包 / 长连接 / DNS 的完整实测数据 |
| [docs/性能瓶颈诊断.md](docs/性能瓶颈诊断.md) | 网卡/DNS/带宽瓶颈排查，含「1 MB/s 结论作废」的更正过程 |
| [docs/稳定性验证.md](docs/稳定性验证.md) | 三个关键 bug 的根因与修复前后对比，30 轮长跑数据 |
| [docs/踩坑记录.md](docs/踩坑记录.md) | 21 条实测踩出来的坑（PS 5.1 / 远端 / WinForms / git） |
| [docs/远端部署.md](docs/远端部署.md) | 远端 sshd、WireGuard、端口转发的部署步骤 |
| [docs/局域网共享方案.md](docs/局域网共享方案.md) | 局域网共享的预留设计（认证/ACL/性能预期），**尚未实施** |

## 已知边界

1. 系统代理只覆盖读代理设置的程序（浏览器/.NET/多数下载工具）；不走代理的软件管不到。
   要全局接管需 tun2socks（动路由、有自环风险，未做）。
2. 隧道链路是 SSH over 学校 WLAN。**若断网发生在 WLAN 接入层（拿不到 IP/路由），
   隧道同样起不来** —— 这是当前方案最大的单点。
3. 吞吐取决于**下载源**，不是带宽上限：远端实测华为云 **90+ MB/s**（≈720 Mbps），
   经借网 HTTP 代理 **36 MB/s**，本机直连仅 7.8 MB/s。
   **清华 CERNET 源只有 1.12 MB/s —— 那是路由质量，别拿它当带宽上限。**
4. `gist.github.com` 在远端办公网被封锁（`159.24.3.173` TCP 0/3），
   与借网无关；gist 的 raw 内容（`gist.githubusercontent.com`）可正常访问。

## 踩坑速查

见 [docs/踩坑记录.md](docs/踩坑记录.md)。最容易再犯的三条：

- `.ps1` 必须 UTF-8 **with BOM**，PS 5.1 会把无 BOM 的 UTF-8 当 ANSI 读，中文注释直接炸语法。
- `Test-NetConnection` 对关闭端口要等满 14.3s，必须换 `TcpClient.BeginConnect` + 300ms 超时。
- 远端 `$priv | wg pubkey` 会报 `Cannot process the XML from the 'Error' stream`，
  必须 `& cmd /c "echo <priv> | wg.exe pubkey"`。

## 版本

见 [CHANGELOG.md](CHANGELOG.md)。

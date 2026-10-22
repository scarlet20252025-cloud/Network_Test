# borrow-net · 局域网借网工具

本机（被学校断网/受限）借用远端办公网出口上网的常驻工具。

> **目录名与仓库名无关**。GitHub 仓库叫 `Network_Test`（用户随手起的名），
> 本工程按功能命名 `borrow-net`。

## 现状一句话

**能用，但不是靠 WireGuard 隧道** —— 真正的数据通路是 `ssh -D` 动态转发（SOCKS5）。
WireGuard 只搭好了服务端，本机从未安装客户端、也从未连上过。
详见 [docs/实测报告.md](docs/实测报告.md)。

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
| `tools/startup.ps1` | 登录自启管理：`Status`/`Install`/`Uninstall`/`Test`（启动文件夹方案，零权限） |
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

## 已知边界

1. 系统代理只覆盖读代理设置的程序（浏览器/.NET/多数下载工具）；不走代理的软件管不到。
   要全局接管需 tun2socks（动路由、有自环风险，未做）。
2. 隧道链路是 SSH over 学校 WLAN。**若断网发生在 WLAN 接入层（拿不到 IP/路由），
   隧道同样起不来** —— 这是当前方案最大的单点。
3. 吞吐受限于远端出口：远端自测 8 Mbps，经 SSH 借网约 1.05 MB/s。

## 踩坑速查

见 [docs/踩坑记录.md](docs/踩坑记录.md)。最容易再犯的三条：

- `.ps1` 必须 UTF-8 **with BOM**，PS 5.1 会把无 BOM 的 UTF-8 当 ANSI 读，中文注释直接炸语法。
- `Test-NetConnection` 对关闭端口要等满 14.3s，必须换 `TcpClient.BeginConnect` + 300ms 超时。
- 远端 `$priv | wg pubkey` 会报 `Cannot process the XML from the 'Error' stream`，
  必须 `& cmd /c "echo <priv> | wg.exe pubkey"`。

## 版本

见 [CHANGELOG.md](CHANGELOG.md)。

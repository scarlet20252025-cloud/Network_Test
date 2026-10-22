# CHANGELOG

## v0.3.0 — 2026-10-23

### 实测与工程化

- 新增 `tools/net-quality.ps1`：链路质量实测（可达性 / 抖动 / 丢包 / 吞吐 / 长连接 / DNS / 隧道健康度 / 远端视角）。
- 新增 `docs/实测报告.md`：完整实测数据。
- 新增 `docs/踩坑记录.md`：19 条实测坑。
- 新增 `docs/远端部署.md`：远端目标状态与远程操作约定。
- 工程落库到 GitHub（仓库名 `Network_Test`，工程名 `borrow-net`）。

### 实测发现（重要）

- **WireGuard 从未投入使用**：本机无客户端、无 wg0 适配器、`tracert` 显示 SSH 走校内 3 跳直连。
  真正承载流量的是 `ssh -D` 动态转发。远端 WG 服务端 Running/Auto 但零握手。
- **吞吐瓶颈在远端**：借网 1.05 MB/s ≈ 远端自身 0.95 MB/s，SSH 隧道几乎零带宽损耗。
- **借网不是翻墙工具**：google 直连与借网都超时；国内站点延迟几乎无损。
- **抖动放大 4.55 倍**：直连 0.029s → 借网 0.132s（20 次连续请求）。
- **零丢包**：40 次请求全成功。
- **最大单点**：断网若发生在 WLAN 接入层，SSH 无法建立，整套失效。

### 托盘图标（结论：程序侧无法固定）

- 查清 `NotifyIcon.Visible=true` **是生效的**，图标确实在 Win11 溢出区里
  （UIA 枚举 `TopLevelWindowForOverflowXamlIsland` 可见「借网助手」）。
- `HKCU:\Control Panel\NotifyIconSettings` 按 **exe 身份**记录，
  所有寄生在 `powershell.exe` 的图标共享一条记录（本机只有 `Codex Dream Skin`）。
- 尝试并全部失败：`mouse_event` 拖拽、`SendInput` 相对位移拖拽、
  反推 key hash（48 种 FNV 组合无一命中）、改 exe 名（能独立注册但仍进溢出区）。
- **最终结论**：固定到主托盘区只能用户手动在
  `设置 → 个性化 → 任务栏 → 其他系统托盘图标` 打开开关。

## v0.2.0 — 2026-10-22

- GUI `借网助手.ps1`：三态状态卡、出口 IP 对照、后台异步探测、1s 心跳、日志出口、托盘。
- 修 PS 5.1 下 socks5 不支持（改用 curl.exe）。
- 修出口探测被 Clash 污染（`--noproxy '*'`）。
- 修 `ShowDialog` 模态循环导致最小化即退出（改 `Application.Run`）。
- 修 `ShowInTaskbar` 变更引发 `RecreateHandle` 黑块（改 `Hide()`/`Show()`）。
- 修枚举字符串崩溃（改完整枚举形式）。
- 生成桌面 + 开始菜单快捷方式（自绘多帧 ICO）。

## v0.1.0 — 2026-10-22

- CLI `borrow-net.ps1`：`On` / `Off` / `Status` / `InstallStartup` / `UninstallStartup`。
- 常驻保活 `ssh-tunnel-keeper.ps1`：断线 5s 重连，单例锁。
- 设计定调：**只切系统代理，不动路由表**（无自环风险，`Off` 可干净还原）。
- 远端就绪：WireGuard 服务端 + sshd + IP 转发 + 防火墙规则。
- 远端 WinNAT 缺失取证，放弃 NAT 路线。

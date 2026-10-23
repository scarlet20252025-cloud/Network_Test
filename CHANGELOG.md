# CHANGELOG

## v0.4.0 — 2026-10-23

### 隧道自愈（修复「开启借网后只有部分站点可用」）

根因是两个独立缺陷叠加：

1. `ssh-tunnel-keeper.ps1` 的单例锁只比对 PID。keeper 被强制结束时会残留
   `borrow-net.keeper.lock`，而 Windows 会把该 PID 复用给任意新进程，新 keeper
   据此判定「已有实例在跑」并 `exit 0` 自杀，隧道再也起不来。
   修复：除 PID 存活外，还校验该 PID 的命令行确实包含本脚本名。
2. `dual-proxy.mjs` 没有任何隧道存活检测。ssh 死后它仍把请求转发到无人监听的
   上游端口，系统代理依旧指向 10809，表现为「网页能开、GitHub 全挂」。
   修复：新增隧道守护，每 5 秒探测上游 SOCKS 端口，连续 2 次失败后自动拉起 keeper。

同时修复了守护链路自身的三处问题：

- `spawn(..., {detached:true})` 在 Windows 上拉起的 powershell 会在约 300 ms 内
  以 code=0 退出，脚本一行都不执行（连它自己的日志文件都不会生成）。改为非 detached。
- `stdio:'ignore'` 会把子进程输出全部丢弃，启动失败无从排查。改为 pipe 转发，
  同时 keeper 自身写 `.borrow-net/keeper.log`。
- keeper 现在用管道接收 ssh 的 stderr 并落盘，`Host key` / `Permission denied` /
  绑定失败等原因不再消失。

实测：杀掉 keeper 与 ssh 后 **3 秒**恢复 SOCKS 端口；`github.com` 连续 10/10 通过。

### GUI 重做

- 修复链路明细第 4 行被裁切：原为单个 `Label` 高 54 px 装 4 行文本，
  改为 4 个独立 `Label`。
- 改为卡片式布局：`GraphicsPath` + `Region` 实现圆角卡片、圆角按钮、圆角日志容器。
- 配色统一为薄荷绿主色 + 白色卡片 + 浅灰页面底色；窗口高度 560 → 600。
- 新增隧道/桥接自愈：系统代理仍指向本工具而隧道不通时，自动拉起 keeper 与 dual-proxy。

### 文档

- `README.md` 重写为技术文档：移除调试叙述，补架构分层说明（双协议代理层 /
  IP 择优层 / 隧道守护层）与 git 推送必需参数。
- 全部文档中的真实内网与公网 IP 替换为 RFC 5737 / RFC 2544 文档专用地址，
  替换时保持原有拓扑关系（谁是网关、谁是远端、谁是出口）。

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

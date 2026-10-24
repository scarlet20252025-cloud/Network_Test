## v0.9.0 — 2026-10-24

> 本版是**工具层**新增（`mihomo/` 目录），客户端 exe 与安装包仍是 0.8.0，
> 无需重新打包。`v0.7.0` / `v0.8.0` 两个版本的记录缺失，成品对应
> `dist/渡口助手.exe` 与 `dist/Ferry-Setup-0.8.0.exe`。

### 统一入口：两条线路合成一个固定端点（`mihomo/`）

在此之前线路是「三个裸端口」（直连 / SSH→pcs:10801 / WG→xt:1080），
用哪条靠人记端口、改系统代理、手动判断存活。现在收进一个端点。

- `mihomo/config.template.yaml` —— mihomo 配置模板，占位符在启动时渲染
- `mihomo/mihomo-keeper.ps1` —— 主控（Status/Start/Watch/Stop/On/Off/Switch/Install/Test）
- 端点固定 **`127.0.0.1:10810`**，HTTP + SOCKS5 同端口
- `ferry` select 组默认 **DIRECT**（=没借网），切 `ferry-auto` 才是借网
- `ferry-auto` 是 fallback 组，健康检查 `generate_204` 每 30s，主线路挂了自动切

**关键实测结论：WG 线路的出口 IP 与直连完全相同**（都是电信 116.23.159.136），
它在「换运营商」上零价值。真正换出口的是 SSH→pcs 的联通线路
（218.107.0.47）。WG 的唯一理论优势（承载 UDP）也因上游只实现 CONNECT 而为零。

### 看护循环（`-Action Watch`）

自启跑的是 Watch 不是 Start。每 20 秒体检：

- mihomo 挂了 → 重新拉起（实测强杀后 25 秒内恢复）
- 上游 SSH 端口顺延（10801 → 10808）→ 重渲染配置 + 重启
  （不做的话表现是「10810 还在听，但所有请求 502」）

带单例锁 + PID 复用防护。`Stop` 会连看护一起停，否则 20 秒后被拉回来。

### 借网状态持久化（`mihomo-state.txt`）

`store-selected: false` 保证开机不自动借网，但副作用是看护重启 mihomo 后
正在借网的用户被静默打回直连（实测发生过）。改成自己记 state 文件：
`On/Off/Switch` 时写，启动时读回来。两个需求都成立。

### 自启

`%APPDATA%\...\Startup\FerryMihomo.cmd`，跑 `-Action Watch`。
用启动文件夹而非计划任务（`schtasks` 需管理员，实测 Access denied）。

这个 `.cmd` 的编码要求三条，每条都踩过：**GBK + 无 BOM + CRLF**。
- 带 BOM → 首行 `@echo off` 失效，报 `'∩╗┐@echo' 不是内部或外部命令`
- 纯 LF → 每行第一个字符被吃掉，`rem` 变成 `'em' 不是内部或外部命令`
- UTF-8 → `chcp 936` 下中文注释乱码（同 `b2e0df1` 那次）

### 不动系统代理

本脚本只提供端点，不写 `HKCU\...\Internet Settings`。实测 `On` 之后
`ProxyEnable=1 ProxyServer=127.0.0.1:7897`（用户自己的 Clash Verge）纹丝不动。

详见 [docs/统一入口.md](docs/统一入口.md)。

---

## v0.6.0 — 2026-10-23

### 更名：borrow-net → Ferry（渡口）

- 工程名统一为 `ferry`，中文显示名「渡口」
- `borrow-net.ps1` → `ferry.ps1`、`借网助手.ps1` → `渡口助手.ps1`
- `Get-BorrowPorts.ps1` → `Get-FerryPorts.ps1`
- 运行期状态目录 `.borrow-net` → `.ferry`
- 局域网域名 **`ferry.internal`**（`.internal` 是 ICANN 保留 TLD）

### 登录门户（新增）

`portal/portal-server.py` —— 远端运行，Python 标准库零依赖。

- 以**局域网 IP 作账号**，需输入**临时密钥**（默认 12 小时、一次性）
- 登录后下载一键部署包（`.cmd` 内嵌 base64 PowerShell，双击即完成
  建密钥 → 建隧道 → 起代理 → 设系统代理 → 注册自启）
- 管理面板：创建/撤销密钥、停用账号、重置令牌、事件日志
- `/setup-hosts` 页给出配置 `ferry.internal` 的命令

安全加固（17 项测试）：

| 项 | 做法 |
|---|---|
| 密钥存储 | 只存 SHA-256 |
| 会话 | HMAC-SHA256 签名 cookie，库存 sid 哈希 |
| CSRF | 所有 POST 带一次性 token |
| 爆破防护 | 5 分钟 5 次锁 15 分钟，**用户/管理分开计数** |
| 响应头 | CSP / X-Frame-Options / nosniff / Referrer-Policy |
| 开放重定向 | Location 只允许站内 |
| IP 校验 | 只接受私网段 |

### 每账号限速（新增，默认 5 MB/s）

- 实现用 **GCRA**，实测精度：20MB@5MB/s → 正好 4.00s / 5.00 MB/s
- 为什么不用令牌桶：初始给满一桶 → 前 1 秒免费 → 实测超速 2.4 倍
- 转发改为队列式 `pipeLimited`（`'data'` 只入队，单泵出队），
  实测 20MB 零丢字节

### 修复

- **HTTP 明文代理返回 0 字节**：把上游原始响应（含状态行）直接写进
  Node 的 `ServerResponse`，Node 自己补 `Content-Length: 0`，body 全丢。
  改为解析响应头后分别 `writeHead` + 转发 body。
- **`close` 时误判**：`relayHttpResponse` 的 `sock.on('close')` 无条件报
  「未返回完整响应头」，导致每个正常响应都变 502。改为只在
  `headersParsed === false` 时报错。
- **诊断脚本端口写死**：`net-quality` / `outage-watch` / `simulate-outage` /
  `verify-outage` 仍用 10808，与端口自适应冲突。统一改走 `Get-FerryPorts`。
- **4 个 PS 脚本缺 BOM**：改名时写入丢了 BOM，PS 5.1 按 ANSI 读会破坏语法。

### 全工程静态审查

`portal/audit.py` 检查空 catch / `.Result` / 硬编码端口 / SQL 拼接 /
令牌进日志 / 缺 encoding / 缺 BOM / TODO。当前 **0 错误**。

测试总计：门户 43 + 安全 17 + 限速 9 = **69 项全通过**。
## v0.5.0 — 2026-10-23

### 多用户与便捷接入

对应用户要求「多用户管理要便捷且容易管理，状态显示也要合格」，
以及「尽可能简化配置，确保没见过的局域网主机也能顺利配置成功」。

**新增**

- `tools/proxy-users.mjs`：用户库与双协议认证。
  HTTP 用 `Proxy-Authorization: Basic`，SOCKS5 用 RFC 1929，
  **同一套 name+token**。零依赖，单文件存储。
- `tools/proxy-admin.mjs`：管理 CLI。`add` 直接打印可转发的令牌；
  `show` 输出该用户的完整接入说明（含浏览器/命令行两种写法）。
- `tools/client-config.mjs`：生成接入配置，`--format cmd|sh|md`。
  自动识别本机 IP（排除 Radmin/easytier/Tailscale 等虚拟网卡）。
- `tools/setup-client.ps1`：使用者机器上一键接入 —— 建密钥、测直连、
  起隧道、打印配置。**不需要管理员权限**。
- `docs/多人使用管理.md`：接入架构、管理命令、面板字段、认证与配额说明。

**端口自适应（用户要求：不能因为客户机端口不一样软件就死了）**

- `dual-proxy.mjs` 加 `--port auto`（默认）：从 10800 起探测可用端口，
  结果写进 `.proxy-state/ports.json`。
- `tools/Get-FerryPorts.ps1`：端口解析唯一入口，`ferry.ps1` /
  `渡口助手.ps1` / `startup.ps1` 共用。优先级：显式参数 > 状态文件 > 默认值。
- 自启 `.cmd` 不再写死端口，改传 `--keeper` 让 dual-proxy 自己探测。

**热加载**

- `UserStore.watch()`：`users.json` 被 `proxy-admin.mjs` 改完后 120ms 内生效，
  加人、改配额、停用都不需要重启代理。用 `fs.watch` + 5 秒兜底轮询
  （Windows 上 rename 替换不可靠）。

**踩坑修复**

- `findPort` 忘了 `await`，`isFree` 返回的 Promise 恒为真值，
  于是每次都返回 base 端口，撞占就 `EADDRINUSE` 崩掉。
- 端口探测没避开面板端口，导致 `http` 与 `panel` 撞成同一个数。
- `users.json` 中文用户名写盘乱码：`writeFileSync(str,'utf8')` 在本机
  仍会按 GBK 落盘。改为 `Buffer.from(str,'utf8')` 不经编码转换。
  （另发现 `Get-Content -Raw` 也会把正确 UTF-8 读成乱码， PowerShell 侧
  统一改用 `[Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes())`。）

**实测**

| 项 | 结果 |
|---|---|
| 无认证访问 | HTTP `407`，SOCKS5 直接断开 |
| 错误令牌 / 未知用户 / 停用用户 | 分别 `407`，面板记录原因 |
| 双协议同令牌 | HTTP 与 SOCKS5 均 `200` |
| 用户隔离 | 乙用甲的令牌 `407` |
| 局域网 IP 访问 | `200`（`--allow-lan` 下） |
| 单用户连接上限 1 | 4 并发 → 1 个 `200` + 3 个 `too-many-connections` |
| 热加载 | 改上限后 3 秒内面板值变化，无需重启 |
| 端口自适应 | `10802/10803/20800` 自动分配，全通 |
## v0.4.2 — 2026-10-23

### 新增
- **断网可用性已验证**：在实验机 `xt` 上删除默认路由（保留 `10.0.0.0/8`）
  模拟学校「禁用上网但局域网通」的断网方式，实测结果 PASS：
  公网三个目标全 `FAIL(000)`，经渡口 4/4 站点 200，20 秒采样 4/4，
  渡口出口 `203.0.113.47` 与实验机自身出口不同，证明走的是另一条路。
- `tools/verify-outage-remote.ps1`：在实验机上断网验证（路由表方案，
  不影响本机会话）。为什么不用防火墙见 `docs/实测数据.md` §6.2。

### 文档整理
- 新增 `docs/实测数据.md` 作为**唯一实测数据来源**，合并并订正了原先
  `实测报告.md` / `性能瓶颈诊断.md` / `稳定性验证.md` 三份文档。
- 删除上述三份旧文档 —— 其中包含已被推翻两轮的结论（「远端带宽上限
  1 MB/s」实际是教育网源路由问题，真实带宽 90+ MB/s），保留会误导。
- 删除 `docs/局域网共享方案.md`：未实施的设计稿，多用户管理将有正式文档。

### 删除冗余工具
- `tools/socks-http-bridge.mjs` —— 被 `dual-proxy.mjs` 完全取代（早期单协议版）
- `tools/egress-diag.ps1` —— 一次性诊断，结论已固化进文档
- `tools/concurrency-probe.mjs`、`tools/gh-probe.mjs`、`tools/test-smart-resolve.mjs`
  —— 与 `bw-probe` / `socks-layer-probe` / `longrun` 功能重复
- `tools/ferry.keeper.lock` —— 运行期文件误入库
## v0.4.1 — 2026-10-23

### 修复
- **流确认逻辑丢字节**：`attachConfirm` 给 Socket 挂 `'data'` 监听后又调用 `sock.pipe()`，
  两个消费者争抢同一份字节，TLS 握手首字节被吞 → `github.com` 从 10/10 掉到 4/10
  且失败表现为 20s 超时。改为自实现 `pipeWithConfirm()`（自己转发 + 背压处理），
  修复后 15/15 通过、p50 2.2s。
- **择优只看 TCP 层**：`connectBest()` 在 SOCKS5 建连成功即 `markOk`，
  但 GitHub 系存在「TCP 通、TLS 被 RST」的 IP，会被永久当成好 IP。
  新增 `deferConfirm` 选项 + `confirm(ok, detail)` 回调，由调用方在应用层跑通后再计分。

### 移除
- `githubusercontent.com` 整族（raw / gist / objects）从候选池移除，交给远端 DNS 解析。
  取证：远端直连与经隧道同时刻各 6 次，成功率均约 1/6，说明是办公网到
  GitHub Fastly 边缘的链路问题，固定 IP 无效反而多耗一次超时预算。

### 文档
- `docs/踩坑记录.md` 新增第 24/25/26 条：`'data'` + `pipe()` 争抢字节、
  只验证 TCP 的择优不可靠、识别「不是本工具的锅」的取证方法（同时刻双路径对比）。
# CHANGELOG

## v0.4.0 — 2026-10-23

### 隧道自愈（修复「开启渡口后只有部分站点可用」）

根因是两个独立缺陷叠加：

1. `ssh-tunnel-keeper.ps1` 的单例锁只比对 PID。keeper 被强制结束时会残留
   `ferry.keeper.lock`，而 Windows 会把该 PID 复用给任意新进程，新 keeper
   据此判定「已有实例在跑」并 `exit 0` 自杀，隧道再也起不来。
   修复：除 PID 存活外，还校验该 PID 的命令行确实包含本脚本名。
2. `dual-proxy.mjs` 没有任何隧道存活检测。ssh 死后它仍把请求转发到无人监听的
   上游端口，系统代理依旧指向 10809，表现为「网页能开、GitHub 全挂」。
   修复：新增隧道守护，每 5 秒探测上游 SOCKS 端口，连续 2 次失败后自动拉起 keeper。

同时修复了守护链路自身的三处问题：

- `spawn(..., {detached:true})` 在 Windows 上拉起的 powershell 会在约 300 ms 内
  以 code=0 退出，脚本一行都不执行（连它自己的日志文件都不会生成）。改为非 detached。
- `stdio:'ignore'` 会把子进程输出全部丢弃，启动失败无从排查。改为 pipe 转发，
  同时 keeper 自身写 `.ferry/keeper.log`。
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
- 工程落库到 GitHub（仓库名 `Network_Test`，工程名 `ferry`）。

### 实测发现（重要）

- **WireGuard 从未投入使用**：本机无客户端、无 wg0 适配器、`tracert` 显示 SSH 走校内 3 跳直连。
  真正承载流量的是 `ssh -D` 动态转发。远端 WG 服务端 Running/Auto 但零握手。
- **吞吐瓶颈在远端**：渡口 1.05 MB/s ≈ 远端自身 0.95 MB/s，SSH 隧道几乎零带宽损耗。
- **渡口不是翻墙工具**：google 直连与渡口都超时；国内站点延迟几乎无损。
- **抖动放大 4.55 倍**：直连 0.029s → 渡口 0.132s（20 次连续请求）。
- **零丢包**：40 次请求全成功。
- **最大单点**：断网若发生在 WLAN 接入层，SSH 无法建立，整套失效。

### 托盘图标（结论：程序侧无法固定）

- 查清 `NotifyIcon.Visible=true` **是生效的**，图标确实在 Win11 溢出区里
  （UIA 枚举 `TopLevelWindowForOverflowXamlIsland` 可见「渡口助手」）。
- `HKCU:\Control Panel\NotifyIconSettings` 按 **exe 身份**记录，
  所有寄生在 `powershell.exe` 的图标共享一条记录（本机只有 `Codex Dream Skin`）。
- 尝试并全部失败：`mouse_event` 拖拽、`SendInput` 相对位移拖拽、
  反推 key hash（48 种 FNV 组合无一命中）、改 exe 名（能独立注册但仍进溢出区）。
- **最终结论**：固定到主托盘区只能用户手动在
  `设置 → 个性化 → 任务栏 → 其他系统托盘图标` 打开开关。

## v0.2.0 — 2026-10-22

- GUI `渡口助手.ps1`：三态状态卡、出口 IP 对照、后台异步探测、1s 心跳、日志出口、托盘。
- 修 PS 5.1 下 socks5 不支持（改用 curl.exe）。
- 修出口探测被 Clash 污染（`--noproxy '*'`）。
- 修 `ShowDialog` 模态循环导致最小化即退出（改 `Application.Run`）。
- 修 `ShowInTaskbar` 变更引发 `RecreateHandle` 黑块（改 `Hide()`/`Show()`）。
- 修枚举字符串崩溃（改完整枚举形式）。
- 生成桌面 + 开始菜单快捷方式（自绘多帧 ICO）。

## v0.1.0 — 2026-10-22

- CLI `ferry.ps1`：`On` / `Off` / `Status` / `InstallStartup` / `UninstallStartup`。
- 常驻保活 `ssh-tunnel-keeper.ps1`：断线 5s 重连，单例锁。
- 设计定调：**只切系统代理，不动路由表**（无自环风险，`Off` 可干净还原）。
- 远端就绪：WireGuard 服务端 + sshd + IP 转发 + 防火墙规则。
- 远端 WinNAT 缺失取证，放弃 NAT 路线。

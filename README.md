# Ferry（渡口）

> **Ferry** = 渡口 / 摆渡。把本机流量摆渡到另一条可用网络上。
> 内网里某些主机访问不了外网时，通过一台能出网的远端主机中转，让本机继续工作。

**局域网域名**：`ferry.internal`（`.internal` 是 ICANN 保留给私有网络的 TLD，
永远不会被公网解析占用）

**当前版本**：客户端 `0.9.30`（`dist/渡口助手.exe`）/ 安装包 `Ferry-Setup-0.9.2.exe`（16.9 MB）

> 0.9.4 的客户端 exe 已编译（意见反馈入口、高 DPI 适配、设置保存即生效），
> 但安装包还没重打 —— 本机没装 Inno Setup（`ISCC.exe`），签名私钥密码也不在仓库里。
> 装好 Inno Setup 后跑 `installer/sign.ps1 -Password <私钥密码>`，把产物放进 `dist/` 即可。

---

## 一、它解决什么问题

典型场景是「**账号外网权限受限，但局域网仍然互通**」。
这种情况下本机拿不到公网出口，但还能连到校内的其他机器。

渡口利用这一点：

```
使用者机器 ──直连 SOCKS5──> 远端主机（能上网）──> Internet
                ↑
           走局域网，不需要公网出口
```

> v0.9 起默认**不走 SSH 加密**，改为直连远端 SOCKS5 ——
> 因为 sshd 转发时源 IP 全变成 127.0.0.1，服务端没法按机器限速。
> 代价是明文（局域网内可嗅探）。想加密就在设置里关掉 `directSocks`，
> 退回 SSH 隧道（那时限速不生效）。


**实测结论**（在实验机上删默认路由、保留 `10.0.0.0/8` 模拟断网）：

```
公网直连      : FAIL(000) FAIL(000) FAIL(000)   ← 确实断了
经渡口访问    : github / api / baidu / bilibili 全 200
出口 IP       : 218.107.0.47（联通，远端出口）
               与实验机自己的 116.23.159.136（电信）不同 → 走的是另一条路
20 秒采样     : 4/4
```

详细数据见 [docs/实测数据.md](docs/实测数据.md)。

---

## 二、快速开始

### 方式 A：装客户端（**推荐**，这是现在的主线）

1. 浏览器打开 `http://10.13.24.1:8080/`（或 `http://ferry.internal:8080/`）
2. 点「下载 Windows 客户端」拿到 `Ferry-Setup-0.9.2.exe`（约 17 MB，内嵌 TUN 运行时）
3. 双击安装 → **会弹一次 UAC**（TUN 要建虚拟网卡，这是必需的）
4. 打开「渡口助手」，填管理员给的密钥激活，之后就不用再管

装完之后：

| 位置 | 用途 |
|---|---|
| 托盘图标 | 常驻。单击开菜单，双击开主窗口 |
| 桌面「渡口助手」 | 主程序 |
| 设置 → 开机自动启动 | 用**计划任务**实现（`RunLevel=HighestAvailable`），登录时**不弹 UAC** |

客户端能做这些：

- **直连远端 SOCKS5**（不走 SSH，所以服务端能按机器限速）
- **TUN 全局接管**（设置里勾「UDP低延迟模式」）—— 让不认代理的程序也走渡口
- 系统代理备份 / 还原（退出时还原成你原来的 Clash，不会把你网搞断）
- 启动时自动检查更新，有新版才弹窗
- 被管理员停用 / 到期后，**5 秒内**自动切回直连

> 为什么不建议用老的 `.cmd` 部署包：那套要装 Node.js、要手动配 SSH 密钥，
> 而且没有 TUN。客户端是把这些全打进了 exe。

### 方式 B：命令行工具（适合自己用 / 排障）

```powershell
# 只建隧道 + 切系统代理
powershell -ExecutionPolicy Bypass -File tools\ferry.ps1 -Action On

# 看状态（含直连与渡口出口 IP 对照）
powershell -ExecutionPolicy Bypass -File tools\ferry.ps1 -Action Status

# 切回直连（还原原始系统代理，不是简单关掉）
powershell -ExecutionPolicy Bypass -File tools\ferry.ps1 -Action Off
```

### 方式 C：mihomo 统一入口（命令行备选，功能已被客户端覆盖）

```powershell
cd mihomo
.\mihomo-keeper.ps1 On        # 借网
.\mihomo-keeper.ps1 Status    # 看三态出口
.\mihomo-keeper.ps1 Off       # 恢复直连
```

把直连 / SSH→pcs / WG→xt 合成一个固定端点 `127.0.0.1:10810`，
**不动系统代理**，只提供端点。

> ⚠ 这一套和客户端是**两套并行方案**，都开时会抢 10801/10800 端口。
> 日常用客户端就够，这套留给「不想装 exe、只要一个 SOCKS 端点」的场景。
> 它的自启项已默认不安装（`FerryMihomo.cmd` 已移除）。


支持 UDP（游戏 / QUIC）—— 这是整条链路第一次具备这个能力。
详见 [docs/一键部署与UDP.md](docs/一键部署与UDP.md)。

---

## 三、端口不用记

所有脚本都读 `.proxy-state/ports.json`（dual-proxy 启动时自动探测并写入）。
端口被 Clash / 公司代理占了也能正常跑。

```powershell
# 想手动指定时才需要（一般不用）
tools\ferry.ps1 -Action On -SocksPort 20808 -HttpPort 20809
```

解析优先级：**显式参数 > `.proxy-state/ports.json` > 默认 10808/10809**

> ⚠️ `ports.json` **不可信**：它只在 dual-proxy 以 auto 模式跑过时才写，
> 手动起的 `ssh -D` 不会写。实测 `ports.json` 记 10800、实际在听 10801。
> 所以 `mihomo/mihomo-keeper.ps1` 不读它，而是现场探测真实监听端口。

---

## 四、多人管理

```powershell
# 加人（直接打印可转发的令牌）
node tools\proxy-admin.mjs add 张三 --note "3 楼实验室"

# 看全部
node tools\proxy-admin.mjs list

# 停用 / 恢复 / 重置令牌 / 删除
node tools\proxy-admin.mjs disable 李四
node tools\proxy-admin.mjs enable 李四
node tools\proxy-admin.mjs reset 王五
node tools\proxy-admin.mjs remove 王五

# 全局设置
node tools\proxy-admin.mjs settings --max-conns 40 --rate-mb 5
```

**改完立即生效**（热加载 120ms），不用重启代理。

### 每账号限速

默认 **5 MB/s**，实测精度：

| 设定 | 实测 | 误差 |
|---|---|---|
| 2 MB/s（源速 0.91） | 0.49 MB/s（设 0.5 时） | 2% |
| 20 MB @ 5 MB/s | 4.00s / 5.00 MB/s | 0% |

实现用 **GCRA**（不是令牌桶 —— 令牌桶初始给满一桶，实测超速 2.4 倍）。

---

## 五、管理门户

```powershell
# 远端启动
python portal\portal-server.py --port 8080 --public-host 10.13.24.1
```

访问 `/admin`，用 `portal-admin.txt` 里的密码登录。

| 功能 | 说明 |
|---|---|
| 创建密钥 | 指定备注 + 有效小时数，明文只在生成时显示一次 |
| 撤销密钥 | 立即失效 |
| 停用账号 | 门户和部署包都拒绝 |
| 重置令牌 | 旧令牌立即失效 |
| 事件日志 | 谁在什么时候登录/失败/下载 |

### 安全措施

| 项 | 做法 |
|---|---|
| 密钥存储 | 只存 SHA-256，明文不落库 |
| 会话 | HMAC-SHA256 签名 cookie，库里只存 sid 哈希 |
| CSRF | 所有 POST 带一次性 token |
| 爆破防护 | 5 分钟内失败 5 次锁 15 分钟（用户/管理**分开计数**，防低权限锁高权限） |
| 响应头 | CSP / X-Frame-Options / nosniff / Referrer-Policy |
| 开放重定向 | Location 只允许站内路径 |
| IP 校验 | 只接受私网段，公网 IP 直接拒 |
| 部署包越权 | 只能下载自己 IP 的包 |

---

## 六、文件清单

> 完整目录说明见 [AGENTS.md](AGENTS.md) 第二节。文档类新增：
> `AGENTS.md`（开发引导）、`docs/架构与原理.md`、`docs/故障排查.md`。

### 客户端（主线）

| 文件 | 作用 |
|---|---|
| `ferry-client/src/Program.cs` | 入口。处理 `/start` `/stop` `/uninstall` `/install-autostart` |
| `ferry-client/src/MainForm.cs` | 主窗口：状态卡、设置、托盘、自动更新、心跳 |
| `ferry-client/src/Proxy.cs` | 内建 HTTP+SOCKS5 双协议代理（上游可指向远端） |
| `ferry-client/src/TunMode.cs` | TUN 全局接管：委托 mihomo，管 PID 文件和残留清理 |
| `ferry-client/src/Ferry.cs` | 系统代理备份/还原（含原代理看护） |
| `ferry-client/src/JobKeeper.cs` | Job Object —— 强杀客户端时连带回收子进程 |
| `ferry-client/src/app.manifest` | `requireAdministrator` + PerMonitorV2 DPI |
| `installer/ferry.iss` | Inno Setup 安装包定义 |
| `wireguard/ferry-socks5-server.py` | 远端 SOCKS5 服务：TCP CONNECT + UDP ASSOCIATE + **按 IP 限速** |

### 核心工具（命令行）

| 文件 | 作用 |
|---|---|
| `tools/dual-proxy.mjs` | 双协议代理（HTTP + SOCKS5 单端口自动识别）、限速、隧道守护 |
| `tools/smart-resolve.mjs` | GitHub 系 IP 择优（健康分 + GCRA 式并发竞速） |
| `tools/ssh-tunnel-keeper.ps1` | 隧道保活，断线 5 秒重连，单例锁 |
| `tools/Get-FerryPorts.ps1` | **端口解析唯一入口**，所有脚本共用 |

### 统一入口（mihomo，⚠ 命令行备选）

| 文件 | 作用 |
|---|---|
| `mihomo/install-all.ps1` | 一键部署（环境检查→远端中继→本机配置→验证→快捷方式） |
| `mihomo/mihomo-keeper.ps1` | 主控：启停、借网开关、看护循环、自启安装 |
| `mihomo/config.template.yaml` | mihomo 配置模板（端口占位符启动时渲染） |
| `wireguard/deploy-udp-relay.ps1` | 远端中继部署（在 pcs 上跑，含 `-RateMb`） |

### 本轮新增的验证脚本

| 文件 | 作用 |
|---|---|
| `tools/verify-proxy-restore.py` | 系统代理还原的三种场景（可达 / 不可达 / 脏值） |
| `tools/verify-ban-kick.py` | 封禁 → 心跳带授权状态 → 客户端秒级断开（13 项） |
| `tools/verify-udp-relay-lifecycle.py` | UDP 关联登记 / 上限 / 回收（15 项） |
| `tools/verify-netctl-bugs.py` | 载荷仪表 + 带宽控制台（21 项） |
| `tools/verify-note-api.py` | 备注编辑的 JSON 契约（保存/清空/404/截断） |
| `mihomo/verify-udp-path.py` | 完整 UDP 链路验证（10810 → 中继 → 互联网） |

### 多用户

| 文件 | 作用 |
|---|---|
| `tools/proxy-users.mjs` | 用户库 + 双协议认证 + 配额 + 热加载 |
| `tools/proxy-admin.mjs` | 用户管理 CLI |
| `tools/client-config.mjs` | 生成接入配置（`cmd` / `sh` / `md`） |
| `tools/setup-client.ps1` | 使用者机器一键接入 |

### 门户

| 文件 | 作用 |
|---|---|
| `portal/portal-server.py` | 登录门户 + 管理面板（Python 标准库，零依赖） |

### 验证与诊断

| 文件 | 作用 |
|---|---|
| `tools/verify-outage.ps1` | 本机一键断网验证（管理员） |
| `tools/verify-outage-remote.ps1` | 在实验机上断网验证（不改本机） |
| `tools/simulate-outage.ps1` | 模拟断网开关 |
| `tools/outage-watch.ps1` | 真实断网时的后台存活监控 |
| `tools/net-quality.ps1` | 可达性 / 抖动 / 丢包 / 吞吐 / 长连接 / DNS |
| `tools/bw-probe.mjs` | 并发带宽测试 |
| `tools/longrun.mjs` | 长跑稳定性 |
| `tools/socks-layer-probe.mjs` | 分层定位 SOCKS 失败阶段 |
| `tools/correlate.mjs` | 同时刻对比远端直连与隧道 |
| `tools/gh-stability.mjs` | GitHub 稳定性对比 |
| `portal/audit.py` | 静态审查（空 catch / 硬编码端口 / SQL 拼接 / 缺 BOM） |

### 测试

| 文件 | 覆盖 |
|---|---|
| `portal/test-portal.mjs` | 门户端到端 **43 项** |
| `portal/test-security.mjs` | 安全加固 **17 项** |
| `portal/test-pipe.mjs` | 限速 + 背压 **9 项** |

---

## 七、文档

**先读这两份**：

| 文档 | 内容 |
|---|---|
| [AGENTS.md](AGENTS.md) | **开发/维护引导**：目录地图、本地怎么跑、发版流程、动手前必须知道的 8 条、当前已知限制 |
| [docs/架构与原理.md](docs/架构与原理.md) | **原理 + 同款部署教程**：拓扑图、三种模式对比、鉴权与限速、分发与更新链路、从零部署一套 |
| [docs/故障排查.md](docs/故障排查.md) | **按现象查**：借网不通 / 装不上 / 更新没反应 / 全局模式 / 门户 / 密钥，每条都给能直接跑的命令 |

其余按主题：

| 文档 | 内容 |
|---|---|
| [docs/实测数据.md](docs/实测数据.md) | **全部实测数据**：可达性 / 抖动 / 带宽多源对比 / DNS / 稳定性 / 断网验证 |
| [docs/踩坑记录.md](docs/踩坑记录.md) | **51 条**实测踩坑，每条都有「为什么」和「怎么做」 |
| [docs/限速与直连模式.md](docs/限速与直连模式.md) | **限速怎么真正生效**：A 方案、GCRA、为什么按 IP 不按连接 |
| [docs/出口旁路.md](docs/出口旁路.md) | **借网自动借用远端 Clash**：竞速分流、五个超时参数的实测依据 |
| [docs/UDP低延迟模式.md](docs/UDP低延迟模式.md) | **TUN 全局接管**：架构、为什么必须管理员、UAC 如何免掉 |
| [docs/多人使用管理.md](docs/多人使用管理.md) | 接入架构、管理命令、面板字段、认证与配额 |
| [docs/断网验证方法.md](docs/断网验证方法.md) | 三种验证方式、四类断网类型判定表 |
| [docs/远端部署.md](docs/远端部署.md) | 远端 sshd / WireGuard / 端口转发 |
| [docs/一键部署与UDP.md](docs/一键部署与UDP.md) | ⚠ 命令行备选：`install-all.ps1` 一键部署 + UDP 打通过程 |
| [docs/统一入口.md](docs/统一入口.md) | ⚠ 命令行备选：mihomo 常驻方案（端点 10810） |

---

## 八、必须知道的限制

1. **断网类型决定成败**
   - ✅ 外网权限受限、局域网通（常见情况）→ 有效，已实测
   - ❌ WLAN 接入层断开（连网关都不通）→ 无效，SSH 起不来
     → 需要另一条物理通路（手机热点）

2. **校园网账号间有端口级隔离**
   实测从另一个账号的机器探本机：`22` 不通、`445` 通、`10803` 通。
   所以「把某台机器的代理共享出去」不可靠。
   **本方案是每台机器各自建隧道直连远端**，远端才是共享点。

3. **google.com 两端都不通**
   远端办公网的出口策略，不是渡口缺陷。

4. **`raw.githubusercontent.com` 约 1/6 成功率**
   办公网到 GitHub Fastly 边缘的链路问题。取证：远端直连与经隧道
   同时刻各 6 次，成功率一致。固定 IP 无效，已从候选池移除。

5. **令牌是明文存在 `users.json` 里**
   这台机器本身要管好，不要把该文件同步到公开仓库。

6. **客户端和 mihomo 方案不要同时开**
   两套都监听 10800/10801 附近端口，抢起来会导致隧道时通时断。
   日常用客户端；mihomo 那套留给「只要一个 SOCKS 端点」的场景。

7. **WG 线路的出口与直连相同**
   实测两路出口都是 116.23.159.136（电信）。`ferry-wg` 节点保留是为了 TCP 冗余，
   不是因为它能换运营商。**换出口靠 SSH→pcs 的联通线路**（218.107.0.47）。

8. **客户端的 TUN 模式要管理员**
   它要建虚拟网卡 + 改路由表（实测普通权限报
   `configure tun interface: Access denied`）。安装包已默认要求管理员，
   所以双击就提权；配套把开机自启改成计划任务（`RunLevel=HighestAvailable`），
   登录时不会弹 UAC。

9. **直连模式是明文 SOCKS5**
   为了「服务端能按机器限速」，客户端默认不走 SSH 加密，直接连 `pcs:1080`。
   同一个局域网里有人抓包能看到你访问了哪些域名和 IP。
   想加密就在设置里关掉 `directSocks`（那时限速不生效）。

10. **限速在远端，不在客户端**
   客户端是用户自己的机器，改个 config 就跳过了。只有远端
   `ferry-socks5-server.py` 那个「所有流量必经」的点才真正有效。

11. **墙外站自动借用远端的 Clash 出口**
    借网流量默认**不用** Clash（国内站直连更快），只有直连不通时才自动
    改走远端 pcs 上的 Clash。对用户完全透明，界面无任何提示。
    前提是远端跑着 Clash（默认探测 7890/7891/7897/10808）；
    探不到就静默关闭，不影响国内站。详见 [docs/出口旁路.md](docs/出口旁路.md)。

---

## 九、命名说明

工程名 `ferry` 与 GitHub 仓库名 `Network_Test` **无关** —— 仓库是早期建的，
工程按功能重新命名。历史版本里叫过 `borrow-net`，v0.6.0 起统一为 Ferry。

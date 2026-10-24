# Ferry（渡口）

> **Ferry** = 渡口 / 摆渡。把本机流量摆渡到另一条可用网络上。
> 校园网断网时，通过一台能上网的远端主机中转，让本机继续访问互联网。

**局域网域名**：`ferry.internal`（`.internal` 是 ICANN 保留给私有网络的 TLD，
永远不会被公网解析占用）

---

## 一、它解决什么问题

学校的断网方式是「**禁用上网权限，但局域网仍然互通**」。
这种情况下本机拿不到公网出口，但还能连到校内的其他机器。

渡口利用这一点：

```
使用者机器 ──ssh -D──> 远端主机（能上网）──> Internet
             ↑
        走局域网，不需要公网出口
```

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

### 方式 A：登录门户（推荐，适合多人）

1. 浏览器打开 `http://ferry.internal:8080/`（或 `http://10.13.24.1:8080/`）
2. 输入**你的局域网 IP** 和**管理员给的临时密钥**（12 小时有效，一次性）
3. 下载 `ferry-<你的IP>.cmd`，双击运行

部署包会自动：检查 SSH 密钥 → 建隧道 → 起双协议代理 → 设系统代理 → 注册自启。

> 第一次需要把你的公钥发给管理员加到远端，之后不用再管。

### 方式 B：命令行（适合自己用）

```powershell
# 只建隧道 + 切系统代理
powershell -ExecutionPolicy Bypass -File tools\ferry.ps1 -Action On

# 看状态（含直连与渡口出口 IP 对照）
powershell -ExecutionPolicy Bypass -File tools\ferry.ps1 -Action Status

# 切回直连（还原原始系统代理，不是简单关掉）
powershell -ExecutionPolicy Bypass -File tools\ferry.ps1 -Action Off
```

### 方式 C：图形界面

```powershell
powershell -ExecutionPolicy Bypass -File tools\渡口助手.ps1
```

托盘常驻，状态卡 + 链路明细 + 日志窗。

### 方式 D：一键部署（**推荐**，装一次之后只看两个图标）

```powershell
cd mihomo
powershell -NoProfile -ExecutionPolicy Bypass -File install-all.ps1
```

一个脚本做完：环境检查 → 部署远端 UDP 中继 → 配本机统一入口 → 端到端验证
→ 建桌面快捷方式。**不需要管理员权限**。

跑完桌面多两个图标，之后双击即可：

| 图标 | 作用 |
|---|---|
| **渡口借网** | 开启借网。TCP 走 SSH→pcs（联通出口），UDP 走 pcs 中继，自动分流 |
| **渡口恢复直连** | 关闭借网 |

需要代理的程序填 **`127.0.0.1:10810`**（HTTP + SOCKS5 同端口）。
**系统代理不会被改动**，你原来的 Clash Verge 照常工作。

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

### 核心

| 文件 | 作用 |
|---|---|
| `tools/dual-proxy.mjs` | 双协议代理（HTTP + SOCKS5 单端口自动识别）、限速、隧道守护、孤儿清理 |
| `tools/smart-resolve.mjs` | GitHub 系 IP 择优（健康分 + GCRA 式并发竞速） |
| `tools/ssh-tunnel-keeper.ps1` | 隧道保活，断线 5 秒重连，单例锁 |
| `tools/Get-FerryPorts.ps1` | **端口解析唯一入口**，所有脚本共用 |

### 统一入口（mihomo）

| 文件 | 作用 |
|---|---|
| `mihomo/install-all.ps1` | **一键部署**（环境检查→远端中继→本机配置→验证→快捷方式） |
| `mihomo/mihomo-keeper.ps1` | 主控：启停、借网开关、看护循环（含 SSH 隧道自管理）、自启安装 |
| `mihomo/config.template.yaml` | mihomo 配置模板（端口占位符启动时渲染） |
| `mihomo/mihomo-state.txt` | 记住上次选择，崩溃恢复后保持借网状态 |
| `mihomo/verify-udp-path.py` | 完整 UDP 链路验证（10810 → 中继 → 互联网） |
| `mihomo/socks5-udp-test.py` | 直连测某 SOCKS5 服务端的 UDP ASSOCIATE |
| `mihomo/probe-udp.py` | 测出站 UDP 是否可用 |
| `wireguard/deploy-udp-relay.ps1` | 远端 UDP 中继部署（在 pcs 上跑） |

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

| 文档 | 内容 |
|---|---|
| [docs/实测数据.md](docs/实测数据.md) | **全部实测数据**：可达性 / 抖动 / 带宽多源对比 / DNS / 稳定性 / 断网验证 |
| [docs/踩坑记录.md](docs/踩坑记录.md) | 33 条实测踩坑，每条都有「为什么」和「怎么做」 |
| [docs/一键部署与UDP.md](docs/一键部署与UDP.md) | **一键部署 + UDP 打通**：方案对比、实测数据、6 个新坑 |
| [docs/统一入口.md](docs/统一入口.md) | mihomo 常驻方案：端点、看护、状态持久化、自启编码三条硬要求 |
| [docs/多人使用管理.md](docs/多人使用管理.md) | 接入架构、管理命令、面板字段、认证与配额 |
| [docs/断网验证方法.md](docs/断网验证方法.md) | 三种验证方式、四类断网类型判定表 |
| [docs/远端部署.md](docs/远端部署.md) | 远端 sshd / WireGuard / 端口转发 |

---

## 八、必须知道的限制

1. **断网类型决定成败**
   - ✅ 禁用上网权限、局域网通（本校情况）→ 有效，已实测
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

6. **统一入口的 TCP 和 UDP 走不同线路**
   TCP 走 SSH→pcs（实测 5.2–9.3 MB/s 最稳），UDP 走 pcs 上的 Python 中继。
   两条各管一段，规则里用 `NETWORK,udp` 分流。

7. **WG 线路的出口与直连相同**
   实测两路出口都是 116.23.159.136（电信）。`ferry-wg` 节点保留是为了 TCP 冗余，
   不是因为它能换运营商。**换出口靠 SSH→pcs 的联通线路**（218.107.0.47）。

8. **仍然不是「全局透明代理」**
   10810 是个 SOCKS5/HTTP 端点，只覆盖「认代理设置」的程序。要让不认代理的
   程序（多数游戏客户端）也走，需要 TUN，而 TUN 需要管理员
   （实测 `configure tun interface: Access denied`）。
   **UDP 能力已经打通**（中继层），但要走 TUN 还得提权。

---

## 九、命名说明

工程名 `ferry` 与 GitHub 仓库名 `Network_Test` **无关** —— 仓库是早期建的，
工程按功能重新命名。历史版本里叫过 `borrow-net`，v0.6.0 起统一为 Ferry。

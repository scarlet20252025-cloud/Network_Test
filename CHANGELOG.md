## v0.14.31 — 2026-10-28

> 客户端 1.0.34：翻墙客户端的出站改走**渡口直通**——断网时段它不再被 DIRECT 送死。

### 现象与误判

1.0.31 给翻墙客户端（Clash/v2rayN/…）的出站加了 `PROCESS-NAME,…,DIRECT`，
理由是「防止 TUN 把它连机场的流量抓回来判境外，绕成死循环」。方向对了一半。

**DIRECT 在断网时段是必死的**：用户被禁网时，物理出口本身就不通，
DIRECT 出去连机场端口不可能成功。当时能看到的现象就是「节点全部 Timeout」，
而且换了 DNS（DoT）也只解决了解析，连不上还是连不上。

### 实测：渡口出口能到机场

用 DoT（TCP 853）解析机场节点域名，拿到真实 IP：

```
g.o.b.v.l.f.3.d.1.hk03-ae5.entry.v51124-6.qpon
  DoT (tls://223.5.5.5)  ->  36.141.40.13
  经渡口 10803 连 36.141.40.13:764  ->  OK
  物理直连同一 IP:764              ->  OK（断网时段会变成失败）
```

也就是说：**断网时段能连上机场的只有「借道 pcs 的隧道」这一条路**。

### 改法：给本地代理开一个「直通」端口

问题在于本地代理看不出「这条连接是浏览器发的，还是 Clash 发给机场的」——
来源永远是 127.0.0.1。而这两类必须区别对待：

| 连接 | 必须有线 | 为什么 |
|---|---|---|
| 浏览器的境外流量 | 交给本机 Clash | 渡口出口到不了境外 |
| Clash 自己连机场 | **走渡口隧道** | 物理出口断着，DIRECT 必死 |

混在一起就是死循环（Clash → 渡口 → 判境外 → 回 Clash）。所以用**端口**把它们分开：

```
_httpPort   混合端口：分流（国内走渡口，境外交给 Clash）
_bypassPort 直通端口：不分流，一律直接上隧道   ← 新增
```

TUN 侧对应加一个代理 `ferry-clash` 指向直通端口，`PROCESS-NAME` 规则全部改过去。

### 规则顺序也重排了（这条很关键）

```
① IP-CIDR 内网/回环 → DIRECT     （Clash 连内网不能被导进隧道）
② NETWORK,udp → ferry-udp        （mihomo 的 PROCESS-NAME **不区分 TCP/UDP**，
                                   放前面会把 Clash 的 UDP 导到只支持 TCP 的
                                   ferry-clash 上，直接失败）
③ PROCESS-NAME <翻墙客户端> → ferry-clash
④ MATCH,ferry
```

### 验证

实验机（10.113.66.6）用真实 mihomo + 生成的配置，`/32` 路由把节点 IP 导进 TUN：

```
[TCP] 198.18.0.1:10077(clash-verge.exe) --> 36.141.40.13:764
      match ProcessName(clash-verge.exe) using ferry-clash     ← 走直通，连接 OK
[TCP] 198.18.0.1:10079(notclash.exe)    --> 36.141.40.13:764
      match Match using ferry[ferry-tcp]                       ← 其余落 MATCH,ferry
```

- `tools/verify-tunconfig.cs` 扩到 24 项断言：直通端口存在、目标必须是
  `ferry-clash`（**不能是 DIRECT**）、内网与 UDP 规则必须排在 PROCESS-NAME 之前。
- 生成的配置过 `mihomo -t`。
- 生命周期回归见提交说明。

### 说明：这条改动只在断网时段有意义

校园网正常时，DIRECT 和 ferry-clash 都能连上机场，看不出差别。
所以这次验证必须在「模拟断网」的测试机上做，不能拿用户机器试 ——
用户机器只在夜里断网，白天测不出来。

## v0.14.30 — 2026-10-28

> 客户端 1.0.33：借网期间守护系统代理（不再被 Clash 抢走）+ 主页常驻版本号。

### 一、系统代理被抢 → 看上去整个网都断了

Clash Verge 启动时会把系统代理设成它自己的 `127.0.0.1:7897`。用户机是靠渡口借网
上网的，系统代理一被抢，浏览器就只走 Clash；而 Clash 的节点连不上（机场在境外、
它的 DNS 又被 TUN 劫持），结果就是**国内网站也打不开**，用户以为网断了。

实测时间线（2026-10-28）：

```
00:45:40  渡口借网成功，系统代理 → http://127.0.0.1:10801
00:46:58  Clash Verge 启动
00:47:xx  系统代理变成 127.0.0.1:7897，浏览器全部超时
```

Clash Verge 自己的 `System proxy guard is disabled`，所以它只设一次、之后不管；
渡口以前也只设一次 —— 谁后启动谁赢。

**改法**：借网期间每 3 秒检查一次系统代理，若被别的程序改走，就改回来并写一行日志：

```
系统代理被其他程序改成 [127.0.0.1:7897]，已改回 [http://127.0.0.1:10802]（借网期间由渡口持有）
```

两个有意的边界：

- **只在借网时守护**。没借网时系统代理归谁管都无所谓，不该去动。
- **不会强行打开代理**。如果代理被关成 0，说明可能是用户手动切直连，渡口不管；
  只处理「被别人改走」这一种。

### 二、主页常驻版本号

版本号以前只在设置页里能看到，排查「他装的是哪一版」时还得让用户去翻设置。
现在主页右上角、更新按钮的正下方常驻显示 `v1.0.33`，更新按钮出现时也不会被挤掉。

### 验证

- 编译通过；界面改动按惯例在 10.113.66.6 上离屏渲染核对（`tools/render-ui.ps1`）。
- 系统代理守护的判定函数 `SameEndpoint` 兼容 `http://127.0.0.1:x` 与 `127.0.0.1:x`
  两种写法（Clash 写的是后者，直接字符串比较会永远判不等、每 3 秒刷一次日志）。

## v0.14.29 — 2026-10-28

> 客户端 1.0.32：TUN 的 DNS 劫持收窄到网关地址 —— 修「开了 Clash 节点全部 Timeout」。

### 现象

装完 1.0.31（防回环例外已无条件写入）后打开 Clash Verge，**47 个节点全部 Timeout**，
界面上一个可用的都没有。国内访问正常（baidu 200/0.17s）。

查 Clash 内核（`verge-mihomo.exe`，pid 36048）的实际连接：

```
Established 198.18.0.22   7826
Established 198.18.0.23    443
FinWait2    198.18.0.6     443
```

**目标全是 `198.18.0.x`** —— 那是渡口 TUN 的 fake-ip。也就是说：
Clash 解析机场节点域名时，拿到的是假地址，然后拿着假地址去连机场。

### 根因：`dns-hijack: any:53` 把朋友也一起劫了

```
Clash 解析 6.1.9.6.6.i.h.d...qpon  → 系统/TUN 的 DNS 被劫持 → 198.18.0.22
Clash 连 198.18.0.22:7826          → 进 TUN → PROCESS-NAME 命中 → DIRECT
DIRECT 到 198.18.0.22              → 根本没有这台主机 → Timeout
```

PROCESS-NAME 放行是对的，但放行到假地址没有意义。1.0.31 修好了「规则没生成」，
这一层是它没覆盖到的（当时 CHANGELOG 里就把 `dns-hijack` 标成残余风险）。

### 改法：只劫持 TUN 网关

```yaml
tun:
  dns-hijack:
    - 198.18.0.2:53     # 原来是 any:53
```

mihomo 把系统 DNS 指到 TUN 网关（198.18.0.2），所以劫持这一个地址就够系统解析用；
用户自己配了 DNS 的程序（Clash 的 `default-nameserver: 223.5.5.5…`、DoH）不再被拦，
它们的查询按规则 `NETWORK,udp` 走 `ferry-udp` → 远端中继 → 拿真实答案。

### 实测对照（测试机 10.113.66.6，上游用 pcs 的真实 socks5 relay）

```
dns-hijack: any:53
    Resolve-DnsName www.baidu.com -Server 223.5.5.5  ->  198.18.0.4      (fake-ip)

dns-hijack: 198.18.0.2:53
    Resolve-DnsName www.baidu.com -Server 223.5.5.5  ->  2408:8756:...   (真实地址)
    Resolve-DnsName www.baidu.com -Server 198.18.0.2 ->  198.18.0.4      (fake-ip，系统路径不变)
```

日志佐证：

```
[UDP] 198.18.0.1:53743 --> 223.5.5.5:53 match Network(udp) using ferry-udp-group[ferry-udp]
```

### 代价（如实写）

1. **非系统 DNS 的查询现在要过隧道**：几十毫秒额外延迟，且占用渡口的 UDP 中继。
   但只有「显式配了自己 DNS 的程序」会走这条（Clash、DoH 客户端），
   普通程序仍用系统 DNS → fake-ip → mihomo 本地解析，不占隧道。
2. **mihomo 的 sniffing 是关着的**，所以经隧道的那条路在上游只拿得到 IP，
   分流从「域名后缀表」降级成「IP 段表」。对 Clash 这种本来就是代理的流量没影响，
   但如果有人把浏览器 DNS 也指到公网，判定精度会降一档。

### 验证

- `tools/verify-tunconfig.cs` 增加两项断言：`dns-hijack` 必须是 `198.18.0.2:53`、
  且**不能**出现 `any:53`。共 21 项 ALL PASS。
- 生成的配置过 `mihomo -t`。
- 对照实验见上。

### 这一轮该记住的

**「放行」和「放行到哪」是两件事。** 1.0.31 让 Clash 的出站走 DIRECT，方向对；
但它的目标是 fake-ip，DIRECT 到假地址等于没放行。看日志时不能只看「规则命中了」
就收工 —— 要接着看**它连的是哪个地址、那个地址是不是真的**。

## v0.14.28 — 2026-10-28

> 客户端 1.0.31：TUN 的防回环例外**不再依赖「启动那一瞬间检测到 Clash」**。

### 现象

用户机 23:21~23:30 境外全断，日志只有这一类：

```
23:21:35 本机代理连不上，改走渡口：backend.accounts.hytale.com
23:24:11 本机代理连不上，改走渡口：api.ipify.org
23:29:53 本机代理连不上，改走渡口：registry.npmjs.org
```

手工验证的结果很矛盾：

```
TcpClient 连 127.0.0.1:7897  → 成功
curl -x http://127.0.0.1:7897 https://www.google.com → CONNECT 200，然后 5 秒超时
渡口自己的 10801 → baidu      → 200（0.17s）
```

### 根因：规则是「有条件生成」的，而条件是时序敏感的

PROCESS-NAME 防回环例外以前包在 `if (_clashPort > 0)` 里 —— 只有**启动 TUN 的
那一刻**检测到本机 Clash，才会写进配置。用户机的日志正好记录了那一刻：

```
23:44:37 没有检测到本机代理，全部流量走渡口
```

那时 Clash 的 7897 恰好不可达（1.0.29 在 23:44:33 还写过「原代理 127.0.0.1:7897
当前不可达」）。于是 tun-config.yaml 里**一条 PROCESS-NAME 都没有**，规则只有
内网 DIRECT + UDP + MATCH,ferry。用户随后打开 Clash：

```
Clash 连机场 → 被 TUN 抓走 → 判境外 → 交给 Clash → 再被抓 → 死循环
```

表现就是「端口能连、CONNECT 200、然后一直等」—— 不是 Clash 坏了，是它的出站
被自己的 TUN 抢走了。

**这是一条竞态**：用户先起渡口再起 Clash（或中途重启过 Clash），保险就永远缺失。

### 为什么 1.0.30 没修好，以及它加错了东西

1.0.30 的判断是「strict 在 gvisor 栈下不匹配」，于是加了 `find-process-mode: always`。
在测试机上实测（mihomo v1.19.32 + gvisor + 手动 /32 路由，避免抢 SSH 的路由）：

```
strict:  [TCP] 198.18.0.1:4243(clash-verge.exe) --> 1.1.1.1:80 match ProcessName(clash-verge.exe) using DIRECT
always:  [TCP] 198.18.0.1:3586(clash-verge.exe) --> 1.1.1.1:80 match ProcessName(clash-verge.exe) using DIRECT
```

**两种模式表现完全一致**，`clash-verge.exe` 这种带扩展名的写法也能匹配。
所以：

1. `always` 是多余开销 —— 它让每个连接都多查一次进程归属，而用户明确抱怨过「卡慢」。
   回退成 `strict`。
2. 1.0.30 真正的漏洞是**规则仍然没有无条件生成**（那段代码还在 `if (_clashPort > 0)` 里），
   所以它对着用户机的现场一无所知。

### 改法

| 改动 | 说明 |
|---|---|
| **PROCESS-NAME 例外无条件写入** | 13 个进程 × 2 种写法 = 26 条。只在名单进程真的存在时才命中，写上纯属保险；不依赖任何检测结果 |
| `find-process-mode` 回 `strict` | 实测与 always 等效，去掉每连接查进程的固定开销 |
| 名单保持固定 | 不枚举用户装了什么（踩坑 66：方案一旦需要「知道用户装了什么」就说明绕路了） |

### 验证

- `tools/verify-tunconfig.cs` 重写为新结构的 19 项断言，**核心一项**：
  `clashPort = 0` 时仍然必须写出 26 条 PROCESS-NAME（这正是用户机的现场）；
  另有一项断言两个场景生成的配置**逐字相同**。
- 两份配置都过 `mihomo -t` 语法校验。
- 测试机上用真实 mihomo（v1.19.32 + gvisor）跑**等价配置**：`find-process-mode: strict`
  + 两条 `PROCESS-NAME`，`auto-route: false` 再手动加一条 `/32` 路由（不抢默认路由，
  SSH 不会断）。模拟进程 `clash-verge.exe` 发起连接 →
  `match ProcessName(clash-verge.exe) using DIRECT`，不再成环。
- 用客户端生成的**完整配置**（把 `auto-route` 改成 false）再跑一次时暴露了实验方法的
  局限：`/32` 路由是全机的，mihomo 自己那条 DIRECT 出站也被抓回 TUN，日志报
  `reject loopback connection to: 1.1.1.1:80`。真实运行时 `auto-route: true` 会让
  mihomo 自动排除自己的出口，不会有这个问题 —— 但这说明**手工路由只能验规则命中，
  不能验端到端转发**，两个结论要分开写。
- 生命周期回归（`tools/verify-lifecycle.cs`，22 项）见提交说明。

### 这次该记住的

**「配置里没有这条规则」和「这条规则没匹配上」是两件事。** 1.0.30 直接跳到了
后者（怀疑 mihomo 的进程匹配），却没去看生成的配置文件里到底有没有那些行 ——
而 `%LOCALAPPDATA%\Ferry	un-config.yaml` 里明摆着一条都没有。
下次先 dump 产物，再怀疑运行时。

## v0.14.27 — 2026-10-27

> 客户端 1.0.30：加固 TUN 的进程例外 —— 修「Clash 端口能连但所有境外都超时」。

### 现象

```
23:21:35 本机代理连不上，改走渡口：backend.accounts.hytale.com
23:22:03 本机代理连不上，改走渡口：sd-gl.xinqiucc.com
23:24:11 本机代理连不上，改走渡口：api.ipify.org
23:26:04 本机代理连不上，改走渡口：ab.chatgpt.com
23:29:53 本机代理连不上，改走渡口：registry.npmjs.org
```

外网全断。实测用户机器：

```
TcpClient 连 127.0.0.1:7897        → 成功
curl -x http://127.0.0.1:7897 ...  → CONNECT 200，然后 5 秒超时
渡口自己的 10801 → baidu           → 200（0.17s）
```

**Clash 的端口是活的、CONNECT 能成功，但数据出不去** —— 因为 Clash
连不上它的机场节点。

### 根因

渡口的 TUN 抢了默认路由，Clash 连机场的出站也被抓进 TUN。
本来靠 `PROCESS-NAME` 把翻墙客户端放行成 DIRECT，但：

- 规则里写的是 `clash-verge.exe` 这种**带扩展名**的形式，
  而 mihomo 不同版本对「进程名含不含 .exe」处理不一致；
- `find-process-mode` 没显式设置，默认的 `strict` 在 gvisor 栈下实测不可靠。

结果：`PROCESS-NAME` 规则在文件里存在，但**从不匹配**。于是：

```
Clash → 连机场 → 被自己的 TUN 抓走 → 判境外 → 交回 Clash → 再被抓 → …
```

死循环。表现就是「端口能连、CONNECT 200、然后一直等」。

### 改法

1. 顶层加 `find-process-mode: always`（强制启用进程查找）
2. `PROCESS-NAME` 两种写法都写：

```yaml
- PROCESS-NAME,verge-mihomo,DIRECT
- PROCESS-NAME,verge-mihomo.exe,DIRECT
```

名单从 9 条扩到 13 个进程 × 2 种写法（补了 clash / mihomo / nekoray / nekobox）。

### 仍然存在的风险（如实写下来）

`tun.dns-hijack: any:53` 会劫持**所有** 53 端口流量，包括 Clash 自己发的
DNS 查询。如果 Clash 的机场域名解析依赖系统 DNS，它可能拿到 fake-ip，
同样连不上 —— 这条路 `PROCESS-NAME` 管不到（DNS 劫持在规则匹配之前）。

本版先修进程例外（这是实测能确认的部分）。如果加装后仍然断，
下一步要动的是 DNS 劫持策略。

## v0.14.26 — 2026-10-27

> 客户端 1.0.29：**UDP 改成一个上游关联服务所有本地关联**。

### 用户看到的异常

```
23:01:54 UDP 关联 18 个（峰值 27），累计上下行包 58591 / 72243
23:06:21 UDP 关联失败：上游不支持或连不上 10.13.24.1:1080
23:06:25 UDP 关联失败：上游不支持或连不上 10.13.24.1:1080
```

关联数稳定了（1.0.28 的空闲回收生效，不再撞上限），但出现新的异常：
**偶发「上游连不上」**。

### 根因

每次本地关联都要**新建一条 TCP 控制连接**去远端做 ASSOCIATE。
峰值 27 个关联 = 27 条 TCP 连接 + 27 个远端 UDP socket，而且不断建/拆。
远端 `ferry-socks5-server.py` 是 Python 单进程 async，accept 一忙，
新的 ASSOCIATE 就超时 —— 就是那两条「上游连不上」。

### 改法

SOCKS5 的 UDP ASSOCIATE 设计上就是**一个关联承载所有目标**（每条数据报
自带目标地址）。所以本地不需要为每个客户端流各建一条上游：

```
改前：N 个客户端流 → N 条 TCP 控制连接 + N 个远端 UDP socket
改后：N 个客户端流 → 1 条 TCP 控制连接 + 1 个远端 UDP socket
```

回包分发：上游回来的包只带源地址，不知道给哪个客户端。
用「最近 5 秒发过包的所有 relay 各发一份」—— 应用会按 SOCKS5 头里的地址
自己过滤，不会串。拿少量冗余换实现简单，比维护完整映射表可靠。

### 途中踩的坑（自己写出来的）

第一版把共享的上游 UDP socket 绑在了 `IPAddress.Loopback` 上：

```csharp
us.Bind(new IPEndPoint(IPAddress.Loopback, 0));   // ← 错
```

源地址变成 127.0.0.1，发往远端 10.13.24.1 的包出不去、回包也收不到。
表现很迷惑：**关联建立成功、日志一切正常，但一个 DNS 应答都没有**
（测试里 0/6）。改成 `IPAddress.Any` 后 6/6。

### 验证

```
PASS  most associations opened                  （6 个客户端关联）
   associations that got a DNS answer: 6/6
PASS  UDP actually flows through the shared upstream
PASS  shared upstream control conn exists
   shared upstream endpoint: 10.13.24.1:65344
   'upstream ready' lines: 1                    ← 6 条流只建了 1 次
PASS  upstream was established exactly once for all 6 streams
   relays after close: 0
PASS  all client relays cleaned up
ALL PASS
```

生命周期回归：ALL PASS。

## v0.14.25 — 2026-10-27

> 客户端 1.0.28：**UDP 关联名额不回收 → 撞上限 → 语音/游戏全卡**。

### 我之前判断错了

上一版（1.0.27）把卡顿归给「每连接 3 个线程、298 线程拖慢整机」。
那个优化本身有用（线程确实降下来了），**但不是这次卡顿的原因**。

真正的证据一直在日志里：

```
22:38:06 UDP 关联 127.0.0.1:49713 → … ⇒ 上游 10.13.24.1:55862
22:38:06 UDP 关联数已达上限 32，拒绝新关联 (127.0.0.1:49725)
22:38:08 UDP 关联数已达上限 32，拒绝新关联 (127.0.0.1:62650)
22:38:09 本机代理连不上，改走渡口：badjs.weixinbridge.com
```

**UDP 关联撞上限被拒** → 语音（Oopz）、游戏（CS2）的 UDP 包发不出去
→ 只能靠重传 / 直接失败 → 卡顿、延迟飙升。

### 为什么名额不还

mihomo 给**每个 UDP 流**建一个 ASSOCIATE，用完**不关 TCP 控制连接**。
而我们的「关联结束」依赖那条控制连接断开：

```csharp
while (ns.Read(sink, 0, sink.Length) > 0) { }   // 它不关，就一直挂着
```

于是 32 个名额很快占满，之后所有新的 UDP 都被拒，应用重试、又产生更多
关联请求 —— 日志就开始刷屏。

### 三处改动

| 改动 | 说明 |
|---|---|
| **空闲回收** | 20 秒没有任何 UDP 包就主动关掉，把名额还回去。语音/游戏的流是持续的，不会被误杀；浏览器 QUIC 那种一次性的会被及时清掉 |
| **上限 32 → 96** | 每个关联本地只占 1 个 socket，真正的资源大头（远端 socket）靠空闲回收控制 |
| **日志降噪** | `UDP 关联`/`关联结束` 不再每条都写（一秒钟几条，界面全被刷掉），改成 60 秒汇总一行：当前关联数、峰值、累计上下行包数。失败和被拒仍然逐条写 |

### 验证

```
PASS  cap raised to 96 (was 32)
   opened 6 associations (control conns left open)   ← 模拟 mihomo 不关控制连接
PASS  they are counted as live
   waiting 26s for idle reclaim...
   live after idle: 0
PASS  idle associations were reclaimed even though TCP stayed open
PASS  no per-association log lines (noise removed)
ALL PASS
```

生命周期回归（含 `borrow-for-udp`）：ALL PASS。

### 教训

**「现象符合某个假设」不等于「那个假设是原因」。**
1.0.27 里我看到「298 线程」就直接认定是线程过多导致卡顿 —— 数字确实异常，
优化也确实有价值，但它解释不了「语音特别卡、游戏延迟跳」这种**特定流量**
的症状。日志里明明写着「关联数已达上限 32，拒绝新关联」，那才是直接原因。

排除故障的顺序应该是：**先看有没有明确的错误/拒绝记录，再看资源指标**。
资源指标异常往往只是伴随现象。

## v0.14.24 — 2026-10-27

> 客户端 1.0.27：**线程从「每连接 3 个」降到 1 个** —— 修「整个网速都很慢」。

### 现象

> 整个网速都很慢
> 之前一直都是没问题的。就刚刚突然卡了

查了一下用户机器上的进程：

```
渡口助手.exe   pid 29048
线程数   298
工作集   145 MB
累计 CPU 180 秒
```

298 个线程。算一下来源：

```
每个被代理的 TCP 连接 = HandleConn 线程 + 上下行各 1 个 copy 线程 = 3 个
浏览器 keep-alive 把连接挂很久
TUN 接管后**所有程序的所有连接**都进这里
→ 100 个连接 = 300 线程
```

线程一多，上下文切换 + 每个线程 1 MB 栈，整机都跟着慢。

### 为什么之前不慢

因为线程是**逐步累积**的：开一堆网页、看视频、语音、游戏，
keep-alive 连接一个个挂上来，每个贡献 3 个线程。到了 300 线程才明显。

### 修法：转发改成异步 IO

`copy` 原来是同步阻塞的 `src.Read()`，只能用专用线程顶着。
改成 `ReadAsync/WriteAsync` 之后，等数据时线程会还给系统（IOCP），
两个方向用一个 `Task.WhenAny` 收尾：

```
每个连接：3 线程 → 1 线程
100 个连接：300 → 100
```

代码里原来有一条注释警告「别用 Task 跑 copy」——那说的是**把同步阻塞代码
丢进线程池**（每秒只扩容 1~2 个线程，会积压）。这里是真异步，
等待期间不占线程，语义完全不同。注释已改写说明区别。

保留的行为不变：任一方向结束就收尾（HTTP 请求体传完不等 EOF）、64 KB 缓冲、
字节统计、3 秒收尾兜底、6 小时防半开。

### 验证

线程数（本机，60 条空闲连接）：

```
threads before: 7
threads with 60 idle conns: 67     ← delta = 60，即每连接 1 个
threads after closing: 7           ← 全部释放，没有泄漏
```

功能（真连渡口访问 www.baidu.com）：

```
PASS  socks handshake
PASS  socks connect success
   bytes received: 18159
PASS  got response body through the async pump
   concurrent ok: 20/20
PASS  at least 15 of 20 concurrent connections succeeded
ALL PASS
```

## v0.14.23 — 2026-10-27

> 客户端 1.0.26：**游戏 UDP 一直是断的** —— 修「打 CS2 延迟突然跳到 130ms」。

### 现象

> 刚刚打 cs2，游戏延迟波动突然跳到 130ms

而且界面显示「全局模式 已开启（UDP / 游戏都走渡口）」—— 看起来正好。

### 根因：UDP 走了一条根本不通的路

客户端生成的 TUN 配置（`%LOCALAPPDATA%\Ferry\tun-config.yaml`）：

```yaml
- name: ferry-udp
  type: socks5
  server: 10.13.24.1      # 远端服务器
  port: 1080              # 但远端根本不在 1080 监听 UDP
  udp: true
```

而 mihomo 的规则把所有 UDP 都交给它：

```yaml
- NETWORK,udp,ferry-udp-group
```

远端的 UDP 机制是：

```
先建一条 TCP 控制连接做 UDP ASSOCIATE
  → 服务端临时 new 一个随机 UDP socket（0.0.0.0:0）
  → 把那个随机端口放进 SOCKS5 应答的 BND 返回
  → 客户端往那个端口发 UDP
```

**1080 上只有 TCP 监听。** 所以 mihomo 发出的每一个 UDP 包都收到
ICMP port unreachable —— 游戏的每个包都在丢，只能靠重传，
表现就是延迟从几十毫秒突然跳到 130ms 以上。

### 修法

`ferry-udp` 改指向**本地代理**（`127.0.0.1:<socksPort>`）：

```yaml
- name: ferry-udp
  type: socks5
  server: 127.0.0.1
  port: 10801
  udp: true
```

本地代理的 `UdpAssociateCore` 已经实现了完整的 UDP ASSOCIATE：
先向上游要动态端口，再把本地 UDP 会话接上去（1.0.8 那轮修的就是这个）。

```
游戏 → TUN → mihomo → ferry-udp(127.0.0.1:10801)
                        → 本地代理 UDP ASSOCIATE
                        → 上游 10.13.24.1:1080 拿动态端口
                        → 远端 Python 中继 → 游戏服务器
```

### 同一个坑犯过两次

`mihomo/mihomo-keeper.ps1` 里的 `$UdpHost/$UdpPort` 在之前那轮已经改成了
`127.0.0.1`（同样是这个原因）。但**客户端自己生成 TUN 配置的那条路径没跟着改** ——
两处代码用不同的方式拼同一个上游地址，改一处漏一处。

（这正好是「两处配合的逻辑，改一处要回头看另一处」那条元教训的第二次发作。）

### 验证

```
PASS  ferry-udp points to 127.0.0.1 (not the remote host)
PASS  ferry-udp uses the local proxy port
PASS  udp enabled on the outbound
PASS  socks handshake ok
PASS  udp associate accepted
PASS  got UDP response through relay        ← 真的收到了 71 字节 DNS 应答
PASS  response is a DNS answer
ALL PASS
```

第二条不是「配置看起来对」，而是**真的发了一个 UDP DNS 查询并收到应答**。

## v0.14.22 — 2026-10-27

> 客户端 1.0.25：修「偶发性的小卡顿」—— 连不通的目标每次都要重等 8 秒。

### 现象

截图里的日志（判定是正确的）：

```
21:19:14 国内目标连不上，改走本机代理：182.50.10.149
21:19:42 国内目标连不上，改走本机代理：125.94.54.107
```

这两个 IP 确实在国内段（判定没错），问题是**渡口连不上它们**。
而 `ConnectSmart` 的超时是统一的 **8000ms**：

```csharp
var s1 = ConnectSocks(_socksPort, ..., timeoutMs);   // 等满 8 秒
if (s1 != null) return s1;
return ConnectSocks(_clashPort, ...);                 // 才轮到另一条路
```

一个页面上只要有一个资源命中这种目标，页面就卡 8 秒；而且**每次重连
都要再等一遍**。

### 三处改动

**① 国内目标走渡口只等 2.5 秒**

国内 RTT 普遍 < 100ms，建个 TCP 连 2.5 秒还没成，基本就是那个目标从出口
不可达。等 8 秒纯粹是浪费用户的时间。

**② 失败记忆（5 分钟）**

同一个 `host:port` 连渡口失败过，5 分钟内直接走 Clash，不再重试。
失败一次总比失败十次好。反向也加了（Clash 连不上 → 之后直接走渡口）。

**③ `ResolveCn` 改成三个 DNS 并发**

原来是串行：每个 1200ms 超时，解析不了就依次等满三个 —— 最坏 3.6 秒。
分流是**每个新域名**都要判一次的，这个等待直接变成卡顿。并发后最坏 1.2 秒。

### 为什么不做「并发竞速」

同时连两条路、谁先通用谁，能彻底消除等待。但：

- 每条连接多开一个 socket，浪费上游（渡口是按机器限速的）
- 两条都通时会随机选路，同一站点一会儿走渡口一会儿走 Clash，
  按 IP 绑定的登录态可能错乱
- 排查时更难解释「这条为什么走了另一条」

失败记忆 + 短超时能拿到大部分收益，代价小得多。

### 验证

```
PASS  domestic quick timeout is 2500ms
PASS  failure is remembered
PASS  different port is not affected
PASS  different route is not affected
PASS  second attempt is not slower than the first
PASS  failure table stays bounded
ALL PASS
```

场景矩阵 5 项、域名判定 22 项：均 ALL PASS（无回归）。

## v0.14.21 — 2026-10-27

> 客户端 1.0.24：分流的收尾优化 + 两处真 bug。新增 `docs/分流架构.md`。

### 一、新增技术文档 `docs/分流架构.md`

分流这块前后错了五次，每次错的方式都不一样，但**都没有留下「为什么这么设计」的
完整记录**，导致下一个版本很容易又改回去。补一份：

- 三层结构图（TUN / 本地代理 / 上游），各层只做什么、不做什么
- 判定真相表（IP / fake-ip / 域名 / IPv6 各按什么判）
- **三条硬约束**：TUN 下拿不到真实 DNS、必须 PROCESS-NAME 防环、分流只能做一次
- 六次尝试的演进史（每版当时的逻辑「看起来都对」）
- 判错代价对照表（为什么一律往「境外」错）
- 怎么验证、要跑哪些脚本

AGENTS.md 的文档索引和「五条硬规则」都已经指向它。

### 二、两个真 bug

**① 静态字段初始化顺序**（我在 1.0.24 自己引入的）

```csharp
static readonly HashSet<string> _cnDomainSet = BuildCnDomainSet();  // 先
static readonly string[] CnDomains = new string[] { ... };           // 后
```

C# 静态字段按**声明顺序**初始化，`BuildCnDomainSet()` 执行时 `CnDomains`
还是 null → 类型初始化抛异常。已经把数组挪到前面。
（这个坑在项目里记过一次 —— 静态字段顺序。）

**② 后缀匹配漏掉了完整域名**

第一版写成「只查最后一个点之后的部分」：

```csharp
int dot = h.LastIndexOf('.');
while (dot > 0) { if (set.Contains(h.Substring(dot + 1))) ... }
```

`baidu.com` 只会去查 `com`，**永远匹配不到 `baidu.com`**。
改成从左往右剥标签、每剥一次查一次表：

```
m701.music.126.net → music.126.net → 126.net（命中）
baidu.com          → 一次就命中
```

测试里那条 `PASS uppercase matches` 抓出来的 —— 如果没有那条断言，
这个 bug 会表现为「所有 .com 的国内站都判境外」。

### 三、优化

| 项 | 之前 | 现在 |
|---|---|---|
| 白名单匹配 | 每条连接线性扫 130 条 `EndsWith` | 哈希表，最多 8 次查找 |
| `LogOnce` 去重键 | 完整 host（`img1.a.com` / `img2.a.com` 各算一条，表刷满 500 就清空重刷） | 一级域名（`a.com`），同类问题只记一次 |
| `PreferIp` | 有个同值三元 `IsFakeIp(ip) ? host : host` + 一段死循环 | 清理，注释写清楚「TUN 下必然退回域名」 |
| 白名单条目 | 约 90 条 | 约 130 条（补了金山云/抖音视频/腾讯云 COS/小红书/拼多多/滴滴/WPS/飞书等） |

### 四、验证

```
域名判定（22 项）      ALL PASS
fake-ip 与 TUN 规则    ALL PASS
场景矩阵（5 场景）     ALL PASS
生命周期（22 项）      ALL PASS
```

发版前把生成的 `tun-config.yaml` 交给 mihomo 自己校验：
`test is successful`。

## v0.14.20 — 2026-10-27

> 客户端 1.0.23：**域名判定不再依赖 DNS** —— 修掉「转来转去」。

### 现象

1.0.22 的日志（用户截图）：

```
20:23:39 国内目标连不上，改走本机代理：114.28.148.16
20:23:49 国内目标连不上，改走本机代理：223.6.6.6
20:24:12 本机代理连不上，改走渡口：gateway.ooz.cn
20:24:13 本机代理连不上，改走渡口：m701.music.126.net
20:25:18 国内目标连不上，改走本机代理：223.5.5.5
```

两类错判，方向相反：

- **纯国内域名被判境外**（`m701.music.126.net` 是网易云音乐的 CDN）→ 交给
  Clash → Clash 连不上 → 回退渡口；
- **DNS 服务器的 IP 被判国内**（223.5.5.5 / 223.6.6.6 / 114.x）→ 走渡口 →
  远端连不上 53 端口 → 回退 Clash。

另一个可见的副作用：出口 IP 显示成了 `116.23.158.149`（电信），而真实借网
出口是 `218.107.0.47`（联通）—— 因为出口探测用的 `myip.ipip.net` 也被判成
境外，交给 Clash 了。

### 根因：这个假设在 TUN 下不成立

1.0.22 的修法是「过滤 fake-ip，再用 223.5.5.5 重新解析」。但：

```
TUN 的 dns-hijack: any:53
  → 客户端自己发的 DNS 查询（UDP 到 223.5.5.5）也被 mihomo 接管
  → 回来的还是 198.18.x.x（fake-ip）
  → 过滤掉 → 空 → 判「境外」
```

**只要 TUN 劫持着 53 端口，「本地解析出真实 IP」这件事就永远做不到。**
所以无论怎么调 ResolveLocal，域名判定都会全军覆没。

### 修法：域名按域名判

| 目标 | 怎么判 |
|---|---|
| IP 字面量 | 查 APNIC 段表（准确，不需要解析） |
| 域名 | 查内置的国内域名后缀表（`.cn` + 常见国内站点/CDN） |

纯字符串比较，微秒级，不碰 DNS，不受 TUN 影响。

判错的代价不对称，所以表只收「确定是国内」的：

- 国内域名判成境外 → 交给用户的 Clash，它的 GEOIP 规则会走直连，**能通**，多一跳；
- 境外域名判成国内 → 走渡口出口，**必然打不开**。

### 顺带

`ipip.net` / `3322.net` / `ip138.com` 加进白名单 —— 出口探测用的是
`myip.ipip.net`，它被判境外时，界面上显示的就不是借网出口，而是用户自己
Clash 的出口（这次就显示成了电信）。

### 验证

```
PASS  m701.music.126.net is domestic
PASS  m801.music.126.net is domestic
PASS  gateway.ooz.cn is domestic
PASS  music.163.com is domestic
PASS  www.baidu.com is domestic
PASS  api.weixin.qq.com is domestic
PASS  www.google.com is foreign
PASS  github.com is foreign
PASS  encrypted-tbn0.gstatic.com is foreign
PASS  notbaidu.com is NOT domestic (no dot boundary)
PASS  baidu.com.evil.com does not match
PASS  fake-ip is foreign
ALL PASS
```

## v0.14.19 — 2026-10-27

> 客户端 1.0.22：**修 1.0.21 引入的「全网变慢」**（国内也慢）。

### 现象

1.0.21 装上去之后：

> 为什么感觉网速明显变慢了，或者加载有点慢，不只是外网，国内网页也是

日志里满屏同一句话：

```
国内目标连不上，改走本机代理：www.google.com
国内目标连不上，改走本机代理：encrypted-tbn0.gstatic.com
国内目标连不上，改走本机代理：chatgpt.com
国内目标连不上，改走本机代理：ep2.adtrafficquality.google
```

也就是说：**每一个境外域名都被判成了国内**，先撞一次渡口（等超时）→
失败 → 再走 Clash。一个页面几十条连接，每条都白等一次超时。
国内网页也慢，是因为页面里嵌了大量境外资源（字体、统计、CDN）。

### 根因链（1.0.21 的错）

```csharp
// 1.0.21 的 IsDomestic
foreach (var a in addrs)
    if (IsFakeIp(a) || CnIpRanges.Contains(a)) return true;
```

TUN 开着 `enhanced-mode: fake-ip`（198.18.0.0/16），系统 DNS 被 mihomo 接管：

```
IsDomestic("www.google.com")
  → ResolveLocal → Dns.GetHostAddresses → 198.18.x.x（fake-ip）
  → IsFakeIp(a) == true → 判「国内」 → 走渡口 → 必然失败 → 回退 Clash
```

`IsFakeIp` 是 1.0.16 加的，本意是「别把假 IP 传给上游」，把它当「国内」是错的：
**fake-ip 里没有任何地理信息，它就是个占位符。**

同一个错误还有两处：

- 在 TUN 层用 `RULE-SET,cn`（1151 条 `IP-CIDR`）做分流 —— 但 fake-ip 下域名
  进 TUN 时是 198.18.x.x，要匹配 IP-CIDR 就得先解析，**每个新域名多一次 DNS
  查询**，而且用的是国内 DNS，墙外域名拿到的是污染地址，判定本身就是错的。
- `ConnectSmart` 的境外分支也走 `PreferIp`，把国内 DNS 的污染结果传给 Clash，
  Clash 就去连那个假 IP（明明它自己有干净的解析）。

### 修法

| 改动 | 为什么 |
|---|---|
| `ResolveLocal` 过滤 fake-ip；全被滤掉时改用 223.5.5.5 重新解析 | 系统 DNS 被接管时只能自己问 |
| `IsDomestic`：fake-ip 一律**不算**国内 | 占位符没有地理信息 |
| `ConnectSmart` 境外分支**传原始域名**，不 `PreferIp` | 污染地址传给 Clash 等于让它去连假 IP |
| 加 DNS 缓存（成功 300 秒 / 失败 30 秒） | 分流是每条连接都要判的，没缓存等于每条连接发一次 DNS 查询 |
| TUN 规则回到 `MATCH,ferry`，删掉 `RULE-SET,cn`、`clash-local`、`rule-providers` | 分流统一在本地代理做，那一层拿得到域名、有缓存 |
| 保留 `PROCESS-NAME,...,DIRECT` 9 条 | 翻墙客户端自己的出站要被放行，否则 Clash 连机场被 TUN 抓走成环 |

修完的分工：

```
浏览器 → (fake-ip) → TUN → mihomo（只转发，不看目标）
                            └─ 翻墙客户端进程 → DIRECT（防环）
                            └─ 其余 → MATCH,ferry → 本地代理
                                                          ↓
                                          本地代理：国内 → 渡口（传 IP）
                                                    境外 → Clash（传域名）
```

### 验证

```
PASS  fake-ip IP is NOT domestic
PASS  198.19.x fake-ip is NOT domestic
PASS  real CN ip is domestic
PASS  real foreign ip is not domestic
PASS  ResolveLocal never returns fake-ip
PASS  second lookup is cached (<5ms)
PASS  no RULE-SET (would need extra DNS per domain)
PASS  no rule-providers (no file dependency)
PASS  no clash-local proxy (split happens in local proxy)
PASS  keeps PROCESS-NAME loop guard
PASS  keeps LAN direct rules
ALL PASS
```

新配置交给 mihomo 自己校验：`test is successful`。

## v0.14.18 — 2026-10-27

> 客户端 1.0.21：**借网时境外能通了** —— TUN 也会把境外流量交给用户自己的 Clash。

### 现象

用户报障（附 Clash 节点延迟截图，全部 80~350ms 正常）：

> 我的 clash 节点延迟检测正常，但是借网状态下还是无法访问外网。

实测复现（同一台机器，两个方向对照）：

```
via ferry  baidu  -> 200
via ferry  google -> FAIL (502) 网关错误      ← 渡口出口到不了境外
via clash  baidu  -> 200
via clash  google -> 200                       ← Clash 完全正常
```

### 根因

两处叠加，都指向同一个设计错误：**把「TUN 存在」当成了「不需要分流」**。

1. `FerryRuntime.cs:210`

   ```csharp
   int clashPort = (AllowSplit && !_cfg.UseTun) ? DetectLocalProxy(...) : 0;
   ```

   1.0.16 引入的 `!_cfg.UseTun`：勾了「全局模式」就不检测本机代理，
   所有流量都往渡口送 —— 而渡口出口是大陆联通，境外到不了。

   当时的理由写的是「TUN 自己的规则已经分好流了，再分一次可能成环」。
   但那时 TUN 规则是 `MATCH,ferry`（**全部**走渡口），根本没有「分好流」。

2. `mihomo` 配置里同样只有 `MATCH,ferry`。

所以无论走系统代理还是走 TUN，境外流量都会被送进渡口出口，得到 502。

### 修法：两层分工，各管一段

| 层 | 国内 | 境外 |
|---|---|---|
| TUN（mihomo 规则） | `RULE-SET,cn` → ferry | `MATCH` → `clash-local`（127.0.0.1:7897） |
| 本地代理（ProxyServer） | `IsDomestic` → 渡口 | `ConnectSmart` → 本机 Clash |

关键的两个细节：

**① 不能用 `GEOIP,CN`** —— 它要下载 `geoip.metadb`：

```
can't download MMDB: Get "https://github.com/MetaCubeX/meta-rules-dat/...": EOF
```

借网场景（校园网禁网）下必然失败，规则加载不了 → TUN 起不来 → 用户直接断网。
改成 `rule-providers` + `type: file`，本地文件，零网络依赖：

- 客户端启动时用 `CnIpRanges.AsCidrRules()` 生成 `cn-cidr.txt`
- 5933 个 /16 合并成 **1151 条** `IP-CIDR`（相邻 /16 合成 /15、/14…）
- 生成失败就退回「全部走渡口」，绝不让 mihomo 因为缺文件拒绝启动

**② 必须排除 Clash 自己的出站** —— 否则成环：

```
用户请求 → TUN 抓走 → 规则判境外 → 交给 Clash
                                     ↓
                              Clash 自己连机场 → 又被 TUN 抓走 → 再交给 Clash …
```

用 `PROCESS-NAME` 把翻墙客户端本身放行成 DIRECT：

```yaml
- PROCESS-NAME,clash-verge.exe,DIRECT
- PROCESS-NAME,verge-mihomo.exe,DIRECT
- PROCESS-NAME,Clash for Windows.exe,DIRECT
- PROCESS-NAME,v2ray.exe,DIRECT
- PROCESS-NAME,sing-box.exe,DIRECT   # 等 9 条
```

**③ /16 位图的已知偏差**：APNIC 把 `1.1.0.0/16` 里的大部分分给了 CN，
于是 `1.1.1.1`（Cloudflare DNS）会被判成国内。在 mihomo 规则里加几条
更细的例外放在 `RULE-SET,cn` **之前**：

```yaml
- IP-CIDR,1.1.1.0/24,overseas,no-resolve
- IP-CIDR,8.8.8.0/24,overseas,no-resolve
- IP-CIDR,8.8.4.0/24,overseas,no-resolve
- IP-CIDR,9.9.9.0/24,overseas,no-resolve
```

### 验证

配置生成（测试机 10.113.66.6，13 项 ALL PASS）：

```
PASS  proxies has clash-local
PASS  clash-local points to 127.0.0.1
PASS  proxy-group overseas exists
PASS  rule-providers section exists
PASS  rule-provider reads local file (no network)
PASS  does not use GEOIP (needs downloading a database)
PASS  PROCESS-NAME exceptions to avoid loops
PASS  exception rule comes before RULE-SET,cn
PASS  MATCH comes after RULE-SET
PASS  clashPort=0 falls back to ferry-only
PASS  clashPort=0 keeps MATCH,ferry
```

`mihomo -t` 校验生成的配置：

```
configuration file ...\tun-config.yaml test is successful
```

**没有 Clash 的用户**：检测不到本机代理 → `clashPort = 0` → 生成旧规则
（`MATCH,ferry`，全部走渡口），行为与 1.0.20 完全一致。

## v0.14.17 — 2026-10-27

> 门户新增**绿色版（解压即用）**下载入口：完整功能、不需要安装。

### 为什么加

两件事凑到一起：

1. 有用户装不上，报 `错误 4551：应用程序控制策略已阻止此文件`
   —— Smart App Control / WDAC 禁止从 `%TEMP%` 执行程序，而 Inno 的
   SetupLdr 天生就是「解压 setup.tmp 到临时目录再执行」（改不掉，
   `UseSetupLdr=no` 会把单文件拆成三个，已试并回退）。
2. 用户明确要求：下载页要有一个「解压就是完整软件包、不需要安装」的版本。

### 做了什么

- `dist/Ferry-Portable-<ver>.zip`：`渡口助手.exe` + `wintun.dll` + `mihomo.exe`
  + `app.ico` + 使用说明，约 22.6MB。
- 构建脚本固化为 `tools/build-portable.py`（不再是一次性临时脚本）。
- 门户新增 `/download-portable`，自动取 dist 里版本号最大的那个 zip。
- 下载页第一屏就是三个并列按钮：**一键安装 / 下载安装包 .exe / 绿色版（解压即用）**，
  下面写明「三个入口功能完全一样」以及绿色版是什么。

### 绿色版能干什么（已在测试机 10.113.66.6 实测）

| 能力 | 验证方式 | 结果 |
|---|---|---|
| 借网 | 从空配置启动 → 按 IP 自服务端恢复激活 → 出口 IP 218.107.0.47 | ✓ |
| 全局模式（TUN） | 日志 `TUN 已启用（网卡 FerryTun）`、`全局转发已生效：所有程序…` | ✓ |
| 自动更新 | `/update` 全链路（下载 → 暂存 → 替换 → 重启） | ✓ |
| 开机自启 | 计划任务建出来且指向绿色版自身路径，`/remove-autostart` 能删干净 | ✓ |

### 一个必须写进说明的限制

绿色版把 `config.json` / `assistant.log` / `tun-config.yaml` 都写在**自己所在目录**。
所以：

- 解压到 `C:\Program Files\`（普通用户不可写）→ 每次打开都要重新激活、出错没日志、
  全局模式起不来，而用户完全看不出是权限问题；
- 直接从压缩包里双击运行 → 那是临时目录，关了就没了。

使用说明里把这条放在最前面，并推荐 `D:\渡口` 这类位置。

### 顺带

下载页那段「装不上、报错误 4551」的小字提示换成了正式按钮 + 一段解释，
因为原来放在角落没人看得到。

## v0.14.16 — 2026-10-27

> 客户端 1.0.20：**「关不掉全局模式」的死路**修掉了，另给装不上的人一条免安装的路。

### 一、全局模式按钮点下去是反的（严重）

TUN 被跳过时（另一个 TUN 在抢路由 / 缺 wintun / 不是管理员）：

- 界面写着 `全局模式  未启用：另一个 TUN 正在接管`
- 按钮写着 `关闭全局模式`

但代码判断的是 `tunUp`（TUN 是否**真的在跑**），不是用户的**意图**：

```csharp
bool tunUp = running && _rt.TunRunning;
if (tunUp) { /* 关闭 */ } else { /* 开启 */ }
```

TUN 没跑 → `tunUp` 是 false → 落进「开启」分支 → `_cfg.UseTun = true`（本来就是
true）→ 重启借网 → 还是起不来。**点一次重启一次，永远关不掉。**

修法：判据改成 `_cfg.UseTun`（用户意图）。界面写「关闭」就真的关。

### 二、TUN 起不来时的降级

原来 `UseTun = true` 就把分流整个关掉（`AllowSplit && !_cfg.UseTun`）。
于是 TUN 起不来的场景下，境内外**全部**走渡口 —— 境外必然打不开，而且没提示。

现在：TUN 被跳过时，如果用户有本机代理，自动降级成「分流模式」
（国内走渡口、境外交给它），并在日志里写清楚。

### 三、TUN 跑着的时候不再动系统代理

`RefreshUi` 里那段「系统代理指错地方就纠正」在 TUN 模式下毫无意义
（TUN 抢的是默认路由，系统代理指向谁都不影响流量），但有副作用：
用户开 Clash 会被改回来，改 3 次后转 `_nodeMode`，界面变成「系统代理未接管」,
可 TUN 明明还在跑 —— **显示和事实不符**。现在 TUN 在跑就跳过纠正。

### 四、火绒误报的进一步处理

1.0.18 已经把 54KB 的 base64 压到 1.4KB（exe 325KB → 152KB），但
`Convert.FromBase64String` + `DeflateStream` 解压出一张表的**代码形状**
本身就是「内嵌加密载荷」的典型特征。

改成最朴素的表示：8192 字节位图 = 2048 个 `uint` 常量字面量。
没有 base64、没有解压、没有反射，就是一串普通数字。exe 158,720 字节。

### 五、装不上的人（错误 4551）

有用户报：

```
无法执行临时目录中的文件。安装程序已中止。
错误 4551: 应用程序控制策略已阻止此文件。
```

这不是杀软报毒，是 Windows 的 **智能应用控制 / WDAC / AppLocker** 禁止
「从临时目录执行程序」。而 Inno 的 SetupLdr 天生就是：
`Setup.exe` → 解压 `setup.tmp` 到 `%TEMP%\is-XXXXX.tmp\` → 执行它。

试过 `UseSetupLdr=no`：能绕开临时目录，但会把单文件拆成
`Setup.exe` + 2 个 `.bin`（17.7MB 那种），分发更麻烦 —— 已试并回退。

所以加了第二条路：**免安装 ZIP**（`dist/Ferry-Portable-<ver>.zip`），
门户新增 `/download-portable`，下载页顶部和 FAQ 都给了入口。
解压到任意目录双击即用，完全不经过 `%TEMP%`。

### 验证

- 隐患扫描 9 项 ALL PASS（含「TUN 起不来时借网整体不失败」「分流与 TUN 互斥」）
- 国内段表 12 项 ALL PASS（改了位图打包方式后重验）
- exe 里已无长 base64 串（扫描 `[A-Za-z0-9+/]{200,}` = 0 处）

## v0.14.16 — 2026-10-27

> 客户端 1.0.19：**全局模式真正能开了**。之前它被「系统代理归谁管」这件事挡着，
> 等于把核心功能废掉了。

### 我错在哪

用户原话：

> 我需要渡口做借网，方便断网的时候使用完整的功能，全局转发才能实现这一点，
> 你把它交给代理软件管理，我这里完全判断不了全局是否生效。

1.0.9 起我引入了一个 `_nodeMode`：检测到系统代理被 Clash 占着，就退让成
「只提供本地端口」，并且顺手把 TUN 也禁了：

```csharp
_nodeMode = OtherProxyActive();   // Clash 占着系统代理 → 节点模式
rt.AllowTun = !_nodeMode;         // 节点模式 → 不许开 TUN
```

结果是：**只要用户开着 Clash，渡口就永远不能全局接管**。
界面就一直停在「全局模式 正在启用…」。

**错在把两个不同层面的东西绑在一起**：

| | 管什么 | 冲突对象 |
|---|---|---|
| 系统代理 | 应用层，只有认代理的程序走 | 另一个也写系统代理的程序（Clash / 加速器） |
| TUN | 网卡层，抢默认路由，所有程序都走 | 另一个也在抢默认路由的 TUN |

Clash 只开系统代理（`tun.enable: false`）时，和渡口的 TUN **完全不冲突**。
我却把「系统代理被占」当成了「不能开 TUN」。

### 现在的行为

**全局模式（勾了就是最高优先级）**：

```
用户勾了全局模式 + 是管理员
  → 不管系统代理归谁，都接管（原值备份，停止时还原）
  → 开 TUN（除非检测到另一个 TUN 真的在抢默认路由）
  → 所有程序（浏览器 / 游戏 / UDP / 不认代理的软件）都走渡口
```

**普通借网（没勾全局模式）**：维持原样 —— 系统代理被占就退让成节点模式。

### 另外一个关键修正：怎么判断「别的 TUN 在抢路由」

第一版按网卡名字判（带 `tunnel` 就算），测试机上一跑就返回 `etgame`
（游戏加速器的虚拟网卡）。但加速器通常只接管特定进程，**不抢默认路由** ——
按名字判会把「开着加速器」误判成「不能开全局」，又变成卡住。

改成问系统一个实在的问题：

```csharp
GetBestInterface(8.8.8.8)   // 去公网实际会走哪块网卡？
```

那块网卡是 TUN 类才拦；还是物理网卡就说明没被抢。测试机验证：
`OtherTunAdapter() = (none)`（etgame 挂着但没抢默认路由，正确放过）。

### 界面能明确看到状态了

| 情况 | 显示 |
|---|---|
| TUN 真的在跑 | `全局模式 已开启（UDP / 游戏都走渡口）` |
| 被另一个 TUN 挡住 | `全局模式 未启用：另一个 TUN 正在接管（xxx）` |
| 启动失败 | `全局模式 启用失败：<具体原因>` |
| 没勾 | `全局模式 未开启` |

日志也会写清楚：`全局转发已生效：所有程序（含游戏 / UDP）都走渡口`。

### 验证

```
PASS  AllowTun no longer bound to node mode
PASS  AllowTun is unconditionally true
PASS  global mode takes priority over node mode
PASS  TunSkipReason exposed for UI
PASS  runtime checks for other TUN
PASS  old misleading message removed
PASS  runtime starts with UseTun=true + AllowTun=false
PASS  TUN did not start when disallowed
PASS  TunSkipped is true
PASS  skip reason is non-empty
ALL PASS
```

生命周期 22 项 + 分流 5 项回归全过。

### 关于火绒

1.0.18 已经把 exe 从 325 KB 瘦回 152 KB（那段 54 KB 的 base64 是误判源头）。
1.0.19 是 153 KB，和 1.0.12（151 KB，从没被报过）基本一致。

## v0.14.15 — 2026-10-27

> 客户端 1.0.18：**修掉火绒误报**（exe 从 325KB 瘦回 152KB）+ 全局模式不再卡住。

### 一、火绒把客户端当木马删了

用户反馈的日志：

```
病毒名称：HEUR:VirTool/MSIL.Obfuscator.gen!BA
病毒路径：C:\Users\ASUS\AppData\Local\Ferry\渡口助手.exe.new
操作结果：已处理，删除文件
```

`HEUR:` 前缀说明是**启发式**判定，不是特征库命中。触发它的组合是：

1. exe 里有一大段 base64 字符串（1.0.13 把 4116 个 IP 段写成 54 KB base64）
2. 这个 exe 会**下载另一个 exe 并替换自己**（自动更新）

「大段 base64 + 自我替换」是教科书级的 MSIL 混淆器特征。对照：

| 版本 | exe 大小 | 火绒 |
|---|---|---|
| 1.0.12 | 151,040 | 正常 |
| 1.0.13 | 325,120（+174KB 的 base64） | **报毒删除** |
| 1.0.18 | 152,576 | 应该正常 |

**修法**：4116 个 `(start,end)` 对 → 65536 个 `/16` 段的**位图**（8192 字节），
Deflate 压缩后只剩 **1062 字节**，base64 1416 字符，拆成 12 个小字符串常量。

精度代价：`/16` 粒度意味着同一段里有一个国内 IP，整段都算国内。
对「决定走哪条路」这个用途够用；少数精度损失会造成实际影响的地址
（1.1.1.1、8.8.8.8、Cloudflare 各段）单独列成例外。

### 二、全局模式一直卡在「正在启用…」

状态矩阵里这个分支：

```csharp
else if (wantTun && runtimeUp && !tunUp) {
    var terr = rt.TunError;
    if (terr 非空) → 「启用失败：xxx」
    else           → 「正在启用…」
}
```

但 `AllowTun = false` 时 TUN 是**被主动跳过**的 —— 没报错、TunError 是空，
于是永远显示「正在启用…」。新增 `FerryRuntime.TunSkipped`，
跳过时显示「未接管（系统代理归其它程序）」。

### 三、去掉 Clash 配置清理

1.0.15 加的 `ClashCleanup` 去掉调用 —— 只有极少数人导过那份配置，
没必要每次启动都动别人的文件。（`Merge.yaml` 已确认清干净：93 字节原始内容。）

### 四、两个自己踩的坑（写在这里提醒下次）

**① C# 静态字段按声明顺序初始化**：

```csharp
static readonly byte[] _bitmap = Build();   // 先执行
static readonly string[] _chunks = {...};   // 后执行 —— Build() 里 _chunks 还是 null
```

`Build()` 里访问 `_chunks` 拿到 null → 走 catch 返回全 0 位图 →
**所有 IP 都判成境外**。改成 `Lazy<byte[]>` 彻底消除顺序依赖。

**② Python 的 `zlib.compress` 不是裸 deflate**：它产出 zlib 格式（带 2 字节头
+ adler32），而 .NET 的 `DeflateStream` 期待**裸 deflate** —— 必须用 `wbits=-15`。
否则解码失败，又走 catch 静默返回全 0。

两个坑叠在一起的表现完全一样（全判境外），但根因完全不同。

### 验证

```
PASS  223.5.5.5 / 114.114.114.114 / 183.2.172.177 ... is CN      (7 项)
PASS  104.16.132.229 / 8.8.8.8 / 1.1.1.1 / 198.18.0.1 ... foreign (8 项)
PASS  10.13.24.1 / 127.0.0.1 / 192.168.1.1 / 172.16.0.1 (private) is CN
ALL PASS
```

新增 `tools/verify-cnranges.cs`。

## v0.14.14 — 2026-10-27

> 客户端 1.0.17：**修「全局模式」的两个 bug** —— 点了没反应 + 三处说法互相矛盾。

### Bug 1：点「关闭全局模式」只回到直连，再点没反应

代码里是这么写的：

```csharp
if (running) { DoStop(); DoStart(); return; }
```

而 `DoStop()` 第一行就是 `if (_busy) return;`，然后自己 `SetBusy(true)`，
后台线程做完才 `SetBusy(false)`。

所以同步连调时：

```
DoStop()  → _busy = true，起后台线程
DoStart() → 立刻进来，_busy 还是 true → return，什么都不做
```

表现就是「停了但没重启」→ 回到直连。再点「开启全局模式」还是同一个问题。

**修法**：加 `_pendingRestart` 标志，在 `DoStop` 完成后的 UI 回调里再调 `DoStart()`。

### Bug 2：显示「已开启」又说「未启用」，三处说法打架

节点模式下 `AllowTun = false`（1.0.16 加的，避免和 Clash 抢 TUN），
所以 TUN 永远起不来。但界面上：

| 位置 | 显示 |
|---|---|
| 全局模式行 | 「已开启 · 下次借网时生效」/「正在启用…」 |
| 大状态描述 | 「已借网，但全局接管（TUN）未启用」 |
| 按钮 | 「关闭全局模式」 |

三处互相矛盾 —— 用户当然会问「到底开没开」。

**修法**：节点模式下点「开启全局模式」= 用户明确要求接管，先弹一句
「现在有别的程序在管理系统代理，接管后它会暂时不生效，确定吗？」，
确认后设 `_forceTakeover = true`，`DoStart` 不再判定为节点模式，
真正抢回系统代理并启用 TUN。

关掉全局模式时 `_forceTakeover = false`，让位回去。

### 验证

测试机 8 项断言全过：

```
PASS  _pendingRestart field exists
PASS  _forceTakeover field exists
PASS  no synchronous DoStop();DoStart(); left (found 0)
PASS  SetBusy exists (the reentrancy guard that caused bug 1)
PASS  node mode: TUN not started (expected)
ALL PASS
```

新增 `tools/verify-globalmode.cs`。

### 教训（已写进踩坑 71）

**`SetBusy` 这种「防重入锁」会把「顺序调用两个异步方法」变成静默失败。**
`DoStop(); DoStart();` 看起来天经地义，实际第二个必然被吞掉，而且**不报错**。
凡是「先停再起」的地方，都要用回调/标志衔接，不能同步连调。

## v0.14.13 — 2026-10-27

> 客户端 1.0.16：**修「开了借网反而上不了网」** —— 1.0.13 的分流和 TUN 撞了。

### 根因链（完整）

```
TUN 启用 → mihomo 用 fake-ip 接管系统 DNS（198.18.0.0/15）
        → 域名解析成 198.18.x.x
        → 1.0.13 的 IsDomestic 拿这个假 IP 查 APNIC 的 CN 段表
        → 198.18.x.x 当然不在里面 → 判成「境外」
        → PreferIp 把 198.18.x.x 传给远端 10.13.24.1:1080
        → 远端连这个不存在的地址 → 失败
        → 国内网站全部打不开
```

1.0.13 之前是「直接把域名交给上游」，所以没这个问题；加了「本地解析」之后才撞上。

### 修法一：识别 fake-ip

`198.18.0.0/15` 是 mihomo / Clash 的 fake-ip 专用段。见到它：

- `IsDomestic` → 当**国内**处理（TUN 已经决定借网了，送来的流量就是要走渡口）
- `PreferIp` → **不要**把假 IP 传给上游，保留原域名

### 修法二：节点模式下不启用 TUN

日志里这两个是矛盾的：

```
16:42:47 系统代理已由其它程序管理，本次不接管（只提供本地端口）
16:42:49 TUN 已启用（网卡 FerryTun）—— 全局接管生效
```

- 节点模式的语义 = 「我只开一个端口，你把流量送过来」
- TUN 的语义 = 「我抢默认路由，所有流量归我」

同时启用会和用户本来开着的 Clash/加速器 TUN 抢路由，而且让「不接管系统代理」
那句日志变成谎话。新增 `FerryRuntime.AllowTun`，节点模式下设 false。

### 顺带：托盘图标

`app.ico` 一直没被打进安装包（安装目录里没有这个文件），所以托盘只能退回
`SystemIcons.Application`。现在补进 `[Files]`。

### 验证

测试机 17 项断言全过：

```
PASS  198.18.0.1 is fake-ip
PASS  183.2.172.177 is NOT fake-ip
PASS  IsDomestic(198.18.0.1) == true (fake-ip treated as domestic)
PASS  PreferIp keeps fake-ip as-is
PASS  FerryRuntime.AllowTun exists
PASS  TUN NOT running (AllowTun=false)
ALL PASS
```

### 教训（已写进踩坑 69）

**「本地解析域名」这个改动，前提是「本地的解析结果是真的」。**
TUN + fake-ip 打破了这个前提 —— 解析出来的是一串内部编号，不是地址。
凡是拿 DNS 结果做判断的代码，都要先问一句：这结果可能是 fake-ip 吗？

## v0.14.12 — 2026-10-27

> 客户端 1.0.15：**修一个我自己造成的严重故障** —— 国内网站全打不开。

### 故障经过

用户报「只能访问外网，无法访问国内的直连网站」。查下来是我的责任：

1.0.9~1.0.12 那几版会往 Clash Verge 的 `profiles\Merge.yaml` 写：

```yaml
prepend-rules:
  - GEOIP,CN,国内出口
```

这条规则排在**所有规则最前面**，把国内流量全部劫持到「国内出口」组 →
`fallback [渡口, DIRECT]` → **渡口没借网时那个端口是空的** → 国内站全死。

外网能开是因为走的是机场节点，不受这条规则影响 —— 所以现象看起来像
「只能上外网」，实际是国内全断。

### 修法（两层）

**① 客户端启动时主动清理**（`ClashCleanup.CleanLegacy`）

1.0.13 起不再写配置文件了，但**已经写进去的不会自己消失**。现在启动时检查
Verge 系的 `Merge.yaml`，发现带我们标记的段落就切掉（先备份 `.bak-ferry`），
然后提示「请重载一次订阅」。

只删带 `# ── 渡口（由渡口助手…` 标记的那一段，用户自己的配置一个字不动。

**② 主界面不再明牌**

原来主界面写着「节点模式 · 在 Clash / 加速器里添加 SOCKS5 127.0.0.1:10801」。
这台机器可能不是本人的，界面上不该出现「怎么用它翻墙」的字眼。

改成中性的：

```
本地端口 10800 / 10801 · 系统代理未接管
```

需要细节的人去设置里点 🐱（那里有完整说明，且用词也改成了「当成一个出站节点」，
不提任何与翻墙有关的词）。

日志里的「节点模式」也换成了「系统代理已由其它程序管理，本次不接管」。

### 验证

测试机 10 项断言全过：

```
PASS  clean file -> no change
PASS  dirty file -> cleaned
PASS  ferry block removed
PASS  hijacking rule removed
PASS  user content preserved
PASS  backup created
PASS  second call is a no-op
PASS  no Merge.yaml -> no throw
```

### 教训（已写进踩坑 66 的补充）

**「写别人的配置文件」这个决定，代价不只是「可能写错」——而是「写对了也会留下后遗症」。**
这次留下的规则在 4 个版本之后还在生效，而且用户完全不知道是国内哪一环坏了。

## v0.14.11 — 2026-10-27

> 客户端 1.0.14：**设置里点「检查更新」就能直接更新完**，不用再回主界面找按钮。

### 之前的交互是不合理的

在设置窗点「检查更新」→ 发现新版本 → 弹框说「请在主界面点「更新 1.0.13」按钮」。

问题是：

1. 用户得关掉设置窗、切到主界面、找到那个按钮、再点一次 —— 明明已经在更新流程里了；
2. 更糟的是**主界面那个按钮不一定在**。它只在「检查更新跑过一次并发现新版」时才显示，
   从设置里知道有新版之后回主界面，很可能什么都看不到，以为没更新。

### 现在

`SettingsWindow` 多接一个 `Action<string> onUpdate`（主窗口的 `DoUpdate`），
用户点「是」就直接开始更新。更新本来就在后台线程跑，设置窗关掉不影响。

顺手修的两个小问题：

- **更新前先落盘设置**：更新会让进程退出、新版本重读配置文件，
  用户在设置窗刚改的选项不保存就丢了（`SaveSettingsOnly`）。
- **连不上门户时说清楚原因**：以前只有一句「拿不到版本信息」，
  现在会说「连不上门户（地址）…如果正在借网，先断开再试」。

### 验证

测试机（10.113.66.6）7 项断言全过：

```
PASS  portal reachable
PASS  latest version parsed
PASS  compareVer sees newer / equal / older
PASS  old ctor (4 args) still exists        ← 向后兼容
PASS  new ctor (5 args, onUpdate) exists
PASS  window constructed with onUpdate
```

界面用 `tools/render-ui.ps1` 渲染确认没有布局变化。新增回归工具 `tools/verify-updatecheck.cs`。

## v0.14.10 — 2026-10-27

> 客户端 1.0.13 收尾：**分流在各种切换下都验过了**，顺带修掉三个隐患。

### 一、修掉三个隐患

**① 没装 Clash 的用户：域名没走本地解析**

`ConnectSmart` 里「没有别的代理」那条分支直接传了原始域名给上游 —— 而远端
（pcs）的 DNS 被污染，等于让上游拿假地址去连。现在所有分支都先 `PreferIp`。

**② 节点模式下会成环**

节点模式的含义是「用户把渡口当成一个节点加进他自己的 Clash 了」，
流量是 Clash 主动送过来的。如果这时还按「境外」把流量送回 Clash，
就是 `Clash → 渡口 → Clash` 的死循环。

修法：`FerryRuntime.AllowSplit`，节点模式下设 false，一个都不分流。

**③ 分流日志会刷屏**

分流是**每条连接**都判的，浏览器打开一个页面几十条连接。代理挂掉时如果每条
都写一行，日志几分钟就几 MB，真正的错误反被冲掉。改成 `LogOnce` 按目标去重。

### 二、场景验证（测试机 10.113.66.6，真实借网）

| 场景 | 期望 | 结果 |
|---|---|---|
| 接管模式 + 有本机代理 | 国内走渡口、境外走本机代理 | ✅ |
| 接管模式 + 没有本机代理 | 全走渡口，不抛异常 | ✅ |
| 节点模式（AllowSplit=false） | 一个都不分流，不成环 | ✅ |
| 本机代理中途挂掉 | 境外回落渡口，不抛异常 | ✅ |
| `DetectLocalProxy` | 能找到、能排除自己 | ✅ |

```
PASS  domestic -> ferry (local proxy untouched)
PASS  foreign -> local proxy (hits=1)
PASS  no exception when local proxy is gone (rep=5)
PASS  node mode: foreign NOT sent back to local proxy (no loop)
PASS  no proxy: domestic still works through ferry (rep=0)
ALL PASS
```

生命周期回归 22 项同时全过（借网/直连反复切换、Clash 开关、UDP、残局清理）。

### 三、一个明确的边界

**UDP 不分流**。SOCKS5 的 UDP ASSOCIATE 是「一个关联承载所有目标」
（每条数据报自己带目标地址），建立关联时根本不知道后面会发给谁，
所以只能选一条上游 —— 选渡口（用户要的是「借网打游戏」，那是国内游戏服务器）。

境外 UDP 走不通。但渡口本来就不做翻墙，这是明确边界，不是缺陷。

### 四、回归工具

- `tools/verify-scenarios.cs`：上面 5 个场景
- `tools/verify-split.cs`：分流基础行为（国内/境外/域名本地解析）
- `tools/verify-lifecycle.cs`：借网生命周期 22 项

## v0.14.9 — 2026-10-27

> 客户端 1.0.13：**借网时不再和 Clash 打架** —— 国内走渡口，境外交给你自己的代理。

### 先说结论：之前的方案是错的

1.0.9 起我一直想让渡口「自动把节点写进 Clash 配置」，为此适配了 Verge 的
`Merge.yaml`、CFW 的 `parsers`，甚至研究过绿色版的路径问题。**这条路本身走不通**：
CFW 有解压即用的绿色版，配置目录不固定；每个用户一个客户端，教不明白；
更糟的是写错会把人家的翻墙配置弄坏。

而且它解决的是错的问题：用户既然装了 Clash，他要的是「借网之后还能翻墙」，
不是「在 Clash 里多一个节点」。

### 现在的做法：渡口自己分流

```
浏览器/系统 → 渡口本地代理 → 判断目标
                              ├─ 国内 IP  → 渡口出口（pcs）→ 公网
                              └─ 境外 IP  → 你本机的 Clash(7897 等) → 你的机场
```

用户什么都不用配。Clash 照常开着，系统代理被渡口接管，但**只有国内流量走渡口**。

### 三个技术点（都是实测撞出来的）

**① 国内 IP 段表用 APNIC 权威数据**

之前是「猜几个大厂 IP 段」，非常不可靠。现在用 APNIC `delegated-apnic-latest`
的 CN 段：4116 个段、覆盖 3.4 亿 IPv4，以 base64 编译进 exe（约 44 KB），
查询是二分。重新生成：`python tools/gen-cn-ranges.py`。

**② 域名必须在客户端解析，不能交给上游**

pcs 的 DNS 被污染：

```
www.google.com    → 31.13.92.37     ← Facebook 的 IP
www.wikipedia.org → 199.59.148.229  ← Twitter 的 IP
```

把域名丢给上游解析，等于让它拿着假地址去连。现在客户端先本地解析成 IP 再传给
上游（`PreferIp`）；解析不出来才退回域名（有些站只认 SNI）。

**③ 判不准就当境外**

判错的代价不对称：国内站判成境外只是多绕一跳；墙外站判成国内则永远打不开，
而且没有任何报错。所以只有「明确落在 CN 段里」才算国内。

### 实测（测试机 10.113.66.6，真实借网）

```
PASS  baidu ip is CN
PASS  twitter ip is foreign
PASS  domestic did NOT touch local proxy (hits=0)
PASS  foreign went to local proxy   targets = 104.244.42.197
PASS  domain was resolved locally (upstream saw an IP)
ALL PASS
```

生命周期回归 22 项也全过（借网/直连反复切换、Clash 开关、UDP、残局清理）。

### 关于「翻墙」这件事

渡口**不提供翻墙能力**：它只把「境外目标」的字节流转给你自己已经在用的代理，
翻墙能力来自你的机场、你的订阅、你的流量账。渡口不知道也不关心通道里是什么。
这也正是取消服务端 `--clash-proxy` 旁路的原因（1.0.8 起默认 `off`）。

### 设置里的小猫按钮

不再写任何配置文件，只显示一段可复制的节点配置。99% 的人用不到 —— 因为分流是自动的。

## v0.14.8 — 2026-10-27

> 客户端 1.0.12：**修正 Clash for Windows 的接入方式**（1.0.11 那个写错了地方）。

### 1.0.11 错在哪

1.0.11 给 CFW 写的是 `parserserry.js` + 在 `profiles.yaml` 里挂 `parsers: [ferry]`。
这是照着 Verge 的思路猜的 —— **CFW 没有这种「每个订阅一个 parser 文件」的机制**。
它把预处理脚本存在 `cfw-settings.yaml` 的 `parsers:` 段里（Settings → Profiles → Parsers
那个文本框），条目形如：

```yaml
parsers:
  - reg: ^.*$
    code: |
      module.exports.parse = async (raw, { yaml }, info) => { ... return yaml.stringify(cfg); }
```

所以用户点了按钮什么也没发生。

### 1.0.12 的做法（双保险）

| 写什么 | 作用 |
|---|---|
| `%USERPROFILE%\.config\clash\config.yaml` | **立刻生效**：在 proxies / proxy-groups / rules 里插入渡口节点、`国内出口` 组、`GEOIP,CN` 规则；原文件备份成 `.bak-ferry` |
| `%USERPROFILE%\.config\clash\cfw-settings.yaml` | **持久**：追加一段 `parsers:`，CFW 每次加载订阅都会重新加上节点 |

为什么两个都要：`config.yaml` 是 CFW 从订阅生成的，订阅一更新就覆盖；Parser 是它
官方的扩展点，能扛住更新。只写 Parser 的话，用户点完必须等下一次订阅刷新才见效；
只写 config.yaml 的话，改完能用但过几天又没了。

安全边界：

- `config.yaml` 里找不到 `proxies:` 段时**不动它**（结构不同就交回手动说明，不硬写）
- `cfw-settings.yaml` 里**已有** `parsers:` 段时不动（用户可能自己写了预处理脚本，
  覆盖掉等于毁了他的配置），只提示去手动合并
- 两个文件都先备份（`.bak-ferry`）
- 重复点：`config.yaml` 里已有「渡口」节点就直接返回「已经有」，不重复插

### 验证（这次是跑起来验的）

1. **C# 侧 15 项断言**（测试机造真实结构的 config.yaml + profiles.yaml）：
   节点/组/规则都插对、原有 proxies 与 rules 保留、规则插在最前、备份生成、
   cfw-settings.yaml 写出 `module.exports.parse`、第二次调用不重复插。

2. **JS 侧用 node 真跑了一遍**（这是 1.0.11 没做的）：
   把生成的 Parser 抽出来，喂一份带 proxies/规则的最小配置，
   确认 `渡口` 节点（port 10800 / udp true）、`国内出口` fallback 组、
   `GEOIP,CN` 规则都进得去，原有内容不丢，重复调用只加一次。

### 仍然不确定的一件事

`cfw-settings.yaml` 这个**存储位置**是从社区资料推断的（CFW 已停更、作者删库）。
`config.yaml` 那条路是确定的（CFW 的运行配置就是它）。所以就算 Parser 没被读到，
用户重启 CFW 也能用 —— 只是订阅更新后需要再点一次按钮。

## v0.14.7 — 2026-10-27

> 客户端 1.0.11：**Clash for Windows 用户也能一键接入了**。

有用户反馈：用 Clash for Windows（CFW）时，小猫按钮不识别配置、什么也没做。

原因是 1.0.9 只认 Verge 系（Clash Verge / Rev / Nyanpasu）的 `Merge.yaml`，
CFW 没有这个机制 —— 它走的是另一套：**Parser**（一个 JS 脚本，订阅加载时被调用，
在内存里改 proxies/groups/rules，订阅更新也不会丢）。

### 现在的行为

| 你的客户端 | 小猫按钮做什么 |
|---|---|
| Clash Verge / Rev / Nyanpasu | 写 `profiles\Merge.yaml`（1.0.9 起） |
| **Clash for Windows** | 写 `%USERPROFILE%\.config\clash\parserserry.js`，并在 `profiles.yaml` 的每个订阅条目上挂 `parsers: [ferry]` |
| 其它 / 没装 | 不写任何文件，只弹一份按客户端分类的手动说明 |

CFW 的 `profiles.yaml` 是它自己的状态文件，改坏了 CFW 起不来，所以：

- 动之前先备份成 `profiles.yaml.bak-ferry`
- 只做最小文本插入（在条目末尾加两行 `parsers:` / `- ferry`）
- 已经有 `parsers` 的条目跳过，重复点不会重复挂
- 一个条目都没挂上时，返回的是「Parser 已写好，但没找到可挂的订阅」+ 手动步骤

`Country.mmdb` 那种「装过但没配置」的残留目录会被忽略，不会被当成 CFW。

### 验证

测试机（10.113.66.6）造了两条订阅的 profiles.yaml，8 项断言全过：

```
PASS  parser js written
PASS  js carries the port
PASS  js adds the ferry node
PASS  profiles.yaml patched
PASS  both profiles got ferry (got 2)
PASS  backup created
PASS  second call does not duplicate
```

**未验证**：真实 CFW 客户端加载这个 Parser 的效果（测试机没装 CFW）。
Parser 的接口按 CFW 官方约定写（`function parse(proxies, groups, rules)`），
如果某个版本的 CFW 报 Parser 错误，把报错发出来再调。

## v0.14.6 — 2026-10-27

> 客户端 1.0.10：一次针对「潜在 bug」的复查，修掉 5 个还没被踩到的问题。

### 1. PAC 模式会被误判成「没人占系统代理」（真 bug）

`OtherProxyActive()` 只读了 `ProxyServer`。而 Clash 和一部分加速器用的是**自动配置脚本**
（`AutoConfigURL` + `ProxyEnable=1`），这时 `ProxyServer` 是**空的** —— 于是被判成
「没有别人占用」，渡口照样去写系统代理，把用户的 PAC 覆盖掉，规则全失效。

现在先查 PAC：见到 `AutoConfigURL` 就当「系统代理归别人」，直接进节点模式。

### 2. 端口被占顺延后，写进 Clash 的是个死端口（真 bug）

配置里写的是 10800，但 `FindFree` 在端口被占用时会往后顺延（最多 40 个）。
之前小猫按钮把 `_cfg.SocksPort`（配置值）写进 Clash，实际监听的可能是 10801 ——
Clash 连一个没人听的端口，表现为「节点超时」。

现在写入前先探一遍：从配置端口往后找**第一个真的在监听**的端口。
一个都没在听（还没开始借网）才用配置值。

### 3. 纠正系统代理会死循环（真 bug）

接管模式下，用户中途开 Clash → Clash 写 7897 → 渡口每秒改回 10801 → 循环，
两边都上不了网。

现在限制纠正次数：**纠正 3 次还被人改走，就认定「这局不归我管」**，
自动转成节点模式并在日志里说清楚该怎么做。

### 4. 探测端口会卡住界面（体验 bug）

`DetectPort` 每个端口等 250ms，最多 8 个 = 2 秒。它在 UI 线程上跑（按钮点击），
表现为「点一下界面卡两秒」。改成 80ms × 6，最坏 480ms。

### 5. UDP 关联数没有上限（健壮性）

每条 UDP 关联要 2 个线程 + 2 个 socket。异常客户端（反复重连）能把线程刷爆。
现在上限 32 条（够覆盖「游戏 + 语音 + 几个 P2P」），超了直接回 SOCKS5 错误码 0x07。

### 验证

- 生命周期回归 22 项：借网↔直连 3 轮、Clash 开/关、UDP 双端口、残局清理 — 全过
- 本轮新增 7 项：PAC 读写、端口探测（在听/没听/跳死端口）、写入用实际端口 — 全过

## v0.14.5 — 2026-10-27

> 客户端 1.0.9：**UDP 通了、和 Clash 不再抢代理**。

### 一、UDP 真正可用（1.0.8）

以前只有 TUN 模式能承载 UDP，而 TUN 模式又依赖 mihomo；「节点模式」「游戏加速」这两条路
一直是「看着有、实际发不出去」。原因有两层：

**本地缺 CMD=0x03**：`Proxy.cs` 的 SOCKS5 只处理 CONNECT，UDP ASSOCIATE 直接回
「命令不支持」。远端服务器 v2 起就支持 UDP 了，缺的一直是本地这一跳。

现在补上，链路是：

```
客户端 → 本地中继 127.0.0.1:随机 → 上游 SOCKS5 的 UDP 端点 → 目标
```

两个关键点（都踩过）：

- 上游的 UDP 端口**不是固定的 1080**。远端每建立一条 TCP ASSOCIATE 才新开一个随机
  UDP socket 并放在 BND 里返回。以前直接往 `10.13.24.1:1080` 发 UDP，永远收不到回包。
- 回包必须发往**客户端 UDP 的来源端口**，不是 TCP 控制连接的端口。两者不是同一个
  socket，早先写成 TCP 的 peer，抓包看得到「发出去了但没有回程」。

**mihomo 的 UDP 线路也是错的**：`mihomo-keeper.ps1` 把 `ferry-udp` 指向
`10.13.24.1:1080`（那是 TCP 端口，不收 UDP），所以全局模式打游戏一直静默失败。
改成指向本地代理端口，由它去和远端协商。

测试机实测：借网状态下经真实运行的本地代理（HTTP/SOCKS 两个端口）发 DNS 查询，
都拿到了应答。

### 二、和 Clash / 加速器不再抢系统代理（1.0.9）

现象：用户开着 Clash（系统代理 7897）再点借网 → 两边每秒互相改回来 →
「借网成功了但什么都打不开」，Clash 也翻不了墙。

根因就是系统代理只有一个位置，而渡口借网时无条件写 `127.0.0.1:10801`，
`RefreshUi` 每秒还在「纠正」指错的代理 —— 那等于每秒把 Clash 的 7897 改回 10801。

现在启动借网前先探测：**系统代理已被别的程序占用 → 进入「节点模式」**：

- 不写系统代理，只监听本地 SOCKS/HTTP
- 不参与「纠正」，停止时也不还原（没改过）
- 界面直接告诉你该怎么做：在 Clash 里加 SOCKS5 `127.0.0.1:10800`

### 三、设置里的小猫按钮（1.0.9）

手动去 Clash 里加节点对非技术用户是道墙，所以设置窗「断网检测间隔」那一行末尾加了
一个 🐱（13px、灰色、无边框，鼠标悬停有说明）。

点它会写进 Clash 的**全局扩展配置**（`profiles\Merge.yaml`，订阅更新不会覆盖）：

```yaml
prepend-proxies:       渡口 = SOCKS5 127.0.0.1:<端口>, udp: true
prepend-proxy-groups:  国内出口 = fallback [渡口, DIRECT]
prepend-rules:         GEOIP,CN → 国内出口
```

规则挂 fallback 组而不是直接指渡口：渡口只在借网时可用，直接指过去的话，
用户一停借网国内站点就全断。fallback 里带 DIRECT，不通自动回落。

**重复点不会重复建配置**：同端口只提示「已经接入过了」，文件不动；
端口变了（10800 被占顺延）会把旧块整段替换，不会出现两份节点。

**不同客户端也能用**：自动探测 Verge Rev / Verge Rev Dev / 旧 Verge / Nyanpasu；
一个都没有时**不写任何文件**，只给按客户端分类的手动说明。

### 四、测试与回归

- `tools/verify-lifecycle.ps1`：借网↔直连反复切换 3 轮、Clash 开/关、UDP、残局清理，
  22 项断言（测试机真借网，出口 218.107.0.47）
- 接入 Clash：16 项断言（写入/幂等/端口替换/只有一份块/三次点击仍只提示）
- 界面：`tools/render-ui.ps1` 渲染确认主页复原、设置窗小猫在行末

### 五、一次事故，一条硬规则

发布 1.0.8 后在 SSH 会话里用 `Start-Process` 重启远端 socks5，SSH 一断进程被
Job Object 回收 —— 服务没起来，所有借网的人（包括我这边）断网。

已写进 `AGENTS.md` 第三条硬规则：**远端服务的启停一律 `schtasks`，改完必须
`netstat` 确认端口在听**。详见 `docs/踩坑记录.md` 第 65 条。

## v0.14.4 — 2026-10-27

> 客户端 1.0.7：**重启 / 更新后回到你离开时的状态** —— 在借网就自己接上，停在直连就别自作主张。

### 之前的做法错在哪

1.0.6 的更新路径写死了「更新前在借网 → 新实例带 `/start`」，但**安装包路径**没有，
所以用户装完还要自己点一次「开始借网」。而 1.0.7 的想法更简单也更准：

> 用「上次离开时是什么状态」来决定，而不是「装之前进程在不在跑」。

两者区别：进程在跑 ≠ 用户想借网（他可能刚点了停止、正连着看网页）。

### 实现

- 借网成功（`DoStart` 的成功回调）→ 写 `last-state.txt` = `borrowing`
- 点「停止并恢复直连」（`DoStop`）→ 写 `direct`
- 主动退出（`RealQuit`，含「退出会恢复直连」确认）→ 写 `direct`
- 启动时（`Kickoff`，更新 / 手动重启 / 开机自启都走这里）：
  - 带 `/start` 参数 → 借网（显式要求，优先级最高）
  - 否则看 `last-state.txt`：`borrowing` 且已激活 → 800ms 后自动 `DoStart`
  - 没有这个文件（全新安装）→ 什么都不做

状态存在**独立小文件**里，不塞 `config.json`：那是用户的配置，不该被每秒级的状态
改写，也不该因为「改了个端口」顺带把借网状态改掉。

安装器那边原本的 `{code:GetRunParams}`（装完带 `/start`）已回退 ——
由客户端自己判断更准。

### 边界

| 场景 | 行为 |
|---|---|
| 借网中崩溃 / 被强杀 | `last-state` 还是 `borrowing` → 下次启动自动接上 |
| 借网中点「停止」再关 | `direct` → 下次不自动借 |
| 借网中直接退出程序 | `direct`（退出确认框里已说明会恢复直连） |
| 关窗口收进托盘 | 状态不变（不断网），下次打开还是原样 |
| 全新安装 | 没有 `last-state.txt`，不自动借 |

### 验证

编译 + 安装包构建通过；`SaveLastState / WasBorrowingLastTime` 与三处调用点均已 review。
**未做端到端**：真实借网只在用户机器上（测试机到出口机的 SOCKS 不通），
这条要等用户实际重启一次确认。

## v0.14.3 — 2026-10-27

> 客户端 1.0.6：**激活交给服务器按 IP 管**、开机不会再被上一次的残局断网。

### 一、激活状态改由服务器管理（解决「卸载/配置被清就要重新要密钥」）

服务端 `/api/config` 新增 `with_token=1`：**只允许查自己来源 IP 的那一份**
（原有的 `src == want` 校验），默认不带该参数时依然返回空 token ——
安装包本身仍然不含凭据，转发安装包不会泄露账号。

客户端在「本地三份都没有 token」时调它补回来，只补
`token / accountIp / expiresAt / portalUrl` 四个字段，其余一律不动。
`portalUrl` 为空时用 `http://10.13.24.1:8080` 兜底 —— 实测踩过：配置被删之后
`portalUrl` 也是空的，如果这里直接返回，整个自愈等于没写。

测试机（10.113.66.6）实测：

```
$ curl "http://10.13.24.1:8080/api/config?with_token=1"
{"registered": true, "accountIp": "10.113.66.6", "token": "TEST-XT-0001", ...}

PASS  empty config is not activated
PASS  RecoverFromServer returned true        ← 配置全空也能恢复
PASS  token restored from server
PASS  second call is a no-op                 ← 已有 token 不动
PASS  user httpPort preserved                ← 用户改过的设置不被覆盖
PASS  user useTun preserved
ALL PASS
```

### 二、开机不会再「上不了网」：启动时清残局

客户端被强杀 / 崩溃 / 注销时，`mihomo`、`FerryTun` 网卡、系统代理都不会自己还原。
用户重启后看到「FerryTun 无法访问 Internet」，严重时整台机器没有出口。

现在启动第一件事就是清：

| 残局 | 处理 |
|---|---|
| 系统代理还指着 `127.0.0.1:10801` | `RestoreDirect` 还原到备份的原代理 |
| `FerryTun` 网卡还在 | 先按 PID 杀残留 mihomo（它退出会回收网卡），还在就 `Remove-NetAdapter` 强删 |
| 上一步都失败 | 日志写明「重启电脑可彻底恢复」，不再静默 |

顺序上，清理放在「判断借网状态」之前 —— 否则会把上一次的残骸误认为「自己已经在借网」。

### 三、FerryTun 不再显示「无法访问 Internet」

旧做法是 TUN 期间把 `EnableActiveProbing` 设 0（关掉系统连通性探测）。
代价有两个：**所有**网络都不再显示「无 Internet」；而且 `FerryTun` 网卡状态
一直挂着「无法访问 Internet」—— 用户看到就以为网没通。

现在改成让探测流量自己走代理：DNS 的 `fake-ip-filter` **不再**排除
`msftconnecttest / msftncsi`，它们拿到 fake-ip → 进 TUN → `MATCH,ferry`
→ 经出口机访问 → 探测成功 → 图标正常。探测失败也只是图标难看，不影响流量。

### 四、更新完自动恢复借网

`DoUpdate` 记录「更新前是否在借网」，新实例的延迟启动命令带上 `/start`。
之前更新完新实例是「干净启动」，用户得自己再去点「开始借网」——
而这台机器往往没有别的出口，那一步不是体验问题，是能不能用的问题。

### 五、卸载：清理干净，但不删用户的激活备份

`/uninstall` 按顺序做：还原代理 → 杀 mihomo → 删 `FerryTun` 网卡 →
删计划任务/启动项 → 清 `%TEMP%` 里的更新中间文件。

**`%APPDATA%\Ferry\config.backup.json` 故意保留**。两条硬规则已写进 `AGENTS.md`：
程序任何自动流程只做「没有就补」，不做「有就删」；
在用户的机器上重启客户端 = 断他的网（这台机器本身靠渡口借网上网）。

### 验证

- 服务端接口：测试机实测返回本机自己的 token
- 客户端恢复逻辑：12 项断言全过（含「有就不动」「用户设置保留」）
- 残局清理：模拟「借网→强杀」，代理从 `10801` 还原到 `7897`，幂等
- `/uninstall`：代理已还原、计划任务已清、`%TEMP%` 无产品残留
- 掉激活：**不会** —— 更新路径只换 exe，测试机上 `config.json` 逐字节不变

### 未验证（如实记录）

`FerryTun` 图标的改动需要真借网才能看到效果，而测试机到出口机的 SOCKS 端口不通，
只能在用户的机器上看。用户装 1.0.6 后反馈正常。

## v0.14.2 — 2026-10-26

> 客户端 1.0.5：**更新时目录里短暂没有 exe 的窗口**被消掉了；启动过程开始留痕。

### 现象

用户要求「按之前的要求继续验证自动更新，比如借网状态不能更新、更新覆盖问题」。
真实跑下来，借网状态下更新是通的（见下方验证记录），但代码里有两个隐患：

1. **旧顺序 `rename(exe→.old)` → `copy(tmp→exe)`** 中间有几百毫秒到几秒，
   目录里**没有可执行的 exe**。这个窗口里进程被杀、断电、杀软拦截、磁盘满，
   客户端就永远起不来 —— 只有一个 `.old`，快捷方式指向的文件不存在。
   用户看到的就是「更新完程序没了」，而且只能重装。
2. **启动过程不写日志就走**。`UnhandledException` 挂在「拿到单实例锁之后」，
   所以启动早期（读配置、恢复备份、WPF 初始化）出问题一律是
   「双击没反应，日志里一个字都没有」。这次测试里就撞上过几次：进程 `exit 0`
   瞬间退出、日志空 —— 完全无法判断是锁冲突还是崩溃。

### 修法

| 改动 | 为什么 |
|---|---|
| 更新顺序改成 **copy→`exe.new` → rename(`exe`→`.old`) → rename(`exe.new`→`exe`)** | 两次 rename 都是 NTFS 原子操作，窗口是微秒级；任何一步失败磁盘上都还有一份完整可执行的 exe |
| `UnhandledException` 提前到 `Main` 最开头，并加 `ProcessExit` 钩子 | 启动早期崩溃/退出也能在 `assistant.log` 里看到 |
| 启动时写 `启动：参数 [...] dir=…`、`拿到单实例锁`、`进入消息循环（借网=… 自动更新=…）`、`进程退出` | 「双击没反应」从此有据可查：是锁冲突、还是崩在 WPF 初始化 |
| `.old` 清不掉时 3 秒后重试一次 | 更新完立刻重启时旧 exe 的文件句柄可能还没释放（实测残留过 133 KB 的 `.old`） |
| `/update` 自检改成和 `DoUpdate` 完全相同的顺序 | 否则「自检通过」不代表真实更新能过 —— 两边顺序不一致时，自检永远测不到真实路径的窗口 |

### 验证：借网状态下的真实自动更新（客户端自己的 `/auto-update`，不是手工覆盖）

```
23:18:40 启动隧道与代理… → 代理已监听 127.0.0.1:10801 → 系统代理已切到渡口
23:18:40 隧道 + 代理已就绪 … 出口 IP：218.107.0.47（真在借网）
23:18:43 [更新] 自动触发（/auto-update）：借网=True，先查版本
23:18:43 [更新] 服务端 1.0.3，当前 1.0.2.0
23:18:43 [更新] 下载完成 132608 字节
23:18:43 [更新] 暂存新版本 → 暂存成功 → 改名成功 → 启用新版本 → 启用成功
23:18:43 [更新] 还原代理并退出（已还原原代理 127.0.0.1:7897，代理已停止）
23:18:43 [更新] 延迟启动器 = True
23:18:49 渡口已启动（新实例，普通权限）
结果：版本 1.0.3、进程 1 个、系统代理已还原到 7897、无 FerryTun 残留、配置未变
```

顺带确认的两件事：
- **更新不碰配置**：测试机上 `config.json` 逐字节不变（`token kept = True`）
- **`.old` 清理生效**：1.0.4→1.0.5 之后目录里只剩 `渡口助手.exe`

## v0.14.1 — 2026-10-26

> 客户端 1.0.2：**卸载 / 重装之后不用再重新填密钥**。

### 现象

用户反馈：「卸载重启，或者更新，有时候还是会出现密钥丢失需要重新填」。

### 根因

激活令牌只存在 `config.json` 里，而这个文件和 `.bak` 都在安装目录。
安装器的卸载段是这么写的：

```
[UninstallDelete]
Type: filesandordirs; Name: "{app}"     ← 整个目录删掉
```

所以「卸载 → 重装」必然丢激活。另外两个场景也会踩到：

- 老版本（0.9.2 时代）是 `PrivilegesRequired=admin` 安装，配置落在**另一个
  管理员账户**的 `%LOCALAPPDATA%\Ferry` 下；换成 per-user 之后读不到那份；
- 装到新目录时，安装器写进来的是「token 为空」的新配置 —— 界面直接显示未激活。

### 修法：三层，任何一层活下来就够了

| 层 | 在哪 | 做什么 |
|---|---|---|
| ① 写备份 | `Cfg.Save` | 每次保存配置时，额外写一份到 **`%APPDATA%\Ferry\config.backup.json`**（不在卸载范围内） |
| ② 启动自愈 | `Cfg.RecoverFromRoamingBackup` | 主配置没有 token、而备份有 → 自动复制回来并刷新 `.bak`，日志写「已从备份恢复激活信息」 |
| ③ 安装器自愈 | `installer/ferry.iss` `ssPostInstall` | 卸载后直接重装（客户端还没启动过）也能恢复：查同一份备份，有 token 就 `CopyFile` 回来 |

两个刻意的边界：

- **只在有 token 时写备份**。否则「刚装好、还没激活」的空配置会把好备份冲掉。
- **主配置的 `.bak` 也有保护**：如果这次要写的内容没有 token、而旧 `.bak` 里有，
  就保留旧的。这一条防的是「配置读坏了 → 内存里是默认值 → 用户点了保存 →
  唯一能用的那份备份被空配置覆盖」。
- 卸载前（`/uninstall`）也会主动 Save 一次，保证备份是最新的。

### 验证

`tools/verify-cfg-recovery.ps1` —— 16 项断言，在测试机上跑（会临时占用
真实的 `%APPDATA%\Ferry\config.backup.json`，脚本用 `finally` 原样还原）：

```
PASS  Save writes the roaming backup
PASS  config gone after uninstall
PASS  RecoverFromRoamingBackup returns true
PASS  token restored / account ip restored / .bak restored too
PASS  no restore when backup has no token
PASS  empty save keeps token in .bak
PASS  Load does not fall back to .bak on empty token
PASS  startup recovery still saves it
ALL PASS
```

同时把「更新不碰配置」也钉住了：测试机从 1.0.1 走真实 `/update` 升到 1.0.2，
更新后 `config.json` **逐字节不变**（`token kept = True`、`cfg equal = True`）。

## v0.14.0 — 2026-10-26

> 客户端重写：WinForms → **WPF**（1.0.1）。界面不再手算任何一个像素。

### 为什么不是再修一次 DPI

这个 bug 修了七轮，每一轮的「根因」都是真的，但每一轮修完换个缩放比例又复发：

| 轮次 | 当时的判断 | 结果 |
|---|---|---|
| 1 | 没声明 `AutoScaleMode` → 改 `Dpi` | 字还是被裁 |
| 2 | 说明 Label 只有 18px 高 → 调到 34px | 换台机器又开始压 |
| 3 | per-monitor 下 `CurrentAutoScaleDimensions` 拿不到 DPI → 退回 `system` | 字不裁了，但变糊 |
| 4 | 控件高度写死 → 全部 `AutoSize=true` | 长句仍然溢出 |
| 5 | 文字是后来填进去的 → 每次 `RefreshUi` 重排 | 部分机器好了 |
| 6 | `MinimumSize` 压制 AutoSize → 去掉 | 按钮文字出来了一部分 |
| 7 | 用 `AutoLabel` 量真实高度 | 又冒出宽度算错 |

七次都修的是「症状」，因为**病根在布局模型**：

```
WinForms：每个控件的位置和高度都由代码写死（SetBounds(x, y, w, 18)）
字体：    pt 单位，随系统 DPI 放大
→ 字号变了、框没变 → 必然裁；谁先算、按什么 DPI 算，决定裁多少
```

只要还是 WinForms，这套「算坐标」的代码就总有某个 DPI 算不对。

### WPF 为什么从根上解决

WPF 的布局是**测量 → 排列**两阶段：控件高度由内容决定，`TextWrapping`
自动换行，`Grid` / `StackPanel` 自动分配空间，所有尺寸都是 DIP（与 DPI 无关）。
100% / 125% / 150% 下**不需要任何特殊处理**，也不需要 `AutoScaleMode`、
`AutoSize`、`PreferredHeight`、离屏测量这一整套补丁。

业务逻辑（借网、代理、TUN、授权、更新、托盘）原样复用，只重写「怎么画」：

| 文件 | 作用 |
|---|---|
| `src/MainWindow.cs` | 主窗口（状态卡、激活卡、按钮、日志、托盘） |
| `src/SettingsWindow.cs` | 设置窗（4 个开关 + 间隔 + 卸载/检查更新/意见反馈） |
| `src/FeedbackWindow.cs` | 意见反馈（可多选附件） |
| `src/Palette.cs` | 配色与字号（唯一一处视觉常量的来源） |
| `src/ProgramWpf.cs` | 入口与命令行参数（`/start` `/stop` `/update` … 全部保留） |
| `src-winforms/` | 旧界面**保留作对照**，不参与编译 |

构建也变了：`build-wpf.ps1`（`csc` 直接编译，仍然不需要 .NET SDK / NuGet，
目标机器只要 .NET Framework 4.8 —— Win10/11 自带）。

### 全局模式：从「复选框」改成「按钮 + 状态矩阵」

原来的复选框表达的是**配置里存了什么**，不是**现在生效没有**：
`useTun=true` 但进程没提权、TUN 没起来 → 勾子勾着却是假的，
只能「取消再勾一次」才触发。

现在主界面那一行是实心按钮（和「开始借网」同一套主色），并且状态严格区分三件事：

| 意图 `useTun` | 运行 `TunRunning` | 是否管理员 | 显示 |
|---|---|---|---|
| 是 | 是 | — | `已开启（UDP / 游戏都走渡口）` + 「关闭全局模式」 |
| 是 | 否 | 否 | `已勾选 · 需管理员授权` + 「去授权」（点了自动提权重启） |
| 是 | 否 | 是 | `正在启用…` / `启用失败：<原因>` |
| 是 | 否 | 是（未借网） | `已开启 · 下次借网时生效` |
| 否 | 否 | — | `未开启` |

这样「授权过、只是暂时没借网」的用户不会再看到「待管理员授权」。

### 验证方式也改了：在远端机器上渲染，不在用户桌面弹窗

界面问题只能看真实渲染，但用户的桌面常被别的程序占着，而且他不希望
验证过程在自己机器上弹窗。新增 `tools/render-ui.ps1`：

```
本地源码 → scp 到 10.113.66.6 → 用 csc 编译一个小 harness
        → RenderTargetBitmap 离屏渲染三个窗口 → scp 回 PNG
```

三个必须知道的坑（都写在 harness 注释里）：
1. SSH 登录的是 **Session 0**（services），没有交互桌面，`Show()` 会失败 → 只对 `Content` 做 Measure/Arrange；
2. 字体/字号/前景色设在 `Window` 上、由子元素继承；离屏时继承链断了会用回 Segoe UI，中文行高偏小、底部按钮被算出画布外 → 必须显式补到根元素；
3. `root.DesiredSize` 不可靠（`SizeToContent` 先给了约束）→ 画布高度按子元素累加，否则底部按钮被切一半。

### 自动更新：这次是端到端验的

在 10.113.66.6 上从同一份源码编出 **1.0.0.0** 的客户端，然后跑它自己的
`/update`（真实链路：查版本 → 下载 → 改名 → 复制 → WMI 启动）：

```
=== /update 开始，当前 1.0.0.0 ===
服务端版本 = 1.0.1
下载 http://10.13.24.1:8080/download-client
下载完成 131584 字节
改名 → 改名成功 → 复制成功 131584 字节
启动新进程 WMI 结果 = True
=== 成功，退出旧进程 ===
=== AFTER === version = 1.0.1.0  size = 131584  procs = 1
```

**顺手抓到一个发版流程的漏洞**：`/download-client` 发的是
`dist\渡口助手.exe`（不是安装包），而发版时最容易只传
`Ferry-Setup-*.exe`。第一次自检就撞上了 —— 版本号说 1.0.1、下下来的却是
0.9.37。现在发版清单里这一步是必须项：

```powershell
scp dist\渡口助手.exe             pcs:C:/ferry-portal/dist/
scp dist\Ferry-Setup-1.0.x.exe    pcs:C:/ferry-portal/dist/
```

## v0.13.22 — 2026-10-26

> 客户端 0.9.32：**按钮文字消失**的根因 —— `MinimumSize` 压制了 AutoSize。

### 现象

「全局模式」那一行的按钮只剩一个浅色边框，**文字不见了**。同一时刻
状态卡里的多行提示也压住了下面几行。

### 根因：`MinimumSize` 会让 AutoSize 的高度失效

为了限制 Label 的宽度，`Theme.Place` 里同时设了：

```csharp
l.MinimumSize = new Size(w, 0);   // ← 这一行是元凶
l.MaximumSize = new Size(w, 0);
```

在 WinForms 里，`MinimumSize` 会**压制 AutoSize 的高度自适应**：
宽度被锁死了，但高度也一起被锁在「设 MinimumSize 那一刻」的值上。

诊断数据（用户那个状态）：

```
_lblDesc.Text   = [系统代理指向渡口，但本地代理端口没响应。点「停止并恢复直连」再重新开始。]
_lblDesc.Bounds = {X=0, Y=54, Width=477, Height=19}    ← 47 个字，只有 19px 高
```

这段文字实际需要 **4 行（76px）**，但控件只有 19px —— 文字画出边界、
盖住下面的「剩余 27 天」「本地代理」等行，最后把按钮也盖住了。

`LayoutCard()` 按 19px 算间距，于是整张卡的布局全部错位。

### 修法：自己算高度，不依赖 AutoSize

新增 `Theme.AutoLabel`（Label 子类）：

- `OnTextChanged` / `OnSizeChanged` 时用临时 Label 量出真实高度再设回去
- 临时 Label 用 `AutoSize = true` + `MaximumSize`，和真实渲染的排版逻辑完全一致
- `Place()` 里**不再设 MinimumSize**

另外修了一个连带问题：`LayoutCard` 的 `PlaceRow` 忘了设宽度，导致
AutoLabel 按上一次的宽度（100px）算成 6 行 114px 高，把卡片撑爆。

### 验证

```
修复前：_lblDesc = 100x114（宽度没设 + MinimumSize 压制）
       卡片 179px，按钮 y=150 压在文字上
修复后：_lblDesc = 430x38（4 行，正确）
       卡片 198px，按钮 y=169，文字完整、按钮可见
```

## v0.13.21 — 2026-10-26

> 客户端 0.9.31：**更新后程序消失**的根因 —— 新进程抢不到单实例锁。

### 现象

点「更新」→ 选「是」→ 程序消失 → **再也不出现**。日志里连 `[更新]` 都没有
（说明这次是更早的版本），但即使进了 `DoUpdate`，也会在最后一步丢掉进程。

### 根因：启动新进程和退出旧进程的**顺序反了**

```csharp
// 0.9.29 的写法
Elevator.StartDetached("cmd.exe /c start \"\" \"" + exe + "\"");  // ① 先起新进程
Thread.Sleep(400);
if (_rt != null) { RestoreDirect(...); _rt.Stop(); }               // ② 借网时要停代理
Environment.Exit(0);                                               // ③ 才退出
```

时序：

```
T+0ms     启动新进程（cmd /c start 有 200~500ms 开销）
T+0ms     旧进程还活着，持有 Local\FerryClient 互斥锁
T+300ms   新进程起来 → 抢锁失败 → FindWindow 找到旧窗口 → 前置它 → 自己退出
T+400ms   旧进程 Environment.Exit(0) → 窗口销毁
结果      两个都没了
```

`Program.cs` 里**本来就有** `/allow-wait` 机制（最多等 10 秒抢锁），
但 `DoUpdate` 启动新进程时**没带这个参数**。

### 为什么「开着借网」时更容易失败

`RestoreDirect()` + `_rt.Stop()` 要还原系统代理、杀掉 mihomo、清理路由 ——
这需要几百毫秒。旧进程活得更久，新进程输掉竞态的概率就更高。
不借网时旧进程退得快，偶尔能赢。

所以不是「借网导致」，而是**借网放大了这个已存在的竞态**。

### 修法

1. **先收拾干净再退出**：还原代理、停运行时，然后才安排重启
2. **延迟 2 秒再拉新进程**：写一个 `ferry-relaunch.cmd`（`timeout /t 2` 后 `start`），
   用 WMI 起它（脱离本进程的 Job），然后自己 `Environment.Exit(0)`
3. **新进程带 `/allow-wait`**：双保险，即使旧进程退得慢，它也会等锁而不是误判

用 `.cmd` 而不是在命令行里嵌套引号，是为了可靠 ——
嵌套引号在 `cmd /c` 里的转义规则很容易写错，而且失败时无从排查。

## v0.13.20 — 2026-10-26

> 客户端 0.9.30：**文字被压住**（不是被裁）的真正修复。

### 上一版修对了一半

0.9.29 把「控件高度写死」改成了 AutoSize，解决了「高度不够、下沿被裁」。
但用户反馈还有问题，截图显示的是**文字被下一行压住**——这是另一回事。

用 125% 缩放模拟复现出来了：

```
系统代理指向渡口，但本地代理端口没响应。点「停止并恢复直连」再重
试。                                   ← 第二行被裁
剩余 27 天 · 2026-11-23 到期           ← 压在上面
```

### 根因：文字是**后来**填进去的

```csharp
_lblDesc = Theme.Text2("", ...);   // 创建时是空串 → 只有 1 行高
...
RefreshUi() → SetText(_lblDesc, "系统代理指向渡口…再重新开始。")   // 变成 2 行
```

Label 是 AutoSize 的，文字变长它会长高；但**下面几行的 y 坐标早在
BuildUi 里定死了** → 直接压住。

前两次改都只算了「创建那一刻」的高度，没考虑文字是运行时才填的。

### 修法：每次文字变化后重排

新增 `LayoutCard()`，在 `RefreshUi()` 末尾调用：

- 逐行用 **`PreferredHeight`**（不是 `Height`）推进 y ——
  后者在控件还没进容器时是 0
- 卡片高度跟着内容走
- 卡片变高后，激活卡和下方按钮整体下移，不会被盖住

### 诊断工具也补了漏

之前的诊断只查「高度够不够装字」，**查不出「被压住」**——所以
96 DPI 下全 OK，用户却看到重叠。现在加了两项：

1. **重叠检测**：遍历同容器里所有可见控件，算矩形交集（>1px 才算）
2. **DPI 模拟器**：`SetProcessDpiAwareness` 强制按 125% 渲染

正是靠 125% 模拟才复现出这个问题——96 DPI 下它不出现，
所以「本机正常」不代表没问题。

### 验证

```
96 DPI：无重叠，无裁切
125% 模拟：两行提示完整显示，「剩余 27 天」正常在下方，无重叠
```

## v0.13.19 — 2026-10-26

> 文档：把这一轮踩的坑全部固化下来，避免重复。

### 新增 7 条踩坑（52–58）

| # | 一句话 |
|---|---|
| 52 | WMI `Win32_Process.Create` 不认「整个命令行被引号包住」，返回码 9；要走 `cmd /c start ""` |
| 53 | 自动更新失败三次的根因：UI 线程阻塞 + 重复请求版本 + `RealQuit` 弹确认框 |
| 54 | 「手工覆盖后版本变了」不是验收；要跑 `渡口助手.exe /update` |
| 55 | 文字被裁的真正原因是**控件高度写死**，不是 DPI 模式；附诊断方法 |
| 56 | AutoSize 控件在没进容器前 `Height` 是 0，读它算间距会重叠 |
| 57 | 拆页面忘带 `<script>`：结构全对、功能全死，curl 查不出来 |
| 58 | 共用脚本的页面用「DOM 里有没有那个元素」判断该不该轮询 |

### AGENTS.md 从 8 条扩到 12 条 + 一张元教训表

新增的四条都是这轮实际翻车的：

- WMI 命令行引号问题
- 自动更新必须后台线程 / 不走 RealQuit / 不重复请求版本 / 每步写日志
- 「手工验证不算验收」
- 控件高度不能写死 + 不要用 MeasureText 算中文高度

另外加了一节 **「一犯再犯的元教训」**，专门记做事方式上的坑：

| 教训 | 具体表现 |
|---|---|
| 别用手工操作代替端到端验证 | 三次宣布更新修好，三次都是手工复制 exe 验的 |
| 别在没定位的情况下改代码 | DPI 问题改了三次缩放模式，方向就是错的 |
| 结构测试通过 ≠ 功能正常 | 门户 38 项全绿，反馈面板从上线起没加载过 |
| 别让两个地方存同一份状态 | 「全局模式」设置和主界面各有一个开关，不同步 |

### 为什么单独强调「元教训」

技术坑有代码兜着（改错了编译不过），**做事方式的坑没有任何自动检查**。
这轮最大的时间浪费不是某个技术难点，而是：

1. 没定位就改（DPI 改了三次）
2. 用假验收自我确认（手工覆盖）
3. 结构测试当成功能测试

这三条比任何具体技术点都更容易重复。

## v0.13.18 — 2026-10-26

> 客户端 0.9.29：**文字被裁的真正修复**（不是改 DPI 模式）。

### 前几次为什么都没修好

我一直在改 `dpiAware` / `AutoScaleMode`，方向就错了。真正的病根是
**控件高度写死，字体却随 DPI 放大**：

```
诊断输出（DPI = 120，即 125% 缩放）
FSmall 8.5pt 单行高 = 20px
Row() 给的高度      = 18px     → 每行字下沿裁 2px
「渡口」标题需要 30px，给 28px   → 裁 2px
```

所有 Label 都是 `AutoSize = false` + `SetBounds(..., 18)`。每个用户的缩放
比例不同，被裁的像素数也不同 —— 所以反馈永远是「不同程度的文字被遮挡」。

### 怎么定位的

写了个诊断程序，**在真实 DPI 下遍历每个 Label**，对比「设定高度」与
「实际需要高度」，输出到 `%TEMP%\ferry-dpi-diag.txt`：

```
Text(前16字)        Width Height  需要   结论
渡口                 120     28    30   裁掉 2px
内网应急接入 · 断网时…   300     18    40   裁掉 22px
本地代理    未运行      430     18    20   裁掉 2px
...
```

改之前 11 个控件被裁（2~22px），改之后 **0 个**。

### 修法

| 控件 | 原来 | 现在 |
|---|---|---|
| `Theme.Text2` 的 Label | `AutoSize=false` + 写死高度 | `AutoSize=true` + `MaximumSize` 限宽，高度自己算 |
| 状态行/标题/副标题 | `SetBounds(x,y,w,h)` | `Theme.Place(l, x, y, w)`，只给位置和宽度 |
| 设置里的 checkbox | `SetBounds(..., 22)` | `AutoSize=true` + `MaximumSize` |
| checkbox 说明文字 | 写死 34px | 同上，且 y 按上一项**实际高度**累加 |
| 开关卡片 | 固定 294 高 | 内容撑开，下方控件按增量下移、窗口加高 |

### 为什么不用 TextRenderer.MeasureText 自己算

试过了，**中文长句算不准**：`WordBreak` 依赖空格断行，中文没有空格，
MeasureText 认为一行放得下、返回单行高度，而 Label 实际渲染会按字符折行。
结果「按测量值给高度」依然被裁。所以最后交给控件自己排。

### 顺带：自动更新也修好了（0.9.27）

三次失败的真正原因：

1. `DoUpdate` **整个跑在 UI 线程** —— 同步下载冻住界面，看起来像「点完就没了」
2. 第一句又调了一次 `Api.Version`，借网时代理指向 10801，可能超时 → 静默 return
3. 结尾调 `RealQuit()`，借网时会弹「确定退出吗」，点否 → 程序还在但 exe 已换掉

现在：版本号由 CheckUpdate 传进来、下载/改名/复制/启动全在 ThreadPool、
成功后直接 `Environment.Exit(0)`、每一步都写 `[更新] xxx` 日志。

另外加了命令行自检入口 `渡口助手.exe /update`，跑完整链路并写
`update-selftest.log` —— 以后说「更新能用」有据可查。

实测（不是手工复制，是真的跑 /update）：

```
更新前: 0.9.27.0
18:45:28  下载完成 129024 字节
18:45:28  改名成功 → 复制成功
18:45:28  WMI 结果 = True
更新后: 0.9.28.0   进程数: 1   残留 .old: False
```

## v0.13.17 — 2026-10-26

> 管理面板拆成独立页面；反馈支持附件上传（网页 + 客户端）。

### 管理面板：一页到底 → 五个独立页

原来所有区块堆在一页里靠锚点跳：密钥 + 反馈 + 设备 + 流量 + 事件，
越加越长，滚到一半就不知道自己在哪；反馈夹在密钥和机器列表中间尤其突兀。

现在：

| 页面 | 内容 |
|---|---|
| `/admin` | 概览：实时流量 + 多维统计 |
| `/admin/keys` | 密钥：生成、撤销 |
| `/admin/users` | 设备：在线状态、速率、备注、限速 |
| `/admin/feedback` | 反馈：卡片列表（带附件缩略图） |
| `/admin/events` | 事件：审计记录 |

导航变成「你在哪一页」的指示器（当前页高亮），每页有独立的标题 + 副标题。

**反馈从表格改成卡片**：正文长短不一、还可能带图，塞进表格单元格里必然挤成一团。
卡片能自然换行、附件缩略图有地方放、状态用左边色条一眼看出。

### 反馈附件上传

三处入口，共用同一个上传接口：

- **网页 `/feedback`**：点击选择 / 拖拽 / 在表单里 Ctrl+V 粘贴截图
- **客户端设置 → 意见反馈**：选文件（可多选）

服务端设计：

```sql
CREATE TABLE feedback_attach (
    id, feedback_id, upload_token, name, mime, size, stored, created_at
);
```

- **先上传后绑定**：附件在反馈提交前就能传（用户粘完图再写描述），
  上传时带一个随机 `upload_token`，提交反馈时带同一个 token，
  服务端靠它把附件挂到刚插入的反馈上
- **安全边界**：白名单 MIME（不接受 exe/html/svg）、单文件 8 MB、
  每条最多 6 个、存盘用随机文件名（原名只入库）、
  下载要登录或带正确 token
- 图片 `inline` 预览，其余一律 `attachment` 下载 + `nosniff`

### 一个踩坑记录

拆页时忘了把 `<script>` 一起带过去 —— 反馈页变成「加载中…」永远不动，
因为 JS 根本没加载。所以把 LIVE_UI 拆成 `ADMIN_UI_HTML`（骨架）和
`ADMIN_JS`（脚本），由 `admin_shell()` 统一追加到每页末尾；
反馈页另外抽了一份轻量的 `ATTACH_JS`，不跟管理面板那 46 KB 共用。

顺带：没有实时图的页面（反馈/密钥/事件）不再轮询 `/api/admin/live` 和统计接口。

### 验证

```
5 个页面全部 200
附件上传 → id=2 → 预览 200
反馈页：nav=5、active=反馈、cards=4、JS ERRORS: none
用户反馈页：hasDrop=true、上传 id=4、预览 200
```

## v0.13.16 — 2026-10-26

> 管理面板新增「多维统计」：10 分钟 / 1 小时 / 5 / 10 / 50 / 100 / 250 小时 / 总览。

### 为什么之前的图表不够用

原来的实时图只看 10 分钟、数据在内存里（`_hourly`，24 小时就丢），
进程一重启全清空。想回答「这个月谁用得最多」「昨天那个峰值多高」完全做不到。

### 新的存储：5 分钟桶，落库

```sql
CREATE TABLE traffic_bucket (
    bucket_ts INTEGER, ip TEXT, rx INTEGER, tx INTEGER,
    peak_rx INTEGER, peak_tx INTEGER,
    PRIMARY KEY (bucket_ts, ip)
);
```

- 粒度 **300 秒**：250 小时 = 3000 桶/设备，SQLite 毫无压力；
  再细（1 分钟）在图上不到 1 像素，纯浪费
- 写入时机：`flush_totals()` 每 60 秒把内存增量落库时**同时**写桶，
  没有增量就不写（不留空桶）
- 保留期 250 小时，`janitor` 每 10 分钟清一次过期桶

### 窗口与粒度

跨度越大，曲线上的点越粗，但**总量永远准确**（桶可加，聚合只是求和）：

| 窗口 | 跨度 | 粒度 | 点数 |
|---|---|---|---|
| 10 分钟 | 600s | 1 分 | 10 |
| 1 小时 | 3600s | 1 分 | 60 |
| 5 小时 | 5h | 5 分 | 60 |
| 10 小时 | 10h | 5 分 | 120 |
| 50 小时 | 50h | 30 分 | 100 |
| 100 小时 | 100h | 1 时 | 100 |
| 250 小时 | 250h | 2 时 | 125 |
| 总览 | 全部 | 1 天 | 视数据量 |

### 面板内容

- **五张汇总卡**：下行总量 / 上行总量 / 合计 / 峰值速率 / 覆盖时段
  （每张卡第二行给「平均速率」「设备数」「桶数」这类辅助信息）
- **趋势图**：上下行双线 + 渐变填充，Y 轴刻度和实时图同一套取整逻辑
- **设备用量排行**：本窗口内每台机器的下行/上行/峰值 + 占比条

### 关键设计

- **平均速率按「实际覆盖时长」算**，不按窗口跨度。刚装好只有几个桶时，
  按 250 小时摊会把平均值算成接近 0，毫无意义。
- **统计和实时图分开**。实时图是「此刻在发生什么」，统计是「过去用了多少」，
  混在一起会让人以为实时图也能切时间范围。
- 前端每 30 秒拉一次（桶本身 5 分钟粒度，刷太快没意义）。

### 验证

造了 10 小时、两个 IP 的测试数据，逐个窗口打 API：

```
10m → 1 点 / 1h → 11 点 / 5h → 59 点 / 10h → 119 点
50h → 21 点 / 100h → 11 点 / 250h → 6 点 / all → 2 点
JS ERRORS: none
```

验证完把测试数据删干净了（`DELETE ... WHERE ip IN (...)` 清掉 240 行）。

## v0.13.15 — 2026-10-26

> 客户端 0.9.25。自动更新彻底改成**进程内替换**，不再依赖外部脚本。

### 为什么又改

0.9.23 的更新走「写一个 .cmd → WMI 起它 → 自己退出 → 脚本覆盖 exe → 启动」。
实测出现过一次：**脚本把临时文件删了（成功路径才会删），但进程没被拉起来**。
排查得同时看日志、TEMP 残留、msg 弹窗，成本太高。

### 现在怎么做（全程在进程内）

```
① 下载新 exe 到 %TEMP%
② 把**正在运行的自己**改名成 渡口助手.exe.old   ← NTFS 允许重命名运行中的 exe
③ 新 exe 复制回原名
④ WMI 起新进程（cmd /c start，脱离本进程的 Job）
⑤ 自己 RealQuit；.old 在下次启动时删掉
```

任何一步失败都**当场回滚**（把 .old 改回来）并弹框说明，不会把用户留在
「程序没了」的状态。

### 两个实测出来的细节

1. **运行中的 exe 确实可以被重命名**（不能被覆盖），这是整个方案的前提，已验证。
2. **WMI 的 `Win32_Process.Create` 不认「整个命令行被引号包住」**：

   ```
   CommandLine = "C:\...\渡口助手.exe"     → 返回码 9（Path Not Found）
   CommandLine = cmd.exe /c start "" "C:\...\渡口助手.exe"  → rc=0
   ```

   所以启动那一句统一走 `cmd /c start`。

### 验证

```
下载 v0.9.25 → rename 运行中的 exe（成功）→ copy → WMI rc=0
→ 旧进程退出 → 版本 v0.9.25、进程数 1
```

## v0.13.14 — 2026-10-26

> 全量风险复查。客户端 0.9.23。

### 又找到一个真泄露：WireGuard 私钥

上一轮只审计了管理密码和激活令牌，这次把「私钥类」也扫了一遍：

```
git log -S"PrivateKey"  →  wireguard/ferry-wg.conf
                            PrivateKey = <旧私钥>（真私钥，不是示例）
```

同时远端 `pcs` 上 `wg0` 网卡还是 **Up** —— 也就是说这把私钥当时**仍然可用**。

处理：

1. 远端停用并删除 `WireGuardTunnel$wg0`（服务 + 网卡都没了）
   - 踩坑：远端默认 shell 是 PowerShell，`sc stop "WireGuardTunnel$wg0"` 里的 `$wg0`
     会被 PowerShell 当变量展开成空 → 报「服务未安装」。改用
     `Get-Service | Where-Object Name -like 'WireGuardTunnel*wg0*' | Stop-Service`
2. `git filter-branch` 再重写一次历史，把私钥替换成 `<REDACTED-WG-PRIVATE-KEY>`
3. 删掉整个废弃的 WireGuard 面：`ferry-wg.conf` / `client.pub` /
   `ferry-wg-server.sh` / `stop-wg.cmd` / `启用WG线路.cmd` / `测试WG线路.cmd`，
   本地 `client.key` 也删了（它没进 git，但躺在工作区里）
4. 保留 `ferry-socks5-server.py` / `deploy-udp-relay.ps1` / `ferry-server-*.ps1`
   —— 这些是**生产在用**的 TCP 服务端和远端管理菜单

### 顺手加固的三处

| 项 | 改动 | 为什么 |
|---|---|---|
| 会话 cookie | 加 `HttpOnly` + `SameSite=Lax` | 即使哪天出 XSS 也偷不走会话；再挡一层 CSRF |
| 意见反馈保留 | 只留最近 **500 条**（原来永久） | 里面可能有微信/QQ 等联系方式，没道理永久留存 |
| 客户端日志 | 超过 2 MB 轮转成 `.1` | 心跳每 5 秒可能写一行，不轮转会一直涨 |

`Secure` 属性**没加** —— 门户是 http（内网自建没有证书），加上 cookie 直接失效。

### 仍然存在、需要你决策的

1. **仓库还是 public**。历史重写只能清掉「能 grep 到的东西」，GitHub 上的旧
   commit 对象在 GC 前仍能用 SHA 打开。转 private 是最彻底的一步。
2. **门户是明文 HTTP**，管理密码在登录时是明文过局域网的。要 HTTPS 就得管证书
   （自签会让浏览器报警告，比明文好但也麻烦）。目前建议只在内网用。
3. **`portal.db` 没有自动备份**。里面是全部账号的 token 和授权到期时间，
   删了就得挨个重新激活。

## v0.13.13 — 2026-10-26

> 客户端 0.9.22。两个「点了没反应 / 文字被埋」的问题。

### 更新要点两次确认，用户以为一次就够了

日志停在这里：

```
10:52:16 发现新版本 0.9.20（当前 0.9.19.0）
（之后什么都没有 —— DoUpdate 一次都没执行到）
```

`CheckUpdate()` 先弹「发现新版本，现在更新吗？」→ 用户点「是」→ 进 `DoUpdate()`，
而 `DoUpdate()` **又弹了一个确认框**。第二个框弹出来时用户没注意（或者以为
第一个框已经生效），于是永远不会下载。

修法：`DoUpdate` 里那个二次确认去掉，只在 `CheckUpdate` 问一次，
文案补上「装完会自动重新打开」。

### 状态卡最后一行被圆角裁掉

`_btnGlobal` 放在 y=176、高 24 → 底边正好压在卡片高度 200 上，
而 `Theme.Card()` 用圆角 `Region` 裁剪，底部十几像素会被切掉。
「待管理员授权」那行贴着底边，字的下沿也被埋。

改：状态卡 200 → 212，三行重排到 128 / 152 / 176，按钮提到 174、高 26，
底部留 16px 余量；激活卡相应下移到 298。

用离屏渲染（`DrawToBitmap`）核对过渲染结果，不是靠算坐标。

## v0.13.12 — 2026-10-26

> 安全：重写 git 历史，清掉历史里的旧密码与旧令牌。

### 做了什么

`git filter-branch` 重写全部 89 个提交，把两个字符串替换成占位符：

| 原值 | 替换为 | 出现位置 |
|---|---|---|
| `<旧管理密码>` | `<REDACTED-ADMIN-PW>` | `tools/verify-netctl-live.py`、`verify-note-edit.py` |
| `sngsb4-…`（旧激活令牌） | `<REDACTED-TOKEN>` | `HANDOVER-打包工作.md` |

然后 `--force` 推送（所有 commit SHA 都变了）。

**两个都是真的泄露**：仓库是 public，这两个值在 HEAD 和历史里都能直接读到。
虽然现在已经轮换（旧值在服务端已失效），但历史里留着就等于把「曾经的管理密码」
公开挂着，所以还是清了。

### 过程中踩的坑

第一遍 filter 只清了 `.py`，`.md` 没动 —— 因为脚本写成了：

```bash
for f in $(git ls-files "*.md"); do ... done
```

`$(...)` 按空白分词，**中文文件名会被拆碎**，`sed -i` 全部失败（错误还被吞了）。
改成 NUL 分隔才对：

```bash
git ls-files -z | while IFS= read -r -d '' f; do ... done
```

### 没有清掉的

GitHub 上旧的 commit 对象仍然可以通过 SHA 直接访问，直到 GitHub 侧 GC。
如果在意这一点，**把仓库改成 private** 比继续折腾历史更彻底 —— 目前 0 fork、1 star。

## v0.13.11 — 2026-10-26

> 客户端 0.9.20：定位改成「应急接手没做完的工作」；界面去掉 SSH 隧道。

### 文案：从「绕开限制」改成「断网时把活干完」

不想让这个工具读起来像「绕过管控」，所以整站的口径换成**应急接手**：

| 位置 | 原来 | 现在 |
|---|---|---|
| 首页 hero | 内网里有机器出不去？借一台能上网的主机做出口 | **网断了，手上的活还没完？借一台能出网的主机顶一下** |
| hero 正文 | 账号外网权限受限、校园网临时故障… | **作业还没交、代码还没推、资料查到一半 —— 断网偏偏都挑这种时候** |
| 场景区标题 | 这些时候最有用 | 这些时候救急 |
| 场景卡 1 | 临时出不去，急着用网 | **断网了，事情还没做完** |
| 技术卡 | 断网时仍能上网 | 断网期间照常工作 |
| 客户端副标题 | 断网时借一台能上网的内网主机中转流量 | 内网应急接入 · 断网时借一台能出网的主机中转 |

### 界面：去掉 SSH 隧道相关

实测一直是直连模式（出口机的 SOCKS），SSH 隧道从没被用过，界面上的痕迹都是噪音：

- 状态卡里的「隧道　直连模式（不走 SSH）」整行删除，剩下三行重新均分（136/156/176）
- 设置里本来就没有 SSH 开关，不需要动

`Tunnel.cs` 与 `Config.DirectSocks` **保留**（代码里留了注释）：如果哪天出口机的
SOCKS 端口被封，这是唯一还能用的后备路径。只是不再有界面入口。

### 安全：git 历史里的旧凭据

见上一条提交。历史重写在 `v0.13.12` 里说明。

## v0.13.10 — 2026-10-26

> 客户端 0.9.19：界面细节 + 自动更新脚本修正 + 门户文案合规调整。

### 自动更新真正跑通了（顺手踩到一个新的）

按「下载裸 exe → 脚本替换 → 重启」重做之后做了完整实验，第一版脚本 still 失败：

```
WMI rc=0（cmd 起来了）
→ 15 秒后：版本没变、旧进程还在、new.exe 没被清理
```

原因：`taskkill /IM 渡口助手.exe /F` 在 **GBK 编码的 .cmd** 里对**中文进程名**不可靠，
静默失败（错误被 `>nul` 吞掉），于是 copy 一直重试到超时。

改成 PowerShell 立刻成功：

```bat
powershell -NoProfile -Command "Get-Process -Name '渡口助手' | Stop-Process -Force"
```

验证（用的就是客户端会写的那份脚本）：
`0.9.18 → 0.9.19，WMI 起脚本 → 旧进程退出 → exe 被覆盖 → 新进程 10:27:41 起来 → new.exe 自动清理`。

### 界面细节

| 原来 | 现在 | 为什么 |
|---|---|---|
| 「直连」用 `Theme.Idle`（很淡的灰） | 用正常文字色 | 太淡像被禁用 |
| 「全局模式 待管理员授权」用红色 | 用橙色（Warn） | 这不是错误，是差一次授权 |
| 状态卡最后一行贴边框 | 上移到 176 | 视觉上被压住 |
| 激活卡与状态卡只隔 2px | 隔 8px，内部上收 | 看起来像重叠 |

### 门户文案：去掉「翻墙」相关

- 功能卡「国内直连，墙外自动分流」→ **「按目标自动选路」**，正文不再提 google/youtube/Clash
- 删掉 FAQ「和 VPN、机场梯子是什么关系」
- 「会影响我本来用的 Clash / 代理吗」→「…代理软件吗」
- `/about` 里「实测 google 两端都不通」→「两端都不通」

删掉的位置补了两条更贴题的：

- **「出口那台机器关机 / 断网了怎么办？」** —— 借网就断，客户端会提示切回直连
- **「开着全局模式会影响局域网 / 打印机 / 共享吗？」** —— 内网地址是直连的，不影响

## v0.13.9 — 2026-10-26

> 客户端 0.9.15。自动更新的最后一块拼图：`/CLOSEAPPLICATIONS`。

### 现象与定位

0.9.12 点「更新」→ 下了 0.9.14、安装包也起来了，但**版本没变**，也没自动启动。

对照实验（同一台机器、同一个包）：

| 参数 | 结果 |
|---|---|
| `/SILENT /NORESTART /CLOSEAPPLICATIONS`（DoUpdate 原来用的） | 静默失败，版本不变 |
| `/SILENT /NORESTART /LOG=…`（手工跑） | `Installation process succeeded`，`[Run]` 也把客户端拉起来了 |

### 为什么

`/CLOSEAPPLICATIONS` 让 Inno 用 **Restart Manager** 去「礼貌地」关闭占用文件的程序。
而我们的客户端在 `OnFormClosing` 里 **cancel**（缩到托盘）——RM 等不到关闭，
安装就在静默模式下静默失败，连个错误框都没有。

关旧客户端本来就由安装器的 `InitializeSetup` 调 `/stop` 负责（它会先还原系统代理），
根本不需要 RM。所以直接去掉这个参数。

### 验证

手工用 `/SILENT /NORESTART` 装 0.9.14 后：
- `exe v0.9.14.0`（文件已替换）
- 日志 `-- Run entry --  Run as: Original user  Filename: …\渡口助手.exe`
- 进程创建时间 01:56:12，比安装完成（01:56:03）晚 9 秒 → **确实是 [Run] 拉起来的**

## v0.13.8 — 2026-10-26

> 客户端 0.9.14。DPI 换了实现方式，文字不再被裁。

### per-monitor 下 WinForms 的 AutoScale 其实没生效

上一版我加的是 `AutoScaleMode.Dpi + AutoScaleDimensions(96,96)`，但真机截图显示
文字照样溢出（状态行被按钮压住、底部署名被咬掉半行）。

原因：per-monitor 模式下，Form 在 `PerformAutoScale()` 时还没确定自己落在哪个
显示器上，`CurrentAutoScaleDimensions` 拿不到正确的 DPI → 缩放因子算成 1.0 →
控件保持 96 DPI 的像素尺寸，而字体已经按系统 DPI（125%/150%）渲染。
**字体比控件大**，于是溢出一路到隔壁控件和窗口边缘。

### 改成 System DPI aware

manifest 从 `true/pm + permonitorv2` 退回 `true + system`，由 Windows 把**整个窗口**
按比例位图放大：

- 布局比例完全不变 → 绝不会裁字
- 代价：125%/150% 这类非整数缩放下字会略微发虚（位图放大）
- `FitToScreen` 也改成按物理像素判断（`DeviceDpi / 96`），超出工作区就缩小 + 开滚动

`AutoScaleMode` 相应改成 `None`（系统已经缩放过整窗，WinForms 再算一次会翻倍）。

### 为什么不能做到「清晰又不出界」

WinForms 用的是 GDI+，要做对 per-monitor 得每个控件都用 AutoSize / TableLayoutPanel，
或者自己处理 `WM_DPICHANGED` 重排 —— 那是把整个界面重写一遍的量级。
绝大多数老 Win32 程序都停在 System aware 这一档，这是「清晰度」和「不出错」
之间实际的取舍。真要做到完美，得迁到 WPF 或 .NET 8 的 WinForms（大工程，另议）。

## v0.13.7 — 2026-10-26

> 客户端 0.9.11 → 0.9.13，管理面板两个真 bug，下载页改版。

### 提权重启：Job Object 又一次是元凶（0.9.11）

日志证据：点「开启全局模式」→ 新实例起来了，**但 IsAdmin() 仍是 false**，
日志写「TUN 需要管理员权限」。

根因：进程在 `JobKeeper` 建的 Job 里（`KILL_ON_JOB_CLOSE`，没有 `BREAKAWAY_OK`）。
在这个上下文里，ShellExecute 的 `runas` **会静默失败并回退成普通权限启动**——
不报错、不弹 UAC。上一版改成 `UseShellExecute = true` 并不能脱离 Job。

修法：用 WMI `Win32_Process.Create` 起中转进程（父进程是 `WmiPrvSE.exe`，
不在我们的 Job 里），再由它 `Start-Process -Verb RunAs`。
同一条链路已实测：`whoami /groups` 拿到 `High Mandatory Level S-1-16-12288`。

### 全局模式：复选框 → 动作按钮（0.9.12）

复选框表达的是「配置里存了什么」，不是「现在生效没有」。用户遇到的是
`useTun=true` 但进程没提权 → 勾子勾着、TUN 没起来，得先取消再勾一次才触发。

现在状态卡第 4 行是「全局模式 未开启 · 只有认代理的程序走渡口　[开启]」，
按钮文字随**真实运行状态**变（TUN 在跑 = 「关闭」）。
设置页里残留的「UDP低延迟模式」选项和「以管理员身份重启」链接已删
（两处状态必然打架），卡片 374→294、窗口 666→586。

### 管理面板：两个真 bug

1. **反馈永远「加载中」** —— `bootAll()` 调用的是 `bindNotes()`，
   而那个函数根本不存在（真名 `bindAllNotes`，且它自己注册了 DOMContentLoaded）。
   一执行就 ReferenceError，后面 `bindNetCtl/bindFeedback/bindNewCode` 全被跳过。
   这个错误是 `08cc62a` 加反馈时写进来的，**反馈面板从上线起就没加载过**。
   之前的验证只查 HTML 结构、不执行 JS，所以一直没暴露。
2. **备注输入框每 3 秒被吃一次** —— `render()` 末尾按 rank 重排所有行
   （`appendChild`），正在编辑的 input 因此失焦、内容清空。
   现在检测到 `.notecell.editing` 就跳过重排。

### 下载页改版

原来是「居中标题 + 一摞同宽卡片」：三段 SmartScreen 说明 + 三步 + 密钥规则
全平铺，一屏塞不下重点也看不出来。改成：

- **双栏 hero**：左边文案 + 两个按钮 + 三个信任点，右边一张版本卡（版本/大小/要求/安装位置）
- **三列步骤**，一屏看完
- 其余全部折叠：SmartScreen 三种解法、密钥规则、历史版本、更新日志、下载中断
- 主按钮是「一键安装」(`/download-cmd`)，直接下 exe 降为次要

顺带把 `chcp 65001` 改成 `chcp 936` —— 生成的一键安装脚本是 GBK 文件，
代码页却是 UTF-8，中文全乱码。

### 安装器：旧版进程占用文件时给可执行提示（0.9.13）

0.9.2 的用户装 0.9.13 会撞到：

```
DeleteFile 失败；错误代码 5。拒绝访问。
[重试] [跳过此文件(不推荐)] [取消安装]
```

因为 0.9.2 的客户端是 `requireAdministrator` 进程，而安装器是普通权限，
杀不掉它。现在 `PrepareToInstall` 会先再调一次 `/stop`，仍在运行就明确告诉用户
「托盘右键退出」或「右键安装包 → 以管理员身份运行」，而不是甩 Inno 的原始错误。

⚠ 这是**从旧版迁移时的一次性问题**：0.9.6 起客户端是 `asInvoker`，
普通安装器能正常关掉它，以后的升级不会再遇到。

## v0.13.6 — 2026-10-26

> 客户端 0.9.10 + 安装器升级加固 + 下载页接上一键安装。0.9.9 的客户端二进制没变。

### 升级不再留残留、不再断网

| 问题 | 做法 |
|---|---|
| 安装目录里堆着 `渡口助手.exe.bak` / `.new` / `*.tmp` | `[InstallDelete]` 升级时清掉（都是历史交付方式 —— 覆盖更新脚本 —— 留下的） |
| 旧客户端还在托盘里、装着装着提示「请关闭程序」 | `InitializeSetup` 先调旧客户端的 `/stop`：它会**先还原系统代理**再退出。直接 taskkill 会跳过这步，用户装完就整个网都上不去 |
| 公共桌面残留 0.9.2 时代的重复快捷方式 | `ssPostInstall` 里 `DeleteFile({commondesktop}\渡口助手.lnk)`，删不掉就算了 |

`InitializeSetup` 里不能用 `{app}`（那时还没定下来），改用
`{localappdata}\Ferry\{#AppExeName}` —— 和 `DefaultDirName` 同一个拼法。

### 下载页接上「一键安装」

`/download-cmd` 早就实现了（返回一个十几行的 .cmd，用 WebClient 拉安装包再启动，
程序化下载**不打 MOTW** → 全程不触发 SmartScreen），但下载页一直没给入口。
现在主按钮就是它，直接下载降为下面的次要链接。

顺便把三步文案改了：per-user 之后安装**不再弹 UAC**，页面还在说「是否允许更改设备」是过期的。

### 关于代码签名（结论：不做）

翻了一遍历史：`HANDOVER-打包工作.md` 里已经评估过并放弃了自签名证书 ——
装根证书时 Windows 会弹「你即将安装来自…的证书」，**必须人工点是**，
每台机器都要手动两次点击，比 SmartScreen 的「更多信息 → 仍要运行」还麻烦。

`ferry-sign.pfx` 的密码不在仓库里，也不在任何交接文档里（只写了「不要外发」）。
重新生成一个新证书没有任何收益 —— 除非买 EV 证书（年费几千，要企业资质），
否则签了名对没装根证书的用户依然是「未知发布者」。

**更优解就是上面的一键安装 .cmd**：成本一次双击，比「更多信息 → 仍要运行」还少一步。

## v0.13.5 — 2026-10-26

> 客户端 0.9.9。三个「点了没反应」的问题，根因是同一个：**Job Object**。

### 根因：JobKeeper 把要交接的进程一起杀了

客户端启动时会给进程建一个 Job Object，后面起的 ssh / mihomo 都收进去 ——
本进程一退出（哪怕被强杀），Windows 自动回收它们。这是个好设计，
漏的地方在于：**「要活下来交接的进程」也被收进去了**。

| 症状 | 实际发生的事 |
|---|---|
| 点「以管理员身份重启」→ 程序只是退出 | 中转的 powershell 在 Job 里，旧进程退出时被杀，新实例根本没起来 |
| 点「更新」→ 无事发生（0.9.2 用户反馈） | 下载好的安装包在 Job 里，`RealQuit()` 一执行它就被杀，连解压都没开始 |

修法：所有「交接给外部」的进程一律 `UseShellExecute = true`（走 ShellExecute，
父进程变成 explorer，不进 Job），提权再加 `Verb = "runas"`。

### 主界面加「全局模式」开关

原来只在设置里那一项，勾选后还要自己点「以管理员身份重启」，很反直觉。
现在主界面「刷新 / 设置」中间就是开关：

- 勾上 + 当前不是管理员 → 说明后果、确认后保存配置并提权重启（开关保持打开）
- 勾上/取消 + 正在借网 → 借网链路按新设置重起一次（TUN 才会真的挂上/摘掉）
- 其他 → 只保存 + 刷界面

提权重启后新实例带 `/allow-wait`：旧实例可能还没退干净，直接判「已在运行」
会激活一个正在销毁的窗口然后自杀 —— 现在最多等 10 秒再抢锁。

### 0.9.2 用户升不上来怎么办

自动更新链路在 0.9.2 里是坏的（上面的 Job 原因），**那批用户点更新不会有反应**，
0.9.9 修好之后他们也拿不到 —— 需要手动装一次：

    http://10.13.24.1:8080/download-page  →  下载 Ferry-Setup-0.9.9.exe

装的时候 `[Code]` 检测到 `config.json` 里有 token 会原样保留，激活不受影响。
这一次之后，更新链路就正常了。

### 交付

- `dist/Ferry-Setup-0.9.9.exe` → 已上传远端，`version.json` = 0.9.9
- 桌面 `Ferry-Setup-0.9.9.exe`

## v0.13.4 — 2026-10-26

> 客户端 0.9.7。修「全局接管开着、实际能上网，但系统和软件都说断网」。

### 现象与根因

开 TUN 之后，Windows 网络图标挂着「无法访问 Internet」，部分软件
（跟着系统 Connectivity API 走的那些）也报断网 —— 但网页、下载都正常。

实测定位（同一时刻、同一台机）：

| 路径 | 结果 |
|---|---|
| 经客户端代理 `http://127.0.0.1:10801` 访问 `www.msftconnecttest.com/connecttest.txt` | **200 `Microsoft Connect Test`** |
| 直连同一 URL | 000（8 秒超时） |
| `Get-NetConnectionProfile` | `FerryTun` / `ferry-wg` / WLAN 全部 "无 Internet" |

所以链路是通的，问题在 **NCSI（Network Connectivity Status Indicator）**：
探测由 `NlaSvc` 服务发起，按物理网卡的 NLA 判定走，不一定进 TUN ——
校园网物理链路本来就没有外网，探测必然失败，于是系统判「无 Internet」。

### 做法

TUN 起来后把主动探测关掉（`HKLM\...\NlaSvc\Parameters\Internet\EnableActiveProbing = 0`），
停止 TUN 时恢复原值：

- 原值写进 `%LOCALAPPDATA%\Ferry\ncsi.bak`，客户端被强杀也能在下次启动时恢复
  （`Program.Main` 里会先调一次 `RestoreNcsiProbing`）
- 已经是 0（别人改过）就不覆盖备份值
- 只在 TUN 成功起来之后才改；TUN 启动失败不碰注册表

**副作用必须说清楚**：关掉之后，任何网络都不会再显示「无 Internet」——
真断网时系统不提示了。所以客户端的「无网络」状态行成了唯一线索。
这是取舍：比起「明明能上却到处报断网」，宁可少一个系统提示。

### 同批

- 客户端 0.9.7 / 安装包 `Ferry-Setup-0.9.7.exe`（17.7 MB）已上传远端，
  `version.json` → 0.9.7
- 安装包改为 per-user（`PrivilegesRequired=lowest`）+ 不再弹语言选择

## v0.13.3 — 2026-10-26

> 客户端 0.9.6（per-user 安装、按需提权）+ 管理面板改版。

### 安装不再要管理员：per-user + 按需提权

三个地方一起改，缺一个都不成立：

| 位置 | 改动 | 为什么 |
|---|---|---|
| `app.manifest` | `requireAdministrator` → `asInvoker` | 改系统代理只是写 HKCU，不需要提权；提权只为了 TUN |
| `installer/ferry.iss` | `PrivilegesRequired=admin` → `lowest` | admin + `{localappdata}` 会让标准用户装进**管理员账户**的 AppData，用户自己找不到 |
| 客户端 | 新增 `src/Elevator.cs`，勾了 TUN 且不是管理员时提供「以管理员身份重启」 | 让用户自己右键「以管理员身份运行」不现实 |

`RestartElevated()` 走 `powershell -Verb RunAs`，并且**先等 1.2 秒再启动** ——
单实例互斥锁还握在旧进程手里时直接启动，新进程会判定「已有实例」然后自杀，
表现是「点了没反应」。重启前会先把借网停干净，否则新进程起来时代理指着死端口。

顺带：`schtasks /RL HIGHEST` 只在当前进程已是管理员时才加 —— 普通用户建
「最高权限」任务会被拒绝访问，而纯代理模式用普通权限就够。

### 安装包不再弹语言选择

`[Setup]` 加 `ShowLanguageDialog=no` + `LanguageDetectionMethod=uilanguage`。
之前安装包资源里除了 chs 还有 Inno 内置的英文条目，第一步就是
"Select Setup Language" 的英文框，默认还是 English。

### 管理面板：密钥改浮窗，刷新不再重复生成

`/admin/newcode` 原来是 **POST 后直接渲染整页**，地址栏停在 `/admin/newcode`，
按 F5 浏览器重发 POST —— **每刷新一次多一个密钥**（还会占库存）。

现在：
- fetch 提交 → 回 JSON → 页内浮窗显示明文 + 一键复制（http 内网下
  `navigator.clipboard` 不可用时自动退回选中文本）
- 无 JS / 直接提交 → 302 到 `/admin?new=<code>`，浏览器手里是 GET 地址，刷新安全
- `GET /admin/newcode` → 404，不再有第二条生成路径

另外做了两处结构性优化（功能一个没少）：

- **分区导航** sticky 在顶部：概览 / 密钥 / 反馈 / 设备 / 事件。
  五个区块都是长表格，没有它得靠滚轮翻半天。
- **「新建密钥」折叠**：低频操作不该常驻首屏，点开才展开备注/有效期/档位。

### 验证

| 环境 | 结果 |
|---|---|
| 本地 18094 | fetch 生成→JSON、`/admin?new=` 渲染、GET `/admin/newcode` → 404 |
| 线上 10.13.24.1 | adminnav / sec-* / newcode 折叠 / codeModal / ncForm / bindNewCode 全部命中 |
| 安装包 | `dist/Ferry-Setup-0.9.6.exe` 17.7 MB，已上传远端，`version.json` → 0.9.6 |

⚠ 本机静默安装测试（`/VERYSILENT`）在本环境失败（进程被取消），
推测是 DSH 的进程没有可用的交互桌面来接 UAC/安装向导。**没有在真机上验证装完的效果**，
需要用户实测一次。

## v0.13.2 — 2026-10-26

> 客户端 0.9.5 + 安装器一处修复。借网链路没动。

### 设置页：意见反馈移到左下角，说明文字不再被裁

两处一起改，都是「看起来像坏了」的观感问题：

1. **反馈按钮挪到窗口左下角**，和「保存 / 取消」同一行。它原来塞在开关卡片中间
   （y=224），既把四个开关的阅读顺序切断，还和「UDP低延迟模式」重叠过。
   现在卡片收回 274 高、窗口回到 566。
2. **每个开关的说明改成两行**（Label 高度 18 → 34，行距 52 → 66）。
   原来一行 18px，而文案普遍 30~60 字，在 370px 宽里必然折行 ——
   第二行直接被裁掉半句，用户看到的就是「文字被挤压遮挡」。
   TUN 那条文案顺手压短，保证两行放得下。

布局用离屏渲染（`DrawToBitmap`）核对过，不是靠算坐标。

### 安装器：装完启动报「错误代码 740」

有用户装完立刻弹：

    无法执行文件：C:\Users\<user>\AppData\Local\Ferry\渡口助手.exe
    CreateProcess 失败；错误代码 740。请求的操作需要提升。

客户端 manifest 是 `requireAdministrator`，而 Inno 的 `[Run]` 默认用 CreateProcess
启动 —— 父进程 token 不满足要求时就是这个错，看起来像「装坏了」。
`[Run]` 加 `shellexec`，改走 ShellExecute 由系统自己弹 UAC，用户点一次「是」即可。

⚠ 这条**还没在真机上验过**：本机没装 Inno Setup（没有 `ISCC.exe`），打不出安装包。
装好 Inno Setup 并把签名私钥密码给我，打 0.9.5 的包时一并验证。

## v0.13.1 — 2026-10-26

> 客户端 0.9.4。借网链路没动，全是界面与设置生效性问题。

### 高 DPI：不同缩放比例的机器上文字被裁

Windows 缩放 125% / 150% 时，字体被系统放大绘制，而 WinForms 的控件高度还是
96 DPI 的像素值 —— 结果勾选项下面的说明文字被压掉半行、窗口看起来「尺寸和
文字高度都不对」，不同用户的表现还不一样（取决于他的缩放比例）。

根因是没声明 `AutoScaleMode`：默认 `Font` 模式按「字体 pt 是否和设计时一致」
判断要不要缩放，而我们的字体 pt 固定不动，于是 WinForms 认为**不需要缩放**。
改成 `AutoScaleMode.Dpi` + `AutoScaleDimensions(96,96)`，按实际 DPI 整体放大。

放大之后的新问题：主窗口 748 高在 150% 下变 1122，1080p 屏装不下，底部按钮
点不到。加 `Theme.FitToScreen()` —— 超出工作区就收进来并开垂直滚动条。

### 设置保存后不刷新、不生效

两件事一起修：

1. **界面不刷新** —— 原来 `OpenSettings` 保存完只改了定时器间隔，状态卡还是
   旧文案。现在保存后立刻 `RefreshUi()`。
2. **正在借网时改了「UDP低延迟模式」不生效** —— 隧道是按启动时的设置跑的，
   保存完只是写进 config。现在检测到这一项变了且正在借网，自动「停 → 按新
   设置重开」，用户不用自己再点一次开始借网。

（实现上用一个 `_restartAfterStop` 标记在 DoStop 的回调里接着 DoStart，
不嵌套线程 —— 那个回调本来就在 UI 线程。）

### 主界面常显「UDP低延迟模式」入口

以前没勾全局接管时，这一行是**隐藏**的 —— 结果用户根本不知道有这能力，
游戏 / QUIC / 语音走不了渡口也说不清为什么。
现在常显一行：`全局接管    未启用 · 设置里开启「UDP低延迟模式」`。

新装用户 `useTun` 默认就是 `false`（纯代理模式），符合「默认不走全局接管」。

### 实测

本地 18092 的 41 项服务端验证仍全过（客户端改动不碰门户）。
客户端用 csc 编译通过，0.9.4 / 114 KB。

## v0.13.0 — 2026-10-25

> 客户端 0.9.3（设置里多了「意见反馈」入口）+ 服务端反馈功能 + 首页改版。
> 借网链路一行没动；改动集中在门户页面、反馈表和设置面板布局。

### 意见反馈：不用登录也能说一句

三个入口，同一个表：

| 入口 | 说明 |
|---|---|
| 网页 `/feedback` | 类型 / 联系方式（选填）/ 描述，IP 自动带上 |
| 客户端「设置 → 💬 意见反馈」 | 子窗体提交，自动带客户端版本号 |
| 管理面板「意见反馈」卡片 | 按状态筛选、改状态、加备注 |

**为什么不要登录**：反馈往往来自「还没装好 / 还没激活」的人，
拿登录挡他们等于没这个功能。IP 本身就是身份 —— 借网本来就按 IP 限速、按 IP 激活。

**冷却 60 秒**（不是 180）：反馈是「刚发现问题想说一句」的动作，
3 分钟太长，误触一次就要干等一分钟以上，用户会以为坏了。

**顺序：先校验，后冷却**。上一版把时间戳写在 `_fb_last` 里再去校验长度，
于是「描述太短」这种手滑一次也要等满冷却，用户看到的是「提交太频繁」。
现在只有**真的写进库**才占用冷却，校验失败不计数。

### 管理面板：相同来源折叠

同一个 IP 的多条反馈默认只展开最新一条，其余收在「共 N 条 ▾」里，点开才显示。

一个人连着报五六条（改一版说一次）会把整个面板顶满，别人的反馈被挤到看不见 ——
而面板的用途是「别漏掉任何一条」。折叠只影响**默认可见性**，不合并、不丢数据；
NAT 后面多个人共用一个出口 IP 时，点开就能看到全部。

### 首页改成落地页

`/` 现在是介绍页（下载入口在 `/download-page`）：

- hero + 实时「正在借网 N 台」
- **这些时候最有用**：断网急用 / 下载看视频不吃自己流量 / 只有一台机器能上网
- **它做到了什么**：6 张技术卡（含「UDP低延迟模式」，和客户端设置同一个叫法）
- 三步、9 条 FAQ（新增：会不会扣自己流量、和 VPN 机场的区别、断网了还能不能装）

零外部资源（无 CDN / 无字体 / 无 JS 库），门户断网也能完整打开。

### 修掉一个客户端布局 bug

0.9.3 新加的「💬 意见反馈」按钮放在 `y=224`，而「UDP低延迟模式（全局接管）」
复选框也在 `y=224` —— **两者重叠**，用户看到的是「UDP 那一项没了」。
现在按钮留在原位，TUN 项下移到 `y=272`，卡片加高到 330，窗口 566 → 622。

### 实测

| 环境 | 项数 | 结果 |
|---|---|---|
| 本地 18091（独立 db） | 41 | 0 失败 |
| 线上 10.13.24.1:8080 | 9 | 0 失败 |

线上验证覆盖：页面可达、csrf、提交落库、60 秒冷却真的挡住第二条、
「太短」先于冷却报错、管理 API 可读、折叠标记存在。
自检写入的两条反馈已标记「已解决 / 上线自检，可忽略」。

部署：`scp portal-server.py` → 停旧进程 → 换文件（留 `.bak-<时间戳>`）→
`schtasks /Run /TN FerryPortal`。旧文件已备份在远端 `C:\ferry-portal\`。

## v0.12.0 — 2026-10-25

> 服务端改动，客户端 exe / 安装包版本不变（仍 0.9.2）。

### 借网自动借用远端的 Clash 出口

学校直连上不了 google / youtube（全部超时），而远端 pcs 上跑着 Clash。
现在中继会把借网流量**按竞速分流**：

    客户端 → 中继:1080 ─┬─ 直连目标        国内站
                        └─ 本机 Clash:7890  墙外站

谁先连上就用谁，输的那条立刻取消。

**为什么不用「按目标 IP 判断国内外」**：IP 表不现实且判断错会让国内站
绕道国外。实测「直连能通的 Clash 也通；直连不通的只有 Clash 通」，
所以竞速既不需要 IP 表，也不会让国内站变慢。

**为什么给直连 0.20 秒优先窗口**：纯竞速时 Clash 会抢走国内站
（它的本地握手几乎是瞬时的），于是 baidu 被路由到国外再回来：
又慢、又浪费机场流量。国内站实测 100ms 内建连，0.20 秒稳赢。

### 实测

| 目标 | 结果 | 出口 |
|---|---|---|
| www.google.com | 200 / 0.574s | clash |
| www.youtube.com | 200 / 0.578s | clash |
| github.com | 200 / 0.808s | direct |
| www.baidu.com | 200 / 0.154s | direct |
| www.bilibili.com | 200 / 0.223s | direct |

大流量下限速仍然精确：16.88 MB / 1.70s = **9.95 MB/s**（设定 10）。

泄漏检测：300 TCP + 200 UDP 关联后 threads 7→7、handles 314→318、
mem 40.9→41.2MB，静置后回落，无泄漏。

### 修掉三个超时 bug

| 场景 | 改前 | 改后 |
|---|---|---|
| Clash 端口死 | 2.02s | **0.92s** |
| Clash 假死 | 8.04s | **2.02s** |
| 关闭旁路 | 10.01s | **3.02s** |

最隐蔽的一个：Windows 上连本机**关闭**的端口不立刻回 RST，会重试 SYN
到超时 —— 所以「连本机 Clash」的超时值**直接等于失败耗时**。
0.8 秒已经是 5ms 回环的 160 倍余量。

### 对用户完全透明

客户端不用改，界面无新选项、无提示。日志里只有中继侧两行。

### 新增

- `docs/出口旁路.md`：竞速设计 + 五个超时参数的实测依据
- `tools/stress-clash-fallback.py`：200 并发 + 分流正确性
- `tools/deep-verify-relay.py`：真实 HTTPS 传输 + 限速精度 + UDP
- `tools/verify-leak.py`：泄漏检测
- `tools/test-clash-failures-remote2.py`：六种 Clash 故障模式

---

## v0.11.0 — 2026-10-24

> 客户端 exe 与安装包：**0.9.2**。安装包约 16.9 MB（内嵌 mihomo + wintun）。

这一版主要三件事：**限速真正生效**、**管理面板重做**、**修掉一批会让人误判的 bug**。

### 限速：从「配了但没人读」修成「实测生效」

查证发现限速代码只存在于 `portal/dual-proxy.mjs`（GCRA 按用户），
而客户端从 0.8.0 起改用 C# 内置 `ProxyServer`，压根不启动它 ——
`users.json` 里的 `bytesPerSecPerUser` 没有任何代码读。

更根本的问题：旧路径 `客户端 → ssh -D → pcs sshd` 里，sshd 转发时源 IP
全变成 127.0.0.1，服务端分不清是哪台机器，**旧架构里限速无处可放**。

改成**直连远端 SOCKS5**（`DirectSocks`，默认开）：

```
客户端 ProxyServer(10801) ─┐
                          ├→ pcs:1080 (按源 IP 限速) → 目标
客户端 TUN ───────────────┘
```

- 远端加 `RateTable` / `RateLimiter`（GCRA），按源 IP 限速，TCP/UDP 共用
- 客户端不再启动 ssh，直连 pcs:1080
- 服务端支持 `rate-mb.txt`（两行：每 IP / 全站）**热更新**，改限速不断连接

实测（用远端门户自己的安装包做源，避开国际带宽干扰）：

| 场景 | 实测 |
|---|---|
| 设定 0.5 MB/s | 0.50 MB/s |
| 设定 10 MB/s（直连中继） | 10.03 MB/s |
| 设定 10 MB/s（经客户端转发） | 9.49 MB/s |
| 不走代理参照 | 34.64 MB/s |

代价：链路上是明文 SOCKS5。想加密就关掉 `directSocks` 退回 SSH 隧道
（那时限速不生效）。

### 管理面板重做

**载荷仪表**（替掉原来那行「下行峰值 / 上行峰值」）：

原峰值是个没有参照物的数字 —— 7 MB/s 到底算快算慢没人知道。
换成「当前总速率 ÷ 远端出口带宽」的百分比 + 三色分区（60% 偏挤 / 85% 饱和），
条上两条刻度线就是边界。并且直接写结论：

    ← 瓶颈在单机限速     （有人撞到自己的配额）
    ← 瓶颈在出口带宽     （总量顶到千兆线）

为什么需要这句：千兆出口下 10 MB/s 单机限速很难打满总量，
「才 16%，没问题」是个很自然的误判，真正卡人的是单机限速。

**侧边带宽控制台**：浮层（**不挤压主体**），可折叠。每台上限 / 全站上限 /
出口带宽三个字段 + 3/5/10/20/不限快捷按钮。改完写文件，中继 5 秒内热生效。

**备注内联编辑**：点笔出现输入框并全选，回车或点别处自动保存，Esc 取消。
空备注显示「点击编辑」。笔是内联 SVG 不是 emoji（✏️ 会被渲染成彩色方块）。

**机器详情弹窗**：点表格里的 IP 打开，显示累计流量、历史峰值、
近 24 小时汇总、授权、时间线、最近事件。新增 `traffic` 表持久化累计量
（内存 `_hourly` 只留 24 小时、重启就清空）。

**「模式」列**：直连 / SSH 一眼可见。老客户端不上报这个字段显示「—」。

**IP 着色**：在线用正常文字色（可点击看详情），离线压暗。
原来一律用 accent 绿，一整列绿色把「在线」这个信息稀释了。

### 封禁 / 到期后 5 秒内切回直连

原来只在 `CheckLicense` 里判（定时器 **6 小时**一次），管理员封了人之后
那段时间用户完全无感、还在正常借网。

改成**心跳捎带授权状态**（心跳本来 5 秒一次，零额外请求），
客户端发现被停用立刻停服务 + 切直连 + 重新拉授权。

服务端抽了 `license_state_of()` 给 `/api/license` 和 `/api/heartbeat` 共用 ——
抄两遍迟早不一致，那时候「心跳说能用、授权接口说不能用」，客户端行为不可预测。

### 修掉的一批 bug

| bug | 根因 |
|---|---|
| 恢复直连后打不开外网（**用户报的**） | 原代理探不通时 `SetProxy(false,"")` 只关开关不更新地址 → 留着渡口死端口；且 Clash 晚一点起来也不会自动接回 |
| 安装时弹「错误代码 740」 | 已提权的安装器用 CreateProcess 启动 requireAdministrator 的 exe |
| 隧道永远显示「重连中…」 | 直连模式没有 Tunnel 对象，旧逻辑把「没有隧道」当「重连中」 |
| 管理面板全部显示离线 | 在线判据是「有到 22 端口的 SSH 连接」，直连模式根本不建 SSH |
| 面板改限速「已生效」实际没变 | 门户以 SYSTEM 跑（`%LOCALAPPDATA%` 指向 systemprofile）+ 机器上有两个 Ferry 目录，glob 先命中错的 |
| 备注点笔没反应 | 那段 script 在表格之前，`querySelectorAll` 拿到空集且不报错 |
| 点「停用」/改第二条备注 403 | `csrf_set()` 每次调用都换 token，局部更新后所有表单失效 |
| 堆出 5 个 mihomo-keeper Watch | 提权进程读不到 CommandLine，`-like` 对 null 返回 false → 误判「旧的已死」 |
| 中继每 5 秒一个 UDP ASSOCIATE 从不回收 | `UDP_IDLE_TIMEOUT` 定义了从没被使用 |

### 性能

- **客户端线程池反模式**：`Pump()` 两个方向都用 `Task.Factory.StartNew`，
  而 `copy()` 是同步阻塞的 `Read` —— 线程池遇到一堆阻塞任务只肯每秒注入
  1~2 个线程，还会饿死同一池上的心跳/授权/UI 回调。改成专用线程后：
  **内存 64.2 MB → 24 MB**，线程 61 → 48（40 连接时）
- 加并发上限 400（TUN 全开时所有连接都进这里，无上限会顶到上千线程 + 句柄上限）
- 门户 `_ssh_peers()` 每 2 秒跑一次 `netstat -ano`（实测 81 ms，在请求线程里
  同步执行），而结果只用于一个诊断字段 → 加 30 秒缓存
- `_hb` / `_totals` 内存表只写不删 → 在 janitor 里清理

### 安装包

- 从 2 MB 变 **16.9 MB**（内嵌 mihomo 58 MB，lzma2 压缩后增量约 15 MB）
- `PrivilegesRequired=admin`（TUN 要建网卡，且免 UAC 自启靠计划任务）
- 开机自启改用**计划任务**（`RunLevel=HighestAvailable`），登录时不弹 UAC
- `tools/fetch-vendor.ps1` 打包前取 mihomo 二进制（不入库，61 MB）

---

## v0.10.0 — 2026-10-24

> 客户端 exe 与安装包升到 **0.9.0**（TUN + 默认管理员）。

### UDP低延迟模式（TUN 全局接管）

10810 那套是个 SOCKS5/HTTP 端点，只覆盖「认代理设置」的程序。多数游戏客户端
直接连服务器、根本不看系统代理 —— 对它们来说渡口等于不存在。

TUN 在网络层截获全部流量（含 UDP），才能做到「像普通网络一样」。

**做法**：客户端不再自己实现 TCP/IP 栈，而是把 TUN 层委托给 mihomo：

```
TUN（截获全部流量）→ mihomo → 127.0.0.1:客户端自己的 SOCKS → ssh → 远端
                    ↘ UDP → 远端 Python 中继
```

这样隧道、代理、认证、限速全部沿用现有实现，TUN 只是加在最外层。
TUN 启动失败也不影响 SOCKS 模式 —— 只记 `TunError`，界面明确提示原因。

**实测**：网卡 FerryTun 建立 → 全局出口从 116.23.159.136（电信）变 218.107.0.47
（联通）→ DNS 被 fake-ip 接管（198.18.0.x）→ UDP 3/3 通。

### 客户端默认以管理员启动

`ferry-client/src/app.manifest` 声明 `requireAdministrator`，build.ps1 用
`/win32manifest` 嵌入。用户双击就提权，不用手动「右键 → 以管理员身份运行」
（那个选项从桌面快捷方式启动时还不生效）。

**代价是弹 UAC**，所以配套解决了两件事：

1. **开机自启改用计划任务**（`RunLevel=HighestAvailable`），登录时不弹 UAC。
   实测普通权限创建这种任务报 `ERROR: Access is denied.`（exit 1），
   所以安装器改成 `PrivilegesRequired=admin`。
2. **安装时只弹一次 UAC**（`PrivilegesRequired=admin` + 装完自动建计划任务）。

### 本轮踩的 8 个坑（每条都是实测）

1. **`BaseDirectory` 末尾的 `\` + 引号 = 转义引号**。拼 `-d "C:\...\Ferry\"` 时
   Windows 把 `\"` 当转义字符，mihomo 收到 `-d C:\...\Ferry" -f ...`，
   报 `open ...tun-config.yaml\config.yaml`。修法：`TrimEnd('\\')`。
2. **`AdapterExists` 判「存在」不够，必须判 `Status == Up`**。用
   `netsh admin=disable` 清残留后，「已禁用」被系统记住，mihomo 重建同名网卡
   直接是 Disabled 状态、且**不报错** —— 界面显示「已开启」，流量却全进不去。
3. **`Get-Command python.exe` 第一个是 WindowsApps 的 Store 存根**，几 KB，
   跑起来弹「去 Microsoft Store 安装」然后退出。必须排除路径含 `WindowsApps` 的。
4. **中文路径 + 提权 = 失败**（仅 DSH workspace 目录）。`E:\肥鱼不乐园\` 下的 exe
   无法提权启动，`E:\肥鱼不乐园2\`、`C:\...\测试中文\` 都正常。开发时要注意，
   真正的安装目录 `%LOCALAPPDATA%\Ferry` 不受影响。
5. **`else if` 链中间不能插新分支**。给 TUN 加失败提示时插了个独立 `if`，
   结果后面那个 `else` 挂到了新 `if` 上，把「正在走渡口」覆盖成「借网异常」——
   功能完全正常却报错。
6. **加第四行明细必须同步加高卡片**。`_card` 高度 190、第四行在 y=180 高 18，
   到 198 被底边裁掉一半。改成 200。
7. **wintun.dll 必须和 mihomo.exe 同目录**。mihomo 用 Windows 默认 DLL 搜索
   顺序，放到别处找不到，报错还很含糊。
8. **客户端被强杀时 mihomo 子进程不跟着死**，网卡残留 → 下次启动撞名。
   用 PID 文件 + `Remove-NetAdapter` 清理（`netsh disable` 没用，名字仍占用）。

### 其它

- 安装包从 2 MB 变成 **16.9 MB**（内嵌 mihomo 58 MB，lzma2 压缩后增量约 15 MB）。
  这是 TUN 的硬成本 —— 不带的话 TUN 只能在装了 Clash Verge 的机器上用。
- `tools/fetch-vendor.ps1`：打包前取 mihomo 二进制（不入库，61 MB）。
- 客户端新增 `/start` `/stop` 参数：可用快捷方式一键借网，也让自动化测试
  不依赖窗口坐标。
- `--noproxy '*'` / `-f` 优先级 / `$ErrorActionPreference='Stop'` 等后端坑
  见 [docs/一键部署与UDP.md](docs/一键部署与UDP.md)。

---

## v0.10.0-tools — 2026-10-24

> 工具层版本（`mihomo/` 目录），编号与上面的客户端 0.9.0 独立。
> 这轮把「两条线路合成一个固定端点」和「UDP 打通」做完了，
> 客户端侧的 TUN 见上面的 0.9.0。

### UDP 打通：整条链路第一次能承载游戏和 QUIC

之前两条上游都只实现 SOCKS5 `CONNECT`（`ssh -D` 在协议层面不支持
`UDP ASSOCIATE`；`ferry-socks5-server.py` v1 第 90 行 `if cmd != 1: 拒绝`），
所以游戏、QUIC、部分视频通话全部不能用。这不是配置问题，是能力缺失。

三条路里选了对成本最低的一条：

| 方案 | 结论 |
|---|---|
| 远端加 NAT（WinNAT） | ❌ 两台 Windows 都缺 WMI 提供程序 `0x80041010 WBEM_E_INVALID_CLASS` |
| pcs 装 mihomo 开 socks inbound | ⚠️ 可行（已验证支持 UDP ASSOCIATE），但二进制 61 MB |
| **升级现有 Python 服务端** | ✅ 16 KB → 17 KB，pcs 已有 Python 3.14，零新增依赖 |

`wireguard/ferry-socks5-server.py` v2 新增 `UDP ASSOCIATE`（RFC 1928 CMD=0x03）：
- 每个关联两个独立 UDP socket（client_sock / target_sock），结构上不会混淆
- 回包用**实际收到包的源地址**，不是 TCP 控制连接的源地址
  （踩过的坑：日志显示上下行都正常、客户端全超时，因为 UDP socket 是
  独立创建的，源端口和 TCP 端口不是一个）
- 关联生命周期绑定控制连接，客户端断开就回收

**关键前提（已实测）**：校园网没有只放行 TCP。本机 `10.141.59.235` 到
pcs `10.13.24.1` 的 UDP 直通，5/5 通、平均 6.8ms。这个前提不成立则方案作废，
所以它被写成第一个要验证的事（`probe-udp.py`）。

### 一键部署（`mihomo/install-all.ps1`）

用户的目标是「便捷好用」，所以一个脚本做完六件事，每步都有 `[OK]`/`[X]`：
环境检查 → 远端可达性 → 部署 UDP 中继 → 配本机统一入口 → 端到端验证 → 建快捷方式。

跑完桌面多两个图标：**渡口借网** / **渡口恢复直连**。
之后双击即可，不需要看任何命令。**不需要管理员权限**。

### 单一自启入口（架构修正）

之前启动文件夹有**两个**自启项（`BorrowNetKeeper.cmd` 拉 ssh-tunnel-keeper，
`FerryMihomo.cmd` 拉 mihomo），各自探测端口、各自拉进程。实测出现：
① 两个抢端口，后起的绑不上，隧道时通时断；② `ssh-tunnel-keeper` 根本没在跑
（某次开机后 10801 是**手动**起的），mihomo 连着一条没人保活的隧道。

现在只留 `FerryMihomo.cmd`，跑 `-Action Watch`，由它统一负责整条链：
SSH 隧道不在就自己拉、mihomo 不在就拉起、上游端口变了就重渲染配置。
`Install` 会自动清掉旧的 `BorrowNetKeeper.cmd`（只删渡口自己的）。

### 其它

- `mihomo/config.template.yaml`：新增 `ferry-udp` 节点与 `ferry-udp-group` 组，
  规则加 `NETWORK,udp,ferry-udp-group`。UDP 必须独立成组 —— `ferry` 组里的
  `ferry-ssh`/`ferry-wg` 都是 `udp: false`，选到它们时 UDP 包被静默丢弃。
- 两个上游的 `udp: true` 改成 `false`：写 true 会让 mihomo 真的去试，
  每个 UDP 包先失败再回落，反而更慢。明确关掉更诚实。
- 新增验证工具：`verify-udp-path.py`（完整链路）、`socks5-udp-test.py`
  （直连测服务端）、`probe-udp.py`（出站 UDP）、`udp-echo.py`/`udp-probe.py`。
- 删除无用的 `geoip.dat` / `geosite.dat`（20 MB，规则里一条 GEOIP 都没有）。

详见 [docs/一键部署与UDP.md](docs/一键部署与UDP.md)。

---

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

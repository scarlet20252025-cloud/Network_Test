# 渡口 Android 版 —— 设计与参考项目调研

> 2026-10-29 起。**热点功能暂停推进**（不是取消，`ferry-client/src/Hotspot.cs` 原样保留），
> 主攻手机端。这份文档记录「抄谁、为什么这么选、踩过的坑」。

---

## 一、结论：不造轮子，三层都站在现成方案上

手机端要「所有 App 都走渡口」，只能在网络层接管 —— 也就是 `VpnService` + TUN。
自己实现一个用户态 TCP/IP 栈是几千行且极易做错（拥塞控制、重传、分片、状态机）。
现成方案已经成熟，直接用：

| 层 | 用什么 | 为什么 |
|---|---|---|
| **TUN → SOCKS5 转换** | [`heiher/hev-socks5-tunnel`](https://github.com/heiher/hev-socks5-tunnel)（2089★, MIT） | C 写的 tun2socks，内置 lwIP + 自己的协程调度（hev-task-system），TCP/UDP 都支持，Android 官方提供 `hev-jni.c` JNI 桥 |
| **整体 Android 架构** | [`esrrhs/pingtunnel-client`](https://github.com/esrrhs/pingtunnel-client)（73★, MIT, Kotlin） | **同类项目里最完整的一个**：hev 集成、VpnService 生命周期、分应用代理、热点共存、DNS 防泄漏、重连退避全都有，且有大量单元测试 |
| **备用参考** | [`tokyoxpa3/5G-Proxy-Client`](https://github.com/tokyoxpa3/5G-Proxy-Client)（14★, MIT） | 不用 hev，自己写了 epoll 引擎（`tcp_sess.c` 36KB / `udp_sess.c` 34KB）。**看它的 UDP 会话管理和 reasm（分片重组）实现最有用** |

其他看过的（只作架构参考，不抄代码）：
`2dust/v2rayNG`（64.8k★，Kotlin，太重）、`immaghzbad/AetherST`（484★，也是用 hev）、
`bropines/tailsocks`（137★，Tailscale）、`mbm110/MSN-GUARD`（696★，多引擎，Rust）。

---

## 二、从 pingtunnel-client 学到的、直接决定成败的细节

这些**每一条都是别人踩过的坑**，不是我自己试出来的。抄之前先看懂为什么。

### 1. MTU 必须是 1400，不是 1500

```kotlin
const val MTU = 1400
```

它的注释写得很清楚，两条独立上限：

> 1. hev 回程 UDP 缓冲**写死 1500 字节**（含 SOCKS5 头），更大的报文会被截断；
> 2. QUIC 禁止 IP 分片，服务端会把 UDP 载荷原样发出，公网路径大约只有 1500。
> 1400 时 IPv4 载荷约 1372，加上域名型 SOCKS 头仍低于 1500，且不低于 QUIC 要求的 1200。

**这直接关系到我们的游戏延迟问题** —— MTU 设大了，UDP 大包会被截断，
表现为「游戏卡顿/丢包」，而 TCP 浏览完全正常，极难查。

### 2. DNS 用 mapdns（fake-ip），绝不让系统直连运营商

```yaml
mapdns:
  address: 198.18.0.2      # hev 自己的虚拟 DNS
  port: 53
  network: 198.18.64.0     # fake-ip 池
  netmask: 255.255.192.0   # /18 → 16K 地址，大于 cache-size 10000
  cache-size: 10000
```

原理：DNS 查询由 hev 在本地应答，返回 fake-ip；后续连接经 SOCKS5 **携带域名**
由服务端解析。所以系统 DNS 永远不直连运营商 → 没有 DNS 泄漏。

### 3. fake-ip 池**不能**用 `100.64.0.0/10`

这是运营商 CGNAT 段。旁路内网时，真实的 100.64 专网流量（营业厅/IPTV）需要
在用户态直连，fake-ip 池与它重叠会**互相串流**。改用 `198.18.64.0/18`
（RFC 2544 基准测试段，公网无真实地址）。

> 这条和我们 Windows 端踩坑 72「别把不知道当成知道」是同一个道理：
> 选了段表之外的地址段，就得确认它真的没被别的东西用。

### 4. TUN 地址 / MTU / mapdns 段必须三处一致

`VpnService.Builder` 侧的地址和 MTU，必须和 hev 的 YAML 配置**完全一致**，
否则 hev 起不来或 DNS 走不通。

### 5. `useLegacyPackaging = true`

```kotlin
packaging { jniLibs { useLegacyPackaging = true } }
```

让 `.so` 落盘为真实文件而不是直接从 APK 内存映射 —— 某些设备上 JNI 加载
压缩过的 `.so` 会失败。

### 6. Manifest 的三个关键点

```xml
<service android:name=".FerryVpnService"
         android:exported="true"
         android:foregroundServiceType="specialUse"
         android:permission="android.permission.BIND_VPN_SERVICE">
    <intent-filter><action android:name="android.net.VpnService" /></intent-filter>
    <property android:name="android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE"
              android:value="..." />
</service>
```

`BIND_VPN_SERVICE` 少写 = 系统不认这个 VpnService；
Android 14+ 必须声明 `foregroundServiceType` 并提供 subtype 说明。

### 7. JNI 类名是**写死的**

`hev-jni.c` 在 `JNI_OnLoad` 里 `FindClass("hev/htproxy/TProxyService")` 然后
`RegisterNatives`。所以：

- 要么保留包名 `hev.htproxy.TProxyService`（推荐，直接用官方 `.so`）
- 要么重新编译时用 `Application.mk` 里的 `PKGNAME`/`CLSNAME` 覆盖

**不要改 Java 类名** —— 改了就得重编 `.so`，而重编必须带对宏。

---

## 三、我们的选择：Java + AppCompat，不用 Kotlin/Compose

参考项目用 Kotlin + Jetpack Compose，但我们**故意不用**：

| 维度 | Kotlin + Compose | Java + AppCompat（选这个） |
|---|---|---|
| 首次构建下载量 | 大（Kotlin 编译器 + Compose BOM + 一堆 androidx） | 小（只要 appcompat + material） |
| 构建失败风险 | 高（AGP/Kotlin/Compose 三方版本必须严格对齐） | 低 |
| 与现有代码风格 | 不搭 | 搭（渡口客户端是 C# + 传统 UI，同为 OOP + 声明式 XML） |
| 团队/维护 | 要会 Kotlin + Compose | Java 就够 |

**代价**：UI 写起来啰嗦、没有 Compose 的响应式状态。
但渡口手机端的界面很简单（一个状态卡 + 一个开关 + 日志），不值得为它引入
一整套工具链。业务逻辑（SOCKS5、隧道、激活）本来就是 Java 更直白。

> 如果将来要复杂 UI（连接列表、流量图），再迁 Compose 不迟 ——
> 业务层与 UI 层是分开的，迁移只动 UI。

---

## 四、架构

```
  手机 App（任意）
        │  全部流量
        ▼
  ┌─────────────────────────────┐
  │  Android VpnService（TUN）   │  FerryVpnService.kt
  │  fd 交给 native              │
  └──────────┬──────────────────┘
             │ tun fd
             ▼
  ┌─────────────────────────────┐
  │  libhev-socks5-tunnel.so    │  C: tun2socks（lwIP + 协程）
  │  把 IP 包翻译成 SOCKS5 请求   │
  └──────────┬──────────────────┘
             │ SOCKS5（127.0.0.1:本地代理端口）
             ▼
  ┌─────────────────────────────┐
  │  本地 SOCKS5→HTTP 转换/鉴权  │  Java: FerryProxy（可选）
  └──────────┬──────────────────┘
             │ SOCKS5 over 局域网
             ▼
  ┌─────────────────────────────┐
  │  远端 pcs 10.13.24.1:1080   │  已有 ferry-socks5-server.py
  │  TCP CONNECT + UDP ASSOCIATE│
  └──────────┬──────────────────┘
             ▼
          互联网
```

**关键简化**：手机端不需要 SSH 隧道（那是 Windows 端为了加密才做的）。
手机直接连远端的 SOCKS5 —— 和 Windows 的「直连模式」一样，
服务端能按源 IP 限速。

---

## 五、与现有门户的接口（复用，不改协议）

| 接口 | 用途 | 请求 |
|---|---|---|
| `POST /api/activate` | 用激活码换 token | `{"code":"..."}` |
| `GET /api/license?ip=<ip>` | 查授权状态/到期 | — |
| `POST /api/heartbeat` | 心跳 | — |
| `GET /api/config?with_token=1` | 拉自己的配置 | — |
| `GET /version.json` | 版本检查 | — |

手机端要新增一个「注册设备」的语义 —— 现有 `activate` 是按 **IP** 发授权的
（`accountIp`）。手机在校园网里是同一个 NAT 出口，所以**能复用**，
但两台设备同时用会互相顶掉。这是后续要解决的产品问题，见第八节。

---

## 六、构建方式（本机磁盘不够，必须去远端）

本机 E 盘只剩 19G，Gradle 缓存 + SDK 动辄 10G+。所以：

```
源码：E:\肥鱼不乐园\borrow-net\android\        （只有几 MB，进 git）
构建：10.113.66.6 (xt) 的 D:\AndroidSdk + D:\ferry-android\
```

流程：
1. 本地写代码 → git commit
2. `tools/build-apk-remote.ps1` 把 `android/` 打包传到 xt
3. xt 上 `gradlew assembleDebug`（首次会下载 Gradle + 依赖，之后增量）
4. `adb install` 或把 APK 拉回本地

xt 的 Android SDK 已装好：`platform-tools` / `platforms;android-34` /
`build-tools;34.0.0` / `ndk;27.0.12077973` / `cmake;3.22.1`。

---

## 七、分阶段计划

| 阶段 | 内容 | 验收 |
|---|---|---|
| **M0** | 工程骨架 + 空 APK | xt 上 `assembleDebug` 产出 APK，能装能开 |
| **M1** | 编译 hev-socks5-tunnel → `.so` | `jniLibs/arm64-v8a/libhev-socks5-tunnel.so` 存在，`TProxyIsRunning()` 返回 false 而不是崩 |
| **M2** | VpnService + hev 接通 | 手机上点「开始」→ 系统 VPN 图标出现 → 能打开网页 |
| **M3** | 接渡口激活/心跳 | 用激活码换到 token，能拉到远端配置 |
| **M4** | 断网场景实测 | 校园网禁网时，手机连渡口能上网 |
| **M5** | 游戏/UDP 验证 | UDP 通，延迟可量化（对比直连） |

---

## 八、已知的待解决问题

1. **两台设备会互相顶掉授权**。现有 `/api/activate` 按 IP 发授权，
   手机和电脑在同一 NAT 下共用一条授权。要么改成按设备号，要么明确「一次一台」。
2. **热点没了，手机自己就是终端**。原来 Windows 热点是为了让手机借网；
   现在手机上直接跑客户端，热点功能在 Android 上**天然不需要** ——
   这也解释了为什么 Windows 热点那条路越修越别扭（单网卡并发本来就不成立）。
3. **UDP 延迟**。Windows 端正在查的「卡顿」问题，手机端一样会遇到。
   hev 的 `udp: 'udp'` 是直连远端 UDP 中继，比 TCP 拼装轻，但仍绕一圈。
   需要同一套量化方法。
4. **§ 与 Clash 类 App 共存**。Android 上一个时刻只能有一个 VpnService，
   用户开着别的 VPN/加速器就起不来。要做明确提示，别静默失败。

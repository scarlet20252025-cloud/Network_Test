# Android 版进度

> 2026-10-29 起。热点功能**暂停推进**（`ferry-client/src/Hotspot.cs` 原样保留，未删）。
> 设计与参考项目调研见 `DESIGN.md`。

---

## 已完成

### M0 工程骨架 + 空 APK ← 2026-10-29 09:24

```
android/
├── DESIGN.md                     设计文档（含参考项目调研结论）
├── PROGRESS.md                   本文件
├── build.gradle / settings.gradle / gradle.properties
├── app/
│   ├── build.gradle
│   ├── proguard-rules.pro
│   └── src/main/
│       ├── AndroidManifest.xml
│       ├── java/
│       │   ├── com/ferry/app/
│       │   │   ├── MainActivity.java        主界面（状态卡 + 激活卡 + 日志）
│       │   │   ├── FerryConfig.java         本地配置（SharedPreferences）
│       │   │   └── FerryApi.java            门户 API（激活/授权/心跳）
│       │   │   └── vpn/
│       │   │       ├── FerryVpnService.java VpnService + 隧道生命周期
│       │   │       ├── TunConfig.java       MTU / TUN 地址 / fake-ip 段 / 旁路网段
│       │   │       └── HevConfig.java       生成 hev 的 YAML
│       │   └── hev/htproxy/TProxyService.java  JNI 绑定（类名不可改）
│       └── res/                            布局、颜色、字符串、图标
└── third-part/hev-socks5-tunnel/          参考源码（不进构建，只用来查）
```

**构建结果**（远端 xt，`gradlew` 实际用 gradle 8.7 + JDK 21 + SDK 34）：

```
BUILD SUCCESSFUL in 1m 28s
35 actionable tasks: 35 executed
APK = D:\ferry-android\app\build\outputs\apk\debug\app-debug.apk
大小 = 5.51 MB
```

### M1 native 库 ← 2026-10-29 09:30

```
ndk-build exit = 0
OK   arm64-v8a    346.8 KB
OK   armeabi-v7a  237.7 KB
→ android/app/src/main/jniLibs/<abi>/libhev-socks5-tunnel.so
```

带 .so 重新打包：

```
BUILD SUCCESSFUL in 14s
APK 大小 = 5.8 MB
```

APK 内容校验（用 python 直接读 zip）：

```
lib/arm64-v8a/libhev-socks5-tunnel.so      355144 bytes
lib/armeabi-v7a/libhev-socks5-tunnel.so    243408 bytes
classes.dex                               9970408 bytes
extractNativeLibs / usesCleartextTraffic / BIND_VPN_SERVICE /
android.net.VpnService / FerryVpnService / foregroundServiceType   全部存在
```

---

## 构建方式（本机磁盘不够，去 xt）

本机 E 盘只剩 19G，Gradle 缓存 + AGP + NDK 装不下。xt 的 D 盘有 283G。

```powershell
# 仓库根目录，一条命令出包
powershell -ExecutionPolicy Bypass -File tools\build-apk-remote.ps1
# → dist\ferry-debug.apk

# 只重编 native（hev 源码没改就不用跑）
powershell -ExecutionPolicy Bypass -File tools\build-native-remote.ps1
# → android\app\src\main\jniLibs\<abi>\libhev-socks5-tunnel.so
```

远端环境（已装好，一次性）：

| 组件 | 路径 |
|---|---|
| JDK 21 | `C:\Program Files\Java\jdk-21` |
| Android SDK | `D:\AndroidSdk`（platform-tools / android-34 / build-tools 34.0.0）|
| NDK | `D:\AndroidSdk\ndk\27.0.12077973` |
| cmake | `D:\AndroidSdk\cmake\3.22.1` |
| Gradle 8.7 | `D:\gradle\gradle-8.7` |
| hev 源码 | `D:\hev-src`（含 4 个子模块）|

### M1.5 三个 ABI + 自检（2026-10-29 11:00 后）

```
ndk-build exit = 0
OK   arm64-v8a    346.8 KB      ← 真机主力
OK   armeabi-v7a  237.7 KB      ← 老真机
OK   x86_64       347.6 KB      ← 模拟器
APK = 5.99 MB（含三个 ABI）
```

`app/build.gradle` 里 debug 打三个 ABI、release 只打两个 arm（省约 400 KB）。

**新增 `SelfCheck.java`**：把「隧道到底通没通」拆成五步逐层验证 ——
① tun 网卡有 198.18.0.1 ② hev native 在跑 ③ DNS 能解析 ④ HTTP 能通
⑤ 出口 IP 是远端。第 ⑤ 步最要紧：前四步全过也可能「其实没走隧道」，
只有出口 IP 变了才证明真的在借网。结果同时写 logcat（`FerryUi`）
和 `files/selfcheck.txt`。

界面多了「自检」按钮；另外留了两个自动化入口（普通启动不受影响）：
`am start ... --ez autostart true --ez selfcheck true`。

### ⚠ 路由表写错过一次（已修，值得单独记）

第一版按「`addRoute("0.0.0.0",0)` + `addRoute("10.0.0.0",8)` 排除局域网」写 ——
**完全相反**。`addRoute` 的语义是「把这些网段导进隧道」，加得越具体优先级越高，
等于把局域网也塞进隧道，然后被 hev 送到远端绕一圈：
访问打印机 / 摄像头 / 路由器后台全部超时。而 VpnService **没有**「排除」API。

参考项目给了两条正路：

| 项目 | 做法 |
|---|---|
| `tokyoxpa3/5G-Proxy-Client`（采用）| 不加 `0.0.0.0/0`，显式列出「所有公网单播」的 46 条 CIDR；私网天然不在表内 → 系统走 wlan0 直连 |
| `esrrhs/pingtunnel-client` | 照加 `0.0.0.0/0`，局域网绕过下沉到用户态规则引擎 |

我们用 hev 直连远端 SOCKS5，没有那层规则引擎，所以走前者。
表放在 `TunConfig.PUBLIC_V4`，正确性由 `tools/verify-android-routes.py` 校验
（覆盖、无重叠、五个私网段必须完全不在表内）。

---

## 踩到的坑（都修了，别再犯）

### 0. 路由语义搞反（见上，最重要的一条）

### 1. 远端构建脚本无 BOM → 中文注释破坏语法

`build-apk-on-xt.ps1` 第一次 scp 上去跑，报：

```
位置 C:\tmp\build-apk.ps1:39 字符: 1
+ }
+ ~
表达式或语句中包含意外的标记"}"

```

PS 5.1 把无 BOM 的 UTF-8 当 ANSI 读，中文注释被拆成乱码，破坏了大括号配对。
**所有含中文的 .ps1 必须带 UTF-8 BOM**。批量修：`python C:\tmp\bom_tools.py`。

### 2. 压缩包层级与构建脚本约定不一致

本地打包时把 `android/` 的**内容**打成了 zip 根（`app/` + 三个 gradle 文件），
而远端脚本按 `D:\ferry-src\android` 找 → 报「源码没传上来」。
现在远端脚本的 `$src` 是 `D:\ferry-src`，注释里写明了原因。

### 3. PowerShell 的 `$Host` 是保留变量

参数 `[string]$Host_ = 'xt'` 不合法（`$Host` 是自动变量），
改成 `$Target`。

### 4. 多层引号转义

`ssh xt "powershell -Command \"Remove-Item 'D:\...'\""` 这类嵌套，
PowerShell 解析器会直接报错。**改成把远端要执行的命令写成 .ps1 传过去再跑**。

### 5. python 从 stdin 读代码时中文路径被截坏

`python - <<'PYEOF'` 里写中文路径 → `SyntaxError` 或 `FileNotFoundError`。
本机 python 按系统 ANSI 解码 stdin。**写 .py 文件跑，或用 `\uXXXX` 转义**。

### 6. 本机磁盘

用户明确要求「去远端主机的 D 盘做实验，本机硬破马上没空间」。
所以：源码在本地 git（只有几 MB，`jniLibs` 的 .so 除外），构建全在 xt。

---

## M2 真机验证 ← 2026-10-29 13:02 通过 ✅

**目标机**（用户自己的手机，插 USB 后走 adb）：

```
型号    vivo V1824A (iQOO)
Android 11 (API 30)   —— minSdk 26 / targetSdk 34 都满足
ABI     arm64-v8a
WiFi    10.141.56.25/22（GDPZ-student-5G）
到 pcs   ping 10.13.24.1 → 6.67 / 14.1 ms
```

**结果**：

```
tun0: <POINTOPOINT,UP,LOWER_UP> mtu 1400
      inet 198.18.0.1/30 scope global tun0

自检 5/5：
  ✓ TUN 网卡    tun0 = 198.18.0.1
  ✓ 隧道进程    tx 31157B / rx 37324B
  ✓ DNS 解析    www.qq.com → 120.233.254.239
  ✓ HTTP 请求   HTTP 302
  ✓ 隧道承载    隧道累计已过 68481 字节
  ✓ 出口 IP（仅供参考）  120.235.20.202
```

**独立于 App 的证据链**（不靠 App 自己说）：

| 证据 | 来源 |
|---|---|
| 手机上多条 `10.141.56.25:xxxxx → 10.13.24.1:1080` 的 ESTABLISHED / TIME_WAIT | `adb shell netstat -an` |
| 远端 `10.13.24.1:1080 ← 10.141.56.25:xxxxx` 的 ESTABLISHED | `ssh pcs 'netstat -an'` |
| `socks5 client tcp -> [android.googleapis.com]:443`、`res.rep 5` | 手机内 `files/hev/hev.log` |
| 路由表里 46 条公网 CIDR 全部指向 tun0（table 1033） | `adb shell ip route show table all` |

`res.rep 5` 是 SOCKS5 的「连接被拒绝」——远端确实**尝试**替手机连
android.googleapis.com，而渡口出口是大陆联通，连不到 Google，属预期。
这恰好证明「远端在替手机做事」，比任何间接指标都直接。

### 真机验证踩到的三个坑（都是产品级缺陷，已修）

**① 出口 IP 判据失效**
原来自检第 ⑤ 步是「看出口 IP 是不是变成远端出口」。实测：这台校园网里
pcs(10.13.24.1) 和手机走**同一个 NAT 出口**，开隧道和关隧道拿到的公网 IP
完全一样（都是 `120.235.20.202`）。判据失效比没有判据更糟 ——
它会让人以为隧道没工作。

**② `/proc/net/tcp6` 判据也失效**
改成读 `/proc/net/tcp{,6}` 找「到远端 1080 的连接」。在 adb shell 里
`netstat -an` 明明能看到那些连接，但**应用内读 /proc/net/tcp6 被 SELinux
挡住**（Android 10 起限制非系统应用），读出来是空的。
教训：**用 adb shell 能看到的，应用内不一定看得到** —— 这类权限差异
只能靠应用自己报错暴露。

**③ 最终判据：hev 自己的流量增量**
读 `TProxyGetStats()`，发一次请求，看前后字节数是否增长。
端到端、只依赖自己的数据、不受网络拓扑影响。当前实现：
增量 > 0 直接通过；增量 0 但累计 > 0 也通过（避免「刚好没有新流量」误判）；
两者都 0 才报失败。

**④ `--ez autostart` 静默不生效**
`launchMode="singleTask"`，Activity 已在前台时再 `am start --ez autostart true`
只走 `onNewIntent`，而原来只在 `onCreate` 里处理 → 命令发出去了、
界面毫无变化。已把处理逻辑抽成 `handleAutoIntent(Intent)`，`onCreate`
和 `onNewIntent` 都调。

### 装 APK 到 vivo 的一个操作要点

vivo 对 adb 安装会弹「是否允许通过 USB 安装」的确认框，`adb install` 直接
报 `INSTALL_FAILED_ABORTED: User rejected permissions`。
**必须在手机上点允许**（或在开发者选项里打开「USB 安装」）。
`adb uninstall` 不需要确认，所以「卸载 → 重装」并不能绕过。

---

## M3 激活联调 ← 2026-10-29 13:17 通过 ✅

用真实激活码走完整链路（不再塞假 token）：

```
手机输入 TEST2026 → 点「激活」
  → POST http://10.13.24.1:8080/api/activate
  → 界面：激活成功，剩余 1 天
  → 状态栏：出口 10.13.24.1:1080 · 剩余 1 天
```

服务端落库核对（`ssh pcs` 直接查 `C:\ferry-portal\portal.db`）：

```
invites:  id=53 hint=TEST**** used_by_ip='10.141.56.25' used_at=13:17:49 days=1
clients:  ip=10.141.56.25 token=qr5p23-3ejc7b-br4nwe-pmz6tw enabled=1
          expires=2026-10-30 13:17
```

激活后再点「开始借网」，隧道起来，自检 5/5，两级证据：

| 证据 | 结果 |
|---|---|
| 手机 `netstat` 到远端 1080 的连接 | 7 条 |
| 远端 `netstat` 看到来自 `10.141.56.25` | 多条 ESTABLISHED |
| hev 会话 | `socks5 client tcp -> [voilatile-pa.googleapis.com]:443`、`res.rep 5` |

测试码制作脚本：`C:\tmp\mk-test-code.py`（在 pcs 上跑，直接把
`handle_admin_newcode` 干的事原样做一遍：生成 code → sha256 → 插 invites）。
`note=android-m3-test`，日后可以按这个标记清理。

### 界面 bug：激活码输入框看不见（2026-10-29 用户报）

**现象**：激活码输入框看不到，输入之后也不显示已输入的文字。

**根因**：主题是 `Theme.MaterialComponents.DayNight.NoActionBar`，
但配色只定义了浅色一套（没有 `values-night`）。系统处于深色模式时，
Material 会把默认文字色切成浅色，而卡片背景是写死的白色 `#FFFFFF`
—— 浅色字画在白底上，等于隐身。`EditText` 又没显式指定 `textColor`
（连 `textColorHint` 也没指定），于是完全吃主题默认值。

**改法（两层都改，缺一不可）**：

1. 主题换成 `Theme.MaterialComponents.Light.NoActionBar` 并显式给出
   `colorOnSurface` / `android:textColorPrimary` / `android:textColorSecondary`。
   不补 `values-night` 是有意的：渡口是工具类 App，浅色一套就够，
   两套配色会让以后每个界面改动都要验两遍。
2. `EditText` 三个样样都写死：`background`（新建 `res/drawable/bg_input.xml`，
   白底 + 1dp 描边 + 8dp 圆角）、`textColor`、`textColorHint`。
   这样即使以后有人把主题改回 DayNight，输入框也不会再消失。

顺带修了一个误导：hint 原来是 `xxxxx-xxxxx-xxxxx-xxxxx`（带连字符），
而门户校验是 `[A-Z0-9]{6,16}`，**没有连字符**。改成「6–16 位字母数字」，
`maxLength=16`，`inputType=textCapCharacters`（门户那边会 `.upper()`，
客户端直接大写更直观）。

---

## 下一步

| 任务 | 说明 |
|---|---|
| **M4 禁网实测** | 校园网禁网时手机连渡口 |
| **M5 UDP/游戏** | hev 的 `udp: 'udp'` 走远端 UDP 中继，量化延迟 |
| **真机长期使用** | 手机一直开着 VPN 用一天，看耗电、断线重连、切 WiFi/4G 表现 |
| **Per-App 分流** | 只让指定 App 走隧道（参考 5G-Proxy-Client 的 `PerAppMode`）|
| **同 NAT 互相顶掉** | 服务端按来源 IP 授权，手机和电脑同网时会互相顶 —— 需产品决策 |

---

## 模拟器验证：此路在 xt 上走不通（2026-10-29 实测）

想用模拟器替代真机做运行时验证，结论是**这台机器跑不起来**。
过程与证据都留着，免得下一个人重走。

### 硬件与加速

```
CPU                             Intel Xeon E3-1275 V2  (Ivy Bridge, 2012)
VirtualizationFirmwareEnabled   True
SecondLevelAddressTranslation   True
Hyper-V / HypervisorPlatform    均 Disabled
```

这个组合正是 AEHD 需要的（AEHD 要求 Hyper-V 关闭）。AEHD 2.2 装好之后：

```
emulator -accel-check
→ AEHD (version 2.2) is installed and usable.
```

**加速层是好的，崩的不是它。**

### 崩溃特征（穷举 6 种组合，全部同样失败）

参数维度：`-gpu` = swiftshader_indirect / guest / angle_indirect / host / off / 默认；
`-accel` = aehd（默认）/ off。

统一特征：

```
emulator 父进程          ：活着（有时也崩，exit 0xC0000005 ACCESS_VIOLATION）
:5554 / :5555            ：在监听
qemu-system-x86_64 子进程：不存在
adb                      ：永远 offline（或根本看不到设备）
日志                     ：停在 "INFO | Userspace boot properties: ..." 的属性列表末尾
```

也就是：**qemu 子进程一启动就死**。`-feature -Vulkan` 救不了。

唯一"活下来"的是 `-gpu off` —— 但那是假活：父进程在、端口在听、
`adb` 是 offline，emulator 自身 CPU=0s、内存 9MB，没有 qemu 子进程。
`-gpu off` 等于不给 SurfaceFlinger 任何渲染器，guest 起不来。
**所以判据必须是「adb 变 device 且 sys.boot_completed=1」，不能是「进程还活着」。**

### 排查过的岔路（都不是原因）

- **「same AVD 已在运行」**：新建 AVD `ferry2` 也报同样的 FATAL，
  说明这个判定是**全局**的，不在 AVD 目录里。已删的锁文件：
  `.android/avd/<name>.avd/hardware-qemu.ini.lock`、`multiinstance.lock`、
  `.android/debug.keystore.lock` —— 删了没用。
- **端口残留**：清空所有 emulator / qemu 进程后，5554/5555 **仍在 LISTEN**，
  说明被别的进程占着。存疑但不阻塞（emulator 会另选端口）。
- **计划任务**：第一版没给 `-Principal`，默认「只使用交互方式」，
  SSH 非交互会话下不执行；改 `-LogonType S4U` 后能执行。
  这一条是真坑，与崩溃无关。

### 结论与备选路径

**假设**：emulator 37.2.12 的 x86_64 二进制需要 Ivy Bridge 没有的指令集
（很可能是 AVX2），qemu 一执行到翻译代码就非法指令 → 0xC0000005。
支持证据：所有加速/图形组合崩在**同一个点**，而且崩点是 qemu 刚开始
真正跑客户机代码的位置 —— 与「配置错误」的表现不符。

三条出路，按推荐度：

| 路径 | 说明 |
|---|---|
| **真机**（推荐）| 你在用的手机就是目标设备，校园网也是真实环境。装 APK 后点「自检」即可 |
| 降级 emulator 到 31.x | 31.x 年代的二进制对老 CPU 更宽容。要重新下、重建 AVD，不保证有效 |
| 打开 Hyper-V/WHPX | 用 WHPX 替代 AEHD，但要**重启 xt**（远程构建机，需要你同意）|

### 真机怎么测（不用命令行也行）

1. 手机连上和电脑同一个网（能访问 `10.13.24.1`）
2. 装上 `dist/ferry-debug.apk`（debug 包，可直接装）
3. 打开 → 填激活码 → 点「激活」→ 点「开始借网」→ 系统弹 VPN 授权点确定
4. 连上后点「自检」，把五项结果截图发我
   （证据链：tun 网卡 198.18.0.1 → hev 在跑 → DNS 能解析 → HTTP 通 → 出口 IP 是远端）
5. 有问题就用 `adb logcat -s FerryUi:V FerryVpn:V` 抓一段

`dist/ferry-debug.apk` 的签名已用 `apksigner verify` 确认有效
（v2 scheme，Android Debug 证书）→ 系统接受安装。

### 静态对账（不需要真机就能验的部分，已通过）

| 检查 | 结果 |
|---|---|
| APK 签名 | `Verifies`，v2 scheme，CN=Android Debug |
| 三个 ABI 的 .so 都在包内 | arm64-v8a 355144 / armeabi-v7a 243408 / x86_64 355920 |
| dex 里有 `Lhev/htproxy/TProxyService;` | 是 |
| dex 里四个 native 方法 | `TProxyStartService` / `TProxyStopService` / `TProxyIsRunning` / `TProxyGetStats` 全部命中 |
| 三个 .so 都有 `JNI_OnLoad` + 四个方法名 + 类名 | 6/6 命中（每个 ABI）|
| 签名与 `hev-jni.c` 的 `native_methods[]` 一致 | `(Ljava/lang/String;I)Z` / `()Z` / `()Z` / `()[J` |
| YAML 与路由 | `tools/verify-android.ps1` 全绿（22 + 36 项）|

也就是说：**Java 与 native 的契约是对上的**，剩下的风险集中在运行时行为
（路由是否真的生效、hev 能不能连上远端、UDP 通不通）—— 这些只能真机验。


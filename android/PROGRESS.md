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

## 下一步

| 任务 | 说明 |
|---|---|
| **M2 真机验证** | 装到手机上，点开始 → 系统 VPN 图标 → 能上网（自检五步全绿）|
| **M3 联调激活** | 用真实激活码走一遍 `POST /api/activate` |
| **M4 禁网实测** | 校园网禁网时手机连渡口 |
| **M5 UDP/游戏** | hev 的 `udp: 'udp'` 走远端 UDP 中继，量化延迟 |

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


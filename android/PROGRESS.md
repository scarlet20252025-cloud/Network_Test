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
| **M2 真机验证** | 装到手机上，点开始 → 系统 VPN 图标 → 能上网 |
| **M3 联调激活** | 用真实激活码走一遍 `POST /api/activate` |
| **M4 禁网实测** | 校园网禁网时手机连渡口 |
| **M5 UDP/游戏** | hev 的 `udp: 'udp'` 走远端 UDP 中继，量化延迟 |

真机验证需要一台 Android 手机（或模拟器）。**注意**：
- 模拟器上 hev 需要 x86_64 的 .so，当前只编了 arm 两个 ABI，
  要在模拟器上测就得加 `x86_64`（改 `build-native-on-xt.ps1` 的 `$Abis`）
- 真机走 `adb install -r dist\ferry-debug.apk`

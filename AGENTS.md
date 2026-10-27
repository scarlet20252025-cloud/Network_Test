# AGENTS.md — 渡口 Ferry 开发与维护引导

> 这个文件是给**下一个接手的人（或 AI）**看的。目标：不用重新踩一遍坑就能改对东西。
> 逐条的「为什么」在 `docs/踩坑记录.md`，这里只放**结论 + 去哪看**。

---

## 一、这是什么

校园网按账号禁网时，借一台能上网的内网主机当出口的工具。

```
用户机（被禁网）                      内网主机 pcs (10.13.24.1)          公网
┌──────────────────┐   局域网   ┌────────────────────────┐
│ 渡口助手.exe      │──────────▶│ ferry-socks5-server    │──────▶ 互联网
│  · HTTP/SOCKS5   │  SOCKS5    │ (TCP CONNECT)          │        (联通出口)
│    127.0.0.1:10801│           │ udp-relay (UDP)        │
│  · 可选 TUN      │           │ portal-server.py :8080 │
│    (mihomo)      │◀──────────│ (门户/管理/分发)        │
└──────────────────┘   HTTP    └────────────────────────┘
```

- **客户端**：C# **WPF**（.NET Framework 4.8，csc 直接编译，无 NuGet / 无 XAML）
  - 1.0.x 起从 WinForms 迁到 WPF。**别再退回 WinForms**，理由见第五节第 9 条。
- **门户**：Python 3 单文件 `portal/portal-server.py`（标准库，无第三方依赖）
- **远端**：Windows Server，Python + 计划任务

## 二、目录地图

| 路径 | 是什么 | 改它的时机 |
|---|---|---|
| `ferry-client/src/` | 客户端源码：业务逻辑 + WPF 界面（约 6000 行） | 界面、借网逻辑、TUN |
| `ferry-client/src-winforms/` | **旧 WinForms 界面，只作对照，不参与编译** | 想不起旧版怎么写的 |
| `ferry-client/build-wpf.ps1` | 编客户端（wpf，当前唯一构建方式） | 改编译参数 |
| `ferry-client/build.ps1` | 旧的 WinForms 构建脚本，**已不再使用** | 仅历史参考 |
| `portal/portal-server.py` | 门户 + 管理面板 + 分发（约 6000 行） | 网页、API、密钥、反馈 |
| `installer/ferry.iss` | Inno Setup 安装脚本 | 安装流程、升级清理 |
| `mihomo/` | clash 内核 + TUN 配置模板 | 分流规则、TUN |
| `wireguard/` | UDP 中继与 wg 相关 | UDP 通路 |
| `tools/` | 验证脚本（大多可独立跑） | 加验证、排查 |
| `tools/render-ui.ps1` | **界面截图**（送到 10.113.66.6 渲染再拉回） | 改了界面之后 |
| `tools/verify-cfg-recovery.ps1` | **配置自愈**验证（卸载/重装后令牌能不能找回） | 动过 Config.cs / 安装器 |
| `tools/verify-lifecycle.cs` | **借网生命周期**（反复启停、Clash 开关、UDP、残局） | 动过 FerryRuntime / Ferry.cs |
| `tools/verify-scenarios.cs` | **分流场景矩阵**（接管/节点模式 × 有无代理 × 代理挂掉） | 动过 Proxy.cs 的分流 |
| `tools/verify-updatecheck.cs` | **检查更新链路**（版本接口、窗口构造、版本比较） | 动过 SettingsWindow / Api |
| `docs/` | 原理与踩坑（**先读**） | 每次踩新坑 |

## 三、本地怎么跑

```powershell
# 客户端：编译（不需要 dotnet SDK，用系统自带 csc）
cd ferry-client
powershell -ExecutionPolicy Bypass -File build-wpf.ps1 -Version 1.0.1
# → ..\dist\渡口助手.exe

# 界面截图（在远端测试机上渲染，别在用户自己电脑上弹窗）
powershell -ExecutionPolicy Bypass -File tools\render-ui.ps1
# → %TEMP%\ferry-ui-shots\shot-main.png / shot-settings.png / shot-feedback.png

# 门户：本地起一个独立实例（不碰线上库）
cd portal
python -u portal-server.py --port 18090 --db test-portal.db
# → http://127.0.0.1:18090/ ，管理密码在 portal-admin.txt

# 验证门户（41 项）
python tools/verify-portal-pages.py            # 默认 127.0.0.1:18090
PORTAL_BASE=http://127.0.0.1:18090 python tools/verify-portal-pages.py

# 验证线上（9 项，会真的写一条反馈）
FERRY_ADMIN_PW=<线上密码> python tools/verify-feedback-live.py
```

客户端没有单元测试，靠 `tools/verify-*.py` + `tools/render-ui.ps1`（WPF 离屏渲染）核对界面。

## 四、发版流程（照做，别自创）

```powershell
# 1. 改版本号
#    installer/ferry.iss 的 #define AppVersion
#    build-wpf.ps1 的 -Version 参数会写进 AssemblyInfo

# 2. 编译客户端（WPF）
cd ferry-client
powershell -ExecutionPolicy Bypass -File build-wpf.ps1 -Version 1.0.x
# → ..\dist\渡口助手.exe

# 3. 打安装包（Inno Setup 6；本机装在 %LOCALAPPDATA%\Programs\Inno Setup 6）
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\ferry.iss
# → dist\Ferry-Setup-1.0.x.exe

# 4. 上门户 —— 两个文件都要传！
#    Ferry-Setup-*.exe 决定 version.json 里的版本号（首页「下载」用）
#    渡口助手.exe      是 /download-client 发的东西（自动更新真正下载的）
#    少传第二个的表现：版本号显示新版，但自动更新下下来的还是旧 exe。
scp dist\Ferry-Setup-1.0.x.exe pcs:C:/ferry-portal/dist/
scp "dist\渡口助手.exe"         "pcs:C:/ferry-portal/dist/渡口助手.exe"
scp CHANGELOG.md                pcs:C:/ferry-portal/CHANGELOG.md

# 5. 部署服务端改动（会重启服务，几秒）
scp portal\portal-server.py pcs:C:/ferry-portal/portal-server.py.new
ssh pcs powershell -ExecutionPolicy Bypass -File C:\ferry-portal\_deploy_remote.ps1

# 6. 验收自动更新（必须在测试机上跑真实链路，别手工覆盖）
#    见 docs/踩坑记录.md 第 54 条
scp dist\渡口助手.exe xt:C:/ferry-test/  # 或让测试机自己编一个旧版本
ssh xt 'C:\ferry-update-test\渡口助手.exe /update'
# → 看 C:\ferry-update-test\update-selftest.log 每一步都在，且版本号真的变了
```

`version.json` 不用手写：门户会从 `dist/Ferry-Setup-*.exe` 里推出最新版本。

## 五、动手前必须知道的 13 条（每条都真栽过）

> 这 13 条是「不知道就会重复犯错」的。每条后面标了踩坑编号，
> 详细经过在 `docs/踩坑记录.md`。

### 一、进程与生命周期

1. **Job Object 会杀掉「要交接出去的进程」**。客户端建的 Job 带
   `KILL_ON_JOB_CLOSE`，任何 `Process.Start` 起的子进程（含 ShellExecute）
   在本进程退出时会被一起回收。提权重启必须走 **WMI `Win32_Process.Create`**。
   → 踩坑 44

2. **WMI 的 `Win32_Process.Create` 不认「整个命令行被引号包住」**。
   `"C:\path\app.exe"` 返回码 9（Path Not Found）；
   必须写成 `cmd.exe /c start "" "C:\path\app.exe"`。
   → 踩坑 52

3. **Inno 的 `/CLOSEAPPLICATIONS` 会和托盘程序打架**。它用 Restart Manager
   「礼貌关闭」，而程序在 `OnFormClosing` 里 cancel → 静默失败，
   连错误框都没有。自动更新只传 `/SILENT /NORESTART`。
   → 踩坑 45

### 二、自动更新（这块翻车最多，全流程都要遵守）

4. **更新必须全程在后台线程**。`DoUpdate` 曾经整个跑在 UI 线程上，
   同步下载把界面冻住，用户看到的就是「点完是就没了」，
   而且冻住期间 MessageBox 根本画不出来。
   → 踩坑 53

5. **更新路径不能走 `RealQuit()`**。它会在「正在借网」时弹「确定退出吗」，
   用户点「否」→ 程序还在，但 exe 已经被改好名了（半更新状态）。
   成功后直接 `Environment.Exit(0)`。
   → 踩坑 53

6. **更新时不要重新请求版本号**。`CheckUpdate` 已经拿到 `v.Latest` 了，
   `DoUpdate` 里再调一次 `Api.Version` 是多余的 —— 而且正在借网时
   系统代理指向 10801，这次请求可能超时，失败就静默 return，
   界面上什么都不发生。把版本号当参数传进去。
   → 踩坑 53

7. **更新链路的每一步都要写日志**。之前几次失败，日志只停在
   「发现新版本」，后面一行都没有 —— 到底是下载失败、改名失败
   还是压根没进 DoUpdate，完全看不出来。现在每步都写 `[更新] xxx`。
   → 踩坑 53

8. **「手动复制 exe 后版本号变了」不是验收**。这是我自己犯过的错：
   三次宣布「更新修好了」，三次都是手工覆盖验证的，真实链路一次没跑通。
   **验收必须跑 `渡口助手.exe /update`**（命令行自检入口），
   它跑完整链路并写 `update-selftest.log`。
   → 踩坑 54

### 三、界面（WPF，1.0.x 起）

9. **别再退回 WinForms**。这个 bug 修了七轮（裁字 → 重叠 → 按钮文字消失 →
   `MinimumSize` 压制 AutoSize → `AutoLabel` 量高度），每轮判断都是真的，
   但换个缩放比例又复发。病根在布局模型：WinForms 要求「坐标 + 高度都由代码
   写死」，而字体是 pt 随 DPI 放大，两者天然冲突。
   WPF 是「测量 → 排列」两阶段、尺寸全是 DIP，100%/125%/150% 都不用特殊处理。
   **界面代码只在 `src/*Window.cs` 里**，业务逻辑（借网/代理/TUN/授权/更新）与界面无关。
   → 踩坑 55 / 56（历史），CHANGELOG `v0.14.0`

10. **界面改动必须真渲染一次**。`tools/render-ui.ps1` 会把源码送到
    10.113.66.6 编译 + `RenderTargetBitmap` 离屏渲染三个窗口，再拉回 PNG。
    结构断言全过 ≠ 显示正常（WinForms 时代就有「按钮文字被卡片圆角裁掉、
    元素树里查不到」的先例）。
    → 踩坑 48 / 57

11. **不要在用户自己的电脑上弹窗做验证**。他明确要求过：验证一律在
    10.113.66.6 上做（那台机器随便跑，Session 0 也能离屏渲染）。
    要截本机的真实窗口就用 `PrintWindow(hwnd, hdc, 2)` 离屏抓，别激活/前置窗口。
    → 见 `tools/render-ui.ps1` 与 `docs/踩坑记录.md` 第 60 条

### 四、前端与文档

12. **前端 JS 出错不会让页面报错，只会「某块永远加载中」**。改完管理面板
    一定要用浏览器控制台或 CDP 跑一遍，别只 curl 看 HTML ——
    结构测试全过、页面照样是死的（函数名笔误、脚本没注入）。
    → 踩坑 48 / 57

### 五、凭证与配置

13. **激活令牌不能只存在安装目录里**。`[UninstallDelete]` 是
    `filesandordirs; Name: "{app}"` —— 卸载会把 `config.json` 和同目录的
    `.bak` 一起删掉，用户就得重新找管理员要密钥（真实发生过）。
    现在有三层：`Cfg.Save` 额外写 `%APPDATA%\Ferry\config.backup.json`（不在卸载
    范围）、启动时 `Cfg.RecoverFromRoamingBackup` 自愈、安装器 `ssPostInstall`
    也查这份备份。两条反直觉的边界写在 `Config.cs` 的注释里：
    **只在有 token 时写备份**、**空 token 的保存不覆盖有 token 的 `.bak`**。
    → 踩坑 62，验证 `tools/verify-cfg-recovery.ps1`

---

## 五之二、几个「一犯再犯」的元教训

这些不是技术点，是做事方式上的坑，比上面 12 条更容易重复。

| 教训 | 具体表现 | 以后怎么做 |
|---|---|---|
| **别用手工操作代替端到端验证** | 手动复制 exe 后看到版本变了就宣布「更新修好了」，真实链路一次没通 | 有命令行入口就跑入口；没有就先加一个 |
| **别在没定位的情况下改代码** | DPI 问题改了三次 `dpiAware`/`AutoScaleMode`，全错 | 先写诊断程序量出数据，再动手 |
| **结构测试通过 ≠ 功能正常** | 门户 38 项验证全绿，但反馈面板从上线起就没加载过（JS 报错） | 页面类改动必须真渲染一次 + 看控制台 |
| **别让两个地方存同一份状态** | 「全局模式」在设置里是复选框、主界面也是开关，两边不同步 | 状态只有一个来源，另一处只读显示 |
| **开关要用「意图」判断，不用「实际状态」** | 全局模式按钮按 `rt.TunRunning` 判断，TUN 起不来时点「关闭」反而去「开启」，永远关不掉 | 开关只问用户意图（配置里的那个字段），实际状态只用于显示 |
| **注释要写「为什么」** | 代码里每条注释都在解释当初为什么这样，这是防回归的主要手段 | 改代码时如果删掉了「为什么」，先想清楚再删 |
| **别去适配每个用户的第三方软件** | 「自动把节点写进 Clash 配置」试了四版（Verge Merge / CFW parsers / CFW config.yaml / 只给片段）全废：CFW 有绿色版、各客户端路径不同、写错会毁人家的配置 | 方案需要「枚举用户装了什么、配置在哪」时，先问是不是在绕弯 —— 能不能变成「我不需要知道他用什么」（→ 踩坑 66） |
| **「请去另一个界面点按钮」是 bug** | 设置里点检查更新 → 弹「请回主界面点更新按钮」，而那个按钮不一定在 | 把能力传过去（回调/参数），别把用户送过去（→ 踩坑 67） |
| **分支逻辑要验场景矩阵** | 分流只验了「国内通/境外通」，漏掉「没有代理」「节点模式成环」「代理挂掉日志刷屏」三个真 bug | 列「模式 × 目标 × 上游状态」的表逐格验（→ 踩坑 68） |

## 六、约定

- **中文写注释，且写「为什么」**：代码里几乎每条注释都在解释「当初为什么这样」，
  因为下一版最容易把正确的改成错误的。只写「做什么」的注释会被删掉。
- **不引第三方依赖**：客户端只用 .NET Framework 自带程序集，门户只用 Python 标准库。
  这是「目标机器什么都不用装」的前提，别为了省事退让。
- **验证脚本跟着功能一起改**：`tools/verify-*.py` 里写死了对页面的结构断言，
  改页面不更新它，验证会**假绿**或者假红。
- **提交信息写现象和根因**，不写「fix bug」。看 `git log` 就是在看排障记录。

### 四条硬规则（2026-10-26/27 用真实事故换来的）

- **绝不主动删用户的配置文件**。程序任何自动流程只做「没有就补」，不做「有就删」；
  卸载也只清安装目录（那是 Inno 的职责），`%APPDATA%\Ferry` 里的激活备份必须留着。
  一次「为了验证服务端恢复」手删 config.json，直接让用户的机器失去激活、断网 40 分钟。
- **远端服务的启停不许用 SSH 会话里的 `Start-Process`**。SSH 一断，Windows 的
  Job Object 会把那个子进程一起回收 —— 服务起不来，而你以为它起来了（2026-10-27
  就这么让所有人断网一次）。一律用 `schtasks /run /tn <任务名>`（任务不挂在任何会话下），
  或者已有的计划任务；改完必须 `netstat -an | findstr ":端口 " | findstr LISTENING` 确认。
- **不要去适配用户的第三方软件配置**。渡口只做「一个代理端口 + 自己分流」，
  不写、不改任何 Clash/加速器的配置文件。试过四版全废（踩坑 66），而且写错
  会毁掉人家的翻墙配置 —— 那比「不做」严重得多。
- **在用户自己的机器上，重启/杀客户端 = 断他的网**。这台机器本身就是靠渡口借网上网的，
  停掉客户端，代理和 TUN 同时消失，而他没有任何别的出口。
  所有需要重启的验证（更新、卸载、残局清理）一律去 **10.113.66.6**。

## 七、当前已知限制（不要误判成 bug）

- 直连模式下链路是明文（同一局域网可嗅探域名/IP）；要加密得关掉「直连模式」。
- TUN 下 Windows 的「无 Internet」提示会被主动关掉（`ncsi.bak` 存原值），
  真断网时系统不再提示。
- 每台限速在服务端执行，客户端改配置绕不过去。
- 安装包未签名 → 浏览器直接下载会触发 SmartScreen；走「一键安装」脚本不会。
- **安装包在部分机器上装不上**：错误 4551「应用程序控制策略已阻止此文件」。
  这是 Smart App Control / WDAC 禁止从 `%TEMP%` 执行程序，而 Inno 的
  SetupLdr 天生就要那么做（改不掉，`UseSetupLdr=no` 会把单文件拆成三个）。
  兜底是 `dist/Ferry-Portable-<ver>.zip` + 门户 `/download-portable`。
  → 踩坑 69

## 八、文档索引

| 想知道 | 看 |
|---|---|
| 整体原理、三种模式怎么选 | `docs/架构与原理.md` |
| 某台机器装不起来 / 连不上 | `docs/故障排查.md` |
| 历史上每个坑的来龙去脉 | `docs/踩坑记录.md` |
| 每个版本改了什么 | `CHANGELOG.md` |
| 从零部署一套（含远端） | `docs/远端部署.md`、`docs/一键部署与UDP.md` |

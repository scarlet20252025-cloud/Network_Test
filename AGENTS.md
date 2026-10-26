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

- **客户端**：C# WinForms（.NET Framework 4.8，csc 直接编译，无 NuGet）
- **门户**：Python 3 单文件 `portal/portal-server.py`（标准库，无第三方依赖）
- **远端**：Windows Server，Python + 计划任务

## 二、目录地图

| 路径 | 是什么 | 改它的时机 |
|---|---|---|
| `ferry-client/src/` | 客户端全部源码（约 6000 行） | 界面、借网逻辑、TUN |
| `portal/portal-server.py` | 门户 + 管理面板 + 分发（约 6000 行） | 网页、API、密钥、反馈 |
| `installer/ferry.iss` | Inno Setup 安装脚本 | 安装流程、升级清理 |
| `mihomo/` | clash 内核 + TUN 配置模板 | 分流规则、TUN |
| `wireguard/` | UDP 中继与 wg 相关 | UDP 通路 |
| `tools/` | 验证脚本（大多可独立跑） | 加验证、排查 |
| `docs/` | 原理与踩坑（**先读**） | 每次踩新坑 |

## 三、本地怎么跑

```powershell
# 客户端：编译（不需要 dotnet SDK，用系统自带 csc）
cd ferry-client
powershell -ExecutionPolicy Bypass -File build.ps1 -Version 0.9.15
# → ..\dist\渡口助手.exe

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

客户端没有单元测试，靠 `tools/verify-*.py` + 离屏渲染（`DrawToBitmap`）核对界面。

## 四、发版流程（照做，别自创）

```powershell
# 1. 改版本号
#    installer/ferry.iss 的 #define AppVersion
#    build.ps1 的 -Version 参数会写进 AssemblyInfo

# 2. 编译客户端
cd ferry-client
powershell -ExecutionPolicy Bypass -File build.ps1 -Version 0.9.x

# 3. 打安装包（Inno Setup 6；本机装在 %LOCALAPPDATA%\Programs\Inno Setup 6）
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\ferry.iss
# → dist\Ferry-Setup-0.9.x.exe

# 4. 上门户，并同步 CHANGELOG（否则网页「更新日志」是空的）
scp dist\Ferry-Setup-0.9.x.exe pcs:C:/ferry-portal/dist/
scp CHANGELOG.md               pcs:C:/ferry-portal/CHANGELOG.md

# 5. 部署服务端改动（会重启服务，几秒）
scp portal\portal-server.py pcs:C:/ferry-portal/portal-server.py.new
ssh pcs powershell -ExecutionPolicy Bypass -File C:\ferry-portal\_deploy_remote.ps1
```

`version.json` 不用手写：门户会从 `dist/Ferry-Setup-*.exe` 里推出最新版本。

## 五、动手前必须知道的 12 条（每条都真栽过）

> 这 12 条是「不知道就会重复犯错」的。每条后面标了踩坑编号，
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

### 三、界面与 DPI

9. **控件高度绝对不能写死**。这是「文字下面一半被遮挡」的真正原因，
   反复改 `dpiAware` / `AutoScaleMode` 都修不好 —— 因为病根不在缩放模式，
   而在 `AutoSize = false` + `SetBounds(..., 18)`：字体是 pt 单位随 DPI 放大，
   125% 下 8.5pt 中文单行就要 20px，18px 的框必然裁掉 2px。
   **正确做法**：Label / CheckBox 一律 `AutoSize = true` + `MaximumSize` 限宽，
   高度交给 WinForms 自己算。
   → 踩坑 55

10. **不要用 `TextRenderer.MeasureText` 自己算中文高度**。`WordBreak`
    依赖空格断行，中文没有空格 → 它认为一行放得下、返回单行高度，
    而 Label 实际渲染会按字符折行。结果「按测量值给高度」依然被裁。
    要量高度就用一个临时 Label 的 `PreferredHeight`。
    → 踩坑 55

11. **AutoSize 的控件在还没有父容器时 `Height` 是 0**。读它来算间距会得到
    0，导致相邻控件重叠。要么先 Add 到容器再读，要么用探针 Label 另量。
    → 踩坑 56

### 四、前端与文档

12. **前端 JS 出错不会让页面报错，只会「某块永远加载中」**。改完管理面板
    一定要用浏览器控制台或 CDP 跑一遍，别只 curl 看 HTML ——
    结构测试全过、页面照样是死的（函数名笔误、脚本没注入）。
    → 踩坑 48 / 57

---

## 五之二、几个「一犯再犯」的元教训

这些不是技术点，是做事方式上的坑，比上面 12 条更容易重复。

| 教训 | 具体表现 | 以后怎么做 |
|---|---|---|
| **别用手工操作代替端到端验证** | 手动复制 exe 后看到版本变了就宣布「更新修好了」，真实链路一次没通 | 有命令行入口就跑入口；没有就先加一个 |
| **别在没定位的情况下改代码** | DPI 问题改了三次 `dpiAware`/`AutoScaleMode`，全错 | 先写诊断程序量出数据，再动手 |
| **结构测试通过 ≠ 功能正常** | 门户 38 项验证全绿，但反馈面板从上线起就没加载过（JS 报错） | 页面类改动必须真渲染一次 + 看控制台 |
| **别让两个地方存同一份状态** | 「全局模式」在设置里是复选框、主界面也是开关，两边不同步 | 状态只有一个来源，另一处只读显示 |
| **注释要写「为什么」** | 代码里每条注释都在解释当初为什么这样，这是防回归的主要手段 | 改代码时如果删掉了「为什么」，先想清楚再删 |

## 六、约定

- **中文写注释，且写「为什么」**：代码里几乎每条注释都在解释「当初为什么这样」，
  因为下一版最容易把正确的改成错误的。只写「做什么」的注释会被删掉。
- **不引第三方依赖**：客户端只用 .NET Framework 自带程序集，门户只用 Python 标准库。
  这是「目标机器什么都不用装」的前提，别为了省事退让。
- **验证脚本跟着功能一起改**：`tools/verify-*.py` 里写死了对页面的结构断言，
  改页面不更新它，验证会**假绿**或者假红。
- **提交信息写现象和根因**，不写「fix bug」。看 `git log` 就是在看排障记录。

## 七、当前已知限制（不要误判成 bug）

- 直连模式下链路是明文（同一局域网可嗅探域名/IP）；要加密得关掉「直连模式」。
- TUN 下 Windows 的「无 Internet」提示会被主动关掉（`ncsi.bak` 存原值），
  真断网时系统不再提示。
- 每台限速在服务端执行，客户端改配置绕不过去。
- 安装包未签名 → 浏览器直接下载会触发 SmartScreen；走「一键安装」脚本不会。

## 八、文档索引

| 想知道 | 看 |
|---|---|
| 整体原理、三种模式怎么选 | `docs/架构与原理.md` |
| 某台机器装不起来 / 连不上 | `docs/故障排查.md` |
| 历史上每个坑的来龙去脉 | `docs/踩坑记录.md` |
| 每个版本改了什么 | `CHANGELOG.md` |
| 从零部署一套（含远端） | `docs/远端部署.md`、`docs/一键部署与UDP.md` |

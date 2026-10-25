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

## 五、动手前必须知道的 8 条（每条都真栽过）

1. **Job Object 会杀掉「要交接出去的进程」**。客户端建的 Job 带
   `KILL_ON_JOB_CLOSE`，任何 `Process.Start` 起的子进程（含 ShellExecute）
   在本进程退出时会被一起回收。提权重启必须走 **WMI `Win32_Process.Create`**。
   → 踩坑 44

2. **Inno 的 `/CLOSEAPPLICATIONS` 会和托盘程序打架**。它用 Restart Manager
   「礼貌关闭」，而程序在 `OnFormClosing` 里 cancel → 静默失败。
   自动更新只传 `/SILENT /NORESTART`。
   → 踩坑 45

3. **安装器脚本里改 `[Run]` 之类的段名要按行匹配**。注释里也出现过 `[Run]`
   这个词，字符串替换会误伤（曾经把文件头注释改成了 `[InstallDelete]`）。
   → 踩坑 46

4. **门户的 CSS 分两块**：`BASE_CSS`（全站）和 `EXTRA_CSS`（介绍页/反馈页/管理面板
   共用）。新增页面样式要放 `EXTRA_CSS`，只放 `LIVE_UI` 里会导致「结构对、样式全丢」。
   → 踩坑 47

5. **前端 JS 出错不会让页面报错，只会「某块永远加载中」**。改完管理面板一定要用
   浏览器控制台或 CDP 跑一遍，别只 curl 看 HTML。
   → 踩坑 48

6. **每 3 秒刷新的表格不能重排 DOM**，否则正在编辑的输入框会失焦、内容被清空。
   → 踩坑 49

7. **WinForms 在 per-monitor DPI 下算不出缩放因子**，界面必须停在 System DPI aware
   （manifest `true + system`）。
   → 踩坑 50

8. **改完客户端/服务端要重启对应进程才生效**。门户有热重启脚本；
   客户端要退出再启动（托盘右键）。

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

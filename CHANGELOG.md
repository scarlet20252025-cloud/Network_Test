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

# 待办任务（持久化）

> **为什么要这个文件**：`todo_write` 的清单只活在一轮对话里，DSH 上下文一压缩、
> 或者会话一续，待办就没了 —— 已经因此丢过好几次。所以待办**以这个文件为准**，
> 每次动手前后都更新它。`todo_write` 只当当前这一轮的临时视图用。

最后更新：2026-10-29 20:56

---

## 正在做

- [ ] **CS2 延迟实测（等用户打一局报 ping）**：1.0.47 已装上并上传门户
  - 2026-10-29 20:41 已确认本机装的是 **1.0.47.0**（注册表 DisplayVersion=1.0.47）
  - 门户 `/version.json` 已报 `1.0.47`，`dist/` 里两个文件都传了
    （安装包 17728206 B + 客户端本体 194048 B/1.0.47.0）
  - 自动更新链路已用 `渡口助手.exe /update` 自检验证通过
  - **下一步：用户打一局 CS2，回报游戏内 ping**

## 已修待验证（1.0.48，2026-10-29 20:55）

- [ ] **界面卡死（1.0.47 报的「点什么都卡死很久」）** —— 已定位并修好，等验证
  - **实测证据**：1.0.47 在「直连、没借网、没 TUN」的空闲状态下，
    UI 线程 5 秒吃掉 **4.09 秒 CPU（≈82% 单核）**，单线程 37404 从启动起
    一直是 Running，累计 4 分 14 秒；而此时日志 5 秒 0 字节增长
    （说明它没在处理业务，纯粹在空转重绘）。
  - **根因（两个叠加）**：
    1. `RefreshUi()` 每秒跑，里面有 **40 处 `.Text =` + 37 处 `.Foreground =`**，
       全部**无条件赋值**。WPF 的 Text/Foreground 是依赖属性，
       赋内容相同的新值也会走完整的 失效→measure→arrange→重绘。
    2. `Palette` 的 12 个 Brush 全是
       `public static Brush TextB { get { return Freeze(new SolidColorBrush(Text)); } }`
       —— **每次 get 都 new 一个**。每秒 37 次 Foreground 赋值 = 每秒 37 个新画刷；
       WPF 用引用相等判等 → 每次都被当成「值变了」→ 必然重绘。
  - **修法**：Brush 改 `static readonly`（注意声明顺序：Color 在前）；
    新增 `SetIfChanged/SetText/SetFg`（先比后写），77 处赋值全部改走它们。
  - **关键澄清**：1.0.47 并没有改界面代码（那一版只有版本号 + TunMode 的 YAML
    改动），所以**不是改坏了界面**，而是既有的性能缺陷在这个版本到了阈值。
  - 验证脚本：`tools/verify-ui-cpu.ps1`（测 10 秒 CPU 增量，基线 82%，期望 ≤5%）
  - 提交 `c9e17f7`；产物 `dist/渡口助手.exe` **1.0.48.0**
  - **下一步：用户打完游戏后，用 1.0.48 重开客户端，跑 verify-ui-cpu.ps1 验证**

## 已解决（2026-10-29 20:41）

- [x] **1.0.47 装不上（「无法创建目录 %TEMP%\is-XXXX.tmp，错误 5」）**
  - **根因：安装包放在中文路径下运行**。`E:\肥鱼不乐园\borrow-net\dist\` 含中文，
    而 Inno 的 SetupLdr 在「从非 %TEMP% 路径启动」时，要先把自己复制到 %TEMP%
    再带 `/SL5` 重跑 —— 这一步在中文源路径下失败，报错却指向 `%TEMP%\is-*.tmp`，
    **报错信息指向了错误的位置**，导致一开始误判成权限/杀软问题。
  - **证据**：对照 10-26 那次成功安装的日志
    （`%TEMP%\ferry-inno.log`）：
    ```
    成功(10-26): Original Setup EXE: %TEMP%\Ferry-Setup-0.9.14.exe
                 Created temporary directory: %TEMP%\is-X53JK277XJ.tmp  ✅
    失败(今天):  直接从 E:\肥鱼不乐园\...\dist\ 运行 → 日志一行都没生成
    成功(复现):  复制到 %TEMP% 再运行
                 Created temporary directory: %TEMP%\is-2YJ5BEU1NT.tmp  ✅
                 Installation process succeeded.
    ```
  - **排除的假设**（都实测过，别再重查）：%TEMP% ACL 正常、可建目录可复制可执行、
    磁盘 9.8G 充足、无 Zone.Identifier、Smart App Control 关闭、无 AppLocker、
    火绒（HipsDaemon 在跑但**不是它拦的**——手动从 Temp 执行 exe 完全正常）。
  - **结论**：历史安装包都是从门户下载到 `%TEMP%` 再运行的，所以一直没暴露；
    从仓库目录直接双击才会中招。**自动更新不受影响**（它本来就下载到本地再执行）。
  - 复现/安装脚本：`install-1.0.47.cmd`（先 `copy` 到 `%TEMP%` 再装）

- [x] **网卡省电休眠**：`IpsEn` 1 → 0（Realtek 8852BE，注册表
  `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-...}\0007`），已生效。
  脚本 `tools/fix-nic-powersave.ps1`（带 `-WhatIf` / `-Revert`）。
  - ⚠ **但它不是卡顿的根因**：改完复测，连续 p50 反而到 75ms、120ms 间隔 52ms，
    两组都抖。先前「省电模式是根因」的结论**被自己的数据推翻**。
  - 仍在查的方向：WiFi 信道干扰（SSID `GDPZ-student-5G`，信道 40）或上游拥塞。

## 已确认的排查事实（免得重查）

- **本机 adb 在 `C:/tmp/ptools/platform-tools/adb.exe`**（8.2 MB，2026-10-29）。
  `C:\Windows\System32\adb.exe` 是老版本 1.0.26（2010 年），daemon 起不来，别用。
  这个路径写在 `tools/test-android-phone.sh` 第 24 行的 `PT_ADB` 默认值里。
- **装了什么安全软件**：火绒安全软件 6.0.12.3（`D:\Huorong\Sysdiag\bin`，
  HipsDaemon + HipsTray 在跑）。Windows Defender 服务是 Stopped。
  另有 McAfee 注册在 WSC 里但没进程。

## 已上线（2026-10-29 20:06 部署到 pcs，服务 FerryPortal 运行正常）

- [x] **管理面板「设备维度」**：`/admin/users` 顶部新增「已登记的设备」表
  （状态/机型/平台/版本/最近 IP/设备标识/最后活动/账号到期/授权/操作），
  每台设备两个动作：**踢下线**（enabled=0，可在面板上恢复）和
  **删除**（真删行，腾名额）。下面原来的「按 IP」表保留，两表并存。
  - 顺带修了一个真 bug：`device_bind` 对已存在设备**无条件写 `enabled = 1`**，
    所以「踢下线」等于没用 —— App 下次启动一查 `/api/license` 就自己复活了。
    现在被停用的设备直接拒绝（`device_disabled`），只有管理员能恢复。
  - 拒绝原因也分开了：以前一律带 `device_limit=True`，用户会看到「设备数已达
    上限」却被拒；现在 `device_limit` / `device_disabled` 分开报，
    `/api/device/restore` 也把「没登记过」「被停用」分成两种说法。
  - `/api/activate` 里原来的 `code_consumed: true` 与代码实际行为相反
    （密钥核销的 UPDATE 在那之后才执行），改成 false，避免误导用户去要新密钥。
- [x] **修管理面板概览页少了 csrf 隐藏域**：限速卡的「应用」按钮
  和保存备注都靠 `querySelector('input[name="csrf"]')` 取 token，
  概览页一个隐藏域都没有 → 取到空串 → 服务端 403 → 前端 `.catch()` 吞掉，
  表现就是「点了没反应」。实测：空 csrf POST `/api/admin/net` 得 403，
  带正确 token 得 200。**这个问题在概览页一直存在，不是今天引入的。**
- [x] 新增 `tools/verify-portal-device.py`（35 项，含上面那个复活 bug 的回归）
  和 `tools/check-admin-page.mjs`（headless Edge + CDP 真渲染 + 控制台 +
  截图，12 项）。后者补的是 AGENTS.md 第五节第 12 条长期缺的一环：
  门户网页之前只有结构断言，没有真渲染检查。
- 线上数据已确认：devices 表里只有一台真设备
  （vivo V1824A / android / 0.1.0），测试数据没有残留。

## 已确认的技术细节（免得下次重查）

- **mihomo 的 `fallback` 语义**（从二进制日志字符串反推，已确认）：
  `ProxyGroup: %s first failed` / `failed count: %d` / `Start New Health Checking`
  → 按顺序检查，用第一个不失败的；持续健康检查，失败就换下一个。
  所以 `[DIRECT, ferry-udp]` = 有网直连 / 禁网落隧道 / 恢复后切回来，
  不需要自己写逻辑，只需把 interval 调小以缩短发现断网的延迟。
- **hev-socks5-tunnel 没有任何 UDP 分流能力**：全部配置项里没有
  route/exclude/bypass。`udp: 'udp'` 是全有或全无。
- **客户端 manifest 是 `asInvoker`**，不强制管理员。`useTun=true` 时会自己
  `runas` 重启一次（见 `Elevator.cs`），所以 `Start-Process -Verb RunAs` 起它会
  静默失败（UAC 根本不弹），得直接启动。

## 手机不在身边（2026-10-29 17:10 起）

手机被用户带出门了，**Android 侧的一切工作暂停**，等回宿舍再接 USB。
接回后要做的第一件事：
1. 把 `remotePort` 从 1099 改回 1080（做 M4 实验时改的，不改就用不了）
2. 确认 adb 能识别（vivo 需要屏幕解锁，且首次安装要手机上点允许）

## 已知隐患（不急，但记着）

- **Android 端的 UDP 是全局开关**：`HevConfig` 里 `udp: 'udp'` 一旦打开，
  所有 UDP（游戏/QUIC/DNS）都走隧道，和 Windows 1.0.26 同类问题。
  实测 hev 的全部配置项里**没有任何 route/exclude/bypass**，
  所以没法照抄 Windows 的 `fallback [DIRECT, ferry-udp]` 方案。
  可行的替代：① VpnService 层做排除（但 UDP 目标 IP 难枚举）② 保持「只借网时才起隧道」

## 排队中

- [ ] **M4 上游故障与弱网场景实测**
  - 已确认前提：这台手机在校园网里**能直连公网**（自检 2/5 那次 DNS 和 HTTP 都成功），
    所以「禁网」在当前条件下构造不出来 —— 改为验证「上游坏掉时的行为」
  - ⚠ 手机已从 USB 掉线，接回后要先修复配置（做实验时把 remotePort 改成了 1099）
- [ ] **M5 UDP/游戏延迟量化**（Android 侧）：hev `udp: 'udp'` 走远端中继的实测
- [ ] **Per-App 分流**：只让指定 App 走隧道（参考 5G-Proxy-Client 的 `PerAppMode`）
- [ ] **长期稳定性**：挂着 VPN 用一天，看耗电、断线重连、WiFi↔4G 切换
- [ ] **手机端功能补充**（用户让评估）：见下面「候选功能」

## 候选功能（手机端，待用户挑）

| 功能 | 价值 | 代价 |
|---|---|---|
| 流量统计（今日/本月，走隧道 vs 直连） | 高：用户最关心「用了多少」 | 小：`TrafficStats` + 本地累计 |
| 分应用代理（Per-App） | 高：只让指定 App 走隧道，省流量也省心 | 中：App 列表 UI + `addAllowedApplication` |
| 快捷开关（Quick Settings Tile） | 中高：不用进 App 就能开关 | 小：一个 `TileService` |
| 开机自启 / 常驻通知增强 | 中：断了自动恢复 | 小：`BOOT_COMPLETED` |
| 节点/线路选择（多个远端） | 中：远端换机时有用 | 中：服务端下发列表 + UI |
| 延迟测试（到各远端 RTT） | 中：选线路时有用 | 小：自己测，不依赖 hev |
| 深色模式配色 | 低（但已踩过坑，见 PROGRESS.md） | 小：补 `values-night` |
| 日志导出/分享 | 中：报障时最有用 | 小：`FileProvider` + `ACTION_SEND` |
| 订阅到期提醒（通知） | 中：避免突然断网 | 小 |

## 已完成（保留备查）

- [x] **Windows 端游戏延迟回归排查**（2026-10-29 15:47，客户端 1.0.46）
  - 根因：1.0.26 修 UDP 时把 `DIRECT` 从 `ferry-udp-group` 删了，
    导致所有 UDP 都被强制绕远端（本机 → pcs → 目标 → pcs → 本机）。
    0.9.30 是 `[DIRECT, ferry-udp]` 默认直连，所以延迟正常。
  - 改法：`fallback [DIRECT, ferry-udp]` + generate_204 测活。
    能直连就直连，禁网才落隧道。
  - 验证：`tools/verify-udp-group.cs` 全 PASS（反射取真实 YAML 断言）
  - 量化：`tools/measure-udp-latency.cs` —— 隧道本身只贵 0.0ms，
    贵的是「本来能直连的流量被绕了一圈」
  - 顺带修：`mihomo-keeper.ps1` 无 BOM 导致 PS 5.1 解析出 29 个语法错误
  - 产物：`dist/Ferry-Setup-1.0.46.exe`、提交 `9791359`
- [x] **Android 界面完善**（2026-10-29 14:05）：授权卡双形态 —— 显示
  「已激活 · 剩余 23 小时 · 2026-10-30 到期」+ 设备信息，激活后隐藏输入框，
  加「换一个激活码」入口。真机验证通过。
- [x] **设备维度授权**（一个账号多设备，上限 3 台）：服务端 `devices` 表 +
  `/api/device/restore`；客户端用 `ANDROID_ID`。清数据后启动能自动恢复授权，
  真机验证通过（「这台设备之前激活过，已自动恢复授权」）。
- [x] **授权状态监控**：licState 状态机（OK/EXPIRED/INVALID/SUSPENDED/
  DEVICE_LIMIT/OFFLINE）、启动即核对、每 5 分钟兜底、连不上门户不改变可用性。
- [x] Android 工程骨架 + 三 ABI native 库（arm64-v8a / armeabi-v7a / x86_64）
- [x] 路由表修正：46 条公网单播 CIDR（原写法 `0.0.0.0/0` + 排除段语义反了）
- [x] 自检功能（5 项：TUN / 隧道进程 / DNS / HTTP / 隧道承载）
- [x] M2 真机验证通过（vivo V1824A / Android 11，两端连接对账）
- [x] M3 激活联调通过（TEST2026 → 服务端落库核对 → 借网 5/5）
- [x] 界面 bug：激活码输入框看不见（DayNight 配色缺失）
- [x] 模拟器路线结论：xt 的 CPU 太旧，qemu 子进程必崩（见 android/PROGRESS.md）


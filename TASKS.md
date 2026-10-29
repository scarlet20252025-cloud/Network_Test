# 待办任务（持久化）

> **为什么要这个文件**：`todo_write` 的清单只活在一轮对话里，DSH 上下文一压缩、
> 或者会话一续，待办就没了 —— 已经因此丢过好几次。所以待办**以这个文件为准**，
> 每次动手前后都更新它。`todo_write` 只当当前这一轮的临时视图用。

最后更新：2026-10-29 15:50

---

## 正在做

- [ ] **1.0.47 待用户安装验证**：UDP 延迟修复已提交并打好包
  （`dist/Ferry-Setup-1.0.47.exe`），需要用户装上后打一局 CS2 看实际延迟
  - 1.0.46 → 1.0.47 只改了一处：健康检查 30s → 10s，缩短禁网切换的空白期
  - 2026-10-29 17:10 用户确认：「先验证1.0.46，手机我不小心带出门了，我回宿舍再继续弄手机」
  - 下一步等用户回报 CS2 实测 ping

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


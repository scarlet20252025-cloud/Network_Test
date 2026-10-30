# 待办任务（持久化）

> **为什么要这个文件**：`todo_write` 的清单只活在一轮对话里，DSH 上下文一压缩、
> 或者会话一续，待办就没了 —— 已经因此丢过好几次。所以待办**以这个文件为准**，
> 每次动手前后都更新它。`todo_write` 只当当前这一轮的临时视图用。

最后更新：2026-10-30 18:55

---

## 正在做

- [x] **🔴 P0 用户反馈：软件卡顿 + 卸载不成功 —— 已定位并修复（1.0.52）**
      （2026-10-30 用户报，同日修完并端到端验证）
  - **用户原话**：「软件操作非常非常卡顿，界面经常卡死，而且卸载也无法成功卸载，
    完成卸载后只是退出了软件，但实际什么都没变化，快捷方式还在，也能打开软件」
  - **三个症状要分开验，别当成一个 bug**：
    1. **操作卡顿 / 界面卡死** —— 注意 1.0.49 已经修过一轮「界面 CPU 82%→7.2%」
       （根因是 `RefreshUi()` 每秒无条件给 40 处 Text + 37 处 Foreground +
       14 处 Content/Background/Visibility 赋值，见 CHANGELOG v0.14.46 第一节）。
       **先确认用户装的是不是 1.0.51**：看 `%LOCALAPPDATA%\Ferry\` 下 exe 的版本号，
       或门户 `/version.json`。若还是 1.0.47/1.0.48，大概率是老问题没升级。
       若已是 1.0.51 仍卡，则是**新的**性能问题，需重新抓
       （`tools/measure-ui-cpu.ps1`，测 10 秒 CPU 增量）。
    2. **卸载后快捷方式还在、还能打开** —— 这是**明确的安装器 bug**，不是幻觉。
       优先怀疑：`installer/ferry.iss` 的 `[Icons]` 段没有对应 `[UninstallDelete]`，
       或卸载程序没被正确注册（`HKCU/HKLM ...\Uninstall\Ferry` 的
       `UninstallString` 指向错误）。
       **验证方法**：装 1.0.51 → 记录 `%LOCALAPPDATA%\Ferry\`、桌面/开始菜单快捷方式、
       注册表 Uninstall 键 → 跑卸载 → 逐项对比还剩什么。
       ⚠ 硬规则：`[UninstallDelete]` 是 `filesandordirs; Name: "{app}"`，
       **卸载只清安装目录**，`%APPDATA%\Ferry` 的激活备份必须留着
       （别为了"卸载干净"去删它 —— 那是踩坑 62 的根因）。
    3. **「卸载只是退出了软件」** —— 可能是卸载器被进程占用卡住，
       也可能用户点的是「退出」不是「卸载」。要问清楚从哪点的卸载
       （控制面板 / 开始菜单 / 安装包本身）。
  - **复现环境**：用户自己的机器（⚠ 硬规则：重启/卸载/杀客户端 = 断他网，
    所有会中断借网的验证一律去 10.113.66.6 测试机做，或先问用户）。
  - **✅ 已修（1.0.52），详见 `docs/踩坑记录.md` 85 / 86：**
    - **卡顿**：1.0.51 把 `Hotspot.HasWiredUplink()` 放进了每秒的 `RefreshUi()`
      （UI 线程）。实测**每次 70~126 ms**，10 秒里累计占 UI 线程 12.4%。
      修法：改缓存 + ThreadPool 后台刷新（15 秒一次），界面只读一个 volatile bool。
      实测 1.0.52 UI CPU **1.6~1.9%**，Running 线程 0。
    - **卸载不成功**：`AppId` 从未变过，但安装方式从 admin 改成了 per-user，
      于是 HKLM 留了老版 0.9.1 的卸载项、HKCU 是新的，**两条指向同一个
      unins000.exe**。用户点中旧那条 → 只清 HKCU → HKLM 那条留着 →
      控制面板/快捷方式都还在。
      修法：客户端 `/uninstall` 检测到 HKLM 残留时，用 WMI+runas 提权
      跑 `/elevated-regclean` 删掉它（安装器是 lowest，删不了 HKLM —— 实测）。
      端到端验证：卸载后安装目录/桌面/开始菜单/HKLM/HKCU **全部清空**。


- [x] **Android 授权到期但隧道不停 —— 已修并真机端到端验证**（2026-10-30 15:22）
  - 根因：授权检查只在 `MainActivity.licWatch` 里跑（界面活着才有意义），
    `FerryVpnService` 自己完全不读授权 → 界面切后台/息屏后隧道照跑
  - 实测浪费：授权 13:17:49 到期，13:54 服务端仍收这台手机 141 条记录，白用 36 分钟
  - 修法三处：
    1. 服务内 `licWatchTick`（30 s 一次，临近到期精确排到到期那一刻）→ 到期 `stopTunnel`
    2. `doStart()` 起隧道前再判一次（防 Always-on `null intent` 绕过 + 时间窗竞态）
    3. `stopTunnel` 加 `!STATE_OFF.equals(lastState)` 守卫 —— 原来每次停止都广播两遍，
       `onDestroy → stopTunnel("服务销毁")` 把真实原因（"授权已到期…"）盖掉
  - 另一处：`MainActivity.onResume` 注册广播后**回读服务状态**（息屏期间那条
    STATE_OFF 广播没人收，亮屏后界面停在旧「已借网」）
  - 真机验证（15:18:50 起 → 15:19:14 息屏 → 15:21:14 到期 → 15:21:56 亮屏）：
    `tun0` 过期后消失；亮屏界面自动变「未连接 / 授权已到期，隧道已断开」，
    按钮回「开始借网」，日志补上 `15:21:56 授权已到期，隧道已断开…`
  - `doStart()` 前门单独验：`forceexpiredstart` 测试入口（改内存+磁盘再直发 ACTION_START）
    → 日志 `授权已到期，不能借网`，`/proc/net/dev` 无 tun0，通知栏无 VPN 通知

- [x] **中继：失败日志 98% 是空的 —— 已修并上线**（2026-10-30 13:15）
  - 根因：`asyncio.TimeoutError` 在 3.11+ 就是内置 `TimeoutError`，
    而 **`str(TimeoutError())` 是空字符串** → 日志只剩「失败: 」
  - 实测：2 MB 日志（28792 行）里失败/异常 3685 条，**3625 条原因为空（98%）**
  - 顺带修 `_open_via_clash` 去重键：原来用 `str(e)[:60]`，超时得 `''`，
    一旦置成 `''` 后续所有超时都当「已记过」→ **永久静音**
  - 验证：生产日志 58 条失败 **0 条为空**，全部显示 `TimeoutError`

- [x] **中继日志从不轮转，单文件 67 MB —— 已修并上线**（2026-10-30 13:20）
  - pcs 上 `udp-relay.log` = 70,766,051 B，C: 只剩 4.8 GB，只增不减
  - 修法在**启动器**（`run-relay.cmd`）：python 里 rename 自己正在追加的文件
    在 Windows 必失败（句柄在 cmd 手里）；启动器里做在 `>>` 之前，无句柄
  - 超 64 MB 滚一份、留 5 份。五个边界全部实测（无文件/1KB/阈值±1/5 份满）
  - **生产端到端验证**：72,284,379 B → `.1`，新日志 3,426 B，baidu 200 / github 301
  - 踩过的错：第一版「无条件挪档案」会让每次重启都把 .1 变 .2，
    重启 5 次历史全没而一次真轮转都没发生 → 移位必须放进阈值分支

- [x] **交接文档 P1 第 1 条被推翻：TCP 的 `done, N bytes` 日志不用改**（2026-10-30 13:10）
  - 原结论「和 UDP 同一个问题，逐条 flush 拖垮事件循环」**实测不成立**
  - `write+flush+fsync` 2.967 ms/行 vs `write+flush` **0.0033 ms/行**（差 900 倍）
  - 事件循环实测（10ms ticker lag）：flush 在 **0→2000 行/秒** 下
    p99 恒定 **6.1 ms**；换成 fsync 立刻 p99 6.1→**39.7 ms**、max 60.5 ms
  - 生产中继实际峰值约 **180 行/分钟**，比测过的 2000 行/秒低三个数量级
  - 所以**没有照抄**，保留逐条的访问日志（含域名/字节数/via）
  - 工具：`tools/bench-log-flush.py`（自带反向对照）

- [ ] **P0 · CS2 延迟实测 —— 仍未闭环，需要用户配合**
  - 用户 11:27 报：`延迟 77ms / 抖动 177ms / 错过 10.5%`；11:45 说「有的时候还是有点卡」
  - **本轮新数据（重要）**：13:00 同一时刻对比 100 包
    ```
    网关 10.141.56.1 : p50=4  p90=12 p99=36  max=36    >100ms=0
    pcs  10.13.24.1  : 第一次 p50=11 p90=136 p99=934 max=934 >100ms=11/100
                       第二次 p50=4  p90=9  p99=16  max=16  >100ms=0
    ```
    → **间歇性、成簇出现**，不是稳定劣化；同一次会话内两次差两个数量级
  - 64Hz 经 echo 服务实测（128 包）：`p50=5.6 p90=13.5 p99=34.0 max=34.2 丢失 0`
  - 待确认：卡的时候走的是**直连还是借网**？两者性质完全不同
  - 下一步：找一个能复现卡顿的窗口，同时跑 `tools/measure-64hz.py`
    与 ping pcs，看尖峰是否成簇、是否与某个客户端的大流量对齐

- [ ] **手机 WiFi 掉线问题**
  - 10-30 07:10 观察到：`Wifi is connected` 但 `wlan0` 没有 IP，
    `ip route` 只剩 tun0 一条
  - 怀疑：VPN 建立/销毁过程中把主路由清了，DHCP 没恢复
  - 验证方法：`adb shell ip -4 addr show wlan0`（应看到 10.141.x.x）
  - 若复现，检查 `FerryVpnService` 的 `closeTun()` 与 VpnService 生命周期

- [x] **自检「3/5 项通过」是假阴性 —— 已修并真机验证**（2026-10-30 16:44）
  - 原因：`addDisallowedApplication(getPackageName())` 让本 App（uid 10293）
    不在 VPN 路由规则内（`ip rule` 里 uidrange 0-10292 / 10294-99999，
    **10293 正好空着**），所以 App 自己发的 HTTP 必然走物理网卡
  - **P2 原方案「读 /proc/net/dev」被实测否掉**：在 App 自己的进程里
    （`untrusted_app` 域，uid 10293）读它 → `EACCES (Permission denied)`。
    （用 `adb shell run-as` 试也报同样错，但那是 `runas_app` 域，
      本来就不能代表 App —— 必须让 App 自己读才算数。）
  - 正确做法：**保留 `TProxyGetStats`**，它和 `/proc/net/dev` 逐位吻合：
    `hev: txPkts=197 txBytes=42290 rxPkts=151 rxBytes=41041`
    `tun0: 收 41041 包 151 / 发 42290 包 197`
    而且更准（直接是 hev 处理过的隧道流量）
  - 改了三处：
    1. ④ HTTP 项改名「HTTP 请求（仅参考）」，文案写明「直连本机网卡，
       不代表隧道状态」——它算通过也不是证据，失败也不是罪证
    2. 汇总口径排除「仅参考」项（原来 5 项全算，禁网时必显 3/5 假阴性）
    3. `checkUplink` 增加包数增量，并区分「读不到统计」和「统计为 0」
  - 真机验证：**有隧道 4/4 通过**；**无隧道 1/4**，三项失败精确指向
    TUN 不在 / hev 未运行 / 统计为 0

- [ ] **vivo 拒绝 adb 安装，需用户手动开权限**
  - `INSTALL_FAILED_ABORTED: User rejected permissions`
  - 屏幕已唤醒、`verifier_verify_adb_installs=0` 也一样被拦
  - **需要**：设置 → 系统管理 → 开发者选项 → **USB 安装**（打开）
  - 或直接在手机上下载门户的 `dist/ferry-debug.apk` 手动安装
  - **本机 adb 当前 0 设备**（手机不在线，2026-10-30 13:00 复查）

## 已修（2026-10-30 凌晨）

- [x] **Android「通道通但上不了网」—— 已修复并真机验证**（2026-10-30 03:05）
  - **根因：`remotePort = 1099`，而 pcs 上只有 1080 在监听**
  - 证据：`hev.log` 里 `io timeout` 出现 **16982 次**（无限重连，每次等满 8 秒）
  - 手机 `nc` 测：1080 OPEN / 1099 Timeout
  - 修复：手机 `shared_prefs/ferry.xml` + `files/hev/tunnel.yaml` 都改成 1080
  - **真机验证**：用系统浏览器打开百度，服务端日志收到
    `10.141.57.9 -> hpd.baidu.com:443 done, 17193 bytes`
  - 客户端加了 `probeUpstream()` 端口预检（1.0.51 APK 已构建）

- [x] **Wi-Fi Direct 虚拟网卡抢射频**（Windows 端，已修）
  - 热点开发时启用、关热点后没清理的
    `Microsoft Wi-Fi Direct Virtual Adapter #2`（`本地连接* 2`）一直在 Up 状态，
    和物理网卡共用射频（MAC 只差首字节），持续做 Wi-Fi Direct 探测
  - 禁用后 64Hz p50 **61.1ms → 4.6ms**，max 76.4 → 15.3ms，慢包 50% → 0%
  - 1.0.51 里 `Hotspot.Stop()` 会自动禁用它，避免复发
- [x] **热点功能上锁**（1.0.51）
  - `HasWiredUplink()`：必须 Ethernet + 非虚拟网卡 + 有 IPv4 和默认网关
  - 三处调用：`Start()` 硬闸 / `OnHotspotToggled()` 弹框 / `RefreshUi()` 置灰
  - `(hsOn || wired)` 保证热点已开时还能关
- [x] **中继 UDP 建关联刷盘拖垮转发**（服务端，已部署）
  - 每建一条关联就 `print(flush=True)`，一小时能建几千条，日志涨到 62 MB
  - 改成每 30 秒汇总一行，日志增长降 65%
  - ⚠ **2026-10-30 13:10 修正归因**：当时的理由写成「flush=True 会让
    单线程 asyncio 停摆」，但实测 `write+flush` 只 **0.0033 ms/行**
    （`fsync` 才是 2.967 ms）。降噪仍然正确 —— 理由是**信噪比和磁盘**，
    不是 CPU。详见 `docs/踩坑记录.md` 79 / `tools/bench-log-flush.py`
- [x] **中继 UDP 目标的 DNS 同步解析**（服务端，已部署）
  - `sendto(payload, (host, port))` 传域名时 asyncio 同步调 `getaddrinfo`
  - 实测：冷域名 **38.90ms** vs 传 IP **0.38ms**
  - 中继单线程 → 38ms 全局停摆 → 所有关联排队
  - 修法：DNS 缓存（TTL 300s）+ 线程池异步解析 + 未命中时丢该包
  - 这解释用户描述的「延迟从 10ms 爬到 50-70ms 再降下来」

## 手机侧（2026-10-29 21:00）

- [x] **remotePort 1099 → 1080 已改回来**
  - adb 路径：`C:/tmp/ptools/platform-tools/adb.exe`（**不是** system32 那个 1.0.26）
  - 设备：`6e870b85` vivo V1824A，已授权
  - 配置：`/data/data/com.ferry.app.debug/shared_prefs/ferry.xml`
  - 改前 `<int name="remotePort" value="1099" />` → 改后 `1080`，XML 格式正确，
    token / remoteHost / deviceId 均保留（备份 `ferry.xml.bak`）
  - ⚠ **坑**：直接用 `sed` 改这个 XML 会把属性名的引号吃掉
    （`name=remotePort"`），导致 App 读不到配置。
    正确做法是 `adb pull` → 本地改 → `adb push` 回去。
    另外 adb push 时必须 `export MSYS_NO_PATHCONV=1`，否则
    `/data/local/tmp/...` 会被 MSYS 改写成本地路径。
- [ ] **真机 UI 验证**：21:00 时手机屏幕锁着（`mWakefulness=Asleep`），
  抓不到界面。需要用户解锁后重跑 `uiautomator dump` 确认 App 显示正常。

## 网络抖动：**方向已定 —— 不在这台机器上，在校园网无线侧**（2026-10-29 21:20）

> ⚠ **这一节的结论后来被自己推翻了，看之前先读这段（2026-10-30 13:00 补）**
>
> 「不是这台机器的问题、是校园网无线侧」这个判断**只在那天的坏窗口里成立**。
> 10-30 05:40 校园网恢复后拿到干净基线：**纯 WiFi 直连网关
> p50=3ms / p90=12ms / max=28ms / 尖峰 0** —— 本机无线链路完全正常。
> 见 `docs/无线延迟排查-20261029.md`（13 个被推翻的假设）。
>
> 本轮（13:00）复测又发现它是**间歇性**的，同一次会话内两次差两个数量级：
> ```
> 网关 10.141.56.1 : p50=4  p90=12  p99=36  max=36   >100ms=0
> pcs  10.13.24.1  : 第1次 p50=11 p90=136 p99=934 max=934 >100ms=11/100
>                    第2次 p50=4  p90=9   p99=16  max=16  >100ms=0
> ```
> 所以下面这些「已推翻的假设」**仍然有效**（都是真测过的），
> 但「方向已定」这四个字不再成立。下次复现时要抓的是
> **尖峰成簇的那几分钟**，同时跑 `tools/measure-64hz.py` 与 ping pcs 对照。

**决定性对照实验**（同一时刻、同一个网关 10.141.56.1）：

```
电脑 20 包:  5 30 4 59 3 56 2 4 69 61 2 50 3 59 69 63 65 2 53 79
手机 20 包:  min 2.151 / avg 15.506 / max 136.570 / mdev 33.113
```
→ **手机的 max 136ms 比电脑还差**。两台设备都在抖，
  **所以不是「这台电脑的问题」，是整个校园网无线侧的问题**。
  之前有一次手机测出 4ms，是因为只发 10 包、恰好赶上好窗口，不能当基线。

**已推翻的四个假设（都实测过，别再重试）：**

1. ~~网卡省电是根因~~ —— `IpsEn` 1→0 后复测毫无改善
2. ~~休眠窗口（连续快/稀疏慢）~~ —— 实测连续 55ms / 稀疏 59ms，两组一样
3. ~~信道干扰~~ —— 信道 40 上 8 个 BSSID，信号 ≥80% 的「真干扰源」0 个
4. ~~interface metric 抢流量~~ —— 热点虚拟网卡 3 秒收发 0 字节

**另一个已推翻的方向**：~~「108 个连接抢 airtime」~~ —— 关掉游戏后
（`cs2` 未运行、连接数仍 96）延迟仍是 `40 7 14 3 29 13 39 3 33 11`，
所以连接数不是主因。

**特征**：锯齿状、周期性。极短间隔连发时清晰可见：
```
3 16 34 55 66 13 2 9 24 35 45 61 77 3 6 17 36 68 60 78
→ 大约每 5-7 个包溢出到 66-78ms 一次，再掉回个位数
```
这是典型的**排队队列周期性溢出**（不是丢包：丢包率 0%）。

### 关于「热点大改」的排查结论

| 改动 | 位置 | 现在是否影响 |
|---|---|---|
| `MaxConns=400` + accept 线程阻塞排队 | `Proxy.cs:367` | **不影响**——客户端没运行 |
| `MaxUdpAssoc` 96→512 | `Proxy.cs` | **不影响**——同上 |
| 热点遗留：`SharedAccess` 仍 Running、`本地连接* 2` 仍挂 `192.168.137.1` | 系统级 | **不影响**（实测 0 流量），但可清理 |

**遗留隐患（不急）**：接口 metric 排序 `Radmin VPN(1) < 本地连接*(25) < WLAN(35)`，
WLAN 排最后。目前无影响，但多网卡广播场景下可能选错网卡。

### 还能做什么（本机侧已到极限）

本机能做的都做完了。剩下的是**环境问题**：
1. 换个位置/换台 AP 对比（BSSID `10:2f:6e:84:73:8f` vs 手机连的 `98:e3:01:81:0f:bc`）
2. 换 2.4G 频段对比（`GDPZ-student-2.4G` 信号 95%）
3. 打游戏时把 Clash 退出，看 max 是否收敛（仍未验证）

工具：`tools/diag-game-latency.ps1`、`tools/diagnose-wifi-jitter.py`、
`tools/check-shared-radio.ps1`、`tools/wifi-channel-survey.ps1`

## 已修（1.0.49，2026-10-29 21:33）—— 界面卡死彻底解决

- [x] **界面卡死（1.0.47 报的「点什么都卡死很久」）** —— **实测 82% → 7.2%**

  | 版本 | 空闲 CPU（10 秒采样） | 说明 |
  |---|---|---|
  | 1.0.47 | **82%** | 界面卡死；Running 线程一直有 1 个 |
  | 1.0.48 | **78%** | 只改了 TextBlock，几乎没用 |
  | 1.0.49 | **7.2%** | 修好；Running 线程数 = 0 |

  - **根因**：`RefreshUi()` 每秒跑一次，里面**所有依赖属性都是无条件赋值**。
    WPF 的 Text / Foreground / Content / Background / Visibility 全是依赖属性，
    **赋内容相同的值也会走完整的 失效 → measure → arrange → 重绘**。
  - **第一轮（1.0.48）只解决了约 5%**：我盯着 `Palette` 的 Brush
    （`get { return Freeze(new SolidColorBrush(...)); }` 每次 new 一个）改成了
    `static readonly`，又给 TextBlock 的 77 处赋值加了「先比后写」。
    结果 82% → 78%，几乎没动。
  - **真正生效的第二轮（1.0.49）**：`RefreshUi` 里还剩 **14 处**
    `_btnGlobal.Content` / `_btnGlobal.Background` / `_txtCode.Visibility` /
    `_btnActivate.Visibility` 在每秒无条件赋值。
    `Content` 变更会让 `ContentPresenter` **重建可视子树**；
    `Visibility` 赋同一个值也会触发布局失效。加上 `SetContent/SetBg/SetVis`
    之后才归零。
  - **教训（重要）**：定位性能问题不要从「看起来最可疑的地方」开始猜。
    第一轮凭直觉改 Palette，白改。正确做法是**把该函数里所有依赖属性赋值
    系统地扫一遍**，或者直接抓线程栈。
  - 验证脚本：`tools/measure-ui-cpu.ps1`（走 WMI 启动避开 UAC 取消，测 10 秒 CPU 增量）
  - 产物：`dist/Ferry-Setup-1.0.49.exe`（17722850 B）、`dist/渡口助手.exe` 1.0.49.0
  - 已部署门户：`/version.json` → `1.0.49`，两个文件（安装包 + 客户端本体）都传了
  - 提交：见 git log

## 已修（1.0.48，2026-10-29 20:55，已被 1.0.49 取代）

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
- [x] **M5 UDP/游戏延迟量化 —— 已修好并真机验证**（2026-10-30 17:20）
  - 详见 `docs/M5-Android-UDP实测.md`
  - 中继净成本：p50 **+0.1 ~ +1.6 ms**（多轮重复，量级在测量噪声内）
  - **根因**：服务端 UDP ASSOCIATE 按 RFC 返回 `BND.ADDR = 0.0.0.0`
    （合规），但 hev 会拿这个地址去 `connect()` 自己的 UDP socket，
    而 Linux/Android 上 `connect(0.0.0.0)` = 「连本机」→ 包全进回环
  - **修法**：`HevConfig` 里加 `udp-address: <socks5.address>`，
    这是官方为此提供的覆盖项（README: "Override the UDP address
    provided by the Socks5 server"）
  - **验证**：手机关联从 `上行 0 B / 0 包` → `上行 8204 B / 23 包`，
    且有真实回包（单条 `4038 B 上行 / 17184 B 下行`）
  - 测法工具：`tools/measure-android-udp.py`（判据看**包数**，不只看关联有没有）
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


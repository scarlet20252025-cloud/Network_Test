# 无线延迟真凶：Wi-Fi Direct 虚拟网卡抢射频（2026-10-29）

> **一句话**：热点开发期间被启用、却从未关闭的
> `Microsoft Wi-Fi Direct Virtual Adapter #2`（`本地连接* 2`），
> 一直在用物理网卡的射频做 Wi-Fi Direct 探测/广播。
> 禁用它之后，64Hz 高频延迟从 **p50=61ms 降到 4.6ms**。

## 一、现象

CS2 一直 89ms 左右、抖动 87ms 让游戏没法玩。特征是：

- **低频（1 秒 1 包）正常**：`ping 网关` 平均 20-30ms
- **高频（64Hz）崩坏**：`p50=61ms, p90=68ms, max=76ms`，50% 的包 >40ms
- 手机连同一个 AP，`avg 5ms / mdev 0.87`，完全正常
- 不借网、直连、没人用的时候，一样慢

## 二、定位过程里唯一的突破口：延迟值被量化

把 60 个延迟样本按 **Windows 时钟节拍 15.625ms** 分组：

```
60 个样本：
  21 个 ≈ 2ms      (0 个节拍)
  16 个 ≈ 62.5ms   (4 × 15.625)
  19 个 ≈ 78.1ms   (5 × 15.625)
  其它档位：几乎为 0
```

**延迟不是连续分布，只堆在 15.625ms 的整数倍上。**

这说明它不是「网络时延」，而是**「等某个固定周期的时间片」**。
射频层的东西是以时间片工作的 —— 这是转向 Wi-Fi Direct / 多信道并发的起点。

## 三、决定性证据：MAC 地址

```
WLAN 物理网卡 MAC : 94-BB-43-4F-A0-32
Wi-Fi Direct MAC : 9A-BB-43-4F-A0-32
                    ↑ 只有首字节不同（本地管理位置位），后 5 字节完全相同
```

这是 Windows 从物理网卡派生 Wi-Fi Direct 虚拟适配器的标准规则。
**这说明它和你的 WLAN 共用同一套射频前端。**

再看它的统计：

```
本地连接* 2   状态 Up
  接收  0 字节
  发送  237,339 字节      ← 一直在发，什么都没收到
```

**它在持续用你的射频发 Wi-Fi Direct 探测/广播，却收不到任何回应。**

## 四、为什么这是「热点改造」留下的

`docs/热点与游戏延迟-20261029.md` 记录过那次事故：

> 单块无线网卡做 SoftAP 时，STA 和 AP 共用同一套射频前端。
> 很多驱动（含 8852BE）接到 SoftAP 请求后**不是拒绝，而是切换射频配置** ——
> 表现就是「AP 起来了、STA 掉了」。

当时为了做移动热点，直接调了 `StartTetheringAsync()`。
**热点关闭后，`Microsoft Wi-Fi Direct Virtual Adapter #2` 没有被清理掉，
一直是 Up 状态**（那份文档里也提到「SoftAP 驱动状态卡死」）。

于是每次它做 Wi-Fi Direct 扫描，你的 STA 数据就要等扫描结束 ——
**这个等待正好是 4-5 个 15.625ms 的时间片，也就是 62.5 / 78.1ms。**

## 五、修复

```powershell
Disable-NetAdapter -Name '本地连接* 2' -Confirm:$false
```

（如果按名字不匹配，用设备管理器禁用两个
`Microsoft Wi-Fi Direct Virtual Adapter` 设备即可。）

### 效果（同一台机器、同一 AP、64Hz × 128 包）

| 指标 | 修复前 | 修复后 | 改善 |
|---|---|---|---|
| p50 | 61.1 ms | **4.6 ms** | 13× |
| p90 | 67.6 ms | **6.9 ms** | 10× |
| max | 76.4 ms | **15.3 ms** | 5× |
| 标准差 | 31.6 ms | **1.9 ms** | 17× |
| 慢包(>40ms) | 50% | **0%** | — |

`ping 网关`（1 秒 1 包）：平均 27-30ms / 最长 77ms → **平均 3ms / 最长 16ms**。

## 六、排查过程中被推翻的假设（都别再重试）

这一轮在找到真凶之前，试过下面这些，**全部无效**：

| 假设 | 排除依据 |
|---|---|
| 网卡省电 `IpsEn` / `LpsEn` / `LpsWowEn` | 改 0 后无变化（甚至变差） |
| 帧聚合 `CoreAMSDU` | 改 0 后无改善；INF 里默认就是 1 |
| PCIe ASPM（全局 + `L1Support`/`L1OffSupport`） | 改 0 后无实质改善 |
| 电源计划的无线适配器设置 | 接电源时本来就是「最高性能」 |
| 完整卸载 + 重装驱动 | 无变化；驱动本就一年没更新 |
| 漫游激进程度 `RegRoamLevel` 70→20 | 无变化 |
| 信道干扰 / 换 AP | 手机同 AP 正常 |
| 校园网 / AP 本身 | 手机同 AP avg 5ms |
| Clash 连接数占满 airtime | 退出 Clash 后无变化 |
| 渡口 TUN / 借网链路 | 不借网、直连、渡口完全退出时一样慢 |
| 共享 UDP 中继被其他用户占满（400 并发） | 直连时同样慢，与中继无关 |
| DNS 慢（3.6-12 秒） | 真实存在但只影响建连，不影响已建立的 UDP |
| MAC 地址随机化 | 30 秒内 MAC 未变化 |

**其中「共享 UDP 中继 400 并发」那条特别值得记**：当时看到中继被
`10.112.70.14`（网易手游 + 一堆广告 SDK）占了 50% 流量、并发 330-405，
就下结论说是这个原因 —— 但用户一句「不借网直连时一样慢」直接推翻。
**这提醒：任何结论都必须先确认「这个测量是在哪条路径上做的」。**

## 七、方法论教训（比结论更重要）

### 1. 延迟值被量化 = 找周期性来源的钥匙

如果延迟值集中堆在某个固定间隔的整数倍上，那不是网络时延，
而是**在等一个固定周期的时间片**。射频、定时器、轮询都长这样。
拿到数据第一件事：**按可疑周期分组看分布**。

### 2. 高频测不出问题，是因为测法不对

用 `ping`（1 秒 1 包）测一直是「20-30ms 还行」；改成 64Hz 才暴露
`p50=61ms`。**CS2 是 64 tick，必须按 64Hz 测**。
工具：`tools/measure-64hz.py`（需要一个回环服务，见脚本头注释）。

### 3. 先确认流量走哪条路，再谈延迟

这个坑踩了整整一晚：`Find-NetRoute` 一查就发现
到公网的流量全走 `FerryTun`（27 条路由盖住几乎整个公网段），
而 `ping 网关` 走 WLAN。**两者测的根本不是同一条路**，
把它们的数字放一起比较，结论必然是错的。

一条命令：`Find-NetRoute -RemoteIPAddress <目标IP> | Select-Object -First 1 InterfaceAlias`

### 4. 虚拟网卡 / 虚拟适配器要当成嫌疑人

`Get-NetAdapter` 里凡是 `Status=Up` 但 `LinkSpeed=0`、
或有发送无接收的虚拟适配器，都值得怀疑。
它们和物理网卡共用射频/中断资源，却不出现在任何「网络慢」的常识排查里。

## 八、待办

- [ ] 确认 `Disable-NetAdapter` 在重启后是否保持（可能需要在客户端启动时检查并禁用）
- [ ] 考虑把这条检查做进渡口客户端：启动时若发现
      `Microsoft Wi-Fi Direct Virtual Adapter` 处于 Up 且零接收，
      提示或自动禁用（**注意：这属于写操作，按 AGENTS.md 铁律要先问用户**）
- [ ] 用修好的环境重跑一次 CS2，确认游戏内延迟回落

---

## 九、根治（1.0.51）：从「事后修」改成「事前拦 + 事后清」

上面那一节只是**手动**把那块虚拟网卡禁掉了。要让它不再复现，软件层得做两件事：

### 1. 事前拦：没有有线上行就锁死热点

`Hotspot.LockedReason()`：

```csharp
public static string LockedReason()
{
    if (HasWiredUplink()) return "";
    return "移动热点已锁定：当前没有检测到有线（以太网）连接。...";
}
```

`HasWiredUplink()` 的判定条件（三条都要满足）：

1. `NetworkInterfaceType == Ethernet` —— 只要有线
2. `!IsVirtualAdapter(desc) && !IsVirtualAdapter(name)` —— **排除 Radmin VPN /
   etgame / WAN Miniport 这些「自称以太网」的虚拟网卡**（踩过：Radmin 是
   iana=6 且 Up，不排除就会被当成有线放行）
3. 有 IPv4 地址 **且有默认网关** —— 只看 `Status==Up` 不够，USB 网卡
   拔了线有时还是 Up

调用点三处，缺一不可：

| 位置 | 作用 |
|---|---|
| `Hotspot.Start()` 第一行 | 真正的硬闸，绕过 UI 也开不起来 |
| `OnHotspotToggled()` | 点了就弹框说明原因，并自动把开关弹回去 |
| `RefreshUi()` | 开关直接置灰，文案写「需有线连接（当前只有 WiFi）」 |

`RefreshUi` 里那个 `(hsOn || wired)` 的判断很重要：**热点已经开着时不能锁掉
开关**，否则网线一拔用户就没法关热点了。

### 2. 事后清：关热点时禁用 Wi-Fi Direct 虚拟适配器

`Hotspot.Stop()` 现在会调 `CleanupWifiDirectAdapters()`：

```csharp
foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
{
    if (描述或名字含 "wi-fi direct")   // 命中就是 Windows 派生的那块
        RunNetsh("interface set interface \"" + ni.Name + "\" admin=disable");
}
```

**为什么必须在 Stop 里做**：热点关闭时 Windows **不会**自动清理这块虚拟适配器，
它就一直留在 Up 状态、持续抢射频 —— 正是这次故障的直接原因。

失败不报错（`RunNetsh` 吞掉所有异常）：这是清理动作，禁不掉也不该让
「关闭热点」显示失败。

### 3. 遗留问题

- `CleanupWifiDirectAdapters()` 用的是 `netsh interface set ... admin=disable`。
  如果某块驱动的虚拟适配器不吃这一条，需要改成 PnP 层
  `Disable-PnpDevice`（要管理员）。目前实测这台机器上 netsh 有效。
- 重启电脑后虚拟适配器会重新出现（但那时热点没开过，它是 Down 状态，不抢射频）。
  只有「开过热点」才会留下 Up 的那块。

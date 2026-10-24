# UDP低延迟模式（TUN 全局接管）

> 目标：不认系统代理的程序（多数游戏客户端、部分桌面软件）也走渡口，
> 并让 UDP 走低延迟链路。
>
> 实现：客户端把 TUN 层委托给 mihomo，自己只管隧道和 SOCKS。

---

## 一、为什么需要它

10810 是个 SOCKS5/HTTP **端点**。只有「认代理设置」的程序会用它：

| 程序类型 | 走 SOCKS 端点？ | 走 TUN？ |
|---|---|---|
| 浏览器（读系统代理） | ✅ | ✅ |
| 下载工具、git、curl | ✅ | ✅ |
| 游戏客户端（直连服务器） | ❌ | ✅ |
| QUIC / HTTP3 流量 | ❌（UDP） | ✅ |
| 视频通话 | ❌ | ✅ |

TUN 在网络层建一块虚拟网卡、把默认路由指过去，这样**所有程序**的流量
（包括 UDP）都会经过它，不需要程序配合。

---

## 二、架构：TUN 只是加一层

```
                    ┌──────────────────────────────┐
  所有程序 ─────────→│  TUN 网卡 FerryTun (mihomo)  │
  （含游戏/UDP）      └───────────┬──────────────────┘
                                  │
                     ┌────────────┴────────────┐
                     │  TCP                    │  UDP
                     ▼                         ▼
        127.0.0.1:<客户端SOCKS>        远端 Python 中继
        （客户端自己起，ssh -D 上游）    (10.13.24.1:1080)
                     │                         │
                     ▼                         ▼
                  ssh 隧道                  远端直发
                     │
                     ▼
              10.13.24.1 (pcs)
```

**关键设计**：mihomo 的上游指向**客户端自己已经跑起来的 SOCKS 端口**。

为什么不自己实现 TUN：那需要一个完整的用户态 TCP/IP 栈（握手、拥塞控制、
重传、分片……），几千行且极难做对。mihomo 自带 gvisor 栈，已经成熟。

为什么不替换现有逻辑：隧道、代理、认证、限速全部沿用现有实现。TUN 只加在最外层，
**它挂了也不影响 SOCKS 模式** —— 只记 `TunError`，界面明确提示原因，借网继续可用。

---

## 三、用法

### 客户端（推荐）

1. 设置 → 勾选「**UDP低延迟模式（全局接管）**」
2. 点「开始借网」

状态卡会出现第四行 **「全局接管  已开启（UDP低延迟模式）」**。

如果 TUN 起不来，大状态下方会直接说明原因，例如：

> 已借网，但全局接管（TUN）未启用 —— 只有认代理的程序生效。
> 原因：TUN 建网卡失败：... 多半是上次残留的 FerryTun 网卡没清掉，重启一次电脑或手动删除网卡即可。

### 命令行（工具层）

```powershell
cd mihomo
.\mihomo-keeper.ps1 Elevate -Target TunOn    # 提权开 TUN（弹一次 UAC）
.\mihomo-keeper.ps1 TunOff                    # 关 TUN，回到 SOCKS 端点
.\mihomo-keeper.ps1 Status                    # 看 TUN 段状态
```

---

## 四、这套东西的硬约束

### 1. 必须管理员

建虚拟网卡 + 改路由表都是内核级操作，普通权限下 mihomo 报
`configure tun interface: Access is denied` 然后退出。

所以客户端 `app.manifest` 声明了 `requireAdministrator`，双击就提权。

### 2. 提权带来 UAC，配套改了两件事

| 场景 | 做法 | 为什么 |
|---|---|---|
| 开机自启 | **计划任务** `RunLevel=HighestAvailable` | 启动文件夹快捷方式每次开机弹 UAC，必须点「是」才能用 |
| 安装 | `PrivilegesRequired=admin` | 实测普通权限创建 `RL HIGHEST` 任务报 `ERROR: Access is denied.`（exit 1） |

代价：装的时候弹一次 UAC。换来的是之后永久免 UAC。

### 3. wintun.dll 必须和 mihomo.exe 同目录

mihomo 用 Windows 默认 DLL 搜索顺序（先找 exe 所在目录）。放到别处找不到，
报错很含糊（`Cannot create a file when that file already exists` 或直接没反应）。

安装包已经把两个都放在 `{app}` 下。

### 4. 安装包从 2 MB 变 16.9 MB

mihomo.exe 58 MB，lzma2 压缩后增量约 15 MB。

不带的话 TUN 只能在「装了 Clash Verge」的机器上用 —— 那就不算一键了。
所以带上，接受这个体积。

---

## 五、实测记录（2026-10-24）

### 客户端 TUN 全链路

| 项 | 结果 |
|---|---|
| TUN 网卡建立 | ✅ `FerryTun` / Meta Tunnel / **Up** |
| 全局出口切换 | ✅ 116.23.159.136（电信，直连）→ 218.107.0.47（联通） |
| DNS 接管 | ✅ fake-ip 生效，`www.qq.com` → `198.18.0.19` |
| UDP | ✅ 3/3 通（不指定代理，直接发 DNS 查询） |
| 系统代理 | ✅ 同时切到 `http://127.0.0.1:10809`，两条路并存 |
| 全新安装（Inno） | ✅ 6 个文件全到位（exe/mihomo/wintun/unins×2/config） |
| 安装器建的自动任务 | ✅ `RunLevel=HighestAvailable` |
| 崩溃后网卡回收 | ✅ mihomo 退出时自动清理，网络立即恢复 |

### 踩坑实测数据

| 坑 | 现象 |
|---|---|
| `BaseDirectory` 末尾 `\` + 引号 | mihomo 报 `open ...tun-config.yaml\config.yaml`，参数全乱 |
| 判「网卡存在」而非「Up」 | 网卡 Disabled 时界面显示「已开启」，实际流量进不去，零报错 |
| `netsh admin=disable` 清残留 | 名字仍被占用，mihomo 照样报 `file already exists` |
| 客户端被强杀 | mihomo 子进程残留 → 网卡残留 → 下次启动撞名 |
| `else if` 链中间插 `if` | 「正在走渡口」被后面的 `else` 覆盖成「借网异常」 |
| 卡片高度没跟着行数加 | 第四行被底边裁掉一半 |

---

## 六、已知限制

1. **TUN 起不来时不会自动降级**。界面会说明原因，但用户得自己决定要不要
   重启电脑（清残留网卡）或关掉 TUN。
2. **同时开着 Clash Verge 的 TUN 会冲突**。两个都想改默认路由，
   后起的通常赢，但行为不可预测。用渡口 TUN 时建议关掉对方的 TUN。
3. **`strict-route: false`**。Windows 上 `strict-route` 偶有问题，所以默认关。
   如果发现「部分流量还是走直连」，可以试着打开。
4. **内网直连规则保留**（`10.0.0.0/8` 等），所以访问校园网内其他主机不走隧道。
   这是故意的 —— 否则局域网打印机/共享/远端 SSH 会失联。

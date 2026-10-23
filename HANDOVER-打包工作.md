# 渡口 Ferry · 交接文档（v0.8.0，2026-10-23 21:25）

> 从 `session-8c5d1238-c2e6-4e46-9820-7083134eabee`（「ops bash操作卡顿原因排查」）转出。
> 那个会话累积到 393 万 token 估算历史，任何模型窗口都装不下，无法继续。
> 工程本身完好，改动都在磁盘上。
>
> 本版是**软件重做**：客户端从 PowerShell 脚本换成 C# WinForms 原生程序，
> 分发从 zip 换成 exe 安装包，密钥从「网页登录」改成「软件内激活」，
> 代理和隧道也用 C# 重写进 exe —— 目标机器只需要 Windows 自带的 ssh.exe。

**工程根**：`E:\肥鱼不乐园\borrow-net`

---

## 一、这次改了什么（一句话版）

| 项 | v0.7 之前 | v0.8.0 现在 |
|---|---|---|
| 客户端 | `渡口助手.ps1`（25 KB，PS+WinForms，启动 1–2 s） | **`渡口助手.exe`（44 KB，C# WinForms，启动 <200 ms）** |
| 分发 | zip（内含 安装.cmd + ps1） | **`Ferry-Setup-<IP>.exe`（Inno Setup，2.03 MB）** |
| 密钥 | 网页登录 → 下部署包 | **网页只管下软件；密钥在软件里输入激活** |
| 授权 | 无期限概念 | **`clients.expires_at` + `/api/license`，显示剩余天数** |
| 配置 | 部署包内置 token | **token 留空，激活后由服务端签发** |

---

## 二、用户提的每一条，对应实现在哪

| 用户要求（paraphrased） | 状态 | 在哪 |
|---|---|---|
| 「做成 exe 安装程序，方便安装、更新推送维护」 | ✅ | `installer/ferry.iss` → `dist/Ferry-Setup-0.8.0.exe` |
| 「程序托盘也要记得保留」 | ✅ | `MainForm.BuildTray()`，关窗收托盘 + 双击还原 + 右键菜单 |
| 「解决可以重复打开多个软件的 bug」 | ✅ | `Program.Main` 里 `Mutex("Local\FerryClient")`，第二次启动顶前已有窗口 |
| 「密钥在软件里输入激活借网权限」 | ✅ | 主界面「输入密钥激活」卡片 → `POST /api/activate` |
| 「而不是用来打开网页，网页只用来获取软件」 | ✅ | `/download` 匿名可下（用来源 IP 当账号），不需要先登录 |
| 「可使用时长等状态监测」 | ✅ | `clients.expires_at` + `GET /api/license` + 主界面剩余天数 |
| 「配色最好还是用之前那种绿色」 | ✅ | `Theme.cs` 青绿 `(32,178,170)`，沿用原 PS 版色值 |
| 「密钥输入那里没有输入框 UI，只有纯文字」 | ✅ 已修 | 输入框从弹窗移到主界面 `_txtCode` |
| 「原本的功能、切换端口自动恢复别漏」 | ✅ | `PortState` 读 `.proxy-state/ports.json`；端口变了自动改系统代理 |

---

## 三、架构（v0.8）

```
使用者机器                                    远端主机              管理员本机
──────────                                   ────────              ─────────
渡口助手.exe                                  sshd                  门户 portal-server.py
 ├ 单实例锁（Mutex）                          ssh -D 动态转发        ├ /                首页（获取软件）
 ├ 托盘 NotifyIcon                                  ↑                ├ /admin           管理面板
 ├ 激活（POST /api/activate）───────┐               │                ├ /download        匿名下 exe
 ├ 授权（GET /api/license）─────────┤               │                ├ /download-latest 更新包
 ├ 启停（起 keeper + dual-proxy）    │               │                ├ /api/activate    软件内激活
 └ 系统代理（备份→切→还原）          │               │                ├ /api/license     查授权
                                    │               │                └ /version.json    更新推送
        ssh-tunnel-keeper.ps1 ──────┘               │
        dual-proxy.mjs ─────────────────────────────┘
                      ↓ 写 .proxy-state/ports.json（实际端口）
                渡口助手.exe 读它 → 系统代理指向真实端口
```

**安装到哪**：`%LOCALAPPDATA%\Ferry\`
```
├─ 渡口助手.exe            56 KB —— 唯一文件，代理和隧道都在里面
├─ config.json             安装时写入，token 留空等激活
├─ state.json              首次借网时备份的用户原代理（还原用）
├─ assistant.log           运行日志（200 KB 轮转）
└─ unins000.exe            Inno 卸载器
```

**完全自包含**：v0.8.0 起 `dual-proxy.mjs` 和 `ssh-tunnel-keeper.ps1`
都用 C# 重写进 exe 了（`Proxy.cs` / `Tunnel.cs`）。目标机器只需要
Windows 自带的 `ssh.exe`（`设置 → 应用 → 可选功能 → OpenSSH 客户端`），
**不再需要 Node.js，也不需要任何 PowerShell 脚本**。

**为什么用 .NET Framework 4.8 而不是 .NET 6/8**：
Win10/11 自带 4.8 运行时，**目标机器什么都不用装**，产物 44 KB。
self-contained 的 .NET 8 单文件要 60 MB+，对一个改代理的小工具太重。
代价是只能用 C# 5 语法（没有 `$"..."` 插值、没有 `?.`），代码里已经避开。

---

## 四、授权模型（这次新增的核心）

### 数据

`clients` 表新增 `expires_at INTEGER DEFAULT 0`（0 = 永久）。
**老库自动迁移**：`DB._migrate()` 用 `PRAGMA table_info` 判断后 `ALTER TABLE` ——
`CREATE TABLE IF NOT EXISTS` 对已有表什么都不做，所以必须单独补。

### 三条链路

1. **激活** `POST /api/activate` `{"code":"XXXXXXXX"}`
   - 校验链和网页登录完全一致：格式 → 限流（共用 user scope）→ `hash_code` 查 `invites`
     → revoked / used / expired → 建号或复活
   - 写 `expires_at = now + license_days*86400`（`CFG['license_days']` 默认 30）
   - 返回 `{token, ip, remoteHost, sshPort, httpPort, expiresAt, daysLeft, portalUrl}`
   - **重新激活 = 续期**（把 `expires_at` 推后），否则密钥用完后用户永远续不了

2. **查授权** `GET /api/license?ip=&token=`
   - 鉴权用 (ip, token) 组合，不只认 IP
   - 返回 `{enabled, expiresAt, daysLeft, hoursLeft, expired, suspended}`
   - 到期不改库，只如实报告 —— 让「到期」是软状态，续费后立即恢复

3. **客户端行为**
   - 启动 + 每 6 小时查一次
   - `expired` 或 `suspended` → **主动掐掉借网**并恢复直连
   - 查不到门户（断网）→ 用本地 `expires_at` 判断，**不一律拒绝**
     （用户断网时本来就连不上门户，这时候拒绝更没道理）

---

## 五、端口自动恢复（用户特别强调的那条）

**问题**：`dual-proxy.mjs` 默认 `port: 'auto'`，从 10800 往上找空闲端口，
并**覆盖**命令行传的 `--listen/--socks`。客户机上 Clash / v2rayN 常占 10808/10809，
写死端口就是「软件起不来」。

**原版客户端的问题**：`client-assistant.ps1` 用 `config.json` 里的 `httpPort` 当系统代理端口，
如果 dual-proxy 实际挪了端口，系统代理就指错 —— 表现是「显示正在借网，网页打不开」。
管理员版 `tools/渡口助手.ps1` 早就会读 `ports.json`，客户端版一直没跟上。

**现在的做法**（`PortState.cs` + `MainForm.RefreshUi`）：
1. 启动 dual-proxy **不传 `--listen/--socks`**（传了也会被覆盖，反而误导）
2. 等 `.proxy-state/ports.json` 出现且该端口真的能连（最多 20 s）
3. 系统代理指向 `ports.json` 里的**实际** http 端口
4. **每 1 秒检查**：如果代理换了端口而系统代理还指着旧的，自动改过来并记日志

`ports.json` 位置：dual-proxy 在 `<appDir>`，它算的是
`dirname(mjs)/../.proxy-state/ports.json` = `%LOCALAPPDATA%\.proxy-state\ports.json`。

---

## 六、原代理备份与还原（卸载安全的关键）

**不做这件事的后果**：用户本来配着公司代理或 Clash，程序直接 `ProxyEnable=0`
等于把人家配置弄丢，下次开会上网全公司找 IT。

实现（`FerryCore.BackupProxyOnce` / `RestoreDirect`）：
1. 切到渡口**之前**，把 `{prevEnabled, prevServer}` 写 `state.json`
2. 只在「原来的代理不是渡口」时才写 —— 否则每次点开始都把渡口地址备份进去，
   关闭时还原成渡口，代理就永远回不去了
3. 停止/卸载时读 `state.json` 还原；没有备份才用 `config.fallbackProxy`；再没有才置 0

**还原顺序**：先还原代理，**再**停进程。反过来的话代理会短暂指向一个已停掉的端口，
那几秒全网上不去。

---

## 七、编译与打包

```powershell
# 1. 编译客户端（44 KB，约 3 秒）
cd "E:/肥鱼不乐园/borrow-net/ferry-client"
powershell -ExecutionPolicy Bypass -File build.ps1              # 默认 0.8.0
powershell -ExecutionPolicy Bypass -File build.ps1 -Version 0.9.0

# 2. 打安装包（2.03 MB）
cd "E:/肥鱼不乐园/borrow-net/installer"
& "C:\Users\63526\AppData\Local\Programs\Inno Setup 6\ISCC.exe" ferry.iss
# → dist/Ferry-Setup-0.8.0.exe

# 3. 端到端自测
python portal/portal-server.py --port 18080 --db "$env:TEMP\t.db" --public-host 10.13.24.1
curl -OJ "http://127.0.0.1:18080/download"     # 应得到 Ferry-Setup-<你的IP>.exe
.\Ferry-Setup-<IP>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

**编译期必须注意的两件事**：
- `build.ps1` **必须带 UTF-8 BOM**。PS 5.1 会把无 BOM 的 UTF-8 当 ANSI 读，
  中文注释直接破坏语法，报错位置还指向无关的行（AGENTS.md 记过这个坑，又踩了一次）。
- `ferry.iss` **也必须带 BOM**，理由同上。
- C# 源码不用 BOM —— `csc` 传了 `/codepage:65001`。

---

## 八、踩过的坑（都实测过，别重试）

### 1. Inno Setup 静默安装被「请填写局域网 IP」弹窗卡死
**现象**：`/VERYSILENT` 退出码 1，无日志，安装目录不出现。
**根因**：文件名叫 `setup.exe`（浏览器下载常改名）→ 解析不到 IP → `NeedIpPage=true`
→ `NextButtonClick` 里 `MsgBox` 仍然执行 → 静默模式下没人点确定 → 永久挂起。
**修法**：`if WizardSilent then Exit;` 放行，`config.accountIp` 留空即可 ——
激活接口用的是请求来源 IP，不需要这个字段。
**教训**：静默路径里**任何 MsgBox 都是地雷**。

### 2. `StringChangeEx` 是 var 原地修改，不能当函数用
```pascal
{ 错 } Cur := StringChangeEx(Cur, '-', '.', False);
{ 对 } StringChangeEx(Cur, '-', '.', False);
```
它返回「替换了几次」（Integer），赋值给 string 直接编译不过。
Inno 的 Pascal 也没有 `StringReplace`。

### 3. `Timer` 在 C# 里会歧义（CS0104）
`System.Threading.Timer` 和 `System.Windows.Forms.Timer` 同名，
两个 `using` 都在时必须写全名：
```csharp
System.Windows.Forms.Timer _fast;
```

### 4. `Theme.Card` 重名（CS0102）
颜色字段 `Card` 和工厂方法 `Card(int)` 不能同名，颜色改叫 `CardBg`。

### 5. PS 5.1 把无 BOM UTF-8 当 ANSI
见上「编译与打包」。加 BOM 用 node 写 `EF BB BF`。

### 6. pwsh 的 `Start-Process` 对中文路径 exe 报「操作已被用户取消」
改用 `ProcessStartInfo` + `UseShellExecute = false` 才稳。

### 7. 关窗不能真退出
直接退出用户就找不到关闭借网的入口，而系统代理还指着死端口 → 「全网上不去」。
必须 `e.Cancel = true; Hide();` 收进托盘。

### 8. 等端口不能只等 2 秒
node 冷启动在慢机器上要 5–10 秒。现在最多等 20 秒（40 × 500 ms），
并且**以 ports.json + 端口可连**为准，不是固定端口。

### 9. Inno 卸载器删不掉系统代理和运行中的进程
`[UninstallRun]` 必须调 `渡口助手.exe /uninstall`，
让程序自己：还原原代理 → 杀 keeper/bridge/ssh → 删自启。

### 10. JS 的 `String.replace` 用字符串做替换时 `$'` 会展开
`$'` 表示「匹配之后的全部文本」，曾把 `portal-server.py` 塞进 1300 行重复内容。
所有 node patch 脚本的 replace 第二参数**必须用函数形式** `() => text`。
事故现场留在 `portal/scratch/portal-server.py.broken*`。

---

## 九、实测数据

| 项 | 数值 |
|---|---|
| 客户端 exe | 44 544 B（44 KB） |
| 安装包 | 2 135 199 B（2.03 MB） |
| 安装后占用 | 约 2.3 MB（含 unins000） |
| 启动到窗口可见 | < 200 ms（原 PS 版 1–2 s） |
| 静默安装耗时 | 约 3 s |
| 卸载耗时 | 约 1 s，目录与快捷方式全清 |

**端到端自测结果（2026-10-23 21:20）**：
```
安装退出码=0   Installation process succeeded.
安装目录: 渡口助手.exe / config.json / dual-proxy.mjs / ssh-tunnel-keeper.ps1 / unins000.*
config.json: portalUrl=http://10.13.24.1:8080, token="", expiresAt=0   ✅
启动: PID 存活, 界面 23 个元素齐全, 日志落盘   ✅
卸载退出码=0, 目录已删, 快捷方式已删   ✅
门户 /download → 2135199 B, Content-Type: application/vnd.microsoft.portable-executable,
              filename="Ferry-Setup-<IP>.exe", X-Ferry-Account: <IP>   ✅
门户 /version.json → {"latest":"0.8.0","built":true}   ✅
```
**本机未验证的**：真实密钥激活（需要有效 invite 码）、真实断网借网（需要断网环境）。

---

## 九点五、v0.8.0 第二轮：网页改版 + 自包含 exe + 代理自测

### 1. 网页端改成「只发软件」

`/` 从登录页换成 `get_page()`：一个大按钮「下载 Windows 客户端」+ 三步说明
+ 密钥规则 + 原理。登录表单整段删掉，`/login` 重定向回 `/`。
密钥彻底变成「软件里的激活码」，网页不再收。

验证：`/` 返回 11 KB，含「下载 Windows 客户端 / 在软件里输入密钥 / 三步用起来 /
Ferry-Setup」四项关键文案，且**不再含「临时密钥」输入框**。

### 2. dual-proxy 和 ssh keeper 用 C# 重写（这才是「exe 直接打开」）

用户要求「条件允许就做成 exe 直接打开包含所有功能」。原来的障碍是
`dual-proxy.mjs` 需要目标机器装 Node.js、`ssh-tunnel-keeper.ps1` 需要 PowerShell。

新增两个文件：

| 文件 | 职责 | 关键点 |
|---|---|---|
| `src/Proxy.cs` | SOCKS5 + HTTP 双协议代理，同端口 | 读首字节判协议（`0x05` = SOCKS5，其余 = HTTP）；上游转发到本地 `ssh -D` 的 SOCKS 端口 |
| `src/Tunnel.cs` | 起 `ssh -D` + 看护重连 | 按 `System32\OpenSSH\ssh.exe` → Git → PATH 顺序找；退避 5 s→60 s |
| `src/FerryRuntime.cs` | 管这两个的生命周期 | 自己分配端口、等 SOCKS 就绪、写 `ports.json` 兼容老工具 |

**结果**：安装目录只剩 `渡口助手.exe` + `config.json` + `unins000.*`。
exe 从 44 KB 长到 56 KB，换来**零外部运行时依赖**（除 Windows 自带的 ssh）。

### 3. 代理自测（`test/TestProxy.cs`）

GUI 里点「开始借网」必须真的连上远端主机，开发机没有那台机器的密钥。
所以写了一个**独立自测程序**：起一个假 SOCKS5 服务器（收到 CONNECT 就直接连目标），
让 `ProxyServer` 指向它，然后真实访问网站。

```powershell
cd ferry-client
csc /target:exe /out:test\out\TestProxy.exe /codepage:65001 ^
    /r:System.dll /r:System.Core.dll src\Config.cs src\Proxy.cs test\TestProxy.cs
.	est\out\TestProxy.exe
```

实测输出：
```
[3] ✅ HTTP 明文转发成功（baidu），响应 16827 字节
[4] ✅ HTTPS CONNECT 成功（baidu），响应 16827 字节
[5] ✅ 同端口 SOCKS5 成功
统计：连接 3，活跃 0，上行 552 B，下行 60167 B
=== 全部通过 ===
```

### 4. 这一轮抓到的两个真 bug（都不在 GUI 里，肉眼看不出来）

**Bug A：`ReadHeader` 的结尾判断偏移量写错**
```csharp
// 错：'...

' 的 Len-2 其实是 '
'，条件恒不成立
if (arr[arr.Length - 2] == '
' && arr[arr.Length - 3] == '
') return arr;
// 对：检查末尾四个字节
if (L >= 4 && arr[L-4]=='
' && arr[L-3]=='
' && arr[L-2]=='
' && arr[L-1]=='
') return arr;
```
后果：HTTP 请求头永远读不完，客户端一路等到超时（实测 90 秒+）。

**Bug B：`Pump` 用 `CountdownEvent(2)` 等两个方向都 EOF**
```csharp
// 错：HTTP 请求体传完后客户端就不再写、只等响应，于是永远等不到第二个 Signal
done.Wait(TimeSpan.FromMinutes(30));
// 对：任一方向结束就收尾
Task.WaitAny(new Task[] { t1, t2 });
try { a.Close(); } catch {} try { b.Close(); } catch {}
```
后果：每个 HTTP 请求都挂 30 分钟才释放。

**教训**：这类 bug 在 GUI 上表现为「点了没反应」，但根因在字节层。
**有代理/协议代码时必须写字节级的自测**，不能只靠点界面。

### 5. UI 时序修正

`_rt = rt`（挂上运行时）原来放在 6 秒的出口 IP 探测之后，
那 6 秒里系统代理已经改了、但 `_rt` 还是 null，界面显示「借网异常」——
用户会以为坏了。现在改为先挂运行时再改系统代理，出口探测放最后。

另外授权剩余不足一天时显示「剩余 N 小时」而不是「剩余 0 天」
（后者看起来像已经过期）。

## 九点六、v0.8.0 第三轮：/portal 收口 + 真实远端端到端验证

### 1. 网页还有一处没改：`/portal`

用户反馈「网页还是没更新」，截图显示的是 `/portal`（登录后的门户页），
仍然写着「已登录 / 下载 ferry-*.cmd / 代理令牌 xxx」。

原因：上一轮只改了 `/`（首页），但浏览器带着 session cookie 访问 `/portal`
还是能进到旧页面。而且**门户服务跑在 10.13.24.1 上，磁盘代码改了之后必须重启那个进程**。

已修：
```python
elif path == '/portal':
    # 登录后的门户页作废：密钥已经改到软件里，网页不再有「登录」这件事
    self.redirect('/')
```
`/logout` 本来就自己 redirect，没动。

**⚠️ 部署提醒**：改完必须重启门户进程（`python portal-server.py ...` 那个），
否则跑的还是内存里的旧代码。用户当前实例在 `10.13.24.1:8080`。

### 2. 真实远端端到端验证（10.113.66.6）

按用户要求用真实主机跑，不再只靠假 SOCKS 服务器。

**测试方法（关键）**：出口 IP 对比在这里**失效**——
实测远端 10.113.66.6 自己的出口也是 `116.23.159.136`（和本机同一个校园网 NAT），
所以「出口变了」根本不该作为判据。改成**访问一个只有远端本地才有的服务**：

```bash
# ① 在远端 10.113.66.6 起一个只监听 127.0.0.1:18999 的 HTTP 服务
iconv -f UTF-8 -t UTF-16LE remote-svc.ps1 | base64 -w0 ⇒
ssh xt "pwsh -NoProfile -NonInteractive -EncodedCommand <b64>"   # 输出 READY

# ② 本机直连 18999 → exit=7（连不上，符合预期）
# ③ 经 ssh -D 隧道访问 127.0.0.1:18999
python socks_verify.py 10900
```

实测结果：
```
响应正文: b'c
XT-REMOTE-OK
0

'
✅ 命中远端服务 —— 流量确实经隧道到了 10.113.66.6
```

**这是最硬的证据**：本机 18999 端口根本没有服务，能拿到 `XT-REMOTE-OK`
只可能是因为请求被转发到了远端、由远端连它自己的 127.0.0.1:18999。

### 3. 用我的 C# 代码跑真实远端（`test/TestRuntime.cs`）

```powershell
csc /target:exe /out:test\out\TestRuntime.exe /codepage:65001 ^
    /r:System.dll /r:System.Core.dll ^
    src\Config.cs src\Proxy.cs src\Tunnel.cs src\PortState.cs src\FerryRuntime.cs test\TestRuntime.cs
.	est\out\TestRuntime.exe 10.113.66.6 Administrator 22
```

实测输出：
```
[0] 直连出口 IP: 116.23.159.136
    > 端口：SOCKS 10800 / HTTP 10801
    > 隧道已启动：ssh -D 127.0.0.1:10800 → 10.113.66.6:22
    > 代理已监听 127.0.0.1:10801（上游 SOCKS 10800）
[1] ✅ 隧道 + 代理就绪
    隧道进程存活: True，代理监听: True
[2] ✅ 经代理访问 baidu 成功，16827 字节
[4] ✅ HTTPS CONNECT 成功，16827 字节
=== 端到端通过 ===
```

出口 IP 那一项显示「与直连相同」是**预期**的（同 NAT），不是故障。

### 4. 这一轮踩的坑

**① Base64 EncodedCommand 必须是 UTF-16LE**
```bash
# 错：base64 读出的是 UTF-8 字节，远端 pwsh 解析成乱码命令
base64 -w0 < script.ps1
# 对：
iconv -f UTF-8 -t UTF-16LE script.ps1 | base64 -w0
```

**② `ssh xt 'powershell -Command "..."'` 里的 `$` 会被吃掉**
三层引号（bash → ssh → pwsh）叠在一起，`$l` 变成 `l`。
必须走 EncodedCommand，不要试图嵌套引号。

**③ 出口 IP 对比在同 NAT 环境下没有意义**
两台机器共用一个公网出口时，走不走隧道出口都一样。
判断隧道是否工作必须用「远端本地独有的服务」，不能用出口 IP。

**④ 远端 HttpListener 看起来「杀不掉」，其实杀错了对象**

现象：`Stop-Process -Id (Get-NetTCPConnection ...).OwningProcess` 报
`无法停止进程"System (4)": 拒绝访问`。

**根因（重要，别被 PID 4 误导）**：.NET 的 `HttpListener` 不是自己开 socket，
它把 URL 注册给**内核驱动 HTTP.sys**，由 HTTP.sys 代为监听。
所以 `Get-NetTCPConnection` 看到的监听者是 `System (PID 4)` —— 那是内核，
当然杀不掉。真正持有这个 URL 的是那个跑 `HttpListener` 的 pwsh 进程。

**正确的查法与杀法**：
```powershell
# ① 从 HTTP.sys 的请求队列里确认 URL 真的注册着
netsh http show servicestate view=requestq | Select-String '18999' -Context 0,3

# ② 按命令行特征找那个 pwsh（会用 EncodedCommand 跑）
Get-CimInstance Win32_Process -Filter "Name='pwsh.exe'" |
  Where-Object { $_.CommandLine -match 'EncodedCommand' } |
  Select-Object ProcessId, CreationDate

# ③ 按 CreationDate 对上启动时刻后杀掉（实例：4056 / 2204 是 21:50:06 起的）
Stop-Process -Id 4056, 2204 -Force
```

实测：杀完两个 pwsh 后 `Get-NetTCPConnection -LocalPort 18999` 返回空，
`netstat | findstr 18999` 也干净了 —— **PORT-FREE**。

**教训**：Windows 上「端口被 System(4) 占着」几乎总是 HTTP.sys 在代持，
要顺着 `netsh http show servicestate` 找真正的宿主进程，而不是试图杀 System。

## 九点七、门户部署实录（10.13.24.1）—— 这一段照抄就能复现

### 门户跑在哪

**不在本机**，在远端 `10.13.24.1`（PCSERVER，Win10 教育版，账号 `pcserver`）：

```
进程   : C:\Users\PCserver\AppData\Local\Python\pythoncore-3.14-64\python.exe
         portal-server.py --port 8080 --db C:\ferry-portal\portal.db
                          --public-host 10.13.24.1 --ssh-user pcserver
工作目录: C:\ferry-portal
数据库   : C:\ferry-portal\portal.db
安装包   : C:\ferry-portal\dist\Ferry-Setup-<版本>.exe
```

所以「改完代码」和「用户看到新页面」之间还差两步：**同步文件 + 重启进程**。

### 部署三步（已验证）

```bash
# ① 同步门户代码（远端 shell 是 PowerShell，别用 copy 命令）
scp "E:/肥鱼不乐园/borrow-net/portal/portal-server.py" pcs:C:/ferry-portal/portal-server.py

# ② 同步安装包（门户从这里发下载）
ssh pcs 'powershell -NoProfile -Command "New-Item -ItemType Directory -Force -Path C:\ferry-portal\dist | Out-Null"'
scp "E:/肥鱼不乐园/borrow-net/dist/Ferry-Setup-0.8.0.exe" pcs:C:/ferry-portal/dist/Ferry-Setup-0.8.0.exe

# ③ 重启（见下，必须用计划任务）
```

### ⚠️ 重启必须用计划任务，`Start-Process` 会被回收

**第一次尝试失败**：用 `Start-Process -WindowStyle Hidden -PassThru` 起 python，
自检 `127.0.0.1:8080` 返回 200、进程存活 —— 但 **SSH 会话一断，进程就没了**。
用户那边立刻变成「无法访问」。

这是 AGENTS.md 记过的坑（「`Start-Process` 起的进程照样被回收」），这次又踩了。

**正确做法**（已落地，顺便解决「机器重启后门户不自启」）：

```powershell
$tr = 'cmd.exe /c cd /d C:\ferry-portal && "C:\Users\PCserver\AppData\Local\Python\pythoncore-3.14-64\python.exe" portal-server.py --port 8080 --db C:\ferry-portal\portal.db --public-host 10.13.24.1 --ssh-user pcserver'
schtasks.exe /Create /TN 'FerryPortal' /TR $tr /SC ONSTART /RU SYSTEM /RL HIGHEST /F
schtasks.exe /Run /TN 'FerryPortal'
```

`/RU SYSTEM` 让进程脱离用户会话，`/SC ONSTART` 让机器重启后自动起来。

改代码后的重启只需：停掉旧的 python → `schtasks /Run /TN FerryPortal`。

### 验证清单（从**本机**发，不是远端本地）

```
GET  http://10.13.24.1:8080/            → 200，13187 字节，含「下载 Windows 客户端」
GET  http://10.13.24.1:8080/portal      → 302（重定向到首页）
GET  http://10.13.24.1:8080/version.json → {"latest":"0.8.0","built":true}
GET  http://10.13.24.1:8080/download    → 200，
     Content-Type: application/vnd.microsoft.portable-executable
     Content-Disposition: filename="Ferry-Setup-10.141.59.235.exe"
     X-Ferry-Account: 10.141.59.235
     2123133 字节，MZ 头，MD5 与本机 dist 里的完全一致
```

`filename` 里带的是**请求方自己的 IP** —— 这就是「一份 exe 服务所有人」的落地方式，
安装程序从文件名解析账号，用户不用输任何东西。

### 这一段的坑

**① 远端 shell 是 PowerShell，不是 cmd**
`ssh pcs 'copy /Y a b'` 会报「找不到接受实际参数的位置形式参数」。
要用 `Copy-Item`，或者干脆用 scp。

**② `Get-NetTCPConnection -LocalPort 8080 -State Listen` 在这台机器上不可信**
`SSH主机-10.13.24.1.md` 早就写了「listen 计数不可信」。
判断服务活没活，**只看 HTTP 探测结果**，别信 netstat / Get-NetTCPConnection。

**③ 防火墙 Public profile 是开的**
但进程用 SYSTEM 跑、端口已放行，外部访问没问题。本次没新增防火墙规则。

## 九点八、两个必现 bug（用户实测撞到的）

### 1. 安装程序不跳过「账号确认」页 —— `ParseIPFromExeName` 的偏移错误

**现象**：浏览器下载到的文件名明明是 `Ferry-Setup-10.141.59.235.exe`，
双击后仍然弹「请输入你的局域网 IP」那一页。

**根因**（旧实现，`installer/ferry.iss`）：

```pascal
{ 旧：把「由数字/./- 组成的连续串」当候选 }
if (C >= '0') and (C <= '9') or (C = '.') or (C = '-') then
  Cur := Cur + C
...
if (Dots = 3) and (Pos('..', Cur) = 0) and (Cur[1] <> '.') and (Cur[Length(Cur)] <> '.') then
```

对 `Ferry-Setup-10.141.59.235.exe` 走一遍：

1. `Setup` 前面的 `-` 被吸收 → `Cur` 从 `-` 开始
2. `.exe` 的那个 `.` 也被吸收 → `Cur = "-10.141.59.235."`
3. `-` 换成 `.` → `".10.141.59.235."` —— **五个点、首尾都是点**
4. `Dots = 3` 判断永远不成立 → 返回空 → `NeedIpPage = True`

**所以这是必现 bug，不是边界情况。** 用户的文件名就是这么长。

**修法**：先 `ExtractFileName` 取纯名，把 `- _ 空格` 统一换成点，
再扫 `[0-9.]` 连续段，**去掉首尾点后**校验「恰好 4 段、每段 1–3 位数字」。

```pascal
S := ExtractFileName(FileName);
for I := 1 to Length(S) do
  if (S[I] = '-') or (S[I] = '_') or (S[I] = ' ') then S[I] := '.';
...
while (Cur <> '') and (Cur[1] = '.') do Delete(Cur, 1, 1);
while (Cur <> '') and (Cur[Length(Cur)] = '.') do Delete(Cur, Length(Cur), 1);
{ 然后逐段验证 1–3 位数字、总共 4 段 }
```

**实测验证**（静默安装，看写出的 config.json）：
```
accountIp: "10.141.59.235"   ✅ 解析成功
token: ""                    ✅ 留给用户激活
```

**教训**：文件名解析这种「看起来很简单」的函数，必须拿**真实文件名**跑一遍。
我上一轮只测了 `setup.exe`（无 IP → 走输入页，符合预期），
没测 `Ferry-Setup-10.141.59.235.exe` 这个真正的形态。

### 2. 安装包把 token 直接写进 config.json —— 绕过激活流程

**现象**：修好 IP 解析后安装，`config.json` 里竟然带着
`"token": "rzhybc-mprqvz-3g89ca-ch8a74"`，软件一打开就是「已激活」。
而且没有 `expiresAt` → 显示「永久授权」。

**根因**：`/api/config` 把 `clients.token` 直接返回，安装程序原样落盘。

**这违背用户的核心要求**（「密钥在软件里输入激活借网使用权限」）：
- 新用户装完就「已激活」，看不到剩余时长，也没走激活这一步
- 安装包一旦被转发，凭据跟着泄露
- 授权时长形同虚设（expiresAt 是 0 = 永久）

**修法**：`/api/config` 的 `token` 固定返回空串，注释写明理由：

```python
'accountIp': want,
# token 故意留空 —— 用户明确要求「密钥在软件里输入激活」。
# 从安装包直接下发 token 的话，新用户装完就是「已激活」状态，
# 既看不到剩余时长，也绕过了激活这一步；而且安装包一旦被转发，
# 凭据就跟着泄露了。token 只能由 POST /api/activate 签发。
'token': '',
```

实测验证：
```
GET /api/config?ip=10.141.59.235
→ {"remoteHost":"10.13.24.1","sshPort":22,"httpPort":8081,
   "accountIp":"10.141.59.235","token":"","fallbackProxy":"",
   "portalUrl":"http://10.13.24.1:8080"}
```

安装后 config.json 的 `token` 为空，界面显示「未激活 —— 在下面的框里输入密钥」。

**设计原则**：安装包只带**连接参数**（远端地址、端口、门户地址、账号 IP），
**绝不含凭据**。凭据只能由用户在软件里交换钥换出来。

### 3. 这一轮的部署顺序（易错）

改 `portal-server.py` 后必须**两个动作都做**，少一个用户就看到旧页面：

```bash
# ① 同步
scp "E:/肥鱼不乐园/borrow-net/portal/portal-server.py" pcs:C:/ferry-portal/portal-server.py
# ② 重启（计划任务，不能用 Start-Process）
ssh pcs "powershell -Command \"Stop-Process -Id <旧PID> -Force; schtasks /Run /TN FerryPortal\""
```

改 `ferry.iss`（安装包）后则要**三步**：

```bash
ISCC.exe ferry.iss                                            # ① 重编译
scp dist/Ferry-Setup-0.8.0.exe pcs:C:/ferry-portal/dist/      # ② 传上去
# ③ 门户不用重启 —— installer_path() 每次请求都重扫 dist 目录
```

`installer_path()` 每次请求都 `listdir + stat`，所以换包**不用重启门户**，
这点和改代码不同，容易记混。

## 九点九、废掉「文件名传 IP」——改成服务端自动识别

### 用户的质疑（成立）

> 为什么不能自动识别局域网 IP，而是要根据网页再给安装包命名，再读取命名

这个批评是对的。原设计让**文件名当数据通道**，有四个硬伤：

1. **浏览器会改名** —— 下第二次变成 `Ferry-Setup-1.2.3.4 (1).exe`
2. **人会手改** —— 改成 `Ferry-Setup.exe` 就没 IP 了
3. **拷到别的机器装就全错** —— 文件名里的 IP 是下载那台机器的
4. **概念绕** —— 用户得理解「文件名不是名字，是参数」

### 新设计：服务端按 `client_address` 判定

安装程序启动时发一个**不带参数**的请求：

```
GET {PortalBase}/api/config
```

服务端用 `self.client_ip()`（即 TCP 连接的对端地址）作为账号 IP。
**这是最准确的来源** —— 它不需要安装程序去枚举网卡（多网卡 / 虚拟网卡 / VPN
很容易挑错那块），也不依赖任何文件名。

服务端改动（`handle_api_config`）：

```python
want = (qs.get('ip') or [''])[0].strip()
src  = self.client_ip()

if want:
    if want != src:          # 显式指定只允许自己（装到别的机器的场景仍支持）
        self.send_json({'error': 'ip mismatch', 'src': src}, 403); return
else:
    want = src               # 不带参数 = 让服务端判断你是哪台机器

cli = self.db.q1('SELECT * FROM clients WHERE ip = ?', (want,))
if not cli or not cli['enabled']:
    # 未注册不阻断安装：回 200 + registered=false，照常写出一份可用 config，
    # 用户在软件里输密钥时才建号
    ...
```

### 安装程序的三级回退

`InitializeWizard` 里按顺序试：

```pascal
① GET {PortalBase}/api/config（无参）→ 取返回的 accountIp
② 失败 → ParseIPFromExeName({srcexe})        （文件名兜底，覆盖「装到另一台机器」）
③ 都失败 → NeedIpPage = True，弹输入页        （最后手段）
```

### 关键细节：必须强制直连，不能走系统代理

```pascal
Http.Open('GET', Url, False);
Http.SetTimeouts(...);
try
  Http.SetProxy(1, '');   { 1 = HTTPREQUEST_PROXYSETTING_DIRECT }
except
end;
Http.Send;
```

**不加这行会静默错**：本机若开着 Clash / 公司代理，请求从代理出去，
服务端看到的 `client_address` 就变成 `127.0.0.1` 或代理出口 IP，
自动识别到的账号直接错掉 —— 而且不会报错，只是「装完发现账号不对」。

### 下载文件名也简化

`/download` 不再拼 IP：
```python
fname = 'Ferry-Setup-' + 版本 + '.exe'
```
文件名回归普通。IP 解析仍然保留在安装程序里作为兜底，
但**不再是必要条件** —— 这是这次改造的核心。

### 实测

**① 用完全不含 IP 的文件名安装**（`setup.exe`）：
```
$ Copy-Item Ferry-Setup-0.8.0.exe $TEMP\ferry-auto\setup.exe
$ .\setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
安装退出码=0

config.json:
{"registered": true, "remoteHost": "10.13.24.1", "sshPort": 22, "httpPort": 8081,
 "accountIp": "10.141.59.235", "token": "", "portalUrl": "http://10.13.24.1:8080"}
```

**`accountIp` 自动识别成 10.141.59.235** —— 文件名一个数字都没有。

**② 服务端直接验证**：
```
GET http://10.13.24.1:8080/api/config        （不带任何参数）
→ {"registered": true, "accountIp": "10.141.59.235", "token": "", ...}
```

**③ 下载下来的文件名**：
```
Content-Disposition: attachment; filename="Ferry-Setup-0.8.0.exe"
```

### 现在三条路都能走通

| 场景 | 走哪条 | 结果 |
|---|---|---|
| 门户可达（正常情况） | ① 服务端识别 | 100% 正确，与文件名无关 |
| 门户连不上 | ② 文件名解析 | 只要文件名没被改就正确 |
| 都没拿到 | ③ 手填 | 用户自己输 |

**设计原则记下来**：能被服务端观察到的信息（TCP 对端地址），
永远比让客户端自报（文件名 / 网卡枚举 / 用户输入）更可靠。

## 九点十、管理面板：实时借网监控 + 圆角白边修复

### 用户要求

> 管理面板还应该实时显示正在借网的用户的在线状态，还有下载上传速度监控等等的，
> 界面尽量现代化加美观，符合专业软件的样子

### 1. 在线状态：为什么不靠客户端心跳

门户**就跑在提供借网的那台主机上**（10.13.24.1）。借网用户会在这个机器上
留一条到 22 端口的 SSH 连接。所以直接看 sshd 的 `ESTABLISHED` 连接就知道谁在线：

```python
def _ssh_peers():
    """正在连本机 22 端口的外网地址 → 连接数。"""
    out = _sh(['netstat', '-ano'])
    for line in out.splitlines():
        parts = line.split()
        if parts[0].upper() != 'TCP': continue
        local, foreign, state = parts[1], parts[2], parts[3]
        if state.upper() != 'ESTABLISHED': continue
        if not (local.endswith(':22') or local.endswith(']:22')): continue
        ip = foreign.rsplit(':', 1)[0].strip('[]')
        if not ip or ip.startswith('127.') or ip == '::1': continue
        peers[ip] = peers.get(ip, 0) + 1
```

**这比客户端上报心跳可靠得多**：
- 客户端崩了、被关了、断网了 → 连接立刻断 → 状态立刻消失
- 不会出现「面板显示在线但人已经走了」这种假数据
- 不需要给客户端加任何上报代码，老版本客户端也能被监控到

### 2. 速率：整机网卡字节差

```python
def _net_bytes():
    """全部网卡的累计收/发字节。"""
    out = _sh(['powershell', '-NoProfile', '-NonInteractive', '-Command',
               '$r=0;$t=0;Get-NetAdapterStatistics|%{$r+=$_.ReceivedBytes;$t+=$_.SentBytes};"$r $t"'])
    return int(seg[0]), int(seg[1])   # 两次采样求差 / 时间 = Bps
```

**限制要说清楚**：这是**整机**上传/下载，不是单用户。
Windows 没有 per-connection 或 per-process 的网络字节计数，
真要做得用 ETW 订阅 TCP/IP 事件 —— 对这个场景不值得。

### 3. 缓存 2 秒（必须）

每次采样要起两个进程（`netstat` + `powershell`），合计约 300 ms。
管理页每 3 秒轮询一次，如果不缓存，光采样就吃掉半秒 CPU。
所以 `sample_live()` 带 2 秒缓存，重复请求直接返回上次结果。

### 4. 接口

```
GET /api/admin/live        （需要管理员登录，否则 403）
→ {
    "ok": true,
    "at": 1792766229,
    "online": [{"ip": "10.141.59.235", "conns": 1}],
    "onlineCount": 1,
    "conns": 1,
    "rxBps": 0, "txBps": 0,
    "peakRxBps": 0, "peakTxBps": 0,
    "users": {"10.141.59.235": {"enabled": true, "registered": true, "note": ""}}
  }
```

实测（真实环境，远端直接调函数）：
```
{"ok": true, "at": 1792766229, "online": [{"ip": "10.141.59.235", "conns": 1}],
 "onlineCount": 1, "conns": 1, "rxBps": 0, "txBps": 0,
 "users": {"10.141.59.235": {"enabled": true, "registered": true, "note": ""}}}
```
（`rxBps/txBps` 为 0 是首次采样没有上一次数据，第二次轮询就有值）

### 5. 前端：四指标卡 + 在线表 + 3 秒轮询

布局：
```
┌ 下行峰值 1.2 MB/s · 上行峰值 340 KB/s        实时 · 22:35:12 ┐
┌──────────┬──────────┬──────────┬──────────┐
│ 正在借网 │ 活跃连接 │ 下行速率 │ 上行速率 │
│    2 台  │   5 条   │ 1.2 MB/s │ 340 KB/s │
│ 5 条隧道 │ 到本机22 │ ▬▬▬▬▬░░  │ ▬▬▬░░░░  │
└──────────┴──────────┴──────────┴──────────┘
┌ 正在借网的机器 ────────────────────────────┐
│ ● 10.115.111.2   2   正常    张三-3楼      │
│ ● 10.115.111.1   3   已停用  测试          │
└────────────────────────────────────────────┘
```

细节：
- **脉冲绿点**（`@keyframes pulse`）表示在线
- **指标卡 `.hot` 状态**：有活动时数字变强调色 + 底部渐变条亮起
- **进度条按「当前/峰值」**显示，一眼看出是不是尖峰
- **速率自动换单位**：`B/s → KB/s → MB/s → GB/s`
- 数字用等宽字体（`Consolas`），避免刷新时宽度跳动
- 断线时右上角变红显示「连接中断 · HTTP xxx」
- **全部用主题变量**（`var(--card)` / `var(--accent)` / `var(--fg2)`），
  切深色/浅色/护眼三套主题都不用改监控区

### 6. 已知道具局限

**管理员自己的 SSH 运维连接会被算成「借网用户」**。
两者在 netstat 层面完全一样（都是到 22 端口的 ESTABLISHED），
除非客户端在连接时带上可区分的特征（比如固定用户名前缀），否则分不开。
当前选择是不区分 —— 管理员看到自己的 IP 出现在列表里应该能理解。

### 7. UI 修复：圆角卡片的白边

用户截图圈出「每个圆角窗口下都留了白边」。

**根因**：`Panel` 的 `BackColor` 是**矩形**填充，只 `DrawPath` 画边框的话，
圆角外面那四个角仍然是白的；父容器是浅灰时四个角就明显跳出来。

**修法**：`Region` 也裁成同一个圆角路径 —— 圆角外的像素根本不属于这个控件。

```csharp
public static Panel Card(int radius)
{
    var p = new Panel();
    p.BackColor = CardBg;
    Action clip = delegate {
        using (var path = Round(new Rectangle(0, 0, p.Width, p.Height), radius))
            p.Region = new Region(path);      // ← 关键
    };
    p.Paint += delegate { /* DrawPath 画 1px 边框 */ };
    p.Resize += delegate { clip(); };          // 尺寸变了要重算
    clip();
    return p;
}
```

Region 用 `(0,0,w,h)` 撑满、边框画在 `(0,0,w-1,h-1)` 内侧 ——
不然 1px 描边会被裁掉一半变细。

**顺手把按钮也做了圆角**（`RoundControl`，半径 8），
因为直角按钮和圆角卡片摆在一起很割裂。

## 九点十一、程序图标 + 设置面板

### 1. 桌面快捷方式图标是「空白文档」—— exe 没嵌图标

用户截图里快捷方式显示成一个空白页 + 箭头，那是 Windows 对「没有图标资源的 exe」
的默认呈现。快捷方式的图标是从目标 exe 里取的，exe 没图标，快捷方式自然也没有。

**根因**：`build.ps1` 里 `/win32icon:` 是条件参数 ——
```powershell
$icon = Join-Path $here 'app.ico'
$iconArg = if (Test-Path $icon) { "/win32icon:$icon" } else { '' }
```
而 `ferry-client/app.ico` 从来就没生成过，所以一直是「静默地不带图标」。

**修法**：用 Pillow 生成多尺寸 ico（`ferry-client/make-icon.py`）。

设计思路：
- **青绿圆角方块底**（`#20B2AA`，就是客户端主色，图标和界面同色系）
- **白色双向箭头 ⇄** = 「借网 / 直连」两条路可切换，一眼能懂
- 多尺寸 16/24/32/48/64/128/256

关键细节：**小尺寸单独重画，不要缩放大图**。
16px 下如果直接缩放 256px 的图，笔画会糊成一团 —— 所以 `draw(size)` 里
笔画宽度和箭头大小都按尺寸分了档：

```python
w = max(2, int(S * (0.085 if size <= 24 else 0.070)))
head = int(S * (0.16 if size <= 24 else 0.135))
```

生成后 16 107 字节，`ExtractAssociatedIcon` 验证 32×32 已嵌入。
快捷方式（安装程序建的）会自动继承这个图标，不用额外配。

### 2. 设置面板

用户要求：「软件里添加设置，设置的内容包括是否开机自启以及断网自动借网等功能」。

放在主界面「刷新」和「卸载」之间的**设置**按钮 → 弹独立对话框
（主窗口已经 526×777，再塞一页会挤；设置是低频操作，弹窗关掉就完事）。

四个开关 + 一个数值：

| 设置项 | 默认 | 作用 |
|---|---|---|
| 开机自动启动 | 关 | 登录 Windows 后自动就绪（收在托盘，不弹窗） |
| 断网时自动借网 | 关 | 检测到连不上网时自动开隧道 + 切系统代理 |
| 启动后最小化到托盘 | 关 | 双击图标时不显示主窗口，直接驻留托盘 |
| 隧道断开时自动重连 | **开** | 远端重启、网络中抖时自动恢复（退避 5 秒起，最长 60 秒） |
| 断网检测间隔 | 15 秒 | 10–300 可调 |

存在 `config.json` 里（`autoBorrow` / `startMinimized` / `autoReconnect` / `netCheckSec`），
门户下发时不带这些字段，`Cfg.Load` 用默认值兜底。

### 3. 「开机自启」不存 config —— 以文件系统为准

这是本次唯一一个**故意不存配置**的开关：

```csharp
public static bool AutoStartOn() { return File.Exists(StartupLnk()); }
public static void SetAutoStart(bool on) { /* 增删 Startup\渡口自动启动.lnk */ }
```

**理由**：安装程序（Inno 的 `[Tasks] autostart`）也会建同一个快捷方式。
如果软件里再记一个 `autoStart: true` 状态，必然出现
「软件显示没勾、实际开机却启动了」这种鬼故事。
**以 `%APPDATA%\...\Startup\渡口自动启动.lnk` 是否存在为唯一真相**，
安装时勾了就是勾了，软件里看到的就是实际状态。

快捷方式用 `WScript.Shell` 建（.NET Framework 里不引第三方库的唯一写法），
`WindowStyle = 7` 让它开机时最小化启动。

### 4. 断网自动借网的触发条件

```csharp
if (!ok && wasNet && _cfg.AutoBorrow && _cfg.Activated && !_busy)
{
    // 只在「有网 → 无网」这一跳触发
    if (rt == null || !rt.Running) { Log("检测到断网，自动开始借网…"); DoStart(); }
}
```

三个刻意的限制：
1. **只在状态从「有网」跳到「无网」时触发**，不是「只要没网就开」——
   否则一旦借网失败，下一个检测周期会无限重试。
2. **不自动停**。网络抖动很常见，来回切系统代理比一直挂着更容易出事；
   用户想停就点一下按钮。
3. **未激活不触发** —— 没授权就开隧道会拿空令牌去连，失败信息对用户毫无意义。

### 5. 这一轮踩的坑

**Pascal / C# 字符串里写字面换行**

`SettingsDialog.cs` 里写提示文案时，我最初写：
```python
'_lblHint = Theme.Text2("间隔越短越灵敏…；\\n断网后…", ...)'
```
这在 Python 里确实产出了 `\n`（反斜杠+n），但经过 heredoc + 多层引号后
中间某处被解释成了**真换行**，C# 源码里出现跨行字符串 →
`error CS1010: 常量中有换行符`。

正确写法：**用 `Environment.NewLine` 拼接**，彻底避开转义：
```csharp
_lblHint = Theme.Text2("间隔越短越灵敏…；" + Environment.NewLine +
                       "断网后大约一个间隔内会自动开始借网。", Theme.FSmall, Theme.Muted);
```

**设置对话框高度要留够**

第一版 430 高，卡片 236 高，「断网检测间隔」那一行正好压在卡片下边缘上被切掉。
改成 512 高 / 卡片 274 高，间隔行挪到 368、提示 400、按钮 452 才干净。

**教训**：WinForms 里手动 `SetBounds` 布局时，**每加一行内容都要把窗口和卡片
一起加高**，否则下面那行会被父容器边界裁掉，而且不会有任何报错。

## 九点十二、性能优化 + 代码签名

### 一、检测间隔到底吃多少性能（用户问）

原实现有三处真实浪费，都改掉了：

**① DNS 探测会漏句柄**

```csharp
// 旧：超时后 AsyncWaitHandle 不 Dispose，回调线程还在
var ar = Dns.BeginGetHostAddresses("www.baidu.com", null, null);
if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
```

每 15 秒漏一个，挂一天 5 760 个；慢网络（DNS 频繁超时）漏得更快。
改成 `GetHostAddressesAsync` + `Task.Wait` —— 超时只是放弃等待，Task 自己会结束。

**② 每次都真的发 DNS 请求**

改成两层：

```csharp
// 第一层：零开销。看有没有「已启用且有 IPv4 地址」的网卡。
// 网线拔了 / Wi-Fi 断了，这一步就知道，一个包都不发。
foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()) { ... }
if (!any) return false;

// 第二层：才真解析一次
var task = Dns.GetHostAddressesAsync("www.baidu.com");
if (!task.Wait(timeoutMs)) return false;
```

**③ 从纯轮询改成事件驱动（这是最大的改进）**

```csharp
NetworkChange.NetworkAvailabilityChanged += delegate { ProbeNet(); };
NetworkChange.NetworkAddressChanged      += delegate { ProbeNet(); };
```

系统网络状态一变就立刻探测。定时器退化成**兜底**（覆盖「Wi-Fi 断了但系统没发通知」
这种边缘情况），正常情况下 15 秒一次的定时器几乎不产生真实 DNS 流量。

**④ UI 每秒重绘**

`Label.Text = ...` 即使文本没变也会触发 `OnTextChanged` → `Invalidate` → 重绘。
1 秒心跳里更新七八个 Label，界面静止时也在白烧 CPU（笔记本上能看出风扇）。

```csharp
static void SetText(Label l, string text, Color color)
{
    if (l.Text != text) l.Text = text;
    if (l.ForeColor != color) l.ForeColor = color;
}
```

**结论**：优化后静止状态下每秒开销约等于「读两次注册表 + 比较几个字符串」，
基本测不出来。真正发 DNS 的只有「启动时 + 网络状态变化时」。

**还顺手修了一个隐患**：`ProbeNet` 里 `_probingNet = false` 原本不在 `finally` 里，
`HasNetwork` 一旦抛异常，`_probingNet` 永远为 true，**网络检测从此静默停摆**。

### 二、代码签名（解决 SmartScreen）

#### 三种路径，按成本排序

| 方案 | 成本 | 效果 | 落地 |
|---|---|---|---|
| **命令行下载**（绕 MOTW） | 零 | SmartScreen 完全不触发 | ✅ 已做，网页上有现成命令 |
| **自签名证书** | 免费 | exe 显示「发布者：Ferry」；SmartScreen 通常放行 | ✅ 已做，需客户端装一次根证书 |
| **EV 代码签名证书** | 3 000–5 000 元/年 | 100% 不弹，云端信誉立刻建立 | 需采购 |

核心原理：**SmartScreen 只检查带 MOTW（Mark of the Web）的 exe**。
浏览器下载会给文件打上「来自 Internet」的备用数据流；用 PowerShell /
curl 直接下载则不带。所以「命令行下载」是零成本且彻底有效的路子。

#### 本机实现（已落地）

```
installer/make-cert.ps1    生成证书
installer/sign.ps1         签名
installer/trust-cert.ps1   客户端装根证书
installer/ferry-sign.cer   公钥证书（发给客户端）
installer/ferry-sign.pfx   私钥（不要提交 git）
```

**踩的第一个坑：`New-SelfSignedCertificate` 用不了**

```
CertEnroll::CX509Enrollment::_CreateRequest: 拒绝访问。0x80090010 (NTE_PERM)
```

它要把密钥写进 CryptoAPI 的密钥容器，本机策略限制了这个。
改用 **Git 自带的 openssl** 在内存里生成（纯文件操作，不碰密钥容器）：

```powershell
& $ossl req -x509 -newkey rsa:2048 -keyout key.pem -out cert.pem -days 1825 -nodes `
    -subj '/CN=Ferry/O=Ferry Network Tool/C=CN' `
    -addext 'extendedKeyUsage=codeSigning' -addext 'keyUsage=digitalSignature'
```

**踩的第二个坑：OpenSSL 3 默认的 pfx 加密 .NET 读不了**

OpenSSL 3 默认用 PBES2（AES-256 + PBKDF2），`.NET Framework` 的
`X509Certificate2` 支持不稳，会报「密码无效」。必须显式指定传统算法：

```powershell
& $ossl pkcs12 -export -certpbe PBE-SHA1-3DES -keypbe PBE-SHA1-3DES -macalg sha1 ...
```

（`-legacy` 参数不行，本机 openssl 没加载 legacy provider，会报
`provider_init:reason(37):name=legacy`）

**签名的正确顺序（很容易搞错）**

```
① build.ps1 编译 exe
② 签 exe          ← 必须在这一步，否则安装包里内嵌的是没签名的版本
③ ISCC 打包
④ 签安装包
```

我第一次是先打包再签名，结果安装包里的 exe 还是未签名的。

**签名成功的判据不是 `Status`**

```powershell
$r = Set-AuthenticodeSignature -FilePath $t -Certificate $cert -HashAlgorithm SHA256
```

本机没装根证书时，`Status` 永远是 `UnknownError`
（「已处理证书链，但是在不受信任的根证书中终止」）—— **这不代表签名失败**。
真正的判据是 `SignerCertificate` 有没有值：

```
渡口助手.exe              85 016 字节  签名=有  发布者=C=CN, O=Ferry Network Tool, CN=Ferry
Ferry-Setup-0.8.0.exe  2 145 584 字节  签名=有  发布者=C=CN, O=Ferry Network Tool, CN=Ferry
```

#### 客户端的最后一公里：装根证书必须人工确认

尝试自动化装根证书时被 Windows 拦下：

```
Exception calling "Add" with "1" argument(s): "操作已被用户取消。"
```

不是权限问题（CurrentUser 库不需要管理员），而是 **Windows 弹了安全警告对话框**
（「你即将安装来自……的证书」），没有人点「是」就超时取消。

**这是 Windows 的安全设计，绕不过去**。所以 `trust-cert.ps1` 必须由用户
右键「使用 PowerShell 运行」手动跑一次，点掉两个框（UAC + 证书确认）。

#### 部署

`ferry-sign.cer` 和 `trust-cert.ps1` 已一起传到门户的 `dist/` 目录，
将来可以让门户提供下载（目前需手动取）。

## 九点十三、SmartScreen：放弃证书方案，改成一键安装脚本

### 用户的问题

> 证书解决不了了吗（如果你说的方法很麻烦那就是没有用，忽略）

这个判断是对的。

### 自签名证书为什么放弃

装根证书时被 Windows 拦下：

```
Exception calling "Add" with "1" argument(s): "操作已被用户取消。"
```

不是权限问题（`CurrentUser\Root` 不需要管理员），是 **Windows 弹了安全警告对话框**
（「你即将安装来自……的证书」），没人点「是」就超时取消。
**这是安全设计，不可能自动化。**

于是每台使用者的机器都要手动：右键 → 使用 PowerShell 运行 →
UAC 确认 → 证书确认。

**这套流程比 SmartScreen 本身麻烦得多**（SmartScreen 只要「更多信息 → 仍要运行」两次点击），
而且是发给几十个不特定的人用 —— 他们不会为了装个软件去动证书库。

**结论：自签名证书对这个场景零价值，放弃。** 脚本留着（`make-cert.ps1` /
`sign.ps1` / `trust-cert.ps1`），将来买 EV 证书或者内部小范围用得上。

### 换成什么：一键安装 .cmd

**原理（已实测）**：SmartScreen 只检查带 **MOTW（Mark of the Web）** 的 exe/msi。
浏览器下载会给文件打上「来自 Internet」的备用数据流；而程序化下载不会。

实测对比（下载 `dist/Ferry-Setup-0.8.0.exe`，2 145 584 字节）：

| 下载方式 | Zone.Identifier | 会触发 SmartScreen |
|---|---|---|
| `Invoke-WebRequest -OutFile` | 无 | ❌ 不会 |
| `(New-Object Net.WebClient).DownloadFile` | 无 | ❌ 不会 |
| 浏览器 | 有（`ZoneId=3`） | ✅ 会 |
| 手工打 MOTW 的对照 | 有 | ✅ 会（证明检测方法有效） |

而 **`.cmd` 本身不在 SmartScreen 的检查范围里**。

所以链路是这样：

```
用户双击「安装渡口.cmd」
    ↓  它内部用 WebClient 把 exe 拉到 %TEMP%
    ↓  exe 不带 MOTW
    ↓  Start-Process 直接启动
SmartScreen 全程不触发
```

**成本：一次双击。比「更多信息 → 仍要运行」还少一步。**

### 实现

门户新增 `GET /download-cmd`，返回一个 782 字节的 `安装渡口.cmd`（GBK 编码，
中文系统才不乱码）：

```bat
@echo off
chcp 65001 >nul 2>&1
setlocal
title 渡口 · 安装
set "URL=http://10.13.24.1:8080/download"
set "OUT=%TEMP%\Ferry-Setup.exe"
if exist "%OUT%" del /q "%OUT%" >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue';
  try {
    (New-Object Net.WebClient).DownloadFile('%URL%','%OUT%');
    Unblock-File -Path '%OUT%' -ErrorAction SilentlyContinue;
    Start-Process '%OUT%'
  } catch { Write-Host ('下载失败：' + $_.Exception.Message); exit 1 }"
if errorlevel 1 pause
```

两个细节：
- **`Unblock-File`** 兜一层 —— 万一某些环境仍打了 MOTW，这一步会去掉
- **脚本只有十几行、没有混淆**，用户右键「编辑」就能一眼看全。这很重要：
  让人双击一个不明脚本的前提是它能被读懂

首页主按钮改成「**一键安装（推荐）**」→ `/download-cmd`，
下面保留一个不显眼的「直接下载 exe」链接（会触发 SmartScreen，但有人就想直接拿文件）。

### 三种方案的成本对照（最终结论）

| 方案 | 用户要做的事 | 评价 |
|---|---|---|
| **一键安装 .cmd** | **双击一次** | ✅ 采用 |
| 直接下载 exe | 点「更多信息」→「仍要运行」 | 保留为备选 |
| 自签名证书 | 手动装根证书（UAC + 证书确认） | ❌ 放弃，比问题本身更麻烦 |
| EV 证书 | 什么都不用做 | 要钱，将来考虑 |

**核心教训**：解决一个摩擦点时，先算「新方案让用户多做什么」。
这里自签名把「两次点击」换成了「改系统信任库」，方向就是错的；
而 .cmd 把它换成了「一次双击」，方向才对。

### 部署验证

```
/            → 200，15 225 字节，含「一键安装（推荐）」+「直接下载 exe」
/download-cmd → 200，782 字节，GBK 编码，无 MOTW
```

## 九点十四、管理页用户列表重做（路由器风格）+ 每用户速率

### 用户需求（原话节选）

> 它们应该是一个列表，比如已登录机器，就显示它们的在线状态，在线的置顶，
> 不在线的往下面靠，然后这个界面是一个滚动窗口，显示每个独立用户的
> 下载、上传速度，像那种专业软件，或者路由器一样。

另外明确：**还是沿用 exe 安装程序**（撤销 .cmd 一键装主按钮，证书不管）。

### 每用户速率：Windows 拿不到，只能让客户端报

先试过纯服务端的路子 —— `Win32_PerfFormattedData_PerfProc_Process`
有每个 sshd 进程的 `IOReadBytesPerSec` / `IOWriteBytesPerSec`，理论上能对上
「每个连接的速度」。**实测首次查询卡住 60 秒以上**（要加载性能计数器库），
远端更不敢赌这条路。

最终方案：**客户端心跳 + 服务端 sshd 连接，两者互补**。

| 数据 | 来源 | 为什么 |
|---|---|---|
| 谁在线 | 服务端 `netstat` 看 22 端口 `ESTABLISHED` | 客户端崩了连接立刻断，不会留假在线 |
| 每用户速率 | 客户端每 5 秒 POST 累计字节 | 只有客户端自己知道走了多少流量 |
| 总速率 | 服务端网卡累计字节差 | 全局参考值 |

**客户端**（`Api.Heartbeat`，5 秒一次，失败静默）：
```csharp
var rx = rt != null ? rt.BytesDown : 0;   // 下行 = 远端 → 本机
var tx = rt != null ? rt.BytesUp : 0;     // 上行 = 本机 → 远端
Api.Heartbeat(_cfg, rx, tx, (int)rt.ConnActive, rt.Running);
```

**服务端**（`note_heartbeat`）两次采样求差：
```python
dt = t - prev['at']
rx_bps = max(0, int((rx - prev['rx']) / dt))   # 客户端重启会让计数归零，负数按 0
```

实测（真实调用三次）：
```
第一次（无基线）        → {"rxBps":0,       "txBps":0}
2.02 秒后 rx +400000    → {"rxBps":197698,  "txBps":14827}   ✅ ≈200 KB/s
2.01 秒后 rx +200000    → {"rxBps":99514,   "txBps":4975}    ✅ ≈100 KB/s
```

鉴权和 `/api/license` 一致（`ip` + `token`，`hmac.compare_digest`），
错 token 返回 401（已测）。

### 列表：在线置顶 + 可滚动 + 等宽数字

**排序在服务端算一遍、前端再按 rank 重排一遍**：

```python
ulist.sort(key=lambda u: (0 if u['online'] else 1,        # 在线优先
                          -(u['rxBps'] + u['txBps']),      # 快的在上
                          -u['lastSeen']))                 # 同速率按最近活动
```

前端 `data-rank`：
- 在线 → `1e9 - (rxBps + txBps)`
- 离线 → `-(lastSeen)`

JS 只做「更新单元格 + 按 rank 重排 DOM」，**不重建行** ——
行里有「停用 / 重置令牌」的 form 和 CSRF token，重建会丢。

```javascript
var rows = Array.prototype.slice.call(body.children);
rows.sort(function(a,b){
  return (parseFloat(b.getAttribute('data-rank'))||0) -
         (parseFloat(a.getAttribute('data-rank'))||0);
});
rows.forEach(function(r){ body.appendChild(r); });
```

### 视觉细节（路由器那味儿）

- **滚动容器**：`.listwrap { max-height:420px; overflow-y:auto }`
- **表头吸顶**：`thead th { position:sticky; top:0; background:var(--card2) }`
  滚动时列名不跟着跑
- **在线行微高亮**：`tr[data-on="1"] { background: color-mix(in srgb, var(--accent) 5%, transparent) }`
- **状态点**：在线是脉冲绿点（`@keyframes pulse` 扩散阴影），离线是灰点且停掉动画
- **数字列**：`font-variant-numeric: tabular-nums` + 等宽字体 + 右对齐 ——
  刷新时数字不会左右抖
- **离线行的速率显示「—」而不是「0 B/s」**，避免误读成「在跑但没流量」
- **滚动条**自绘（`::-webkit-scrollbar`），不用系统那个突兀的方块

### 降级行为

老版本客户端不上报心跳：
- 该用户**在线状态照常准确**（服务端看连接）
- 速率列显示「—」，`title` 提示「该客户端未上报心跳（老版本）」

这比「显示 0 B/s」诚实 —— 后者会被误读成「连上了但没流量」。

### 撤销 .cmd 一键装

用户明确要沿用 exe 安装程序，首页主按钮改回：

```html
<a class="dl" href="/download">下载 Windows 客户端</a>
```

`/download-cmd` 端点保留（不占地方，将来要再用不必重写），但页面不再露出。

## 九点十五、「此应用无法在你的电脑上运行」—— 是 0 KB 残片，不是安装包坏了

### 现象

用户双击安装包，Windows 弹：

```
此应用无法在你的电脑上运行
若要找到适用于你的电脑的版本，请咨询软件发布者。
```

用户判断：「之前的还能安装，你这个直接完全不能安装了」。

### 排查结论：服务端完全正常，用户点的是 0 KB 的空文件

看一眼用户截图里的文件列表就清楚了：

```
Ferry-Setup-0.8.0(1).exe            0 KB      ← 用户点的这个
Ferry-Setup-0.Nju4fx_v8.0(1).exe.part         ← Edge 的未完成临时文件
Ferry-Setup-0.8.0.exe           2,093 KB      ← 这个是好的
Ferry-Setup-10.141.59.235(1).exe 2,074 KB
Ferry-Setup-10.141.59.235.exe    2,074 KB
```

**「此应用无法在你的电脑上运行」正是 0 字节 exe 的标准报错。**
Windows 加载 PE 头失败，不知道该用什么架构跑，就报这句 ——
它跟「SmartScreen 拦截」「架构不对」是两回事。

服务端侧三次连续下载（从本机打远端门户）：

```
第1次: 2143228 字节, HTTP 200, 0.239s
第2次: 2143228 字节, HTTP 200, 0.293s
第3次: 2143228 字节, HTTP 200, 0.258s

MD5: 38c3bfe5780d41fddde65cab3a45a336（三次完全一致，也与本机 dist 一致）
MZ 头: 4D 5A（正确）
```

所以不是包坏了，是**下载中断留下了残片**，浏览器又给它加了个 `(1)` 后缀。
`.part` 文件的存在也印证了这一点 —— 那是 Edge 没下完的临时文件。

### 改进（虽然根因在客户端，服务端能做的都做了）

**1. 下载改分块写**

```python
CHUNK = 65536
try:
    for i in range(0, len(data), CHUNK):
        self.wfile.write(data[i:i + CHUNK])
except (BrokenPipeError, ConnectionResetError):
    return
```

一次 `write` 2 MB 时，如果中途被杀软或代理掐断，TCP 会断在任意位置；
分块写能在中断时**立刻察觉并干净退出**，不会在服务端留下挂起的连接。

`/download` 和 `/download-latest` 都改了。

**2. 响应头加 `X-Ferry-Bytes`**

```
Content-Length: 2143228
X-Ferry-Bytes: 2143228
```

浏览器开发者工具能直接看到期望大小，方便核对。

**3. 首页加显眼的完整性提示**

```html
<div class="hint" style="background:var(--warnbg);color:var(--warn)">
  下载完成后请确认文件大小约 <b>2.1 MB</b>。<br>
  如果只有 0 KB，说明下载被中断了 —— 删掉那个文件重新点一次。
  双击 0 KB 的文件会提示「此应用无法在你的电脑上运行」。
</div>
```

### 判断安装包是否完整的三个办法（给用户）

1. **看大小**：右键 → 属性 → 应是 `2,143,228 字节`（约 2.09 MB）
2. **看文件头**：PowerShell 跑
   ```powershell
   [IO.File]::ReadAllBytes('路径')[0..1] -join ','
   # 应输出 77,90（即 MZ）
   ```
3. **看响应头**：下载时 F12 → Network → 该请求的 `X-Ferry-Bytes`

### 教训

遇到「程序坏了」的报障，**先看文件本身多大**。
0 KB 的 exe 会伪装成一堆完全不同的症状（无法运行、双击没反应、闪退），
很容易被当成「你改坏了」。

顺带：**浏览器重复下载同名文件会加 `(1)` `(2)` 后缀**，
用户很容易点到那个失败的残片 —— 首页那句提示就是冲着这个来的。


## 九点十六、输入密钥激活失败 —— 服务端 NameError 导致 500

### 现象

用户输入管理面板刚生成的密钥 `ECX7BWY2`，点激活「弹了一下服务端输入了看不懂的内容，
然后一直未激活」。

### 根因：`exp` / `days` 在 `handle_api_activate` 里是未定义变量

第四个补丁（授权时长）把 `days` / `exp` 的**计算**加到了 `handle_admin_newcode`
（约 2105 行），但 `handle_api_activate`（2466 行）的 `send_json` 里
也用了这两个名字：

```python
self.send_json({
    ...
    'expiresAt': exp,        # ← NameError: name 'exp' is not defined
    'daysLeft': days,        # ← 同上
})
```

`days` 在 2105 行是**另一个函数的局部变量**，跨函数不可见。
于是**用新密钥激活必然 500** —— Python 抛 `NameError`，`http.server` 返回
一整页 traceback HTML。

客户端 `Api.Activate` 拿到的是 HTML 而不是 JSON，解析失败，
把响应体原样当成错误信息显示 —— 用户看到的就是「看不懂的内容」，
而且因为它撑爆了一行 Label，只闪了一下就没了。

**为什么之前没发现**：手动测试用的密钥已经 `used_by_ip` 非空，
代码在「已用过」那一行就 return 了，**根本没走到 `send_json`**。
直到用户拿真·新密钥才踩到。

### 同类 bug：一次性密钥没有幂等

第二个问题是设计缺陷：`invites.used_by_ip` 非空就一律拒绝，
**包括同一台机器重复激活**。

用户点两次、客户端网络重试、或者上次激活后 `config.json` 没写成功，
都会走到这个门口 —— 而它拦得毫无意义：密钥本来就绑定 IP，
同一个 IP 再激活一次**不会扩大任何权限**，只会让用户以为软件坏了。

### 修法

```python
# 同一台机器重复激活 = 幂等放行
same_machine = bool(inv['used_by_ip']) and inv['used_by_ip'] == ip
if inv['used_by_ip'] and not same_machine:
    return 401 "这个密钥已经在别的机器（x.x.x.x）上用过了"
if inv['expires_at'] < now() and not same_machine:
    return 401 "密钥已过期"
```

**注意**「别的机器」这条现在会**把对方 IP 显示出来** —— 用户至少知道
「不是我这边的问题，是密钥被别人先用了」。

### 顺带把授权时长真正写进库

```python
days = int(CFG.get('license_days') or 0)
exp = 0 if days <= 0 else now() + days * 86400
days_left = 0 if exp <= 0 else max(0, int((exp - now()) // 86400))

# 建号
INSERT INTO clients (..., expires_at) VALUES (..., exp)
# 已有账号 = 续期
UPDATE clients SET enabled = 1, last_seen = ?, expires_at = ? WHERE ip = ?
```

之前 `expires_at` 只在 `handle_admin_newcode` 里算、从没写进 `clients` 表 ——
所以即使激活成功，客户端 `Api.License` 拿到的 `expiresAt` 永远是 0（显示"永久授权"）。

### 客户端：不再把服务端 HTML 甩给用户

```csharp
if (code401 >= 500)
    res.Error = "服务端出错了（HTTP " + code401 + "）。请稍后重试，或联系管理员看服务端日志。";
else if (string.IsNullOrEmpty(res.Error))
    res.Error = body.StartsWith("<")
        ? "服务端返回了网页而不是数据（HTTP " + code401 + "）。请确认门户地址正确。"
        : "服务端返回异常（HTTP " + code401 + "）";
```

用户看到「一坨看不懂的内容」等于没看到错误。500 是**服务端的问题**，
不该让用户去猜 HTML 里写了什么。

### 修复后实测（用户那个密钥，原样重试）

```
POST /api/activate  {"code":"ECX7BWY2"}
→ 200
{"ok": true, "token": "sngsb4-9ez7s9-vxzr26-mbhnpq", "ip": "10.141.59.235",
 "expiresAt": 1795361481, "daysLeft": 30, "portalUrl": "http://10.13.24.1:8080"}

GET /api/license?ip=10.141.59.235&token=sngsb4-...
→ {"ok": true, "enabled": true, "expiresAt": 1795361481, "daysLeft": 30,
   "hoursLeft": 720, "expired": false, "suspended": false}
```

**同一个密钥、同一台机器，直接成功了** —— 因为幂等放行。

### 教训

1. **补丁跨函数加变量时，一定要搜索这个变量名在整个文件里的所有使用处。**
   我在 `handle_admin_newcode` 里定义了 `days`/`exp`，以为激活那边也用了同名的，
   就没回头查 —— 而 Python 的局部变量不会跨函数共享，直接 NameError。
2. **测试要用「真正会走到最后一行」的数据。** 我测的时候密钥已被用过，
   在第二个 return 就结束了，`send_json` 那几行一行都没执行。
3. **一次性凭据必须对「同一主体重复提交」幂等。**
   现实里网络会重试、用户会连点、写盘会失败 —— 这些都不是攻击。

## 九点十七、密钥 12 小时 vs 账号 30 天 —— 两个完全不同的时长

### 用户的疑问（合理的困惑）

> 为什么设置的 12 小时的密钥，激活了显示是 30 天

因为这是**两个独立的东西**，而界面上没说清楚：

| 时长 | 存在哪 | 含义 | 当前值 |
|---|---|---|---|
| **密钥有效期** | `invites.expires_at` | 密钥生成后**多久内必须用掉**，过期作废 | 12 小时（生成时可填） |
| **账号授权时长** | `clients.expires_at` | 激活成功后**能借网多久** | 30 天（`CFG['license_days']`） |

流程是：`密钥（12h 内用掉）→ 换到 30 天授权`。
密钥那 12 小时是「取票窗口」，不是「票的使用期限」。

### 修法

**① 表单文案说清楚**

原来只有一个输入框「有效小时数」，谁都会以为是「能用多少小时」。
改成：

```
密钥有效期（小时）  [12]

这个「小时」是密钥本身的有效期 —— 生成后必须在这个时间内用掉，过期作废。
激活成功后，账号可借网的时长是另一回事：当前配置 30 天
（服务端 --license-days 参数，0 = 永久）。
```

**② 授权天数改成可配**

新增命令行参数：

```python
ap.add_argument('--license-days', type=int, default=None,
                help='激活后账号可借网的天数（0 = 永久；缺省用内置 30）')
...
if args.license_days is not None:
    CFG['license_days'] = int(args.license_days)
```

想改成 7 天就 `--license-days 7`，想永久就 `--license-days 0`。

**③ 管理页动态显示当前配置**

`admin_page` 里算一次 `license_days_txt`（`'永久'` 或 `'30 天'`），
表单文案直接引用 —— 改参数后页面上的说明会跟着变，不会说了不算。

### 顺带：ssh 输出中文乱码

用户日志里出现：

```
[ssh] channel 1: open failed: connect failed: 262.273\326\252\252\265\300\325\342...
```

那串 `\326\252` 是 **GBK 字节被逐字节当拉丁编码处理**的结果。

**根因**：`ProcessStartInfo` 的 `StandardErrorEncoding` / `StandardOutputEncoding`
没设置。GUI 程序（winexe）没有控制台，`Console.OutputEncoding` 不可靠，
`DataReceivedEventArgs.Data` 就按单字节编码解释了。

**修法**：

```csharp
try
{
    var gbk = System.Text.Encoding.GetEncoding(936);
    psi.StandardErrorEncoding = gbk;
    psi.StandardOutputEncoding = gbk;
}
catch (Exception) { }
```

中文 Windows 上 ssh.exe 输出 GBK（代码页 936）。
.NET Framework 4.8 默认带这个编码，不需要 `CodePagesEncodingProvider`。

> 注：那些 `channel open failed: connect failed` 本身是**正常噪音** ——
> 用户访问的某个站点远端也连不上，ssh 就报这个。不是隧道故障。

### 一个成功的现场（这轮最有价值的一次验证）

用户激活后界面显示：

```
正在走渡口
流量经远端主机中转 · 出口 IP: 218.107.0.47  来自于: 中国 广东 广州 chinanet.com.cn
剩余 30 天 · 2026-11-22 到期
隧道     已连接（端口 10800）
本地代理  运行中（端口 10801）
系统代理  开  http://127.0.0.1:10801
```

**出口 IP 218.107.0.47 正是文档里记录过的远端出口**（联通，与实验机自己的
116.23.159.136 电信不同）。这条链终于端到端跑通了：

```
网页下 exe → 装 → 软件内输密钥 → 激活成功 → 建隧道 → 改系统代理 → 出口变了
```

## 九点十八、密钥分档：1 / 2 / 3 / 7 / 30 天

### 需求

> 激活之后能借网的时间取决于不同等级密钥，管理界面也这样设置，
> 生成密钥变为对应不同的激活 1天、2天、3天、7天、30天

之前授权天数是**全局**的（`CFG['license_days']`，默认 30），
所有密钥换来的时长都一样。现在改成**每个密钥自带档位**。

### 数据

`invites` 表加一列：

```sql
license_days INTEGER DEFAULT NULL     -- 0 = 永久；NULL = 老密钥，回退全局
```

迁移用现有的 `DB._migrate()`（`PRAGMA table_info` 判断后 `ALTER`，幂等）：

```python
icols = {r['name'] for r in self.conn.execute('PRAGMA table_info(invites)')}
if 'license_days' not in icols:
    self.conn.execute('ALTER TABLE invites ADD COLUMN license_days INTEGER DEFAULT NULL')
```

**为什么老密钥留 NULL 而不是 30**：那样会「悄悄地」把历史密钥的时长改掉。
NULL 表达的是「这条记录生成时还没有这个概念」，
读取时回退到全局默认 —— 语义准确。

### 统一的时长计算

```python
LICENSE_TIERS = (1, 2, 3, 7, 30)

def license_days_for(inv):
    """优先级：密钥自带的 license_days → 全局 CFG['license_days']。"""
    v = inv['license_days'] if inv is not None and 'license_days' in inv.keys() else None
    if v is not None:
        return int(v)
    return int(CFG.get('license_days') or 0)

def expiry_of(days):
    return 0 if (days is None or days <= 0) else now() + int(days) * 86400

def days_left_of(exp):
    return 0 if not exp or exp <= 0 else max(0, int((exp - now()) // 86400))
```

**两条建号路径都改成用它**（`handle_login` 网页登录、`handle_api_activate` 软件激活）——
否则「网页登录换来的账号」和「软件激活换来的账号」时长会不一致，那才是真 bug。

### 管理页

档位用一排可点的按钮（比下拉框少一次点击）：

```html
<div class="tiers">
  <label class="tier"><input type="radio" name="license_days" value="1"><span>1 天</span></label>
  ...
  <label class="tier"><input type="radio" name="license_days" value="30" checked><span>30 天</span></label>
  <label class="tier"><input type="radio" name="license_days" value="0"><span>永久</span></label>
</div>
```

选中态用 `:has(input:checked)` 做（现代浏览器都支持），
不用 JS 去监听 radio 的 change：

```css
.tier:has(input:checked){border-color:var(--accent);
  background:color-mix(in srgb, var(--accent) 12%, transparent)}
```

密钥列表也加了一列「可用时长」，老密钥显示「默认」。

### 实测（六个档位逐个验证）

```
密钥 CODE1XXX  -> license_days=1   -> 到期 2026-10-24  剩余 1 天
密钥 CODE2XXX  -> license_days=2   -> 到期 2026-10-25  剩余 2 天
密钥 CODE3XXX  -> license_days=3   -> 到期 2026-10-26  剩余 3 天
密钥 CODE7XXX  -> license_days=7   -> 到期 2026-10-30  剩余 7 天
密钥 CODE30XX  -> license_days=30  -> 到期 2026-11-22  剩余 30 天
密钥 CODE0XXX  -> license_days=0   -> 永久             剩余 0 天（前端显示"永久"）

老密钥 license_days = None -> 回退天数 = 30 | 全局 CFG = 30
```

管理页 9 项结构检查全部通过（档位容器、六个选项、30 天默认选中、
两时长说明、列表新列）。

### 这一轮踩的坑

**`tier_text(r)` 传了整行而不是字段**

```python
<td>{_tier_txt(r)}</td>              # 错：传入 sqlite3.Row
<td>{_tier_txt(r['license_days'])}</td>   # 对
```

报 `TypeError: int() argument must be... not 'sqlite3.Row'`。
**教训**：`sqlite3.Row` 看起来像字典（支持 `r['col']`），
但 `int(r)` 不会像 `int(dict)` 那样失败得早 —— 它会一路传到函数内部才炸。
这类错误在模板拼接里很难一眼看出来。

## 十、还没做的

1. **图标**：现在用 `SystemIcons.Application`。要专业点得配一个 `.ico`
   （放 `ferry-client/app.ico`，`build.ps1` 的 `/win32icon:` 已经接好，放进去就生效）。
   附带好处：有了自己的 exe 身份，托盘图标能独立注册（原 PS 版因为寄生在
   `powershell.exe` 上，**永远**只能待在 Win11 溢出区）。
2. **自动更新**：`/version.json` 和 `CheckUpdate()` 已经通，面板上会显示
   「更新 x.y.z」按钮，但没做后台静默升级。**建议别做全自动** —— 这工具会改系统代理，
   静默升级中途失败会让用户全网上不去，必须留用户确认这一道。
3. **本地管理员端同步**：`tools/渡口助手.ps1`（41 KB，功能更全：管隧道、双协议代理、
   出口 IP 探测、Clash 兜底）和客户端 `渡口助手.exe` 是两套代码。
   合并建议：抽 `ferry-common` 做配置与状态探测，但**别现在合**，先把新链路跑稳。
4. **`portal/client-assistant.ps1` / `client-setup.ps1` 已废弃**，可以删
   （留着当参考也行，但别再改，会误导下一个接手的人）。
5. **`HEAD /download` 不返回响应头**（`ResponseEnded`）。浏览器下载走 GET 不受影响，
   想让 `curl -I` 之类工具也能探测的话要给 `do_HEAD` 补一条分支。

---

## 十一、未提交的改动

```
M  portal/portal-server.py        加 build_deploy_zip / handle_download 发 exe / api_config /
                                  api_activate / api_license / version / installer_path /
                                  read_version_manifest / CFG.license_days / DB._migrate
M  .gitignore                     加 __pycache__/ *.pyc
?? ferry-client/                  新增：C# 客户端源码 + build.ps1
?? installer/ferry.iss            重写（v0.8 架构）
?? installer/渡口助手.cmd          旧入口，v0.8 已不需要，可删
?? portal/_patch_zip.py           打补丁脚本（幂等性未验证，建议改造成「已应用就跳过」再入库）
?? portal/client-setup.ps1        已废弃
?? portal/client-assistant.ps1    已废弃
?? portal/dual-proxy.mjs          从 tools/ 复制（打包要用）
?? portal/ssh-tunnel-keeper.ps1   同上
?? portal/渡口助手.ps1             同上
```

**`portal/scratch/` 下的东西全部不要入库**（已在 `.gitignore`）：
里面有 10 多个 patch 脚本、两个 `portal-server.py.broken*`（JS `$'` 展开事故的现场）、
以及测试用的 ps1 片段。想复现某次改动可以看它们。

---

## 十二、一句话交接

**客户端已重做成原生 exe；安装包能静默装/静默卸；密钥在软件里输入激活；
授权带剩余天数并在到期时自动掐网；端口会自动跟随 dual-proxy 的探测结果。
本机端到端（下载→装→启动→卸）已验证通过；真实密钥激活和真实断网借网还没测。**

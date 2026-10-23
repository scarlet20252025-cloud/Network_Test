# 版本存档

> 每个阶段留一份快照，方便回退。目录形如 `versions/20261024-0230/`。
> 回退方法见每节末尾。

## 命名与内容

每次存档会保存：

| 文件 | 为什么存 |
|---|---|
| `渡口助手.exe` | 客户端成品 |
| `Ferry-Setup-0.8.0.exe` | 安装包成品 |
| `portal-server.py` | 服务端（回退要连服务端一起退，协议可能变了） |
| `ferry.iss` | 安装脚本 |
| `ferry-client-src.zip` | 客户端完整源码（编译不依赖别的） |
| `META.txt` | 版本号、时间、git commit、MD5 |

同时打一个 git tag `snapshot-<时间戳>`，源码可以 `git checkout` 回去。

## 回退怎么做

**只回退客户端**（大多数情况够了）：

```powershell
# 1) 卸载当前版本
& "$env:LOCALAPPDATA\Ferry\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES
# 2) 装旧版
.\versions\<时间戳>\Ferry-Setup-0.8.0.exe /VERYSILENT /SUPPRESSMSGBOXES
```

**连服务端一起回退**：

```bash
scp versions/<时间戳>/portal-server.py pcs:C:/ferry-portal/portal-server.py
ssh pcs 'powershell -Command "Stop-Process -Id (Get-CimInstance Win32_Process -Filter \"Name=''python.exe''\" | Where-Object { $_.CommandLine -like ''*portal-server*'' }).ProcessId -Force; schtasks /Run /TN FerryPortal"'
```

**源码回退**：

```bash
git checkout snapshot-<时间戳>
```

⚠️ 服务端回退要注意数据库：新版本可能加过列（如 `invites.license_days`），
旧代码不认识新列但不会崩；反过来旧库缺新列会报错，所以**降级前先备份 `portal.db`**。

## 存档清单

| 时间 | 阶段 | 关键变化 |
|---|---|---|
| 20261024-0230 | WG 实验前 | 当前稳定版：SSH 线路 + 完整管理页 + 自动更新 |

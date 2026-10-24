# 渡口 UDP 中继 · 远端部署脚本（在 pcs 上跑）
#
# 为什么需要这个脚本而不是直接 python xxx.py：
#   1. 要放到固定目录（不是 Temp），否则重启/清理后文件就没了
#   2. 要注册成开机自启，否则 pcs 一重启借网就断
#   3. 要能重复执行（幂等），装完再装不会产生第二份进程
#
# 用法（在 pcs 上，普通用户权限即可）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File deploy-udp-relay.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File deploy-udp-relay.ps1 -Uninstall

param(
  [int]$Port = 1080,
  [string]$InstallDir = "$env:LOCALAPPDATA\Ferry",
  [switch]$Uninstall,
  [switch]$NoAutostart
)

$ErrorActionPreference = 'Stop'

$Target = Join-Path $InstallDir 'ferry-socks5-server.py'
$LogDir = Join-Path $InstallDir 'logs'
$LogFile = Join-Path $LogDir 'udp-relay.log'
$PidFile = Join-Path $InstallDir 'udp-relay.pid'
$StartupDir = [Environment]::GetFolderPath('Startup')
$StartupCmd = Join-Path $StartupDir 'FerryUdpRelay.cmd'

function Say { param($m) Write-Host "  $m" }
function Ok { param($m) Write-Host "  $m" -ForegroundColor Green }
function Warn { param($m) Write-Host "  $m" -ForegroundColor Yellow }
function Bad  { param($m) Write-Host "  $m" -ForegroundColor Red }

# 真的连一次，比查端点表可靠（见 Start-Relay 里的说明）。
function Test-PortOpen {
  param([string]$HostName = '127.0.0.1', [int]$Port, [int]$TimeoutMs = 500)
  try {
    $c = New-Object Net.Sockets.TcpClient
    $iar = $c.BeginConnect($HostName, $Port, $null, $null)
    $ok = $iar.AsyncWaitHandle.WaitOne($TimeoutMs, $false)
    $c.Close()
    return [bool]$ok
  } catch { return $false }
}

# 找 Python。pcs 上装在 LocalAppData 的 pythoncore 目录里，不在 PATH。
#
# ⚠ 必须排除 WindowsApps 下的 Store 存根：那个 python.exe 只有几 KB，
# 跑起来会弹「去 Microsoft Store 安装」然后退出。实测踩过 ——
# Get-Command python.exe 第一个返回的就是它，直接用它会导致
# 「部署脚本说成功、端口却没起来」，而且看不出原因。
function Find-Python {
  $cands = @()
  # 1) 真解释器优先：pythoncore（本机实测路径）/ Programs\Python / C:\Python*
  $cands += (Get-ChildItem "$env:LOCALAPPDATA\Python\pythoncore-*\python.exe" -ErrorAction SilentlyContinue |
             Sort-Object FullName -Descending | Select-Object -ExpandProperty FullName)
  $cands += (Get-ChildItem "$env:LOCALAPPDATA\Programs\Python\Python*\python.exe" -ErrorAction SilentlyContinue |
             Sort-Object FullName -Descending | Select-Object -ExpandProperty FullName)
  $cands += (Get-ChildItem 'C:\Python*\python.exe' -ErrorAction SilentlyContinue |
             Select-Object -ExpandProperty FullName)
  # 2) PATH 里的放最后，并排除 Store 存根
  $c = Get-Command python.exe -ErrorAction SilentlyContinue
  if ($c) { $cands += $c.Source }
  foreach ($p in $cands) {
    if (-not $p) { continue }
    if (-not (Test-Path $p)) { continue }
    if ($p -like '*WindowsApps*') { continue }
    return $p
  }
  return $null
}

# 按 PID 文件找我们的进程，并校验命令行里真的有本脚本 ——
# 只比 PID 不够：强杀会留 PID 文件，Windows 会把 PID 复用给别的进程。
function Get-RelayProc {
  if (-not (Test-Path $PidFile)) { return $null }
  $raw = ("$(Get-Content $PidFile -Raw -ErrorAction SilentlyContinue)").Trim()
  if (-not $raw) { return $null }
  $procId = 0
  if (-not [int]::TryParse($raw, [ref]$procId)) { return $null }
  $p = Get-CimInstance Win32_Process -Filter "ProcessId=$procId" -ErrorAction SilentlyContinue
  if (-not $p) { return $null }
  if ($p.Name -notmatch 'python') { return $null }
  if ($p.CommandLine -notlike '*ferry-socks5-server*') { return $null }
  return $p
}

function Stop-Relay {
  $p = Get-RelayProc
  if ($p) {
    try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop; Ok "已停止 PID $($p.ProcessId)" }
    catch { Warn "停止失败: $($_.Exception.Message)" }
  }
  Remove-Item $PidFile -Force -ErrorAction SilentlyContinue
}

function Start-Relay {
  param([string]$Python)
  $args = '"{0}" -u "{1}" --port {2} --udp' -f $Python, $Target, $Port
  # 用 WMI 创建进程：Process::Start 起的子进程会落在父进程的 Job 里，
  # ssh 会话一断（或调用方退出）整棵树被回收 —— 实测「刚装好就没了」。
  $cmdline = 'cmd.exe /c "' + $args + ' >> "' + $LogFile + '" 2>&1"'
  $res = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
    CommandLine      = $cmdline
    CurrentDirectory = $InstallDir
  }
  if ($res.ReturnValue -ne 0) { Bad "创建进程失败 ReturnValue=$($res.ReturnValue)"; return $false }

  # 等端口起来。
  #
  # 为什么不直接用 Get-NetTCPConnection 判一次：实测在 pcs 上它**返回空**，
  # 但端口其实是通的（本机 Test-NetConnection 返回 True、python 进程也在跑）。
  # 那是 Get-NetTCPConnection 的已知局限（某些 Win10 版本对非系统账户
  # 查不了别的进程的端点），不是服务没起来。
  # 所以改用「真的连一次」—— 这是唯一可信的判据。
  $ok = $false
  for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 500
    if (Test-PortOpen '127.0.0.1' $Port) { $ok = $true; break }
  }
  if (-not $ok) {
    Bad "端口 $Port 连不上 —— 看日志: $LogFile"
    if (Test-Path $LogFile) { Get-Content $LogFile -Tail 15 | ForEach-Object { Say $_ } }
    return $false
  }

  # 反查 PID 写进 PID 文件。Get-NetTCPConnection 不可靠，改用命令行匹配，
  # 并校验确实是 python + 我们的脚本（双重条件，不会误杀别的 python）。
  $proc = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
          Where-Object { $_.Name -match '^python' -and $_.CommandLine -like '*ferry-socks5-server*' } |
          Select-Object -First 1
  if ($proc) {
    [IO.File]::WriteAllText($PidFile, "$($proc.ProcessId)", (New-Object Text.UTF8Encoding $false))
    Ok "已启动 PID $($proc.ProcessId)，监听 0.0.0.0:$Port"
  } else {
    Warn "端口 $Port 已通，但没找到对应的 python 进程（PID 文件未写）"
  }
  return $true
}

# ────────────────────────── 主流程 ──────────────────────────

Write-Host '=== 渡口 UDP 中继部署 ==='

if ($Uninstall) {
  Stop-Relay
  if (Test-Path $StartupCmd) { Remove-Item $StartupCmd -Force; Ok '已移除自启' }
  Warn "程序文件保留在 $InstallDir（手动删或留着下次装）"
  exit 0
}

# 1) 找 Python
$py = Find-Python
if (-not $py) {
  Bad '找不到 python.exe'
  Say 'pcs 上应该装在 %LOCALAPPDATA%\Python\pythoncore-*\ 下'
  exit 1
}
Ok "Python: $py"

# 2) 准备目录
if (-not (Test-Path $InstallDir)) { $null = New-Item -ItemType Directory -Path $InstallDir -Force }
if (-not (Test-Path $LogDir)) { $null = New-Item -ItemType Directory -Path $LogDir -Force }
Ok "安装目录: $InstallDir"

# 3) 停旧进程（幂等：重复执行不会起两份）
$old = Get-RelayProc
if ($old) { Stop-Relay }
# 端口被别的东西占了也要报出来，别静默失败
$busy = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($busy) {
  $bp = Get-CimInstance Win32_Process -Filter "ProcessId=$($busy[0].OwningProcess)" -ErrorAction SilentlyContinue
  Bad "端口 $Port 已被占用: PID $($busy[0].OwningProcess) $($bp.Name)"
  Say '换个端口：-Port 1081'
  exit 1
}

# 4) 脚本必须已经在位（由调用方 scp 上来）
if (-not (Test-Path $Target)) {
  Bad "缺少 $Target"
  Say '先把 ferry-socks5-server.py 传上来，或从仓库拷到该路径'
  exit 1
}
Ok "服务端脚本: $Target ($((Get-Item $Target).Length) B)"

# 5) 启动
if (-not (Start-Relay -Python $py)) { exit 1 }

# 6) 自启
if (-not $NoAutostart) {
  if (-not (Test-Path $StartupCmd)) {
    $content = @"
@echo off
chcp 936 >nul
rem 渡口 UDP 中继 - 登录时拉起（TCP CONNECT + UDP ASSOCIATE）
start "" /min "$py" -u "$Target" --port $Port --udp
"@
    # .cmd 必须 GBK + 无 BOM + CRLF，三条都踩过：
    # 带 BOM → cmd.exe 把 BOM 当命令一部分，首行 @echo off 失效
    # 纯 LF  → 解析器吃掉每行第一个字符（rem 变 em）
    # UTF-8  → chcp 936 下中文注释乱码
    $content = ($content -replace "`r`n", "`n") -replace "`n", "`r`n"
    [IO.File]::WriteAllText($StartupCmd, $content, [Text.Encoding]::GetEncoding(936))
    Ok "已装自启: $StartupCmd"
  } else {
    Warn "自启已存在: $StartupCmd"
  }
}

# 7) 自检
Write-Host ''
Write-Host '--- 自检 ---'
$t = Test-NetConnection -ComputerName 127.0.0.1 -Port $Port -InformationLevel Quiet -WarningAction SilentlyContinue
if ($t) { Ok "TCP $Port 可连" } else { Bad "TCP $Port 连不上" }
$udp = Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue
if ($udp) { Ok "UDP 中继端口已在用（关联时动态分配）" } else { Warn 'UDP 端点是按需分配的，此时无关联是正常的' }
Write-Host ''
Ok '部署完成'

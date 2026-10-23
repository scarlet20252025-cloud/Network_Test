#requires -Version 5.1
<#
  startup.ps1 — 管理「本机登录自启」

  为什么不用计划任务：
    Register-ScheduledTask / schtasks 都需要管理员权限（实测当前会话
    Administrator=False，两者均报 Access is denied）。
    启动文件夹（%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup）
    普通用户可写，登录时由 explorer 自动拉起，效果等同且零权限要求。

  自启内容（两段）：
    1. ssh-tunnel-keeper.ps1  → 建 ssh -D SOCKS5 隧道（10808）
    2. dual-proxy.mjs         → 双协议代理 HTTP+SOCKS5 单端口（10809）

  为什么必须两段：
    Windows 系统代理只支持 HTTP 代理。只起 ssh -D 时系统代理填 SOCKS 端口
    会让浏览器/.NET/git 把 SOCKS5 当 HTTP 连 —— 秒失败（实测 1.4ms 返回 000）。

  用法：
    .\startup.ps1 Status      查看状态
    .\startup.ps1 Install     装到启动文件夹
    .\startup.ps1 Uninstall   移除
    .\startup.ps1 Test        真实模拟一次登录启动并验证
#>
[CmdletBinding()]
param(
  [ValidateSet('Status', 'Install', 'Uninstall', 'Test')]
  [string]$Action = 'Status',
  [string]$RemoteHost = 'pcs',
  # 0 = 自动（读 .proxy-state\ports.json）。写死只在极少数调试场景用。
  [int]$SocksPort = 0,
  [int]$HttpPort = 0
)

$ErrorActionPreference = 'Stop'

$KeeperScript = Join-Path $PSScriptRoot 'ssh-tunnel-keeper.ps1'
$BridgeScript = Join-Path $PSScriptRoot 'dual-proxy.mjs'
$StartupDir   = [Environment]::GetFolderPath('Startup')
$CmdPath      = Join-Path $StartupDir 'BorrowNetKeeper.cmd'
$LnkPath      = Join-Path $StartupDir 'BorrowNetKeeper.lnk'
$LockPath     = Join-Path $PSScriptRoot 'borrow-net.keeper.lock'

# ---- 端口解析（与 borrow-net.ps1 / 借网助手.ps1 共用同一入口）----
. (Join-Path $PSScriptRoot 'Get-BorrowPorts.ps1')
$_ports    = Get-BorrowPorts -SocksPort $SocksPort -HttpPort $HttpPort
$SocksPort = $_ports.SocksPort
$HttpPort  = $_ports.HttpPort

function Write-Ok($m)   { Write-Host "  $m" -ForegroundColor Green }
function Write-Warn($m) { Write-Host "  $m" -ForegroundColor Yellow }
function Write-Bad($m)  { Write-Host "  $m" -ForegroundColor Red }

function Get-NodeExe {
  $n = 'D:\nodejs\node.exe'
  if (Test-Path $n) { return $n }
  $c = Get-Command node -ErrorAction SilentlyContinue
  if ($c) { return $c.Source }
  return $null
}

function Test-PortOpen([int]$Port, [int]$TimeoutMs = 400) {
  try {
    $c = New-Object Net.Sockets.TcpClient
    $iar = $c.BeginConnect('127.0.0.1', $Port, $null, $null)
    $ok = $iar.AsyncWaitHandle.WaitOne($TimeoutMs, $false)
    $c.Close()
    return [bool]$ok
  } catch { return $false }
}

function Get-KeeperProc {
  Get-CimInstance Win32_Process -Filter "Name='powershell.exe' or Name='pwsh.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*ssh-tunnel-keeper.ps1*' }
}

function Get-BridgeProc {
  Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*dual-proxy.mjs*' }
}

switch ($Action) {

  'Status' {
    Write-Host '--- 登录自启配置 ---'
    if (Test-Path $CmdPath) {
      Write-Ok "已安装: $CmdPath"
      Get-Content $CmdPath | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    } elseif (Test-Path $LnkPath) {
      Write-Ok "已安装（旧 .lnk 版）: $LnkPath"
      Write-Warn '  建议重跑 Install 换成 .cmd 版（会同时拉起双协议代理）'
    } else {
      Write-Warn "未安装  →  装: .\startup.ps1 Install"
    }

    Write-Host ''
    Write-Host '--- 当前运行状态 ---'
    $kp = Get-KeeperProc
    if ($kp) { Write-Ok "keeper 运行中: PID $(($kp.ProcessId) -join ',')" }
    else     { Write-Bad 'keeper 未运行' }

    $ssh = Get-Process ssh -ErrorAction SilentlyContinue
    if ($ssh) {
      foreach ($s in $ssh) {
        $age = [int]((Get-Date) - $s.StartTime).TotalMinutes
        Write-Ok "ssh 运行中: PID $($s.Id)，已存活 $age 分钟"
      }
    } else { Write-Bad 'ssh 未运行' }

    if (Test-PortOpen $SocksPort) { Write-Ok "SOCKS 127.0.0.1:$SocksPort 监听中" }
    else                          { Write-Bad "SOCKS 127.0.0.1:$SocksPort 未监听" }

    $bp = Get-BridgeProc
    if ($bp) { Write-Ok "双协议代理运行中: PID $(($bp.ProcessId) -join ',')" }
    else     { Write-Bad '双协议代理未运行（系统代理会失效）' }

    if (Test-PortOpen $HttpPort) { Write-Ok "HTTP 127.0.0.1:$HttpPort 监听中" }
    else                          { Write-Bad "HTTP 127.0.0.1:$HttpPort 未监听" }

    Write-Host ''
    Write-Host '--- 系统代理 ---'
    $k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $p = Get-ItemProperty $k
    Write-Host "    ProxyEnable=$($p.ProxyEnable)  ProxyServer=$($p.ProxyServer)"
    if ($p.ProxyServer -eq "127.0.0.1:$SocksPort") {
      Write-Warn '    ⚠ 系统代理指向 SOCKS 端口，Windows 只认 HTTP —— 浏览器会连不上'
    }
  }

  'Install' {
    Write-Host '--- 安装登录自启 ---'
    if (-not (Test-Path $KeeperScript)) { throw "找不到 $KeeperScript" }

    $node = Get-NodeExe
    # 注意：.cmd 必须用系统 ANSI 代码页（中文 Windows = GBK/936）保存。
    # 用 ASCII 会把中文路径写成 ?????，cmd 找不到脚本 → keeper 起不来（实测踩过）。
    # 另外不能用 timeout /t：非交互环境报 "Input redirection is not supported"，
    # 改用 ping 等 5 秒。
    $lines = @(
      '@echo off',
      'chcp 936 >nul',
      'rem 借网自启：1) ssh -D SOCKS5 隧道  2) 双协议代理(HTTP+SOCKS5)',
      "start `"`" /min powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$KeeperScript`" -RemoteHost $RemoteHost -SocksPort $SocksPort",
      'rem 等隧道起来（timeout 在非交互环境会失败，用 ping 代替）',
      'ping -n 6 127.0.0.1 >nul'
    )
    if ($node -and (Test-Path $BridgeScript)) {
      # 不传 --listen / --socks：让 dual-proxy 自己探测可用端口。
      # 写死端口在「客户机环境」会撞车（别人的 Clash / 公司代理爱占 10808/10809），
      # 一旦被占，自启就起不来，用户看到的是「软件死了」。
      # dual-proxy 会把实际端口写进 .proxy-state\ports.json，GUI/CLI 都读它。
      $usersArg = ''
      $usersFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'users.json'
      if (Test-Path $usersFile) { $usersArg = " --users `"$usersFile`"" }
      $lines += "start `"`" /min `"$node`" `"$BridgeScript`" --keeper `"$KeeperScript`" --remote $RemoteHost$usersArg"
    } else {
      Write-Warn '找不到 node 或 dual-proxy.mjs，只装隧道部分'
    }
    # GBK 编码写入
    $gbk = [Text.Encoding]::GetEncoding(936)
    [IO.File]::WriteAllLines($CmdPath, $lines, $gbk)
    Write-Ok "已写入: $CmdPath"

    if (Test-Path $LnkPath) { Remove-Item $LnkPath -Force; Write-Host '    已移除旧的 BorrowNetKeeper.lnk' }

    Write-Host ''
    Write-Host '下次登录 Windows 时自动启动隧道 + 双协议代理（不自动切系统代理）。' -ForegroundColor Cyan
    Write-Host '断网时跑 borrow-net.ps1 On 或双击「借网助手」切代理。' -ForegroundColor Cyan
  }

  'Uninstall' {
    Write-Host '--- 移除登录自启 ---'
    $removed = $false
    foreach ($f in @($CmdPath, $LnkPath)) {
      if (Test-Path $f) { Remove-Item $f -Force; Write-Ok "已移除 $(Split-Path -Leaf $f)"; $removed = $true }
    }
    if (-not $removed) { Write-Warn '本来就没装' }
  }

  'Test' {
    Write-Host '--- 真实模拟一次登录启动 ---'

    Write-Host '[1/6] 停掉现有 keeper / ssh / 双协议代理'
    Get-KeeperProc | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; Write-Host "      停 keeper $($_.ProcessId)" }
    Start-Sleep -Milliseconds 1000
    Get-Process ssh -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue; Write-Host "      停 ssh $($_.Id)" }
    Get-BridgeProc | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; Write-Host "      停 bridge $($_.ProcessId)" }
    Remove-Item $LockPath -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    Write-Ok '已清理'

    Write-Host '[2/6] 从启动文件夹启动'
    if (-not (Test-Path $CmdPath)) { throw "先跑 Install：$CmdPath 不存在" }
    # 不要给 Start-Process 加 -WindowStyle Hidden：会连 cmd 内部 start 起的子进程
    # 一起隐藏到不可见状态，实测导致 keeper 起不来。让 .cmd 自己用 /min 控制。
    Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', "`"$CmdPath`"")
    Start-Sleep -Seconds 12

    Write-Host '[3/6] 检查 keeper 与 ssh'
    $kp = Get-KeeperProc
    if (-not $kp) { throw 'keeper 没起来' }
    Write-Ok "keeper PID $(($kp.ProcessId) -join ',')"
    $ssh = Get-Process ssh -ErrorAction SilentlyContinue
    if (-not $ssh) { throw 'ssh 没起来' }
    Write-Ok "ssh PID $(($ssh.Id) -join ',')"

    Write-Host '[4/6] 检查端口'
    if (-not (Test-PortOpen $SocksPort)) { throw "SOCKS $SocksPort 未监听" }
    Write-Ok "SOCKS $SocksPort 监听中"
    if (-not (Test-PortOpen $HttpPort)) { throw "HTTP $HttpPort 未监听（双协议代理没起来）" }
    Write-Ok "HTTP $HttpPort 监听中"

    Write-Host '[5/6] 过代理验证'
    $code = & curl.exe -s -o NUL -w '%{http_code}' --proxy "http://127.0.0.1:$HttpPort" --max-time 20 'https://github.com' 2>$null
    if ($code -ne '200') { throw "github.com 经代理返回 $code" }
    Write-Ok "github.com 经 HTTP 代理 code=$code"
    $ip = & curl.exe -s --proxy "http://127.0.0.1:$HttpPort" --max-time 15 'http://myip.ipip.net' 2>$null
    Write-Ok "借网出口: $ip"

    Write-Host '[6/6] 读系统代理的程序验证（.NET）'
    try {
      $r = [System.Net.WebRequest]::Create('https://github.com')
      $r.Timeout = 20000
      $r.Proxy = [System.Net.WebRequest]::GetSystemWebProxy()
      $resp = $r.GetResponse()
      Write-Ok ".NET 经系统代理: $([int]$resp.StatusCode)"
      $resp.Close()
    } catch { Write-Warn ".NET 经系统代理失败（系统代理未指向 HTTP 端口？）: $($_.Exception.Message)" }

    Write-Host ''
    Write-Host '✅ 登录自启链路完整可用' -ForegroundColor Green
  }
}

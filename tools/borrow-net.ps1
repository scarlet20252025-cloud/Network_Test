#requires -Version 5.1
<#
  borrow-net.ps1 — 借网主控脚本（常驻版）

  场景：本机被学校断网时，借远端 10.13.24.1（办公网）的出口上网。
  远端已就绪：WireGuard 隧道 Running + IP 转发 Enabled + 防火墙放行；
  远端 Windows 10 教育版缺 WinNAT 组件，所以不走 NAT，改走 SSH SOCKS。

  设计原则（用户 m07961：最稳定、最舒服、可灵活切换）：
    1. 只建隧道 + 只切系统代理，**不动路由表、不动默认网关** —— 无自环风险，
       任何时候切回直连都是一条命令，不会把自己锁在外面。
    2. 一键切换：-Action On / Off / Status
    3. 断线自动重连：由 ssh-tunnel-keeper.ps1 保活（常驻，5s 间隔重启）
    4. 开机自启：可选 -Action InstallStartup 注册计划任务

  用法：
    .\borrow-net.ps1 -Action On             # 启用借网（启动隧道 + 切系统代理）
    .\borrow-net.ps1 -Action Off            # 切回直连（停隧道 + 还原系统代理）
    .\borrow-net.ps1 -Action Status         # 看当前状态 + 出口 IP 对照
    .\borrow-net.ps1 -Action InstallStartup # 注册登录自启

  注：脚本用 UTF-8 with BOM 保存（PS 5.1 要求），改内容时务必保留 BOM。
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [ValidateSet('On', 'Off', 'Status', 'InstallStartup', 'UninstallStartup')]
  [string]$Action,

  [string]$RemoteHost = 'pcs',
  [int]$SocksPort = 10808,
  # HTTP 代理端口：Windows 系统代理只认 HTTP，不能直接填 SOCKS 端口
  [int]$HttpPort = 10809,
  [string]$ProxyMode = 'System'   # System = 切系统代理；App = 只建隧道，自己给应用配
)

$ErrorActionPreference = 'Stop'

$ToolsDir   = $PSScriptRoot
$KeeperScript = Join-Path $ToolsDir 'ssh-tunnel-keeper.ps1'
$StateDir   = Join-Path $ToolsDir '.borrow-net'
$TaskName   = 'borrow-net-keeper'

# ---- 工具函数 ----

function Write-Step { param($msg) Write-Host "==> $msg" -ForegroundColor Cyan }

function Test-Admin {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-KeeperProc {
  Get-CimInstance Win32_Process -Filter "Name='powershell.exe' or Name='pwsh.exe'" |
    Where-Object { $_.CommandLine -like "*ssh-tunnel-keeper.ps1*" }
}

function Test-PortOpen {
  param([int]$Port, [int]$TimeoutMs = 300)
  # 与 Test-SocksAlive 同样的理由：不用 Test-NetConnection（关闭端口要等 14.3s）
  try {
    $c = New-Object Net.Sockets.TcpClient
    $iar = $c.BeginConnect('127.0.0.1', $Port, $null, $null)
    $ok = $iar.AsyncWaitHandle.WaitOne($TimeoutMs, $false)
    $c.Close()
    return [bool]$ok
  } catch { return $false }
}

function Test-SocksAlive {
  try {
    $t = Test-NetConnection -ComputerName 127.0.0.1 -Port $SocksPort -WarningAction SilentlyContinue
    return $t.TcpTestSucceeded
  } catch { return $false }
}

function Get-EgressIp {
  param([string]$Proxy = '', [switch]$NoProxy)
  # 依次尝试多个出口 IP 查询源；单源失败不阻塞整行。用 curl 而不是 Invoke-WebRequest：
  # PS 5.1(.NET Framework) 的 -Proxy 不支持 socks5，GUI 跑在 powershell 5.1 下会全失败。
  $urls = @('http://myip.ipip.net', 'http://ip.3322.net', 'http://members.3322.org/dyndns/getip')
  foreach ($u in $urls) {
    try {
      $curlArgs = @('-s', '--max-time', '6', $u)
      if ($NoProxy) { $curlArgs += '--noproxy', '*' }
      elseif ($Proxy) { $curlArgs += '--socks5-hostname', "127.0.0.1:$SocksPort" }
      $out = & curl.exe @curlArgs 2>$null
      if ($LASTEXITCODE -eq 0 -and $out) {
        $m = [regex]::Match("$out", '\d+\.\d+\.\d+\.\d+')
        if ($m.Success) { return $m.Value }
      }
    } catch {}
  }
  return ''
}

function Get-ProxyState {
  $k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
  $en = (Get-ItemProperty $k -Name ProxyEnable -ErrorAction SilentlyContinue).ProxyEnable
  $srv = (Get-ItemProperty $k -Name ProxyServer -ErrorAction SilentlyContinue).ProxyServer
  return @{ Enabled = [bool]($en -eq 1); Server = "$srv" }
}

# ---- 主逻辑 ----

switch ($Action) {

  'On' {
    $ps = Get-ProxyState
    $alreadyOn = $ps.Enabled -and ($ps.Server -eq "127.0.0.1:$SocksPort" -or $ps.Server -eq "http://127.0.0.1:$HttpPort") -and (Test-SocksAlive)
    if ($alreadyOn) {
      Write-Host "借网已在运行中（SOCKS 127.0.0.1:$SocksPort 存活）" -ForegroundColor Green
      return
    }

    Write-Step "启动保活进程 ssh-tunnel-keeper.ps1"
    if (Get-KeeperProc) {
      Write-Host "  keeper 已在运行，复用" -ForegroundColor DarkGray
    }
    else {
      # 用 wscript 起一个隐藏窗口的后台 keeper，避免占用当前控制台
      $arg = "-NoProfile -ExecutionPolicy Bypass -File `"$KeeperScript`" -RemoteHost $RemoteHost -SocksPort $SocksPort"
      $shell = New-Object -ComObject WScript.Shell
      $null = $shell.Run("powershell.exe $arg", 0, $false)
      Start-Sleep -Seconds 3
      Write-Host "  keeper 已启动（隐藏窗口）" -ForegroundColor DarkGray
    }

    if (-not (Test-SocksAlive)) {
      throw "SOCKS 127.0.0.1:$SocksPort 未起来，请检查 ssh 到 $RemoteHost 是否可用（ssh $RemoteHost hostname）"
    }
    Write-Host "  SOCKS 127.0.0.1:$SocksPort 已就绪" -ForegroundColor DarkGray

    # ---- 启动双协议代理（HTTP+SOCKS5 单端口）----
    # 关键：Windows 系统代理只支持 HTTP 代理。把 ProxyServer 直接写成 SOCKS5 端口
    # 会让所有读系统代理的程序（浏览器/.NET/git）把 SOCKS5 当 HTTP 代理连 —— 秒失败。
    # 实测：ProxyServer=127.0.0.1:10808(SOCKS5) 时 curl -x http 返回 000/1.4ms。
    $BridgeScript = Join-Path $ToolsDir 'dual-proxy.mjs'
    $NodeExe = 'D:\nodejs\node.exe'
    if (-not (Test-Path $NodeExe)) {
      $nc = Get-Command node -ErrorAction SilentlyContinue
      if ($nc) { $NodeExe = $nc.Source }
    }

    if ((Test-Path $BridgeScript) -and $NodeExe) {
      $bridgeProc = Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*dual-proxy.mjs*' }
      if (-not $bridgeProc) {
        Write-Step "启动双协议代理（HTTP+SOCKS5 单端口 $HttpPort）"
        # 用 WMI 创建：完全脱离父进程，不会随当前控制台退出被回收
        $cmdLine = "`"$NodeExe`" `"$BridgeScript`" --listen $HttpPort --socks $SocksPort"
        $cr = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmdLine }
        if ($cr.ReturnValue -ne 0) { Write-Host "  启动失败 code=$($cr.ReturnValue)" -ForegroundColor Yellow }
        Start-Sleep -Seconds 2
      }
      else {
        Write-Host "  双协议代理已在运行（PID $($bridgeProc.ProcessId)）" -ForegroundColor DarkGray
      }

      # 等端口就绪
      $bridgeOk = $false
      for ($i = 0; $i -lt 15; $i++) {
        if (Test-PortOpen -Port $HttpPort) { $bridgeOk = $true; break }
        Start-Sleep -Milliseconds 400
      }
      if ($bridgeOk) { Write-Host "  HTTP 代理 127.0.0.1:$HttpPort 已就绪" -ForegroundColor DarkGray }
      else { Write-Host "  ⚠ HTTP 代理未起来，系统代理将退回 SOCKS 端口（多数程序不可用）" -ForegroundColor Yellow }
    }
    else {
      Write-Host "  ⚠ 未找到 dual-proxy.mjs 或 node，系统代理只能填 SOCKS 端口" -ForegroundColor Yellow
      $bridgeOk = $false
    }

    if ($ProxyMode -eq 'System') {
      Write-Step "切换系统代理到 HTTP 127.0.0.1:$HttpPort"
      $k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
      # 备份当前值，供 Off 还原
      if (-not (Test-Path $StateDir)) { $null = New-Item -ItemType Directory -Path $StateDir }
      $cur = Get-ProxyState
      Set-Content (Join-Path $StateDir 'proxy-backup.txt') -Value "$($cur.Enabled)|$($cur.Server)" -Encoding ASCII
      Set-ItemProperty $k -Name ProxyEnable -Value 1 -Type DWord
      $sysProxy = "127.0.0.1:$SocksPort"
      if ($bridgeOk) { $sysProxy = "http://127.0.0.1:$HttpPort" }
      Set-ItemProperty $k -Name ProxyServer -Value $sysProxy -Type String
      # 广播 Internet Settings 变更，让 Explorer / 已运行的浏览器立刻感知
      # （部分程序只在收到 OPTIONS_CHANGED 时才重读注册表）
      $sig = @'
using System;
using System.Runtime.InteropServices;
public class InetOptRefresh {
  [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
  public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
  public const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
  public const int INTERNET_OPTION_REFRESH = 37;
  public static void Refresh() {
    InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
    InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
  }
}
'@
      if (-not ('InetOptRefresh' -as [type])) {
        Add-Type -TypeDefinition $sig -Language CSharp -ErrorAction SilentlyContinue
      }
      try { [InetOptRefresh]::Refresh() } catch {}
      Write-Host "  系统代理已切换（已广播刷新通知）" -ForegroundColor DarkGray
    }
    else {
      Write-Step "App 模式：不动系统代理，请自行把应用配到 SOCKS 127.0.0.1:$SocksPort"
    }

    Write-Step "验证出口"
    $direct = Get-EgressIp
    $borrow = Get-EgressIp -Proxy "socks5://127.0.0.1:$SocksPort"
    Write-Host ("  直连出口 : {0}" -f $(if ($direct) { $direct } else { '不可用（已断网？）' }))
    Write-Host ("  借网出口 : {0}" -f $(if ($borrow) { $borrow } else { '失败' }))
    if ($borrow) { Write-Host "借网已生效。" -ForegroundColor Green }
  }

  'Off' {
    Write-Step "还原系统代理"
    $k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $bakFile = Join-Path $StateDir 'proxy-backup.txt'
    if (Test-Path $bakFile) {
      $bak = (Get-Content $bakFile -Raw).Trim()
      $en, $srv = $bak -split '\|', 2
      # 备份里写的是 $true/$false 字符串（Get-ProxyState 返回 [bool]），这里转回 1/0
      $enVal = if ($en -match '^(1|true)$') { 1 } else { 0 }
      Set-ItemProperty $k -Name ProxyEnable -Value $enVal -Type DWord
      if ($srv) { Set-ItemProperty $k -Name ProxyServer -Value $srv -Type String }
      Write-Host "  已还原为 ProxyEnable=$enVal ProxyServer=$srv" -ForegroundColor DarkGray
    }
    else {
      Set-ItemProperty $k -Name ProxyEnable -Value 0 -Type DWord
      Write-Host "  无备份，直接关闭系统代理" -ForegroundColor DarkGray
    }

    Write-Step "停止保活进程与隧道"
    $procs = Get-KeeperProc
    if ($procs) { $procs | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; Write-Host "  keeper pid $($_.ProcessId) 已停" -ForegroundColor DarkGray } }
    else { Write-Host "  keeper 未在运行" -ForegroundColor DarkGray }
    $sshProcs = Get-CimInstance Win32_Process -Filter "Name='ssh.exe'" | Where-Object { $_.CommandLine -like "*-D*127.0.0.1:$SocksPort*" }
    if ($sshProcs) { $sshProcs | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; Write-Host "  ssh pid $($_.ProcessId) 已停" -ForegroundColor DarkGray } }
    else { Write-Host "  ssh 隧道未在运行" -ForegroundColor DarkGray }
    Write-Host "已切回直连。" -ForegroundColor Green
  }

  'Status' {
    $alive = Test-SocksAlive
    $st = Get-ProxyState
    Write-Host "--- 借网状态 ---"
    Write-Host ("  SOCKS 127.0.0.1:{0} : {1}" -f $SocksPort, $(if ($alive) { '存活' } else { '未监听' }))
    Write-Host ("  系统代理         : {0} {1}" -f $(if ($st.Enabled) { '开' } else { '关' }), $st.Server)
    $keeper = Get-KeeperProc
    Write-Host ("  保活进程         : {0}" -f $(if ($keeper) { "运行中 (pid $($keeper.ProcessId -join ','))" } else { '未运行' }))
    Write-Host "--- 出口 IP ---"
    $direct = Get-EgressIp -NoProxy
    $borrow = if ($alive) { Get-EgressIp -Proxy "socks5://127.0.0.1:$SocksPort" } else { '' }
    Write-Host ("  直连出口 : {0}" -f $(if ($direct) { $direct } else { '不可用（已断网）' }))
    Write-Host ("  借网出口 : {0}" -f $(if ($borrow) { $borrow } else { '未启用/失败' }))
    if ($direct -and $borrow) {
      if ($direct -eq $borrow) { Write-Host "  注意：两者相同，借网可能没生效（代理没走通）" -ForegroundColor Yellow }
      else { Write-Host "  两者不同，借网链路可用" -ForegroundColor Green }
    }
  }

  'InstallStartup' {
    if (-not (Test-Admin)) { throw '注册计划任务需要管理员权限，请用管理员 PowerShell 运行' }
    $arg = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$KeeperScript`" -RemoteHost $RemoteHost -SocksPort $SocksPort"
    $act = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arg
    $trg = New-ScheduledTaskTrigger -AtLogOn
    $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1)
    $prn = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
    $null = Register-ScheduledTask -TaskName $TaskName -Action $act -Trigger $trg -Settings $set -Principal $prn -Force
    Write-Host "已注册登录自启任务 '$TaskName'（只建隧道，不自动切代理；断网时跑 borrow-net.ps1 -Action On 即可）" -ForegroundColor Green
  }

  'UninstallStartup' {
    if (-not (Test-Admin)) { throw '需要管理员权限' }
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "已移除登录自启任务" -ForegroundColor Green
  }
}

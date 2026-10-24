# 渡口服务端 · 总控脚本
#
# 一个脚本管两件事：UDP/TCP 中继（:1080）+ 登录门户（:8080）。
#
# 为什么需要它：
#   原来中继和门户是两套独立的部署脚本 + 两个启动方式，
#   服务器重启后要分别确认「在不在」，还得记得哪个要提权。
#   现在一个命令看全部、一个命令拉全部。
#
# 为什么状态判断不用 Get-CimInstance -Filter CommandLine：
#   提权进程的 CommandLine 在普通权限下读不到（返回 null），
#   `$_.CommandLine -like '*xxx*'` 对 null 返回 false ——
#   于是进程明明在跑，却被判成「未运行」。
#   实测踩过：状态卡显示未运行、但端口 1080 明明可连。
#   改成「查端口 owner → 看那个 PID 是不是 python」，这条链不依赖命令行。

param(
  [ValidateSet('Start', 'Stop', 'Restart', 'Status', 'Install', 'Uninstall')]
  [string]$Action = 'Status',
  # 每 IP 限速（MB/s）。0 = 不限
  [double]$RateMb = 10,
  [int]$RelayPort = 1080,
  [int]$PortalPort = 8080
)

$ErrorActionPreference = 'Continue'

$RelayDir = Join-Path $env:LOCALAPPDATA 'Ferry'
$PortalDir = 'C:\ferry-portal'
$RelayPy = Join-Path $RelayDir 'ferry-socks5-server.py'
$PortalPy = Join-Path $PortalDir 'portal-server.py'
$RelayLog = Join-Path $RelayDir 'logs\udp-relay.log'
$PortalLog = Join-Path $PortalDir 'portal.log'
$RateFile = Join-Path $RelayDir 'rate-mb.txt'
$RelayRunner = Join-Path $RelayDir 'run-relay.cmd'

function Say { param($m) Write-Host "  $m" }
function Ok { param($m) Write-Host "  $m" -ForegroundColor Green }
function Warn { param($m) Write-Host "  $m" -ForegroundColor Yellow }
function Bad { param($m) Write-Host "  $m" -ForegroundColor Red }
function Head { param($m) Write-Host ''; Write-Host "── $m" -ForegroundColor Cyan }

# ── 基础探测 ──

# 真的连一次。不用 Get-NetTCPConnection —— 实测在这台机上对某些进程返回空。
function Test-Port {
  param([int]$Port, [int]$TimeoutMs = 700)
  try {
    $c = New-Object Net.Sockets.TcpClient
    $iar = $c.BeginConnect('127.0.0.1', $Port, $null, $null)
    $ok = $iar.AsyncWaitHandle.WaitOne($TimeoutMs, $false)
    $c.Close()
    return [bool]$ok
  } catch { return $false }
}

# 端口 owner PID。走 netstat 而不是 CIM：netstat 给的是事实，不需要读命令行。
function Get-PortOwner {
  param([int]$Port)
  $out = netstat -ano 2>$null | Select-String (':' + $Port + ' ') | Select-String 'LISTENING'
  foreach ($l in $out) {
    $parts = ($l.Line.Trim() -split '\s+')
    if ($parts.Length -ge 5) {
      $pid2 = 0
      if ([int]::TryParse($parts[-1], [ref]$pid2)) { return $pid2 }
    }
  }
  return 0
}

# 中继是否在跑：端口在听，且 owner 是 python。
function Get-RelayState {
  $pid2 = Get-PortOwner $RelayPort
  if ($pid2 -le 0) { return @{ Running = $false; Pid = 0 } }
  $p = Get-Process -Id $pid2 -ErrorAction SilentlyContinue
  if ($p -and $p.ProcessName -match 'python') {
    return @{ Running = $true; Pid = $pid2 }
  }
  return @{ Running = $false; Pid = $pid2; Foreign = $true }
}

function Get-PortalState {
  $pid2 = Get-PortOwner $PortalPort
  if ($pid2 -le 0) { return @{ Running = $false; Pid = 0 } }
  $p = Get-Process -Id $pid2 -ErrorAction SilentlyContinue
  if ($p -and $p.ProcessName -match 'python') {
    return @{ Running = $true; Pid = $pid2 }
  }
  return @{ Running = $false; Pid = $pid2; Foreign = $true }
}

function Find-Python {
  $cands = @()
  $c = Get-Command python.exe -ErrorAction SilentlyContinue
  if ($c -and $c.Source -notlike '*WindowsApps*') { $cands += $c.Source }
  $cands += (Get-ChildItem "$env:LOCALAPPDATA\Python\pythoncore-*\python.exe" -ErrorAction SilentlyContinue |
             Sort-Object FullName -Descending | Select-Object -ExpandProperty FullName)
  foreach ($p in $cands) { if ($p -and (Test-Path $p)) { return $p } }
  return $null
}

function Is-Admin {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  $pr = New-Object Security.Principal.WindowsPrincipal($id)
  return $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ── 启动 / 停止 ──

function Start-Relay {
  param([string]$Python)
  $st = Get-RelayState
  if ($st.Running) { Say "中继已在跑（PID $($st.Pid)）"; return $true }
  if ($st.Foreign) { Bad "端口 $RelayPort 被别的进程占了（PID $($st.Pid)）"; return $false }

  if (-not (Test-Path $RelayRunner)) {
    # 没有 run-relay.cmd 就临时拼一个（带日志重定向）
    $body = '@echo off' + "`r`n" + 'chcp 936 >nul' + "`r`n" +
            ('"' + $Python + '" -u "' + $RelayPy + '" --port ' + $RelayPort +
             ' --udp --rate-mb ' + $RateMb + ' >> "' + $RelayLog + '" 2>&1') + "`r`n"
    [IO.File]::WriteAllText($RelayRunner, $body, [Text.Encoding]::GetEncoding(936))
  }

  $logDir = Split-Path -Parent $RelayLog
  if (-not (Test-Path $logDir)) { $null = New-Item -ItemType Directory -Path $logDir -Force }

  # WMI 创建：父进程是 WmiPrvSE，不随当前会话结束被回收
  $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
    CommandLine      = 'cmd.exe /c "' + $RelayRunner + '"'
    CurrentDirectory = $RelayDir
  }
  if ($r.ReturnValue -ne 0) { Bad "启动中继失败 ReturnValue=$($r.ReturnValue)"; return $false }

  for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 500
    if (Test-Port $RelayPort) {
      $s2 = Get-RelayState
      Ok "中继已启动（PID $($s2.Pid)，端口 $RelayPort）"
      return $true
    }
  }
  Bad "中继端口 $RelayPort 起不来 —— 看日志: $RelayLog"
  if (Test-Path $RelayLog) { Get-Content $RelayLog -Tail 10 | ForEach-Object { Say $_ } }
  return $false
}

function Start-Portal {
  param([string]$Python)
  $st = Get-PortalState
  if ($st.Running) { Say "门户已在跑（PID $($st.Pid)）"; return $true }
  if ($st.Foreign) { Bad "端口 $PortalPort 被别的进程占了（PID $($st.Pid)）"; return $false }

  if (-not (Test-Path $PortalPy)) { Bad "缺少 $PortalPy"; return $false }

  $cmd = 'cmd.exe /c ""' + $Python + '" -u portal-server.py --port ' + $PortalPort +
         ' --db portal.db --public-host 10.13.24.1 --ssh-user pcserver >> "' +
         $PortalLog + '" 2>&1"'
  $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
    CommandLine      = $cmd
    CurrentDirectory = $PortalDir
  }
  if ($r.ReturnValue -ne 0) { Bad "启动门户失败 ReturnValue=$($r.ReturnValue)"; return $false }

  for ($i = 0; $i -lt 24; $i++) {
    Start-Sleep -Milliseconds 500
    if (Test-Port $PortalPort) {
      $s2 = Get-PortalState
      Ok "门户已启动（PID $($s2.Pid)，端口 $PortalPort）"
      return $true
    }
  }
  Bad "门户端口 $PortalPort 起不来 —— 看日志: $PortalLog"
  if (Test-Path $PortalLog) { Get-Content $PortalLog -Tail 10 | ForEach-Object { Say $_ } }
  return $false
}

function Stop-Service2 {
  param([int]$Port, [string]$Name)
  $pid2 = Get-PortOwner $Port
  if ($pid2 -le 0) { Say "$Name 本来就没在跑"; return }
  $p = Get-Process -Id $pid2 -ErrorAction SilentlyContinue
  if (-not $p) { Say "$Name 的端口 owner $pid2 已不存在"; return }
  if ($p.ProcessName -notmatch 'python') {
    Warn "$Name 端口 $Port 的 owner 是 $($p.ProcessName)（不是 python），跳过"
    return
  }
  try { Stop-Process -Id $pid2 -Force -ErrorAction Stop; Ok "已停止 $Name（PID $pid2）" }
  catch { Bad "停止 $Name 失败: $($_.Exception.Message)" }
}

# ── 计划任务 ──

function Register-OneTask {
  param(
    [string]$TaskName,
    [string]$Description,
    [string]$Command,
    [string]$Arguments,
    [string]$WorkingDir
  )
  $user = "$env:COMPUTERNAME\$env:USERNAME"

  $xml = @'
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>@@DESC@@</Description>
    <URI>\@@TASK@@</URI>
  </RegistrationInfo>
  <Triggers>
    <BootTrigger><Enabled>true</Enabled><Delay>PT20S</Delay></BootTrigger>
    <LogonTrigger><Enabled>true</Enabled><UserId>@@USER@@</UserId><Delay>PT5S</Delay></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>@@USER@@</UserId>
      <LogonType>S4U</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <RestartOnFailure><Interval>PT1M</Interval><Count>5</Count></RestartOnFailure>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>@@CMD@@</Command>
      <Arguments>@@ARGS@@</Arguments>
      <WorkingDirectory>@@DIR@@</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
'@

  # XML 转义
  $xml = $xml.Replace('@@DESC@@', $Description).
    Replace('@@TASK@@', $TaskName).
    Replace('@@USER@@', $user).
    Replace('@@CMD@@', $Command).
    Replace('@@ARGS@@', $Arguments).
    Replace('@@DIR@@', $WorkingDir)

  $xmlPath = Join-Path $env:TEMP ('_task_' + $TaskName + '.xml')
  [IO.File]::WriteAllBytes($xmlPath,
    ([byte[]](0xFF, 0xFE) + [Text.Encoding]::Unicode.GetBytes($xml)))

  $out = cmd.exe /c ('schtasks /Create /TN "' + $TaskName + '" /XML "' + $xmlPath + '" /F 2>&1')
  $code = $LASTEXITCODE
  Remove-Item $xmlPath -Force -ErrorAction SilentlyContinue

  if ($code -eq 0) { Ok "计划任务 $TaskName 已注册"; return $true }
  Bad "注册 $TaskName 失败: $($out -join ' ')"
  return $false
}

function Install-Tasks {
  param([string]$Python)
  if (-not (Is-Admin)) {
    Bad '注册计划任务需要管理员权限'
    Say '请右键「渡口服务端」→ 以管理员身份运行'
    return $false
  }

  Head '注册计划任务（开机自启，无需登录）'

  $r1 = Register-OneTask -TaskName 'FerryUdpRelay' `
    -Description '渡口 Ferry UDP/TCP 中继（SOCKS5，按 IP 限速）' `
    -Command $RelayRunner -Arguments '' -WorkingDir $RelayDir

  $r2 = Register-OneTask -TaskName 'FerryPortal' `
    -Description '渡口 Ferry 登录门户（HTTP）' `
    -Command $Python `
    -Arguments ('portal-server.py --port ' + $PortalPort +
                ' --db C:\ferry-portal\portal.db --public-host 10.13.24.1 --ssh-user pcserver') `
    -WorkingDir $PortalDir

  # 任务注册成功后，清掉启动文件夹里的旧自启（留着会起两份抢端口）
  if ($r1 -or $r2) {
    $startup = [Environment]::GetFolderPath('Startup')
    foreach ($n in @('FerryUdpRelay.cmd', 'FerryPortal.cmd')) {
      $p = Join-Path $startup $n
      if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue; Say "已移除启动文件夹 $n" }
    }
  }
  return ($r1 -and $r2)
}

# ── 状态 ──

function Show-Status {
  Head '服务状态'

  $relay = Get-RelayState
  $portal = Get-PortalState

  if ($relay.Running) { Ok ("中继  运行中  PID " + $relay.Pid + "  端口 " + $RelayPort) }
  elseif ($relay.Foreign) { Warn ("中继  端口 " + $RelayPort + " 被 " + $relay.Pid + " 占用") }
  else { Bad ("中继  未运行  （端口 " + $RelayPort + " 无监听）") }

  if ($portal.Running) { Ok ("门户  运行中  PID " + $portal.Pid + "  端口 " + $PortalPort) }
  elseif ($portal.Foreign) { Warn ("门户  端口 " + $PortalPort + " 被 " + $portal.Pid + " 占用") }
  else { Bad ("门户  未运行  （端口 " + $PortalPort + " 无监听）") }

  Head '访问入口'
  if ($portal.Running) { Say ("管理面板  http://10.13.24.1:" + $PortalPort + "/admin") }
  if ($relay.Running) { Say ("SOCKS5    " + "10.13.24.1:" + $RelayPort + "（客户端自动使用）") }

  Head '限速'
  if (Test-Path $RateFile) {
    $lines = @(Get-Content $RateFile -ErrorAction SilentlyContinue)
    if ($lines.Count -gt 0) { Say ("每 IP  " + $lines[0] + " MB/s") }
    if ($lines.Count -gt 1) { Say ("全站   " + $lines[1] + " MB/s") }
  } else { Warn '还没有限速配置（默认 10 MB/s）' }

  Head '计划任务'
  foreach ($tn in @('FerryUdpRelay', 'FerryPortal')) {
    $t = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
    if ($t) {
      $i = Get-ScheduledTaskInfo -TaskName $tn -ErrorAction SilentlyContinue
      Ok ($tn.PadRight(14) + ' ' + $t.State + '  上次结果=' + $i.LastTaskResult)
    } else {
      Warn ($tn.PadRight(14) + ' 未注册（重启后不会自动拉起）')
    }
  }

  $startup = [Environment]::GetFolderPath('Startup')
  $left = @(Get-ChildItem $startup -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like 'Ferry*' })
  if ($left.Count -gt 0) {
    Warn '启动文件夹里还有渡口自启（可能和计划任务重复）：'
    foreach ($f in $left) { Say ('  ' + $f.Name) }
  }

  Head '最近日志'
  if (Test-Path $RelayLog) {
    $sz = [math]::Round((Get-Item $RelayLog).Length / 1KB, 1)
    Say ("中继日志 " + $sz + " KB，最近 5 行：")
    Get-Content $RelayLog -Tail 5 -ErrorAction SilentlyContinue | ForEach-Object { Say ('  ' + $_) }
  }
  if (Test-Path $PortalLog) {
    $sz2 = [math]::Round((Get-Item $PortalLog).Length / 1KB, 1)
    Say ("门户日志 " + $sz2 + " KB")
  }
}

# ── 主流程 ──

Write-Host ''
Write-Host '════════════════════════════════════════════'
Write-Host '  渡口 Ferry · 服务端'
Write-Host '════════════════════════════════════════════'

$py = Find-Python

switch ($Action) {

  'Status' {
    Show-Status
  }

  'Start' {
    if (-not $py) { Bad '找不到 python.exe'; break }
    Head '启动服务'
    $a = Start-Relay -Python $py
    $b = Start-Portal -Python $py
    Show-Status
    Write-Host ''
    if ($a -and $b) { Ok '全部就绪' } else { Warn '有服务没起来，看上面的输出' }
  }

  'Stop' {
    Head '停止服务'
    Stop-Service2 -Port $PortalPort -Name '门户'
    Stop-Service2 -Port $RelayPort -Name '中继'
  }

  'Restart' {
    if (-not $py) { Bad '找不到 python.exe'; break }
    Head '停止'
    Stop-Service2 -Port $PortalPort -Name '门户'
    Stop-Service2 -Port $RelayPort -Name '中继'
    Start-Sleep -Seconds 2
    Head '启动'
    $a = Start-Relay -Python $py
    $b = Start-Portal -Python $py
    Show-Status
  }

  'Install' {
    if (-not $py) { Bad '找不到 python.exe'; break }
    Head '启动服务'
    $a = Start-Relay -Python $py
    $b = Start-Portal -Python $py
    $c = Install-Tasks -Python $py
    Show-Status
    Write-Host ''
    if ($a -and $b -and $c) { Ok '全部完成：服务已跑，任务已注册' }
    else { Warn '有步骤失败，看上面的输出' }
  }

  'Uninstall' {
    if (-not (Is-Admin)) { Bad '卸载需要管理员权限'; break }
    Head '移除计划任务'
    foreach ($tn in @('FerryUdpRelay', 'FerryPortal')) {
      $out = cmd.exe /c ('schtasks /Delete /TN "' + $tn + '" /F 2>&1')
      if ($LASTEXITCODE -eq 0) { Ok "已移除 $tn" } else { Say "$tn 本来就不存在" }
    }
    Head '停止服务'
    Stop-Service2 -Port $PortalPort -Name '门户'
    Stop-Service2 -Port $RelayPort -Name '中继'
    Say '程序文件保留（要删手动删）'
  }
}

Write-Host ''

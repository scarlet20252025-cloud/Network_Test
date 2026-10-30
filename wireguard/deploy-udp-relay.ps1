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
  [switch]$NoAutostart,
  # 每个源 IP 的带宽上限（MB/s）。0 = 不限速。
  # 为什么默认 10：这是「一个人别把共享出口占满」的经验值 ——
  # 千兆校园网里 10 MB/s 单人已经很快，又不至于让第二个人卡死。
  [double]$RateMb = 10,
  # 不建桌面快捷方式（默认建）
  [switch]$SkipShortcut
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
  <#
    三条路都走一遍，因为每一条单独都不可靠：

    ① PID 文件：精确，但强杀/覆盖安装后文件可能是旧的，
       而且 Windows 会把死掉的 PID 复用给别的进程（要靠命令行二次校验）。

    ② 端口 owner：最接近"真的有人在服务这个端口"这个事实。
       PID 文件丢了也要能停掉。实测踩过：PID 文件里的号对应的进程早就没了，
       而真正的中继还在跑 —— 于是"停止"什么都没做，新进程启不来（端口被占），
       部署脚本却报"已启动 PID xxx"（那个 xxx 其实是新进程刚起就退出的号）。
       表现是：文件是新的、命令行参数也对，就是不生效 —— 因为跑的是旧进程。

    ③ 命令行匹配全杀：兜底，处理"多个残留实例"的情况。
  #>
  $killed = @()

  $p = Get-RelayProc
  if ($p) {
    try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop; $killed += $p.ProcessId }
    catch { Warn "PID 文件里的进程停不掉: $($_.Exception.Message)" }
  }

  # 端口 owner
  try {
    $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
            Select-Object -First 1
    if ($conn) {
      $owner = [int]$conn.OwningProcess
      if ($owner -gt 0 -and $killed -notcontains $owner) {
        $op = Get-CimInstance Win32_Process -Filter "ProcessId=$owner" -ErrorAction SilentlyContinue
        if ($op -and $op.Name -match 'python') {
          try { Stop-Process -Id $owner -Force -ErrorAction Stop; $killed += $owner } catch { }
        }
      }
    }
  } catch { }

  # 兜底：所有命令行里带 ferry-socks5-server 的 python
  try {
    Get-CimInstance Win32_Process -Filter "Name='python.exe'" -ErrorAction SilentlyContinue |
      ForEach-Object {
        if ($_.CommandLine -and $_.CommandLine -like '*ferry-socks5-server*' -and
            $killed -notcontains $_.ProcessId) {
          try { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop; $killed += $_.ProcessId } catch { }
        }
      }
  } catch { }

  if ($killed.Count -gt 0) { Ok ("已停止中继进程: " + ($killed -join ', ')) }
  Remove-Item $PidFile -Force -ErrorAction SilentlyContinue
  if ($killed.Count -gt 0) { Start-Sleep -Seconds 2 }
}

function Start-Relay {
  param([string]$Python)
  # --rate-mb：每个源 IP 的带宽上限。0 表示不限速。
  # 为什么限速在服务端而不是客户端：客户端是用户自己的机器，改个 config 就绕过去了。
  $argStr = '-u "{0}" --port {1} --udp --rate-mb {2}' -f $Target, $Port, $RateMb

  # ⚠ 用 .cmd 启动器，不要直接 `cmd.exe /c "..."` 里塞带引号的命令行。
  #
  # 踩过的坑：`cmd.exe /c ""C:\...\python.exe" -u "C:\...py" --port 1080 ... >> "log" 2>&1"`
  # 这种三层引号在 WMI 下会被拆坏，python 拿到的是残缺参数，
  # 表现是「spawn ret=0 看起来成功、端口却一直不通」——而且日志里什么都没有。
  # 实测就这么白排查了一轮：文件是新的、命令行也带 --rate-mb，就是没限速，
  # 因为进程根本没起来，一直是旧进程在服务。
  #
  # .cmd 里一行一条命令，没有嵌套引号，cmd.exe 解析不会出错。
  $runner = Join-Path $InstallDir 'run-relay.cmd'

  # ── 启动前轮转日志 ──
  #
  # 为什么必须在**启动器里**做、不能在 python 里做：
  #   日志是 `.cmd` 用 `>>` 追加打开的，句柄在 cmd 手里。python 里去 rename
  #   自己正在追加的文件，Windows 上会直接失败（文件被占用）。
  #   而这一步在重定向**之前**执行，此时还没有句柄，rename 一定成功。
  #
  # 为什么必须有（2026-10-30 实测）：
  #   pcs 上 udp-relay.log 单文件已到 **70,766,051 字节（67 MB）**，
  #   而 pcs 的 C: 只剩 4.8 GB。没有任何轮转意味着它会一直涨到吃满盘，
  #   一旦写不进去，中继的所有日志（包括真正的错误）就静默消失。
  #   67 MB 里绝大部分是 TCP `done, N bytes` 这类流水账（2 MB 样本中占 82%）。
  #
  # 留 5 份历史：够追溯一两天，最多占 ~5×64MB。
  #
  # 为什么把「挪档案」写在轮转成功的分支里（实测踩到）：
  #   第一版是「先无条件把 .4→.5、.3→.4 …，再看当前文件大不大」。
  #   结果每次**重启**（哪怕日志只有 1 KB、根本没到阈值）都会把
  #   log.1 变 log.2、log.2 变 log.3 …… 重启 5 次历史就全没了，
  #   而真正的轮转一次都没发生 —— 存档变得比不轮转还不可靠。
  #   现在只有「确实超了 64MB」才进入移位分支，平时一条 move 都不执行。
  #
  # 为什么写成 `for %A` 取大小而不是 %%~zA 直接嵌在 if 里：
  #   cmd 的 `if` 不支持取文件大小，必须先 `for` 把它放进一个变量；
  #   而 `%%A` 在 .cmd 文件里写（不是命令行里写）就是要双百分号。
  #   实测：阈值 67108864 时小文件不动、64MB 文件移成 .1，行为正确。
  $rotate = @(
    'rem ---- 日志轮转：超过 64MB 才滚一份，最多留 5 份 ----',
    ('for %%A in ("' + $LogFile + '") do if %%~zA GEQ 67108864 goto doRotate'),
    'goto afterRotate',
    ':doRotate',
    ('if exist "' + $LogFile + '.5" del /q "' + $LogFile + '.5" >nul 2>&1'),
    ('if exist "' + $LogFile + '.4" move /Y "' + $LogFile + '.4" "' + $LogFile + '.5" >nul 2>&1'),
    ('if exist "' + $LogFile + '.3" move /Y "' + $LogFile + '.3" "' + $LogFile + '.4" >nul 2>&1'),
    ('if exist "' + $LogFile + '.2" move /Y "' + $LogFile + '.2" "' + $LogFile + '.3" >nul 2>&1'),
    ('if exist "' + $LogFile + '.1" move /Y "' + $LogFile + '.1" "' + $LogFile + '.2" >nul 2>&1'),
    ('if exist "' + $LogFile + '" move /Y "' + $LogFile + '" "' + $LogFile + '.1" >nul 2>&1'),
    ':afterRotate'
  )

  $lines = @(
    '@echo off',
    'chcp 936 >nul'
  ) + $rotate + @(
    ('"' + $Python + '" ' + $argStr + ' >> "' + $LogFile + '" 2>&1')
  )
  $content = ($lines -join "`r`n") + "`r`n"
  [IO.File]::WriteAllText($runner, $content, [Text.Encoding]::GetEncoding(936))

  # 用 WMI 创建进程：Process::Start 起的子进程会落在父进程的 Job 里，
  # ssh 会话一断（或调用方退出）整棵树被回收 —— 实测「刚装好就没了」。
  $res = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
    CommandLine      = 'cmd.exe /c "' + $runner + '"'
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

# 6) 自启：注册**计划任务**，不用启动文件夹
#
# 为什么必须用计划任务：
#   启动文件夹里的 .cmd 只在该用户**登录后**才执行。服务器重启后没人登录，
#   中继就不在 —— 所有人借网立刻全断，而且没有任何提示。
#   这比门户挂了更严重（门户至少页面还能看到）。
#
# 为什么用 XML 而不是 New-ScheduledTaskPrincipal：
#   实测这台机器的 PS 5.1 对 -UserId 格式要求很怪，传裸用户名和 SID
#   都报 `(18,8):UserId:`。XML 是任务计划的原生格式，宽松得多。
#
# 为什么 S4U 而不是 Interactive：
#   S4U（Service For User）不需要密码、不需要交互登录，开机就能跑。
if (-not $NoAutostart) {
  $taskName = 'FerryUdpRelay'
  $runnerPath = Join-Path $InstallDir 'run-relay.cmd'
  # 用 COMPUTERNAME 而不是 USERDOMAIN：
  # ssh 会话不是交互登录，$env:USERDOMAIN 会是 WORKGROUP，
  # 拼出来的 WORKGROUP\pcserver 任务计划不认（账户名与安全标识无映射）。
  $user = "$env:COMPUTERNAME\$env:USERNAME"

  $xml = @'
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>渡口 Ferry UDP/TCP 中继（SOCKS5，按 IP 限速）</Description>
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
      <Command>@@RUNNER@@</Command>
      <WorkingDirectory>@@DIR@@</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
'@
  $xml = $xml.Replace('@@TASK@@', $taskName).
    Replace('@@USER@@', $user).
    Replace('@@RUNNER@@', $runnerPath).
    Replace('@@DIR@@', $InstallDir)

  # schtasks 对带 BOM 的 UTF-16 最稳
  $xmlPath = Join-Path $InstallDir '_relay-task.xml'
  [IO.File]::WriteAllBytes($xmlPath,
    ([byte[]](0xFF, 0xFE) + [Text.Encoding]::Unicode.GetBytes($xml)))

  $out = cmd.exe /c ('schtasks /Create /TN "' + $taskName + '" /XML "' + $xmlPath + '" /F 2>&1')
  $taskOk = ($LASTEXITCODE -eq 0)
  if ($taskOk) {
    Ok "已注册计划任务 $taskName（开机即跑，无需登录）"
  } else {
    Bad "注册计划任务失败: $($out -join ' ')"
    Say '退回启动文件夹自启（缺点：需要登录才跑）'
    $content = "@echo off`r`nchcp 936 >nul`r`nstart `"`" /min `"$runnerPath`"`r`n"
    $content = ($content -replace "`r`n", "`n") -replace "`n", "`r`n"
    [IO.File]::WriteAllText($StartupCmd, $content, [Text.Encoding]::GetEncoding(936))
    Ok "已写启动文件夹自启（退路）"
  }
  Remove-Item $xmlPath -Force -ErrorAction SilentlyContinue

  # 只有任务注册成功才清启动文件夹。
  # 踩过的坑：无条件清，结果注册失败时退路刚写完就被删，
  # 变成「既没任务也没自启」，下次重启中继直接不启动。
  if ($taskOk -and (Test-Path $StartupCmd)) {
    Remove-Item $StartupCmd -Force -ErrorAction SilentlyContinue
    Say '已移除启动文件夹自启（计划任务接管）'
  }
}

# 6.5) 清理调试残留 + 日志轮转
#
# 为什么清理：Ferry 目录里曾经堆了十几个 _chk*.py / _diag*.py，
# 都是排查时临时传上来的。留着会让人误以为是项目文件。
$junk = Get-ChildItem $InstallDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^_(chk|diag|hot|rl|tw|cp|run|slow|inspect|restart)' }
if ($junk) {
  $n = @($junk).Count
  $junk | Remove-Item -Force -ErrorAction SilentlyContinue
  Ok ("清理调试残留 " + $n + " 个")
}

# 日志轮转：超过 8 MB 就截断到最后一万行。
# 中继每建一个连接写一行，正常用一天能到几十 MB，不轮转会一直涨。
try {
  if ((Test-Path $LogFile) -and (Get-Item $LogFile).Length -gt 8MB) {
    $keep = Get-Content $LogFile -Tail 10000
    [IO.File]::WriteAllLines($LogFile, $keep, [Text.Encoding]::GetEncoding(936))
    Ok '日志已轮转（保留最后 1 万行）'
  }
} catch { Warn "日志轮转失败: $($_.Exception.Message)" }

# 6.6) 桌面快捷方式
#
# 建两个：
#   渡口中继状态 —— 看一眼在不在跑、当前限速、最近日志
#   渡口中继重启 —— 挂了/改了限速之后一键重来
#
# 用 .cmd 而不是 .lnk：lnk 要用 WScript.Shell 建，路径含中文时容易出问题；
# .cmd 就是纯文本，双击直接执行，用户也能右键编辑。
# 编码仍然必须是 GBK + 无 BOM + CRLF（这个坑踩过三次了）。
if (-not $SkipShortcut) {
  $desk = [Environment]::GetFolderPath('Desktop')
  if ($desk -and (Test-Path $desk)) {
    $selfPath = $MyInvocation.MyCommand.Path
    if (-not $selfPath) { $selfPath = Join-Path $InstallDir 'deploy-udp-relay.ps1' }

    # ── 状态卡 ──
    # 用单引号 here-string 原样保留，只把占位符换成实际值。
    # 这样里面的 $ 和引号都不会被 PowerShell 提前展开。
    $tplStatus = @'
@echo off
chcp 936 >nul
title 渡口中继状态
echo === 渡口中继 ===
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "@@DIR@@\relay-status.ps1"
echo.
pause
'@
    $tplStatus = $tplStatus.Replace('@@DIR@@', $InstallDir)

    $statusPs = @'
$ErrorActionPreference = 'Continue'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$log = Join-Path $dir 'logs\udp-relay.log'
$rate = Join-Path $dir 'rate-mb.txt'

$p = Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
     Where-Object { $_.CommandLine -and $_.CommandLine -like '*ferry-socks5-server*' }
if ($p) {
  Write-Host ('  运行中  PID ' + (($p | ForEach-Object { $_.ProcessId }) -join ',')) -ForegroundColor Green
} else {
  Write-Host '  未运行' -ForegroundColor Red
}

$listening = $false
try {
  $c = New-Object Net.Sockets.TcpClient
  $iar = $c.BeginConnect('127.0.0.1', @@PORT@@, $null, $null)
  $listening = $iar.AsyncWaitHandle.WaitOne(600, $false)
  $c.Close()
} catch { }
if ($listening) { Write-Host '  端口 @@PORT@@ 可连' -ForegroundColor Green }
else { Write-Host '  端口 @@PORT@@ 连不上' -ForegroundColor Red }

if (Test-Path $rate) {
  $lines = Get-Content $rate -ErrorAction SilentlyContinue
  Write-Host ''
  Write-Host ('  限速：每 IP ' + $lines[0] + ' MB/s')
  if ($lines.Count -gt 1) { Write-Host ('        全站 ' + $lines[1] + ' MB/s') }
}

if (Test-Path $log) {
  $sz = [math]::Round((Get-Item $log).Length / 1KB, 1)
  Write-Host ''
  Write-Host ('--- 最近 12 行日志（共 ' + $sz + ' KB）---')
  Get-Content $log -Tail 12 -Encoding UTF8 -ErrorAction SilentlyContinue
} else {
  Write-Host ''
  Write-Host '  （还没有日志）'
}

Write-Host ''
Write-Host ('  安装目录：' + $dir)
'@
    $statusPs = $statusPs.Replace('@@PORT@@', [string]$Port)
    [IO.File]::WriteAllText((Join-Path $InstallDir 'relay-status.ps1'),
      $statusPs, (New-Object Text.UTF8Encoding $true))

    # ── 重启卡 ──
    $tplRestart = @'
@echo off
chcp 936 >nul
title 渡口中继重启
echo 正在重启渡口中继（每 IP @@RATE@@ MB/s）...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "@@SELF@@" -Port @@PORT@@ -RateMb @@RATE@@
echo.
pause
'@
    $tplRestart = $tplRestart.Replace('@@SELF@@', $selfPath).
      Replace('@@PORT@@', [string]$Port).Replace('@@RATE@@', [string]$RateMb)

    $gbk = [Text.Encoding]::GetEncoding(936)
    [IO.File]::WriteAllText((Join-Path $desk '渡口中继状态.cmd'),
      ($tplStatus -replace "`r`n", "`n" -replace "`n", "`r`n"), $gbk)
    [IO.File]::WriteAllText((Join-Path $desk '渡口中继重启.cmd'),
      ($tplRestart -replace "`r`n", "`n" -replace "`n", "`r`n"), $gbk)

    Ok "已建桌面快捷方式：渡口中继状态.cmd / 渡口中继重启.cmd"
  } else {
    Warn '找不到桌面目录，跳过快捷方式'
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

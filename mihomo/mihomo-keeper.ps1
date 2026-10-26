#requires -Version 5.1
<#
  mihomo-keeper.ps1 — 渡口统一入口（常驻）

  解决什么问题：
    原来有两条线路（SSH→pcs 联通出口、WG→xt 电信出口），但它们各自是一个
    SOCKS 端口，用哪条要靠人记住端口号、改系统代理、还得自己判断哪条活着。
    这个脚本把两条线合成**一个固定端点**，并且自动选活的、自动切换。

  为什么不自己写一个代理，要用 mihomo：
    * 自动故障切换（fallback 组 + 健康检查）—— 自己写要维护状态机
    * 域名分流（国内直连、需要借网的才走线路）—— 自己写要维护规则引擎
    * 将来接 UDP / TUN 只需要改配置，不用改代码
    mihomo 已经在你机器上（Clash Verge 自带），零新增依赖。

  关键设计：
    1. **端口不写死**。SSH 隧道的 SOCKS 端口会顺延（10800 被占就 10801），
       所以每次启动都现场探测真实监听端口，渲染进 config.yaml。
       踩过的坑：ports.json 记 10800，实际在听 10801，写死就连死端口。
    2. **默认 DIRECT**。ferry 组默认选 DIRECT，等于「没借网」。
       切到 ferry-auto 才是借网。一个开关管两件事。
    3. **不动系统代理**。本脚本只提供 127.0.0.1:10810 这个端点。
       要不要把系统代理指过来，由 ferry.ps1 / 渡口助手 决定 ——
       它们已经有「备份/还原用户原代理」的逻辑，这里再动一次会打架。
    4. **看护循环（Watch）**。自启跑的是 `-Action Watch` 而不是 Start：
       每 20 秒体检一次，mihomo 挂了自动拉起；上游 SSH 端口顺延了
       （10801 → 10808）就重渲染配置再重启 —— 否则 mihomo 会一直连死端口，
       表现是「10810 还在听，但所有请求 502」。
       带单例锁 + PID 复用防护，重复拉起不会起两份。

  用法：
    .\mihomo-keeper.ps1 Status     看状态（线路、当前选中、健康、看护）
    .\mihomo-keeper.ps1 Start      启动（后台常驻，不含看护）
    .\mihomo-keeper.ps1 Watch      启动看护循环（自启用的就是这个）
    .\mihomo-keeper.ps1 Stop       停止（会连看护一起停，否则会被拉回来）
    .\mihomo-keeper.ps1 On         借网（ferry → ferry-auto）
    .\mihomo-keeper.ps1 Off        恢复直连（ferry → DIRECT）
    .\mihomo-keeper.ps1 Switch <DIRECT|ferry-auto|ferry-ssh|ferry-wg>
    .\mihomo-keeper.ps1 Install    装到登录自启（跑 Watch）
    .\mihomo-keeper.ps1 Uninstall  移除自启
    .\mihomo-keeper.ps1 Test       跑一遍自检

  注：本脚本用 UTF-8 with BOM 保存（PS 5.1 要求），改内容时务必保留 BOM。
#>
[CmdletBinding()]
param(
  [ValidateSet('Status', 'Start', 'Stop', 'On', 'Off', 'Switch', 'Install', 'Uninstall', 'Test', 'Restart', 'Watch', 'TunOn', 'TunOff', 'Elevate')]
  [string]$Action = 'Status',
  [string]$Target = 'DIRECT'
)

$ErrorActionPreference = 'Stop'

# ---------------- 路径与常量 ----------------

$Here       = $PSScriptRoot
$Template   = Join-Path $Here 'config.template.yaml'
$ConfigPath = Join-Path $Here 'config.yaml'
$LogPath    = Join-Path $Here 'mihomo-keeper.log'
$PidPath    = Join-Path $Here 'mihomo-keeper.pid'
$WatchLock  = Join-Path $Here 'mihomo-watch.lock'
$StatePath  = Join-Path $Here 'mihomo-state.txt'

# 端点端口。10810/19010 是 2026-10-24 实测空闲的。
# 选这两个是因为它们紧挨着 ferry 现有端口段，一眼能看出是渡口的。
$MixedPort = 10810
$ApiPort   = 19010

$MiExe = 'D:\Clash Verge\verge-mihomo.exe'

# SSH 隧道端口候选（按优先级）。探测时会跳过没在听的。
$SshPortCandidates = @(10800, 10801, 10808, 10809)

# UDP 线路：走**本地**代理（客户端自己的 SOCKS，10800/10801）。
#
# 以前这里写的是 10.13.24.1:1080，实际一直是坏的：远端 ferry-socks5-server
# 并不在 1080 监听 UDP —— 它按 RFC 1928，每建立一条 ASSOCIATE 就新开一个
# 随机 UDP socket，端口放在回复的 BND 里。直接往 1080 发报文只会收到
# ICMP port unreachable（2026-10-27 实测确认）。
#
# 现在客户端本地 SOCKS 补上了 UDP ASSOCIATE（1.0.8），所以 mihomo 只要把 UDP
# 交给 127.0.0.1:<本地端口>，由客户端去和上游协商真实端口即可。
$UdpHost = '127.0.0.1'
$UdpPort = 0   # 渲染时换成探测到的本地 SOCKS 端口，见 Build-Config

# SSH 隧道目标。pcs 在 ~/.ssh/config 里有别名，但这里写全参数更明确 ——
# 自启时环境变量/配置目录可能不完整，别名解析失败会得到「Could not resolve」。
$SshTarget = 'pcserver@10.13.24.1'
$SshPort   = 22

# 记录上一次用过的 SSH 端口，避免看护循环每次都重复渲染配置
$script:LastSshPort = -1

# ── TUN 状态 ──
#
# TUN 要管理员权限（创建虚拟网卡 + 改路由表）。所以：
#   · 默认 false，普通权限下完全不碰 TUN，SOCKS 端点照常可用
#   · 只有显式 -Action TunOn 且确实已提权时才开
#   · 提权判断用 WindowsPrincipal.IsInRole，不要靠 try/catch 试探 ——
#     试探会真的去创建网卡，失败后可能留下半成品状态
$script:TunEnabled = $false
$TunStatePath = Join-Path $Here 'mihomo-tun.txt'

# 是否已经提权
function Test-Admin {
  try {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  } catch { return $false }
}

# 读上次的 TUN 选择。开机自启时据此决定要不要开 TUN ——
# 用户上次开着 TUN，重启后应该还是开着的（他提权过就有权继续开）。
function Restore-TunState {
  if (-not (Test-Path $TunStatePath)) { return }
  $want = ("$(Get-Content $TunStatePath -Raw -ErrorAction SilentlyContinue)").Trim()
  if ($want -eq '1' -and (Test-Admin)) { $script:TunEnabled = $true }
}

function Save-TunState {
  param([bool]$On)
  [IO.File]::WriteAllText($TunStatePath, $(if ($On) { '1' } else { '0' }), (New-Object Text.UTF8Encoding $false))
}

# TUN 虚拟网卡是否真的存在（用来判断「说开了但没生效」）
function Test-TunAdapter {
  try {
    $n = Get-NetAdapter -Name 'FerryTun' -ErrorAction SilentlyContinue
    return [bool]$n
  } catch { return $false }
}

$StartupDir = [Environment]::GetFolderPath('Startup')
$StartupCmd = Join-Path $StartupDir 'FerryMihomo.cmd'

# ---------------- 日志 ----------------

function Write-KLog {
  param([string]$Msg, [string]$Level = 'INFO')
  $line = '[{0}] [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Msg
  try {
    Add-Content -Path $LogPath -Value $line -Encoding UTF8
    $fi = Get-Item $LogPath -ErrorAction SilentlyContinue
    if ($fi -and $fi.Length -gt 524288) {
      $keep = Get-Content $LogPath -Tail 400
      Set-Content -Path $LogPath -Value $keep -Encoding UTF8
    }
  } catch { }
}

function Say  { param($m) Write-Host "  $m" }
function Ok   { param($m) Write-Host "  $m" -ForegroundColor Green }
function Warn { param($m) Write-Host "  $m" -ForegroundColor Yellow }
function Bad  { param($m) Write-Host "  $m" -ForegroundColor Red }

# ---------------- 小工具 ----------------

function Test-PortOpen {
  param([int]$Port, [int]$TimeoutMs = 400)
  try {
    $c = New-Object Net.Sockets.TcpClient
    $iar = $c.BeginConnect('127.0.0.1', $Port, $null, $null)
    $ok = $iar.AsyncWaitHandle.WaitOne($TimeoutMs, $false)
    $c.Close()
    return [bool]$ok
  } catch { return $false }
}

# 探测 SSH 隧道真实在听的 SOCKS 端口。
# 为什么不直接读 .proxy-state\ports.json：那个文件只在 dual-proxy 以 auto 模式
# 跑过时才更新，手动起的 ssh -D 不会写它。实测 ports.json 说 10800、实际在听 10801。
function Get-SshSocksPort {
  foreach ($p in $SshPortCandidates) {
    if (Test-PortOpen $p 200) { return $p }
  }
  # 都没在听：读 ports.json 作为「预期值」，让 mihomo 先起着，
  # 隧道起来后由 keeper 的看护循环重启 mihomo 让它重新探测。
  $pf = Join-Path (Split-Path -Parent $Here) '.proxy-state\ports.json'
  if (Test-Path $pf) {
    try {
      $j = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($pf)) | ConvertFrom-Json
      if ($j.socks) { return [int]$j.socks }
    } catch { }
  }
  return 10800
}

# 识别「我们自己的」mihomo 进程。
#
# 为什么不靠命令行匹配：这台机器上还跑着 Clash Verge 自己的 verge-mihomo，
# 按进程名匹配会把它一起选中 —— 停一次就把用户的 Clash 杀了。
# 而且跨会话读 CommandLine 常常是空的（权限），匹配会漏。
#
# 所以用 PID 文件做唯一凭据，并校验那个 PID 确实还是 verge-mihomo。
function Get-MiProc {
  if (-not (Test-Path $PidPath)) { return $null }
  $raw = Get-Content $PidPath -Raw -ErrorAction SilentlyContinue
  if (-not $raw) { return $null }
  $procId = 0
  if (-not [int]::TryParse($raw.Trim(), [ref]$procId)) { return $null }
  if ($procId -le 0) { return $null }
  $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
  if (-not $p) { return $null }
  if ($p.ProcessName -ne 'verge-mihomo') { return $null }
  return $p
}

function Invoke-Api {
  param([string]$Path, [string]$Method = 'GET', [string]$Body = $null, [int]$TimeoutSec = 5)
  $u = "http://127.0.0.1:$ApiPort$Path"
  try {
    if ($Method -eq 'GET') {
      return Invoke-RestMethod -Uri $u -TimeoutSec $TimeoutSec -ErrorAction Stop
    } else {
      return Invoke-RestMethod -Uri $u -Method $Method -Body $Body -ContentType 'application/json' -TimeoutSec $TimeoutSec -ErrorAction Stop
    }
  } catch { return $null }
}

# 经统一端点查出口 IP。
#
# 为什么不用 Invoke-WebRequest：PS 5.1 的 Invoke-WebRequest 会复用
# ServicePoint 连接池，同一个主机（myip.ipip.net）的连接被上一个请求的上游
# 缓存下来 —— 实测「切成 ferry-ssh 后仍返回 DIRECT 的出口 IP」，三态全显示
# 电信，看起来像分流失效，其实是测量工具的问题。
# curl.exe 每次新连接，没有这个坑。
#
# 为什么写到文件再读：PS 5.1 把外部程序 stdout 按控制台 OEM 代码页（936）
# 解码，而 curl 输出是 UTF-8 —— 直接捕获会得到「褰撳墠 IP锛?16...」这种
# 二次编码乱码。落盘后显式按 UTF-8 读，绕开管道编码。
function Get-ExitIp {
  param([int]$TimeoutSec = 15)
  $tmp = Join-Path $env:TEMP ("ferry-exit-{0}.txt" -f $PID)
  try {
    $null = & curl.exe -s --max-time $TimeoutSec -x "http://127.0.0.1:$MixedPort" -o $tmp 'https://myip.ipip.net' 2>$null
    if (-not (Test-Path $tmp)) { return 'FAIL' }
    $r = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($tmp)).Trim()
    if (-not $r) { return 'FAIL' }
    if ($r -match 'IP[：:]\s*(\d+\.\d+\.\d+\.\d+)') {
      $ip = $Matches[1]
      $who = ''
      if ($r -match '自[于]?[：:]\s*(.+)$') { $who = $Matches[1].Trim() }
      return ("{0}  {1}" -f $ip, $who).Trim()
    }
    return $r
  } finally {
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
  }
}

# ---------------- 配置渲染 ----------------

function Build-Config {
  if (-not (Test-Path $Template)) { throw "模板缺失: $Template" }
  $sshPort = Get-SshSocksPort
  $raw = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($Template))
  $out = $raw.
    Replace('__MIXED_PORT__', [string]$MixedPort).
    Replace('__API_PORT__', [string]$ApiPort).
    Replace('__SSH_PORT__', [string]$sshPort).
    Replace('__UDP_HOST__', $UdpHost).
    Replace('__UDP_PORT__', [string]$(if ($UdpPort -gt 0) { $UdpPort } else { $sshPort })).
    Replace('__TUN_ENABLE__', $(if ($script:TunEnabled) { 'true' } else { 'false' }))
  # mihomo 要求 UTF-8 无 BOM（带 BOM 会报 yaml 解析错）
  [IO.File]::WriteAllText($ConfigPath, $out, (New-Object Text.UTF8Encoding $false))
  Write-KLog "配置已渲染: SSH=$sshPort mixed=$MixedPort api=$ApiPort UDP=$UdpHost`:$([string]$(if ($UdpPort -gt 0) { $UdpPort } else { $sshPort })) TUN=$($script:TunEnabled)"
  return $sshPort
}

# 远端 UDP 中继是否活着。
#
# 为什么要单独探测而不是等 mihomo 自己发现：mihomo 的健康检查只测 TCP
# （fallback 组的 url 是 http://...）。UDP 线路死了它不会报，用户只会看到
# 「游戏连不上」而查不到原因。这里用一次 TCP 连接确认 relay 进程在，
# 因为同一个进程既提供 TCP CONNECT 也提供 UDP ASSOCIATE。
function Test-UdpRelay {
  param([int]$TimeoutMs = 800)
  try {
    $c = New-Object Net.Sockets.TcpClient
    $iar = $c.BeginConnect($UdpHost, $UdpPort, $null, $null)
    $ok = $iar.AsyncWaitHandle.WaitOne($TimeoutMs, $false)
    $c.Close()
    return [bool]$ok
  } catch { return $false }
}

# ---------------- 启停 ----------------

function Stop-Mi {
  $p = Get-MiProc
  if (-not $p) {
    Remove-Item $PidPath -Force -ErrorAction SilentlyContinue
    return $false
  }
  try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { }
  Remove-Item $PidPath -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 800
  return $true
}

function Start-Mi {
  if (-not (Test-Path $MiExe)) { throw "找不到 mihomo: $MiExe" }
  $sshPort = Build-Config

  # 用 WMI Win32_Process.Create 启动，而不是 [Diagnostics.Process]::Start。
  #
  # 为什么：Process::Start 起的子进程会落在父进程的 Job 对象里。当这个脚本
  # 是被 DSH 的 ops_bash / pwsh 后台任务拉起时，任务一结束（或被 cancel），
  # 整棵进程树被回收 —— 实测「日志显示 mihomo 已就绪，两分钟后端口消失」。
  # WMI Create 由 WmiPrvSE 代为创建，不属于调用方的 Job，才能真正常驻。
  $cmd = '"{0}" -d "{1}" -f "{2}"' -f $MiExe, $Here, $ConfigPath
  $res = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
    CommandLine      = $cmd
    CurrentDirectory = $Here
  } -ErrorAction Stop
  if ($res.ReturnValue -ne 0) {
    Write-KLog "WMI 创建进程失败 ReturnValue=$($res.ReturnValue)" 'ERROR'
    return $false
  }
  $newPid = [int]$res.ProcessId
  [IO.File]::WriteAllText($PidPath, [string]$newPid, (New-Object Text.UTF8Encoding $false))
  Write-KLog "mihomo 已拉起 PID=$newPid"

  # 等端点起来（最多 10 秒）。mihomo 正常 0.2 秒内就绪。
  for ($i = 0; $i -lt 50; $i++) {
    Start-Sleep -Milliseconds 200
    if (Test-PortOpen $MixedPort 150) {
      Write-KLog "mihomo 已就绪: 127.0.0.1:$MixedPort (SSH 上游 $sshPort)"
      Restore-Selection
      return $true
    }
  }
  Write-KLog "mihomo 启动超时（10s 内 $MixedPort 未监听）" 'ERROR'
  return $false
}

# 恢复用户上次选中的线路。
#
# 为什么需要：config.template.yaml 里 `profile.store-selected: false`，
# 所以 mihomo 每次启动 ferry 组都回到默认的 DIRECT。这对「开机不该自动借网」
# 是对的，但看护循环一旦因为上游端口顺延而重启 mihomo，正在借网的用户就会
# 被静默打回直连 —— 表现是「网突然变慢/打不开」，而界面显示一切正常。
# 实测：11:18:14 那次自动重启后 ferry 就回到了 DIRECT。
#
# 解法：不看 mihomo 的 cache，自己记一个 state 文件。用户 On/Off/Switch 时写，
# 启动时读回来。这样「开机默认不借网」（state 是 DIRECT 或不存在）
# 和「崩溃恢复后保持借网」（state 是 ferry-auto）两件事都成立。
function Restore-Selection {
  if (-not (Test-Path $StatePath)) { return }
  $want = ("$(Get-Content $StatePath -Raw -ErrorAction SilentlyContinue)").Trim()
  if (-not $want) { return }
  if (@('DIRECT', 'ferry-auto', 'ferry-ssh', 'ferry-wg') -notcontains $want) { return }
  if ($want -eq 'DIRECT') { return }   # 默认就是这个，不用 PUT
  $null = Invoke-Api '/proxies/ferry' 'PUT' (@{ name = $want } | ConvertTo-Json -Compress)
  # UDP 组跟着一起恢复，否则重启后 TCP 在借网、UDP 在直连，用户会觉得时好时坏
  $null = Invoke-Api '/proxies/ferry-udp-group' 'PUT' (@{ name = 'ferry-udp' } | ConvertTo-Json -Compress)
  Write-KLog "已恢复上次选择: $want（UDP 也切到 ferry-udp）"
}

function Save-Selection {
  param([string]$Name)
  [IO.File]::WriteAllText($StatePath, $Name, (New-Object Text.UTF8Encoding $false))
}

function Get-KeeperProc {
  Get-CimInstance Win32_Process -Filter "Name='powershell.exe' or Name='pwsh.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*mihomo-keeper.ps1*' -and $_.CommandLine -like '*Watch*' }
}

# ---------------- SSH 隧道自管理 ----------------
#
# 为什么 keeper 要自己管 SSH 隧道：
#   原来有**两个**自启项（BorrowNetKeeper.cmd 拉 ssh-tunnel-keeper，
#   FerryMihomo.cmd 拉 mihomo）。它们各自探测端口、各自拉起进程，
#   实测出现两种烂状态：
#     ① 两个都跑起来，抢同一个端口，后起的绑不上，隧道时通时断
#     ② ssh-tunnel-keeper 根本没在跑（实测某次开机后 10801 是手动起的，
#        keeper 进程数为 0），mihomo 连着一条没人保活的隧道，
#        断了就永久断
#   所以改成**单一自启入口**：只留 FerryMihomo.cmd，由它统一负责
#   「SSH 隧道 + mihomo 端点」整条链。多一个自启项就多一处状态竞争。

# 我们自己拉起的 ssh 进程 PID（用于停止时精确回收，不误杀用户手动起的）
$SshPidPath = Join-Path $Here 'mihomo-ssh.pid'

function Get-OwnSshProc {
  if (-not (Test-Path $SshPidPath)) { return $null }
  $raw = ("$(Get-Content $SshPidPath -Raw -ErrorAction SilentlyContinue)").Trim()
  if (-not $raw) { return $null }
  $procId = 0
  if (-not [int]::TryParse($raw, [ref]$procId)) { return $null }
  $p = Get-CimInstance Win32_Process -Filter "ProcessId=$procId" -ErrorAction SilentlyContinue
  if (-not $p) { return $null }
  # 双重校验：必须是 ssh.exe，且命令行里是 -D 动态转发
  if ($p.Name -ne 'ssh.exe') { return $null }
  if ($p.CommandLine -notlike '*-D*') { return $null }
  return $p
}

# 确保有一条可用的 SSH SOCKS 隧道。返回实际在听的端口（失败返回 0）。
#
# 关键决策：**已经有隧道在听就不动它**。
# 用户可能手动起了一条（实测常见），或者 keeper 上次起的还在。
# 只要端口通，就没必要重开 —— 重开会在切换瞬间断网，体验更差。
function Ensure-SshTunnel {
  # 1) 已经有隧道在听？直接用
  $existing = Get-SshSocksPort
  if (Test-PortOpen $existing 300) {
    if ($existing -ne $script:LastSshPort) {
      Write-KLog "SSH 隧道已在听: 127.0.0.1:$existing（复用，不重开）"
      $script:LastSshPort = $existing
    }
    return $existing
  }

  # 2) 没有 → 自己拉一条
  $sshExe = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh.exe'
  if (-not (Test-Path $sshExe)) {
    $c = Get-Command ssh.exe -ErrorAction SilentlyContinue
    if ($c) { $sshExe = $c.Source } else { $sshExe = 'ssh.exe' }
  }

  # 选一个空闲端口。优先 10801（渡口的惯用端口），被占就顺延。
  $port = 0
  foreach ($cand in $SshPortCandidates) {
    if (-not (Test-PortOpen $cand 150)) { $port = $cand; break }
  }
  if (-not $port) {
    Write-KLog '所有候选端口都被占用，无法起 SSH 隧道' 'ERROR'
    return 0
  }

  # 参数拼装必须用括号把 -f 的模板整体括起来。
  # 踩过的坑：写成
  #   '-N ... {0} ... ' + '-o ... {2}' -f $port, $SshPort, $SshTarget
  # 时 `-f` 只作用于**最后那个字符串字面量**（+ 的优先级高于 -f），
  # 结果 ssh 收到的是字面量 `-D 127.0.0.1:{0}`，报
  #   Bad dynamic forwarding specification '127.0.0.1:{0}'
  # 而且因为 -N 立刻失败退出，日志里只会看到「12s 内未就绪」，看不出真因。
  $argLine = ('-N -T -D 127.0.0.1:{0} -p {1} -o BatchMode=yes -o ExitOnForwardFailure=yes ' +
              '-o ServerAliveInterval=15 -o ServerAliveCountMax=3 -o StrictHostKeyChecking=accept-new {2}') -f
              $port, $SshPort, $SshTarget
  $cmd = '"{0}" {1}' -f $sshExe, $argLine
  try {
    $res = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
      CommandLine = $cmd
    } -ErrorAction Stop
  } catch {
    Write-KLog "拉起 ssh 失败: $($_.Exception.Message)" 'ERROR'
    return 0
  }
  if ($res.ReturnValue -ne 0) {
    Write-KLog "WMI 创建 ssh 失败 ReturnValue=$($res.ReturnValue)" 'ERROR'
    return 0
  }
  $newPid = [int]$res.ProcessId
  [IO.File]::WriteAllText($SshPidPath, "$newPid", (New-Object Text.UTF8Encoding $false))

  # 等隧道就绪（最多 12 秒）。ssh 建连 + 认证通常 1–3 秒。
  for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 300
    if (Test-PortOpen $port 150) {
      Write-KLog "SSH 隧道已拉起 PID=$newPid → 127.0.0.1:$port（目标 $SshTarget）"
      $script:LastSshPort = $port
      return $port
    }
  }
  Write-KLog "SSH 隧道 12s 内未就绪（端口 $port）—— 检查 ssh $SshTarget 能否连通" 'ERROR'
  return 0
}

# 看护循环的单例判定。
#
# 为什么不只比 PID：强杀（任务管理器 / 注销 / Stop-Process）会留下 lock 文件，
# 而 Windows 会把那个 PID 复用给任意新进程 —— 新 keeper 看到「PID 存在」就
# 自杀，服务再也起不来。这个坑在 tools/ssh-tunnel-keeper.ps1 里已经踩过：
# 表现是 watchdog 反复拉起、revives 一路涨，但端口始终不通。
# 所以除了 PID 存活，还必须确认该 PID 的命令行里真的有本脚本 + Watch。
function Test-WatchAlive {
  param([string]$ProcId)
  if (-not $ProcId) { return $false }
  $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcId" -ErrorAction SilentlyContinue
  if (-not $proc) { return $false }
  if ($proc.Name -notmatch 'powershell|pwsh') { return $false }

  # ⚠ 命令行读不到时**必须保守认为它还活着**。
  #
  # 踩过的坑：提权启动的进程，普通权限查 CommandLine 拿到的是空字符串
  # （Windows 的跨完整性级别限制）。原来的写法
  #   return ($proc.CommandLine -like '*mihomo-keeper.ps1*' -and ... -like '*Watch*')
  # 对空字符串返回 false → 判定「旧的已经死了」→ 接管锁 → 又起一个 Watch。
  # 实测堆出了 5 个 Watch 同时跑，每个都在无效重启 mihomo，用户看到
  # 任务管理器里十几条进程。
  #
  # 正确做法：只有**明确读到**命令行且不是 Watch 时，才判定它死了。
  if (-not $proc.CommandLine) { return $true }
  return ($proc.CommandLine -like '*mihomo-keeper.ps1*' -and $proc.CommandLine -like '*Watch*')
}

# ---------------- 动作 ----------------

# 开机自启时先恢复上次的 TUN 选择。
# 放在 switch 之前，这样 Start/Watch/Status 看到的都是正确状态。
# 注意：只有当前确实提权了才会真的启用（见 Restore-TunState）。
Restore-TunState

switch ($Action) {

  'Status' {
    Write-Host '--- 端点 ---'
    if (Test-PortOpen $MixedPort) {
      Ok "统一入口 127.0.0.1:$MixedPort 监听中（HTTP + SOCKS5 同端口）"
    } else {
      Bad "统一入口 $MixedPort 未监听  →  .\mihomo-keeper.ps1 Start"
    }

    $mi = Get-MiProc
    if ($mi) { Ok "mihomo 进程: PID $($mi.Id)（启动于 $($mi.StartTime)）" }
    else     { Bad 'mihomo 未运行（PID 文件缺失或进程已退出）' }

    $sshPort = Get-SshSocksPort
    if (Test-PortOpen $sshPort) { Ok "上游 ferry-ssh: 127.0.0.1:$sshPort 在听" }
    else                        { Bad "上游 ferry-ssh: $sshPort 没在听（隧道断了）" }

    if (Test-PortOpen 1080) { }  # WG 的 SOCKS 在远端，本地不监听，不能这样测

    Write-Host ''
    Write-Host '--- TUN（全局接管）---'
    $isAdmin = Test-Admin
    if ($script:TunEnabled) {
      if (Test-TunAdapter) { Ok "已开启（网卡 FerryTun 存在）—— 全局接管生效，UDP 走低延迟链路" }
      elseif ($isAdmin) { Warn '配置里开了但网卡没建起来 —— 看日志，可能 mihomo 启动失败' }
      else { Warn '配置里开了但当前不是管理员 —— mihomo 会因建网卡失败而退出' }
    } else {
      if ($isAdmin) { Say "未开启（当前是管理员，可直接开：.\mihomo-keeper.ps1 TunOn）" }
      else { Say '未开启（需要管理员权限：.\mihomo-keeper.ps1 Elevate -Target TunOn）' }
      Say '不开也能用，但只有「认代理设置」的程序生效；要有 UDP 低延迟就开 TUN'
    }

    Write-Host ''
    Write-Host '--- 线路状态 ---'
    if (Test-PortOpen $MixedPort) {
      $g = Invoke-Api "/proxies/ferry"
      if ($g) {
        $cur = $g.now
        $color = if ($cur -eq 'DIRECT') { 'Yellow' } else { 'Green' }
        Write-Host "  ferry      → $cur" -ForegroundColor $color
        Say  "  可选: $($g.all -join ' | ')"
      } else { Warn '  读不到组状态（API 没响应）' }

      $a = Invoke-Api "/proxies/ferry-auto"
      if ($a) {
        Ok "ferry-auto → $($a.now)   (健康检查 http://connect.rom.miui.com/generate_204 每 30s)"
      }

      Write-Host ''
      Write-Host '--- 出口实测 ---'
      foreach ($n in @('DIRECT', 'ferry-ssh', 'ferry-wg')) {
        $null = Invoke-Api "/proxies/ferry" 'PUT' (@{ name = $n } | ConvertTo-Json -Compress)
        # 1 秒：等 mihomo 内部把新选择应用到新建连接上。
        # 300ms 时偶发读到上一态的结果（连接池还没换）。
        Start-Sleep -Milliseconds 1000
        $ip = Get-ExitIp -TimeoutSec 15
        Write-Host ("  {0,-10} {1}" -f $n, $ip)
      }
      # 测完还原成进来时的选择
      if ($g -and $g.now) {
        $null = Invoke-Api "/proxies/ferry" 'PUT' (@{ name = $g.now } | ConvertTo-Json -Compress)
      }
    }

    Write-Host ''
    Write-Host '--- 自启 ---'
    if (Test-Path $StartupCmd) { Ok "已安装: $StartupCmd" } else { Warn '未安装  →  .\mihomo-keeper.ps1 Install' }

    $wp = $null
    if (Test-Path $WatchLock) {
      $wpid = ("$(Get-Content $WatchLock -Raw -ErrorAction SilentlyContinue)").Trim()
      if (Test-WatchAlive $wpid) { $wp = $wpid }
    }
    if ($wp) { Ok "看护循环: PID $wp（20s 体检，挂了自动拉起）" }
    else     { Warn '看护循环未运行 —— 崩溃后不会自动恢复，建议装自启或手动 -Action Watch' }
  }

  'Start' {
    if (Get-MiProc) {
      Warn 'mihomo 已在运行，先停再起'
      $null = Stop-Mi
    }
    if (Start-Mi) {
      Ok "已启动: 127.0.0.1:$MixedPort"
      $g = Invoke-Api "/proxies/ferry"
      if ($g) { Say "ferry → $($g.now)" }
    } else {
      Bad '启动失败，看日志: ' + $LogPath
      exit 1
    }
  }

  'Restart' {
    $null = Stop-Mi
    Start-Sleep -Milliseconds 500
    if (Start-Mi) { Ok "已重启: 127.0.0.1:$MixedPort" } else { Bad '重启失败'; exit 1 }
  }

  'Stop' {
    # 必须先停 Watch 再停 mihomo —— 反过来的话 Watch 会在 20 秒内把 mihomo
    # 又拉起来，表现是「Stop 说停了，过一会儿端口又在了」。
    $w = $null
    if (Test-Path $WatchLock) {
      $wpid = ("$(Get-Content $WatchLock -Raw -ErrorAction SilentlyContinue)").Trim()
      if (Test-WatchAlive $wpid) {
        try { Stop-Process -Id ([int]$wpid) -Force -ErrorAction Stop; $w = $wpid } catch { }
      }
      Remove-Item $WatchLock -Force -ErrorAction SilentlyContinue
    }
    if ($w) { Ok "看护循环已停 (PID $w)" }
    if (Stop-Mi) { Ok 'mihomo 已停止' } else { Warn 'mihomo 本来就没在跑' }

    # 只停我们自己拉起来的 ssh，不动用户手动起的那条。
    # 这是从 2026-10-23 那次事故学来的：当时用 Get-Process python | Stop-Process
    # 按进程名误杀了正式服务。按 PID 文件 + 双重校验才安全。
    $ownSsh = Get-OwnSshProc
    if ($ownSsh) {
      try {
        Stop-Process -Id $ownSsh.ProcessId -Force -ErrorAction Stop
        Ok "已停止本工具拉起的 SSH 隧道 (PID $($ownSsh.ProcessId))"
      } catch { }
    }
    Remove-Item $SshPidPath -Force -ErrorAction SilentlyContinue
  }

  'On' {
    $r = Invoke-Api '/proxies/ferry' 'PUT' (@{ name = 'ferry-auto' } | ConvertTo-Json -Compress)
    if ($null -ne $r -or (Test-PortOpen $MixedPort)) {
      # UDP 是独立的组，必须一起切 —— 否则用户以为开了借网，
      # 结果游戏/QUIC 还在走直连，而且没有任何报错提示。
      $null = Invoke-Api '/proxies/ferry-udp-group' 'PUT' (@{ name = 'ferry-udp' } | ConvertTo-Json -Compress)
      Save-Selection 'ferry-auto'
      $a = Invoke-Api '/proxies/ferry-auto'
      $u = if (Test-UdpRelay) { 'ferry-udp' } else { '不可用（relay 没起）' }
      Ok "已借网 → TCP: ferry-auto → $($a.now)"
      Ok "         UDP: $u"
    } else { Bad '切换失败（端点没起来？先 Start）'; exit 1 }
  }

  'Off' {
    $null = Invoke-Api '/proxies/ferry' 'PUT' (@{ name = 'DIRECT' } | ConvertTo-Json -Compress)
    $null = Invoke-Api '/proxies/ferry-udp-group' 'PUT' (@{ name = 'DIRECT' } | ConvertTo-Json -Compress)
    Save-Selection 'DIRECT'
    Ok '已恢复直连（TCP 与 UDP 都回到 DIRECT）'
  }

  'Switch' {
    $allowed = @('DIRECT', 'ferry-auto', 'ferry-ssh', 'ferry-wg')
    if ($allowed -notcontains $Target) {
      Bad "只能选: $($allowed -join ' / ')"; exit 1
    }
    $null = Invoke-Api '/proxies/ferry' 'PUT' (@{ name = $Target } | ConvertTo-Json -Compress)
    # 选具体某条 TCP 线路时，UDP 跟着一起开（用户意图是「借网」），
    # 因为 ferry-udp 是唯一能承载 UDP 的，没有别的选择。
    if ($Target -ne 'DIRECT') {
      $null = Invoke-Api '/proxies/ferry-udp-group' 'PUT' (@{ name = 'ferry-udp' } | ConvertTo-Json -Compress)
    } else {
      $null = Invoke-Api '/proxies/ferry-udp-group' 'PUT' (@{ name = 'DIRECT' } | ConvertTo-Json -Compress)
    }
    Save-Selection $Target
    Ok "已切到 $Target"
  }

  'Install' {
    # 覆盖写，不跳过：自启内容会随代码演进（比如从 Start 改成 Watch），
    # 用「已存在就不动」会导致升级后还在跑旧命令 —— 实测踩过。
    $content = @"
@echo off
chcp 936 >nul
rem 渡口统一入口 - 登录时拉起（SSH 隧道 + mihomo 端点，不切系统代理）
rem 单一入口：不要再装 ssh-tunnel-keeper 的自启，两个会抢端口
start "" /min powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "$Here\mihomo-keeper.ps1" -Action Watch
"@
    # .cmd 必须写成 GBK + CRLF + 无 BOM，三条都踩过：
    # 带 BOM：cmd.exe 把 BOM 三个字节当命令的一部分，首行 @echo off 失效，
    #         窗口里冒一行「'∩╗┐@echo' 不是内部或外部命令」。
    # 写 UTF-8：中文注释在 936 代码页下变乱码（仓库 commit b2e0df1 踩过）。
    # 只有 LF：cmd.exe 的解析器把行尾的 CR 当命令终止符，纯 LF 会吃掉
    #         每行第一个字符 —— 实测 `rem ...` 变成 `'em' 不是内部或外部命令`。
    $content = ($content -replace "`r`n", "`n") -replace "`n", "`r`n"
    $gbk = [Text.Encoding]::GetEncoding(936)
    [IO.File]::WriteAllText($StartupCmd, $content, $gbk)
    Ok "已写入自启: $StartupCmd"

    # 顺手清掉旧的、会抢端口的自启项。
    # 为什么自动清而不是提示用户手动删：用户的目标是「一键」，让他去
    # 启动文件夹里找文件删，就已经不算一键了。这里只删**渡口自己的**旧项，
    # 不动别人的（额度监控、WorkBuddy 那两个是别的项目的）。
    $stale = @('BorrowNetKeeper.cmd', 'FerryMihomo-old.cmd')
    foreach ($s in $stale) {
      $p = Join-Path $StartupDir $s
      if (Test-Path $p) {
        Remove-Item $p -Force -ErrorAction SilentlyContinue
        Ok "已清理旧自启: $s（避免与统一入口抢端口）"
      }
    }
  }

  'Uninstall' {
    if (Test-Path $StartupCmd) { Remove-Item $StartupCmd -Force; Ok '自启已移除' }
    else { Warn '本来就没装自启' }
  }

  'Test' {
    Write-Host '=== 自检 ==='
    if (-not (Test-PortOpen $MixedPort)) { Bad "端点 $MixedPort 没起来，先 Start"; exit 1 }
    Ok "端点 $MixedPort 在听"

    $g = Invoke-Api '/proxies/ferry'
    if (-not $g) { Bad 'API 无响应'; exit 1 }
    Ok "组读取正常，当前 $($g.now)"

    Write-Host ''
    Write-Host '--- 三态出口 ---'
    $results = @{}
    foreach ($n in @('DIRECT', 'ferry-ssh', 'ferry-wg')) {
      $null = Invoke-Api '/proxies/ferry' 'PUT' (@{ name = $n } | ConvertTo-Json -Compress)
      Start-Sleep -Milliseconds 1000
      $ip = Get-ExitIp -TimeoutSec 15
      $results[$n] = $ip
      if ($ip -eq 'FAIL') { Bad ("  {0,-10} 失败" -f $n) }
      else { Write-Host ("  {0,-10} {1}" -f $n, $ip) }
    }

    Write-Host ''
    if ($results['DIRECT'] -ne 'FAIL' -and $results['ferry-ssh'] -ne 'FAIL' -and $results['DIRECT'] -ne $results['ferry-ssh']) {
      Ok '出口确实不同 —— 分流生效'
    } else {
      Warn '出口没变化，检查上游隧道'
    }

    $null = Invoke-Api '/proxies/ferry' 'PUT' (@{ name = $g.now } | ConvertTo-Json -Compress)
    Write-Host ''
    Ok '自检完成'
  }

  # 看护循环：整条链的健康都由它负责。
  #
  # 每 20 秒体检三件事：
  #   ① SSH 隧道在不在 → 不在就自己拉一条（Ensure-SshTunnel）
  #   ② mihomo 端点在不在 → 不在就拉起
  #   ③ 上游端口变了没 → 变了就重渲染配置 + 重启 mihomo
  #
  # 为什么③必须做：mihomo 自己不会在 SSH 隧道换端口后自动重连新端口 ——
  # config.yaml 里的 port 是死的。隧道从 10801 顺延到 10808 时，mihomo 会
  # 一直连那个已经不存在的 10801，表现是「代理端口还在听、但所有请求 502」。
  'Watch' {
    # 单例用命名 Mutex，不用 PID 文件。
    #
    # 为什么换：PID 文件方案有两个死穴 ——
    #   ① 提权进程读不到 CommandLine，`Test-WatchAlive` 会误判「旧的已死」
    #      （见该函数的注释，实测因此堆出 5 个 Watch）
    #   ② 两个进程同时读-写锁文件有竞态
    # Mutex 是内核对象：进程一退出自动释放，不存在「残留文件」和「PID 复用」。
    try {
      if (-not $script:WatchMutex) {
        $script:WatchMutex = New-Object System.Threading.Mutex($false, 'Local\FerryMihomoWatch')
      }
      $acquired = $false
      try {
        $acquired = $script:WatchMutex.WaitOne(0)
      } catch [System.Threading.AbandonedMutexException] {
        # 上一个持锁进程是被强杀的 —— 锁已经归我们，继续。
        $acquired = $true
      }
      if (-not $acquired) {
        Write-KLog "watch already running (mutex held), nothing to do"
        exit 0
      }
    } catch {
      Write-KLog "mutex 不可用（$($_.Exception.Message)），退回 PID 文件判断" 'WARN'
    }

    if (Test-Path $WatchLock) {
      $oldPid = ("$(Get-Content $WatchLock -Raw -ErrorAction SilentlyContinue)").Trim()
      if ($oldPid -and $oldPid -ne "$PID" -and (Test-WatchAlive $oldPid)) {
        Write-KLog "watch already running (pid $oldPid), nothing to do"
        exit 0
      }
    }
    [IO.File]::WriteAllText($WatchLock, "$PID", (New-Object Text.UTF8Encoding $false))
    Write-KLog "watch start pid=$PID mixed=$MixedPort udp=$UdpHost`:$UdpPort"

    $lastUpstream = -1
    try {
      # 先体检一轮再睡：登录自启时直接就是「Watch 起来 → 立刻把整条链拉好」，
      # 不用等第一个 20 秒周期。
      while ($true) {
        # ① SSH 隧道
        $sshPort = Ensure-SshTunnel

        # ② mihomo 端点
        if (-not (Get-MiProc) -or -not (Test-PortOpen $MixedPort 300)) {
          if ($sshPort) {
            Write-KLog '体检：端点不通，拉起 mihomo' 'WARN'
            $null = Stop-Mi
            if (Start-Mi) {
              $lastUpstream = $sshPort
              Write-KLog "体检：已恢复，上游 $lastUpstream"
            }
          } else {
            Write-KLog '体检：SSH 隧道不可用，暂不拉起 mihomo（否则它连死端口）' 'WARN'
          }
        } else {
          # ③ 上游端口变了 → 重渲染配置并重启，否则 mihomo 连的是死端口
          if ($sshPort -and $sshPort -ne $lastUpstream) {
            if ($lastUpstream -ne -1) {
              Write-KLog "体检：上游端口 $lastUpstream → $sshPort，重渲染配置" 'WARN'
              $null = Stop-Mi
              if (Start-Mi) { Write-KLog "体检：已切到新上游 $sshPort" }
            }
            $lastUpstream = $sshPort
          }
        }

        # ④ UDP 中继可达性：不可达只记日志，不重启任何东西 ——
        # relay 在远端，重启本地 mihomo 解决不了它。
        if (-not (Test-UdpRelay)) {
          if (-not $script:UdpWarned) {
            Write-KLog "体检：UDP 中继 $UdpHost`:$UdpPort 不可达（TCP 不受影响）" 'WARN'
            $script:UdpWarned = $true
          }
        } elseif ($script:UdpWarned) {
          Write-KLog "体检：UDP 中继已恢复"
          $script:UdpWarned = $false
        }

        Start-Sleep -Seconds 20
      }
    }
    finally {
      Write-KLog "watch stop pid=$PID"
      Remove-Item $WatchLock -Force -ErrorAction SilentlyContinue
    }
  }

  # ── TUN 开关 ──
  #
  # TUN 让 mihomo 建虚拟网卡、接管全局路由，这样连不认代理的程序
  # （多数游戏客户端、部分桌面软件）也走渡口。
  #
  # 代价是**必须管理员**。普通权限下 mihomo 会报
  #   Start TUN listening error: configure tun interface: Access is denied.
  # 然后整个进程退出 —— 连带把本来能用的 SOCKS 端点也搞没了。
  # 所以这里先判权限，没权限就明确报错并给提权命令，绝不半途而废。
  'TunOn' {
    if (-not (Test-Admin)) {
      Bad 'TUN 需要管理员权限'
      Say '用这个命令提权（会弹 UAC）：'
      Say "  .\mihomo-keeper.ps1 Elevate -Target TunOn"
      exit 1
    }
    # 检查 wintun.dll —— mihomo 靠它建网卡，缺了会报一个很难懂的错
    $wintun = Join-Path $Here 'wintun.dll'
    if (-not (Test-Path $wintun)) {
      Bad "缺少 wintun.dll: $wintun"
      Say '从 Clash Verge 或 easytier 目录拷一个过来：'
      Say '  copy "C:\Users\<你>\AppData\Local\easytier-gui\wintun.dll" mihomo\'
      exit 1
    }
    $script:TunEnabled = $true
    Save-TunState $true
    Ok 'TUN 已启用，重启 mihomo 生效'
    $null = Stop-Mi
    Start-Sleep -Milliseconds 500
    if (Start-Mi) {
      Start-Sleep -Seconds 2
      if (Test-TunAdapter) {
        Ok '网卡 FerryTun 已创建 —— 所有程序现在都走渡口'
      } else {
        Bad 'mihomo 起来了但网卡没建出来'
        Say "看日志: $LogPath"
        Say '常见原因：wintun.dll 版本不匹配，或被杀软拦了'
      }
    } else {
      Bad '重启失败 —— 看日志，可能要 TunOff 回到 SOCKS 模式'
      $script:TunEnabled = $false
      Save-TunState $false
    }
  }

  'TunOff' {
    $script:TunEnabled = $false
    Save-TunState $false
    Ok 'TUN 已关闭，回到 SOCKS/HTTP 端点模式（127.0.0.1:10810）'
    $null = Stop-Mi
    Start-Sleep -Milliseconds 500
    $null = Start-Mi
  }

  # 提权自己再跑一次指定动作。
  #
  # 为什么用 RunAs 而不是让用户自己开管理员 PowerShell：
  # 用户的目标是「便捷」。让他去找「以管理员身份运行」、再 cd 到目录、
  # 再敲命令，就已经不算便捷了。这里一次 UAC 点击搞定。
  'Elevate' {
    # PS 5.1 不允许 `$var = if (...) {...} else {...}` 这种把 if 当表达式用，
    # 必须先用默认值再覆盖。这个坑在项目里已经踩过一次（docs/踩坑记录.md 第 2 条）。
    $t = 'TunOn'
    if ($Target -and $Target -ne 'DIRECT') { $t = $Target }

    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = 'powershell.exe'
    # PS 5.1 也不支持 `$Here'\xxx'` 这种相邻拼接，用 Join-Path。
    $selfPath = Join-Path $Here 'mihomo-keeper.ps1'
    $psi.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Action {1}' -f $selfPath, $t
    $psi.Verb = 'runas'          # 触发 UAC
    $psi.UseShellExecute = $true
    try {
      $null = [Diagnostics.Process]::Start($psi)
      Ok "已请求提权（动作 $t）—— 在弹出的 UAC 窗口点「是」"
    } catch {
      Warn "提权被取消或失败: $($_.Exception.Message)"
    }
  }
}

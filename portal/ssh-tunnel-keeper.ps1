#requires -Version 5.1
<#
  ssh-tunnel-keeper.ps1 — 常驻保活：保持 ssh -D SOCKS 隧道连通远端办公网

  由 ferry.ps1 / 渡口助手.ps1 / dual-proxy.mjs watchdog 启动，或登录自启。
  设计要点：
    * 本进程前台跑 ssh，ssh 退出（断线/重启）自动重启，5s 间隔防风暴
    * 本进程被杀会带走子 ssh（父子进程），不会留孤儿
    * 单例锁 ferry.keeper.lock 防止重复启动（带 PID 复用防护）
    * 只建立隧道，不改系统代理、不改路由
    * 自己写 .ferry/keeper.log —— 被 spawn(stdio:'ignore') 拉起时没有 stdout，
      日志是唯一的排查手段

  参数可与主脚本不一致，主脚本会按当前配置传参。
#>
[CmdletBinding()]
param(
  [string]$RemoteHost = 'pcs',
  [int]$SocksPort   = 10808
)

$ErrorActionPreference = 'Stop'

# ---------------- 日志 ----------------
# 为什么自己写文件而不靠父进程接管道：
#   本脚本常被 dual-proxy.mjs 以 spawn() 方式拉起，或用 WMI Create 创建，
#   这两种方式都没有可读的 stdout（前者 stdio:'ignore' 直接丢弃，后者无控制台）。
#   实际踩过：watchdog 每 20 秒拉起一次 keeper，隧道始终不通，
#   而所有输出都被丢掉，日志里只剩一句「keeper 已启动」的假象。
$LogDir  = Join-Path $PSScriptRoot '.ferry'
$LogPath = Join-Path $LogDir 'keeper.log'

function Write-KLog {
  param([string]$Msg)
  $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Msg
  try {
    if (-not (Test-Path $LogDir)) { $null = New-Item -ItemType Directory -Path $LogDir -Force }
    Add-Content -Path $LogPath -Value $line -Encoding UTF8
    $fi = Get-Item $LogPath -ErrorAction SilentlyContinue
    if ($fi -and $fi.Length -gt 524288) {
      $keep = Get-Content $LogPath -Tail 400
      Set-Content -Path $LogPath -Value $keep -Encoding UTF8
    }
  }
  catch { }
  Write-Host $line
}

$sshArgs = @(
  '-N'
  '-o', 'BatchMode=yes'
  '-o', 'ExitOnForwardFailure=yes'
  '-o', 'ServerAliveInterval=30'
  '-o', 'ServerAliveCountMax=3'
  '-o', 'ConnectTimeout=10'
  $RemoteHost
  '-D', "127.0.0.1:$SocksPort"
)

$lockPath = Join-Path $PSScriptRoot 'ferry.keeper.lock'

# --- 单例检查 ---
# 坑：只比对 PID 是不够的。keeper 被强杀（任务管理器 / 注销 / Stop-Process）时
# lock 文件会残留，而 Windows 会把这个 PID 复用给任意新进程 —— 于是新 keeper
# 看到「PID 存在且是 powershell」就 exit 0 自杀，隧道再也起不来。
# 实测表现：watchdog 反复拉起 keeper，revives 一路涨，但 ssh 始终为 0、10808 不通。
# 正解：除了 PID 存活，还必须确认该 PID 的命令行里真的有本脚本名。
function Test-KeeperAlive {
  param([string]$ProcId)
  if (-not $ProcId) { return $false }
  $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcId" -ErrorAction SilentlyContinue
  if (-not $proc) { return $false }
  if ($proc.Name -notmatch 'powershell|pwsh') { return $false }
  return ($proc.CommandLine -like '*ssh-tunnel-keeper.ps1*')
}

if (Test-Path $lockPath) {
  $oldPid = ("$(Get-Content $lockPath -Raw -ErrorAction SilentlyContinue)").Trim()
  if ($oldPid -and $oldPid -ne "$PID" -and (Test-KeeperAlive $oldPid)) {
    Write-KLog "keeper already running (pid $oldPid), nothing to do"
    exit 0
  }
  Write-KLog "stale keeper lock (pid $oldPid), taking over"
}
Set-Content -Path $lockPath -Value "$PID" -Encoding ASCII
Write-KLog "keeper start pid=$PID -> $RemoteHost socks=127.0.0.1:$SocksPort"

# ssh 的 PATH 可能因非交互启动而不完整，先固化绝对路径
$sshExe = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh.exe'
if (-not (Test-Path $sshExe)) {
  $cmd = Get-Command ssh.exe -ErrorAction SilentlyContinue
  if ($cmd) { $sshExe = $cmd.Source } else { $sshExe = 'ssh.exe' }
}

$attempt = 0
try {
  while ($true) {
    $attempt++
    Write-KLog "ssh starting (attempt $attempt): $sshExe $($sshArgs -join ' ')"
    $t0 = Get-Date

    # 用管道接住 ssh 的 stderr：ssh 的失败原因（Host key / Permission denied /
    # Could not resolve hostname / bind failed）全在这里，丢了就只能瞎猜。
    try {
      & $sshExe @sshArgs 2>&1 | ForEach-Object { Write-KLog "ssh: $_" }
      $code = $LASTEXITCODE
    }
    catch {
      Write-KLog "ssh 启动异常: $($_.Exception.Message)"
      $code = -1
    }

    $secs = [int]((Get-Date) - $t0).TotalSeconds
    Write-KLog "ssh exited code=$code after ${secs}s, restart in 5s"
    Start-Sleep -Seconds 5
  }
}
finally {
  Write-KLog "keeper stop pid=$PID"
  Remove-Item $lockPath -Force -ErrorAction SilentlyContinue
}

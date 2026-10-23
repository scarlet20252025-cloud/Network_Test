#requires -Version 5.1
<#
  outage-watch.ps1 — 断网存活监控：持续记录「直连」与「借网」两条路的状态

  为什么需要它：
    要验证「断网后借网还能用」，必须真的断一次网。但断网时你可能连不上
    这台机器、也看不到屏幕输出。本脚本在后台每 5 秒写一行日志，
    断网 → 恢复之后直接翻日志就能看到断网期间借网是否还活着。

  用法：
    # 1) 先开借网（GUI 点「开启借网」或 borrow-net.ps1 On）
    # 2) 启动监控（不要关这个窗口，或者用 -Background 隐藏）
    .\outage-watch.ps1

    # 3) 手动断网：关 WLAN / 拔网线 / 关路由器
    #    等 1-2 分钟，再恢复网络
    # 4) 看结果
    .\outage-watch.ps1 -Report

  判定标准：
    直连 000 + 借网 200  → ✅ 断网期间借网有效
    直连 000 + 借网 000  → ❌ 断的是 WLAN 接入层，SSH 也断了
    直连 200 + 借网 200  → 没断成，重试
#>
[CmdletBinding()]
param(
  [int]$SocksPort = 10808,
  [int]$HttpPort = 10809,
  [int]$IntervalSec = 5,
  [switch]$Report,
  [switch]$Background
)

$ErrorActionPreference = 'Continue'
$StateDir = Join-Path $PSScriptRoot '.borrow-net'
if (-not (Test-Path $StateDir)) { $null = New-Item -ItemType Directory -Path $StateDir -Force }
$LogFile = Join-Path $StateDir 'outage-watch.log'

function Write-Log {
  param([string]$Msg)
  $line = "[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $Msg
  Add-Content -Path $LogFile -Value $line -Encoding UTF8
  if (-not $Background) { Write-Host $line }
}

function Test-Direct {
  # 直连：显式绕过系统代理，否则测的是借网
  $r = & curl.exe -s -o NUL -w '%{http_code}' --noproxy '*' --max-time 6 'http://myip.ipip.net' 2>$null
  if ($r -match '^2') { return $true }
  return $false
}

function Test-Borrow {
  $r = & curl.exe -s -o NUL -w '%{http_code}' --max-time 8 -x "http://127.0.0.1:$HttpPort" 'http://myip.ipip.net' 2>$null
  if ($r -match '^2') { return $true }
  return $false
}

function Test-SshAlive {
  # SSH 到远端是否还通 —— 断网时这一项决定「借网还能不能救回来」
  $r = & ssh -o ConnectTimeout=5 -o BatchMode=yes pcs 'echo ok' 2>$null
  return ("$r" -match 'ok')
}

if ($Report) {
  if (-not (Test-Path $LogFile)) { Write-Host "没有日志：$LogFile"; exit 1 }
  Write-Host "=== 断网存活记录 ===" -ForegroundColor Cyan
  Get-Content $LogFile -Encoding UTF8 | Select-Object -Last 200
  Write-Host ''
  Write-Host '=== 判定 ===' -ForegroundColor Cyan
  $lines = Get-Content $LogFile -Encoding UTF8
  $outage = $lines | Where-Object { $_ -match '直连=断' }
  if (-not $outage) { Write-Host '日志里没有「直连=断」的记录，说明没抓到断网时刻。' -ForegroundColor Yellow; exit 0 }
  $borrowOk = ($outage | Where-Object { $_ -match '借网=通' }).Count
  $borrowNg = ($outage | Where-Object { $_ -match '借网=断' }).Count
  Write-Host ("断网期间采样 {0} 次：借网通 {1} 次 / 借网断 {2} 次" -f $outage.Count, $borrowOk, $borrowNg)
  if ($borrowOk -gt $borrowNg) {
    Write-Host '✅ 断网期间借网可用' -ForegroundColor Green
  } else {
    Write-Host '❌ 断网期间借网也断了 —— 断的是 WLAN 接入层，SSH 通路一起没了' -ForegroundColor Red
  }
  exit 0
}

Write-Log '=== 监控开始（Ctrl+C 结束）==='
Write-Log "参数: SOCKS=$SocksPort HTTP=$HttpPort 间隔=${IntervalSec}s"
Write-Log '现在可以手动断网，恢复后运行 .\outage-watch.ps1 -Report 看结果'

$lastState = ''
while ($true) {
  $d = Test-Direct
  $b = Test-Borrow
  $s = Test-SshAlive

  $dTxt = if ($d) { '通' } else { '断' }
  $bTxt = if ($b) { '通' } else { '断' }
  $sTxt = if ($s) { '通' } else { '断' }
  $state = "直连=$dTxt 借网=$bTxt SSH=$sTxt"

  # 只在状态变化或每 12 次（1 分钟）记一次，避免日志爆炸
  if ($state -ne $lastState) {
    Write-Log "[变化] $state"
    $lastState = $state
  }
  else {
    $script:tick = ($script:tick + 1)
    if ($script:tick % 12 -eq 0) { Write-Log "[持续] $state" }
  }

  Start-Sleep -Seconds $IntervalSec
}

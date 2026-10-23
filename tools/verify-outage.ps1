#requires -Version 5.1
<#
  verify-outage.ps1 — 一次跑完「模拟断网 → 验证渡口 → 自动恢复 → 出报告」

  为什么做成全自动：
    模拟断网会同时切断本机所有公网出站，**包括 DSH 自己的模型调用**。
    如果中途需要人再敲一条命令恢复，就可能卡死在那儿。
    本脚本用 try/finally 保证无论成功失败都还原防火墙规则。

  模拟的断网方式（对齐学校实际情况）：
    学校是「禁用学生账号的上网能力，局域网仍通」。
    所以规则是：
      - 放行 10.0.0.0/8、172.16.0.0/12、192.168.0.0/16  → 局域网通（含远端 SSH）
      - 放行 127.0.0.0/8                                → 回环通（dual-proxy / ssh -D）
      - 阻断其余出站                                    → 公网断

  用法（必须管理员）：
    .\verify-outage.ps1

  输出：
    控制台即时打印 + 写文件 .ferry\verify-outage.log
    ⚠️ 运行期间（约 40 秒）本机无法上公网，属预期；结束会自动恢复。
#>
[CmdletBinding()]
param(
  [string]$RemoteHost = 'pcs',
  [int]$SocksPort = 0,
  [int]$HttpPort = 10809,
  [int]$HoldSeconds = 25
)

# ---- 端口解析（与 ferry.ps1 / 渡口助手.ps1 共用同一入口）----
# 不自带默认值：写死端口会让本脚本在「开了端口自适应」的环境里探错端口。
. (Join-Path $PSScriptRoot 'Get-FerryPorts.ps1')
$_fp = Get-FerryPorts -SocksPort $SocksPort -HttpPort $(if ($PSBoundParameters.ContainsKey('HttpPort')) { $HttpPort } else { 0 })
$SocksPort = $_fp.SocksPort
if ($PSBoundParameters.ContainsKey('HttpPort')) { $HttpPort = $_fp.HttpPort }

$ErrorActionPreference = 'Continue'
$Tag = 'Ferry-OutageSim'
$StateDir = Join-Path $PSScriptRoot '.ferry'
if (-not (Test-Path $StateDir)) { $null = New-Item -ItemType Directory -Path $StateDir -Force }
$LogFile = Join-Path $StateDir 'verify-outage.log'

function Log {
  param([string]$Msg, [string]$Color = 'Gray')
  $line = $Msg
  Write-Host $line -ForegroundColor $Color
  Add-Content -Path $LogFile -Value ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $Msg) -Encoding UTF8
}

function Test-Admin {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# 采样：返回 'OK' / 'FAIL' / 'code=xxx'
function Probe([string]$Url, [switch]$ViaBorrow, [int]$TimeoutSec = 8) {
  $a = @('-s', '-o', 'NUL', '-w', '%{http_code}', '--max-time', "$TimeoutSec", '--ssl-no-revoke')
  if ($ViaBorrow) { $a += @('-x', "http://127.0.0.1:$HttpPort") }
  else { $a += @('--noproxy', '*') }
  $a += $Url
  $code = & curl.exe @a 2>$null
  if ("$code" -match '^[23]') { return 'OK' }
  return "FAIL($code)"
}

function ProbeSsh {
  $r = & ssh -o ConnectTimeout=5 -o BatchMode=yes $RemoteHost 'echo ok' 2>$null
  if ("$r" -match 'ok') { return 'OK' }
  return 'FAIL'
}

function Remove-SimRules {
  $rules = Get-NetFirewallRule -DisplayName "$Tag-*" -ErrorAction SilentlyContinue
  if ($rules) { $rules | Remove-NetFirewallRule -ErrorAction SilentlyContinue }
}

# ─────────────────────────────────────────────

Set-Content -Path $LogFile -Value "=== verify-outage $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===" -Encoding UTF8

if (-not (Test-Admin)) {
  Write-Host '❌ 需要管理员权限（要改防火墙规则）' -ForegroundColor Red
  Write-Host ''
  Write-Host '做法：' -ForegroundColor Yellow
  Write-Host '  1. 按 Win 键，输入 powershell'
  Write-Host '  2. 右键「Windows PowerShell」→「以管理员身份运行」'
  Write-Host '  3. 粘贴执行：'
  Write-Host ("     powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`"") -ForegroundColor Cyan
  exit 1
}

Log '=== 开始验证 ==='
Log ''
Log '[0/5] 断网前的基线'
$ssh0 = ProbeSsh
$d0 = Probe 'http://myip.ipip.net'
$b0 = Probe 'http://myip.ipip.net' -ViaBorrow
Log ("  SSH 到远端      : $ssh0")
Log ("  直连出公网      : $d0")
Log ("  渡口出公网      : $b0")
if ($b0 -ne 'OK') {
  Log ''
  Log '❌ 渡口现在就不通，先修好再验证（GUI 点「开启渡口」或跑 ferry.ps1 On）' 'Red'
  exit 1
}
Log ''

try {
  Log '[1/5] 启用模拟断网（局域网通 / 公网断）'
  Remove-SimRules
  foreach ($cidr in @('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16')) {
    $safe = $cidr -replace '[./]', '_'
    New-NetFirewallRule -DisplayName "$Tag-Allow-LAN-$safe" -Direction Outbound `
      -RemoteAddress $cidr -Action Allow -Profile Any -ErrorAction Stop | Out-Null
  }
  New-NetFirewallRule -DisplayName "$Tag-Allow-Loopback" -Direction Outbound `
    -RemoteAddress '127.0.0.0/8' -Action Allow -Profile Any -ErrorAction Stop | Out-Null
  New-NetFirewallRule -DisplayName "$Tag-Block-Public" -Direction Outbound `
    -Action Block -Profile Any -ErrorAction Stop | Out-Null
  Log '  已启用' 'Yellow'
  Start-Sleep -Seconds 3

  Log ''
  Log "[2/5] 确认公网确实断了（这是前提）"
  $d1 = Probe 'http://myip.ipip.net'
  $d2 = Probe 'https://www.baidu.com'
  Log ("  直连 myip      : $d1")
  Log ("  直连 baidu     : $d2")
  $publicBlocked = ($d1 -ne 'OK' -and $d2 -ne 'OK')
  if ($publicBlocked) { Log '  ✅ 公网已断，符合学校断网状态' 'Green' }
  else { Log '  ⚠️ 公网没断干净，结论会失真（可能有多网卡或规则被覆盖）' 'Yellow' }

  Log ''
  Log "[3/5] 关键验证：断网状态下渡口是否可用"
  $results = [ordered]@{}
  $results['SSH 到远端'] = ProbeSsh
  $results['github.com'] = Probe 'https://github.com' -ViaBorrow -TimeoutSec 20
  $results['api.github.com'] = Probe 'https://api.github.com' -ViaBorrow -TimeoutSec 15
  $results['baidu.com'] = Probe 'https://www.baidu.com' -ViaBorrow -TimeoutSec 15
  $results['bilibili.com'] = Probe 'https://www.bilibili.com' -ViaBorrow -TimeoutSec 15
  $results['出口IP(渡口)'] = Probe 'http://myip.ipip.net' -ViaBorrow

  foreach ($k in $results.Keys) {
    $v = $results[$k]
    $color = if ($v -eq 'OK') { 'Green' } else { 'Red' }
    Log ("  {0,-18} : {1}" -f $k, $v) $color
  }

  Log ''
  Log "[4/5] 保持 $HoldSeconds 秒，观察是否稳定（模拟持续断网）"
  $okCount = 0
  $total = 0
  for ($i = 1; $i -le 5; $i++) {
    Start-Sleep -Seconds ([Math]::Max(1, [int]($HoldSeconds / 5)))
    $r = Probe 'https://github.com' -ViaBorrow -TimeoutSec 20
    $total++
    if ($r -eq 'OK') { $okCount++ }
    Log ("  第 {0} 次 github : {1}" -f $i, $r) $(if ($r -eq 'OK') { 'Green' } else { 'Red' })
  }

  Log ''
  Log '[5/5] 恢复网络'
}
finally {
  Remove-SimRules
  Log '  已移除模拟断网规则' 'Green'
}

# ── 结论 ──
Log ''
Log '================ 结论 ================'

$borrowAll = @($results['github.com'], $results['api.github.com'], $results['baidu.com'], $results['bilibili.com'])
$borrowOk = @($borrowAll | Where-Object { $_ -eq 'OK' }).Count

Log ("  断网时 SSH 到远端 : {0}" -f $results['SSH 到远端'])
Log ("  断网时渡口出公网  : {0}/4 个站点通（持续采样 {1}/{2}）" -f $borrowOk, $okCount, $total)
Log ''

if ($publicBlocked -and $borrowOk -ge 3) {
  Log '  ✅ 验证通过：公网被切断的情况下，渡口仍然可用。' 'Green'
  Log '     说明学校那种「禁用上网但局域网通」的断网，本方案能顶住。' 'Green'
}
elseif (-not $publicBlocked) {
  Log '  ⚠️ 没能真正切断公网，本次验证无效，请检查防火墙/多网卡。' 'Yellow'
}
elseif ($results['SSH 到远端'] -ne 'OK') {
  Log '  ❌ SSH 到远端也断了 —— 说明模拟规则误伤了局域网，' 'Red'
  Log '     或远端确实不可达。这种情况渡口无法救场。' 'Red'
}
else {
  Log '  ❌ SSH 通但渡口站点不通 —— 问题在 dual-proxy / 系统代理层。' 'Red'
  Log '     看 .ferry\bridge.log 里 CONNECT 的记录。' 'Red'
}

Log ''
Log "详细记录：$LogFile"

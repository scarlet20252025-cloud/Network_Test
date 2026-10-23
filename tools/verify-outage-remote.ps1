#requires -Version 7.0
<#
  verify-outage-remote.ps1 — 在被控机上验证「断网后借网仍可用」

  为什么要用「另一台机器」做实验：
    模拟断网会切断本机所有公网出站，包括远程会话本身。
    在 xt (10.113.66.6) 上做，即使它断了公网，本机与它的 SSH 走的是
    局域网（10.141.x → 10.113.x），不受影响，实验可完整跑完。

  实验逻辑：
    1. 起一条 ssh -D 隧道指向 pcs (10.13.24.1) —— 借网的通道
    2. 加防火墙规则：放行局域网三段 + 回环，阻断其余出站
       （这正是学校「禁用上网但局域网通」的做法）
    3. 验证：直连应失败、经隧道应成功
    4. 无论如何都还原防火墙并停隧道

  用法（由本机远程调用，无需手动执行）：
    powershell -File verify-outage-remote.ps1
#>
$ErrorActionPreference = 'Continue'

$GitSsh     = 'C:\Program Files\Git\usr\bin\ssh.exe'
$RemoteUser = 'pcserver'
$RemoteHost = '10.13.24.1'
$SocksPort  = 10808
$Tag        = 'BorrowNet-OutageSim'

function Log($m, $c = 'Gray') { Write-Output $m }

function Probe($url, [switch]$ViaSocks, $timeout = 12) {
  $a = @('-s', '-o', 'NUL', '-w', '%{http_code}', '--max-time', "$timeout", '--ssl-no-revoke')
  if ($ViaSocks) { $a += @('--socks5-hostname', "127.0.0.1:$SocksPort") }
  else { $a += @('--noproxy', '*') }
  $a += $url
  $code = & curl.exe @a 2>$null
  if ("$code" -match '^[23]') { return 'OK' }
  return "FAIL($code)"
}

function Remove-SimRules {
  $r = Get-NetFirewallRule -DisplayName "$Tag-*" -ErrorAction SilentlyContinue
  if ($r) { $r | Remove-NetFirewallRule -ErrorAction SilentlyContinue }
}

# ───────────── 0. 环境自检 ─────────────
Log "===== 环境 ====="
Log ("  主机: " + $env:COMPUTERNAME)
Log ("  ssh : " + $(if (Test-Path $GitSsh) { $GitSsh } else { '缺失!' }))
if (-not (Test-Path $GitSsh)) { Log '  ❌ 没有 Git ssh，无法建隧道'; exit 1 }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
  [Security.Principal.WindowsBuiltInRole]::Administrator)
Log ("  管理员: " + $isAdmin)
if (-not $isAdmin) { Log '  ❌ 需要管理员权限才能改防火墙'; exit 1 }

# 断网前的基线
Log ''
Log '===== [0] 断网前基线 ====='
$d0 = Probe 'http://myip.ipip.net'
Log ("  直连出公网 : $d0")

# ───────────── 1. 起隧道 ─────────────
Log ''
Log '===== [1] 建立借网隧道（xt → pcs） ====='
Remove-SimRules
$tunnel = [Diagnostics.Process]::Start([Diagnostics.ProcessStartInfo]@{
  FileName               = $GitSsh
  Arguments              = "-N -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 $RemoteUser@$RemoteHost -D 127.0.0.1:$SocksPort"
  UseShellExecute        = $false
  CreateNoWindow         = $true
})
Start-Sleep -Seconds 6
if ($tunnel.HasExited) {
  Log ("  ❌ 隧道退出 code=" + $tunnel.ExitCode)
  exit 1
}
Log ("  隧道 PID=" + $tunnel.Id)
$b0 = Probe 'https://github.com' -ViaSocks
Log ("  借网 github : $b0")
if ($b0 -ne 'OK') {
  Log '  ❌ 隧道起来了但借网不通，先排查再继续'
  $tunnel.Kill()
  exit 1
}

try {
  # ───────────── 2. 模拟断网 ─────────────
  Log ''
  Log '===== [2] 启用模拟断网（局域网通 / 公网断） ====='
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
  Start-Sleep -Seconds 3
  Log '  已启用'

  # ───────────── 3. 确认公网真断了 ─────────────
  Log ''
  Log '===== [3] 确认公网确实断了（前提） ====='
  $d1 = Probe 'http://myip.ipip.net'
  $d2 = Probe 'https://www.baidu.com'
  $d3 = Probe 'https://github.com'
  Log ("  直连 myip    : $d1")
  Log ("  直连 baidu   : $d2")
  Log ("  直连 github  : $d3")
  $publicBlocked = ($d1 -ne 'OK' -and $d2 -ne 'OK' -and $d3 -ne 'OK')
  Log ("  公网已断: " + $publicBlocked)

  # ───────────── 4. 关键验证 ─────────────
  Log ''
  Log '===== [4] 断网状态下经借网访问 ====='
  $results = [ordered]@{}
  $results['github.com']      = Probe 'https://github.com'          -ViaSocks 20
  $results['api.github.com']  = Probe 'https://api.github.com'      -ViaSocks 15
  $results['baidu.com']       = Probe 'https://www.baidu.com'       -ViaSocks 15
  $results['bilibili.com']    = Probe 'https://www.bilibili.com'    -ViaSocks 15
  foreach ($k in $results.Keys) { Log ("  {0,-16} : {1}" -f $k, $results[$k]) }

  $ipOut = & curl.exe -s --max-time 15 --socks5-hostname "127.0.0.1:$SocksPort" http://myip.ipip.net 2>$null
  Log ("  借网出口     : " + ($ipOut -replace '[\r\n]+',' '))

  # ───────────── 5. 稳定性 ─────────────
  Log ''
  Log '===== [5] 持续采样（20 秒） ====='
  $okN = 0; $tot = 0
  for ($i = 1; $i -le 4; $i++) {
    Start-Sleep -Seconds 5
    $r = Probe 'https://github.com' -ViaSocks 20
    $tot++; if ($r -eq 'OK') { $okN++ }
    Log ("  第 {0} 次 : {1}" -f $i, $r)
  }
  Log ("  稳定率: $okN/$tot")
}
finally {
  Log ''
  Log '===== [6] 还原 ====='
  Remove-SimRules
  Log '  已移除防火墙规则'
  if ($tunnel -and -not $tunnel.HasExited) { $tunnel.Kill(); Log '  已停止隧道' }
  Start-Sleep -Seconds 2
  $dEnd = Probe 'http://myip.ipip.net'
  Log ("  直连恢复: $dEnd")
}

# ───────────── 结论 ─────────────
Log ''
Log '===== 结论 ====='
$pass = @($results.Values | Where-Object { $_ -eq 'OK' }).Count
Log ("  公网被切断: $publicBlocked")
Log ("  借网站点通: $pass/4")
Log ("  稳定性    : $okN/$tot")
if ($publicBlocked -and $pass -ge 3) {
  Log '  RESULT: PASS — 断网后借网可用'
} elseif (-not $publicBlocked) {
  Log '  RESULT: INVALID — 公网没断干净，结论不可信'
} else {
  Log '  RESULT: FAIL — 断网后借网也不可用'
}

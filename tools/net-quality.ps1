# 渡口链路真实可达性 + 稳定性实测
# 不看出口 IP，而是量：DNS / TCP / TLS / HTTP / 吞吐 / 抖动 / 丢包 / 长连接保持
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File net-quality.ps1
param(
  [int]$SocksPort = 0,
  [int]$Rounds = 3
)
# ---- 端口解析（与 ferry.ps1 / 渡口助手.ps1 共用同一入口）----
# 不自带默认值：写死端口会让本脚本在「开了端口自适应」的环境里探错端口。
. (Join-Path $PSScriptRoot 'Get-FerryPorts.ps1')
$_fp = Get-FerryPorts -SocksPort $SocksPort -HttpPort $(if ($PSBoundParameters.ContainsKey('HttpPort')) { $HttpPort } else { 0 })
$SocksPort = $_fp.SocksPort
if ($PSBoundParameters.ContainsKey('HttpPort')) { $HttpPort = $_fp.HttpPort }

$ErrorActionPreference = 'Continue'
$logDir = Join-Path $PSScriptRoot '.ferry'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir 'net-quality.log'
function L($m) {
  $line = "[{0:HH:mm:ss.fff}] {1}" -f (Get-Date), $m
  Add-Content -Path $log -Value $line
  Write-Host $line
}
Set-Content -Path $log -Value "=== net-quality 开始 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===" -Encoding UTF8

$socks = "socks5h://127.0.0.1:$SocksPort"

function Curl-Time([string]$url, [switch]$Borrow, [switch]$NoProxy) {
  $a = @('-s', '-o', 'NUL', '-w', '%{http_code} %{time_namelookup} %{time_connect} %{time_appconnect} %{time_starttransfer} %{time_total} %{size_download} %{speed_download}', '--max-time', '20')
  if ($Borrow) { $a += '--proxy', $socks }
  elseif ($NoProxy) { $a += '--noproxy', '*' }
  $a += $url
  $out = & curl.exe @a 2>$null
  if (-not $out) { return $null }
  $parts = $out -split '\s+'
  if ($parts.Count -lt 8) { return $null }
  return [pscustomobject]@{
    Code = $parts[0]
    Dns = [double]$parts[1]
    Tcp = [double]$parts[2]
    Tls = [double]$parts[3]
    Ttfb = [double]$parts[4]
    Total = [double]$parts[5]
    Bytes = [long]$parts[6]
    Speed = [double]$parts[7]
  }
}

function Stat($arr) {
  if (-not $arr -or $arr.Count -eq 0) { return $null }
  $s = $arr | Sort-Object
  $n = $s.Count
  $avg = ($s | Measure-Object -Average).Average
  return [pscustomobject]@{
    N = $n
    Min = $s[0]
    P50 = $s[[int][Math]::Floor($n * 0.5)]
    P95 = $s[[Math]::Min($n - 1, [int][Math]::Floor($n * 0.95))]
    Max = $s[$n - 1]
    Avg = [Math]::Round($avg, 3)
  }
}

# ---------- 1. 四类目标 × 两种通道 ----------
$targets = [ordered]@{
  '国内HTTP'    = 'http://www.baidu.com'
  '国内HTTPS'   = 'https://www.baidu.com'
  '国外HTTPS'   = 'https://www.google.com'
  'GitHub API'  = 'https://api.github.com'
  '腾讯云'      = 'https://cloud.tencent.com'
}
L ''
L '========== 1. 各目标可达性（每目标 3 次取中位）=========='
$rows = @()
foreach ($name in $targets.Keys) {
  $url = $targets[$name]
  foreach ($mode in @('直连', '渡口')) {
    $totals = @(); $ttfbs = @(); $codes = @(); $ok = 0
    for ($i = 0; $i -lt 3; $i++) {
      if ($mode -eq '渡口') { $r = Curl-Time $url -Borrow } else { $r = Curl-Time $url -NoProxy }
      if ($r) { $codes += $r.Code; $totals += $r.Total; $ttfbs += $r.Ttfb; if ($r.Code -match '^[23]') { $ok++ } }
      Start-Sleep -Milliseconds 200
    }
    $st = Stat $totals
    $stt = Stat $ttfbs
    $cstr = ($codes -join '/')
    if ($st) {
      L ("  {0,-10} {1,-4} code={2,-10} ok={3}/3 total_med={4:N3}s ttfb_med={5:N3}s" -f $name, $mode, $cstr, $ok, $st.P50, $stt.P50)
    } else {
      L ("  {0,-10} {1,-4} 全部失败" -f $name, $mode)
    }
    $tp = $null; $tp2 = $null
    if ($st) { $tp = $st.P50 }
    if ($stt) { $tp2 = $stt.P50 }
    $rows += [pscustomobject]@{ Target = $name; Mode = $mode; Code = $cstr; Ok = $ok; TotalP50 = $tp; TtfbP50 = $tp2 }
  }
}

# ---------- 2. 抖动与丢包（连打 20 次小请求）----------
L ''
L '========== 2. 抖动与丢包（20 次连续请求）=========='
foreach ($mode in @('直连', '渡口')) {
  $totals = @(); $fail = 0
  for ($i = 0; $i -lt 20; $i++) {
    if ($mode -eq '渡口') { $r = Curl-Time 'http://www.baidu.com' -Borrow } else { $r = Curl-Time 'http://www.baidu.com' -NoProxy }
    if ($r -and $r.Code -match '^[23]') { $totals += $r.Total } else { $fail++ }
  }
  $st = Stat $totals
  if ($st) {
    $jitter = [Math]::Round($st.Max - $st.Min, 3)
    L ("  {0}: 成功 {1}/20 失败 {2}  min={3:N3} p50={4:N3} p95={5:N3} max={6:N3} 抖动={7:N3}s" -f $mode, $st.N, $fail, $st.Min, $st.P50, $st.P95, $st.Max, $jitter)
  } else { L "  ${mode}: 全部失败" }
}

# ---------- 3. DNS 解析路径 ----------
L ''
L '========== 3. DNS 解析（渡口是否走远端解析）=========='
$dnsTargets = @('www.baidu.com', 'github.com', 'www.google.com', 'nonexistent-abcxyz-12345.com')
foreach ($d in $dnsTargets) {
  $a = & curl.exe -s -o NUL -w '%{http_code} %{time_namelookup}' --proxy $socks --max-time 12 "http://$d" 2>$null
  $b = & curl.exe -s -o NUL -w '%{http_code} %{time_namelookup}' --noproxy '*' --max-time 12 "http://$d" 2>$null
  L ("  {0,-32} 渡口[{1}]  直连[{2}]" -f $d, $a, $b)
}

# ---------- 4. 吞吐（下载 3MB）----------
L ''
L '========== 4. 吞吐（下载 3MB 测试文件）=========='
$dlUrl = 'https://speed.cloudflare.com/__down?bytes=3000000'
foreach ($mode in @('直连', '渡口')) {
  $a = @('-s', '-o', 'NUL', '-w', '%{http_code} %{time_total} %{speed_download}', '--max-time', '40')
  if ($mode -eq '渡口') { $a += '--proxy', $socks } else { $a += '--noproxy', '*' }
  $a += $dlUrl
  $out = & curl.exe @a 2>$null
  if ($out) {
    $p = $out -split '\s+'
    if ($p.Count -ge 3) {
      $mbps = [Math]::Round([double]$p[2] * 8 / 1MB, 2)
      L ("  {0}: code={1} 耗时={2}s 速度={3} MB/s ({4} Mbps)" -f $mode, $p[0], $p[1], [Math]::Round([double]$p[2]/1MB,2), $mbps)
    } else { L "  ${mode}: 无输出" }
  } else { L "  ${mode}: 失败" }
}

# ---------- 5. 长连接保持（30s 内持续请求）----------
L ''
L '========== 5. 长连接稳定性（60s 内每 3s 一次，看有无中途断）=========='
$sw = [Diagnostics.Stopwatch]::StartNew()
$okN = 0; $failN = 0; $seq = @()
while ($sw.Elapsed.TotalSeconds -lt 60) {
  $r = Curl-Time 'http://www.baidu.com' -Borrow
  if ($r -and $r.Code -match '^[23]') { $okN++; $seq += '.' } else { $failN++; $seq += 'X' }
  Start-Sleep -Seconds 3
}
$sw.Stop()
L ("  60s 内: 成功 {0} 失败 {1}  序列: {2}" -f $okN, $failN, ($seq -join ''))
L ("  期间失败率 = {0:N1}%" -f (100.0 * $failN / [Math]::Max(1, $okN + $failN)))

# ---------- 6. 隧道本身健康度 ----------
L ''
L '========== 6. 隧道健康度 =========='
$kp = Get-CimInstance Win32_Process -Filter "Name='powershell.exe' or Name='pwsh.exe'" -EA SilentlyContinue |
  Where-Object { $_.CommandLine -like '*ssh-tunnel-keeper.ps1*' }
if ($kp) { L "  keeper 进程: PID $($kp.ProcessId -join ',')" } else { L '  keeper 进程: 未运行' }
$sshP = Get-CimInstance Win32_Process -Filter "Name='ssh.exe'" -EA SilentlyContinue
if ($sshP) {
  foreach ($s in $sshP) {
    $age = [int]((Get-Date) - $s.CreationDate).TotalMinutes
    L "  ssh 进程: PID $($s.ProcessId) 已存活 $age 分钟"
  }
} else { L '  ssh 进程: 无' }
$listen = Get-NetTCPConnection -LocalPort $SocksPort -State Listen -EA SilentlyContinue
$lis = '否'
if ($listen) { $lis = '是' }
L "  SOCKS 监听: $lis"

# ---------- 7. 远端视角 ----------
L ''
L '========== 7. 远端 wg0 与转发状态 =========='
$enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes(
  'Write-Output "--- wg show ---"; & "C:\Program Files\WireGuard\wg.exe" show 2>&1 | Out-String -Width 200; Write-Output "--- 接口转发 ---"; Get-NetIPInterface -AddressFamily IPv4 | Where-Object { $_.Forwarding -eq "Enabled" } | Select-Object -First 8 InterfaceAlias,Forwarding | Format-Table -AutoSize | Out-String -Width 120'))
$remote = ssh -o ConnectTimeout=8 pcs "powershell -NoProfile -EncodedCommand $enc" 2>&1
$remote | ForEach-Object { L "  $_" }

L ''
L '=== net-quality 结束 ==='

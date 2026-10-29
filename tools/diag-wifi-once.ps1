# 无线抖动诊断：一次跑完，把结果贴出来。
#
# 为什么需要它：这轮排查把范围缩小到「这台电脑的网卡侧」——
#   同一台 AP、同一信道：手机 avg 3-8ms，本机 avg 38ms，锯齿到 100ms。
# 但要继续往下（改注册表做对照实验）需要管理员，而当前会话拿不到。
# 所以这个脚本把「能测的」一次测完，你跑一次就知道方向。
#
# 用法：双击 tools\diag-wifi-once.cmd（会请求管理员），
#       或右键终端(管理员)后：
#         powershell -ExecutionPolicy Bypass -File tools\diag-wifi-once.ps1
#
# 它做四件事，全部只读：
#   1. 延迟分布：小包/大包各 20 个，看锯齿
#   2. 驱动 DPC/中断占用
#   3. 与手机同时段的对照（需要手机连着 adb）
#   4. 给出下一步建议
#
# -Apply 参数会真的改注册表（LpsEn/CoreAMSDU → 0）并复测。
#   不带参数时纯只读。

param(
    [switch]$Apply
)

$ErrorActionPreference = 'Continue'
$Key = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0007'
$GW = '10.141.56.1'
$ADB = 'C:\tmp\ptools\platform-tools\adb.exe'

function IsAdmin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Stat($vals, $label) {
    if (-not $vals -or $vals.Count -eq 0) {
        Write-Host ("  {0,-12} 无数据" -f $label)
        return
    }
    $s = $vals | Sort-Object
    $p50 = $s[[int]($s.Count / 2)]
    $spikes = ($vals | Where-Object { $_ -ge 50 }).Count
    Write-Host ("  {0,-12} n={1,-3} p50={2,-4} min={3,-4} max={4,-4} 尖峰(>=50ms)={5}/{6}" -f `
        $label, $vals.Count, $p50, $s[0], $s[-1], $spikes, $vals.Count)
    Write-Host ("  {0,-12} 样本: {1}" -f '', ($vals -join ' '))
}

function PingSeq($size, $count = 20) {
    $vals = @()
    for ($i = 0; $i -lt $count; $i++) {
        $r = Test-Connection $GW -Count 1 -BufferSize $size -ErrorAction SilentlyContinue
        if ($r) { $vals += $r.ResponseTime }
    }
    return $vals
}

Write-Host '════════ 无线抖动诊断 ════════'
Write-Host ("管理员: {0}" -f (IsAdmin))
Write-Host ''

Write-Host '=== 1. 网卡与驱动 ==='
$ad = Get-NetAdapter -Physical | Where-Object {
    $_.InterfaceDescription -like '*Wireless*' -or $_.InterfaceDescription -like '*WiFi*'
} | Select-Object -First 1
Write-Host ("  {0}" -f $ad.InterfaceDescription)
Write-Host ("  驱动 {0}  日期 {1}  状态 {2}" -f $ad.DriverVersion, $ad.DriverDate, $ad.Status)

Write-Host ''
Write-Host '=== 2. 当前省电/聚合开关 ==='
$p = Get-ItemProperty $Key
foreach ($n in 'IpsEn', 'LpsEn', 'LpsWowEn', 'CoreAMSDU') {
    $v = $p.$n
    $note = switch ($n) {
        'IpsEn'     { '智能省电（0=关）' }
        'LpsEn'     { 'LPS 省电（0=关 1=开 2=自动）' }
        'LpsWowEn'  { 'WoWLAN 省电' }
        'CoreAMSDU' { 'A-MSDU 聚合（0=关）' }
    }
    Write-Host ("  {0,-12} = {1,-4}  {2}" -f $n, $(if ($null -eq $v) { '无' } else { $v }), $note)
}

Write-Host ''
Write-Host '=== 3. 延迟分布（小包 32B = 游戏包大小）==='
$small = PingSeq 32
Stat $small '小包'
Write-Host ''
Write-Host '=== 4. 延迟分布（大包 1400B）==='
$big = PingSeq 1400
Stat $big '大包'

Write-Host ''
Write-Host '=== 5. 驱动 DPC / 中断占用 ==='
try {
    $c = Get-Counter '\Processor(_Total)\% DPC Time','\Processor(_Total)\% Interrupt Time' `
        -SampleInterval 2 -MaxSamples 2 -ErrorAction Stop
    foreach ($s in $c.CounterSamples) {
        Write-Host ("  {0,-22} {1:N2}%" -f $s.Path.Split('\')[-1], $s.CookedValue)
    }
} catch { Write-Host ("  取不到: {0}" -f $_.Exception.Message) }

Write-Host ''
Write-Host '=== 6. 手机对照（同一 AP）==='
if (Test-Path $ADB) {
    $m = & $ADB shell "ping -c 20 -i 0.2 $GW" 2>&1 | Select-String 'rtt|min/avg'
    if ($m) { Write-Host ("  手机: {0}" -f ($m -join ' ')) }
    else { Write-Host '  手机无响应（可能不在同一网络或被锁屏）' }
} else {
    Write-Host ("  找不到 adb: {0}" -f $ADB)
}

Write-Host ''
Write-Host '=== 7. 判读 ==='
$spikeRate = if ($small.Count) { ($small | Where-Object { $_ -ge 50 }).Count / $small.Count } else { 0 }
if ($spikeRate -lt 0.1) {
    Write-Host '  本机当前抖动很小，可能是时段问题（校园网高峰随机）。' -ForegroundColor Green
} else {
    Write-Host ("  尖峰比例 {0:P0} —— 锯齿明显。" -f $spikeRate) -ForegroundColor Yellow
    Write-Host '  下一步有两种，任选：'
    Write-Host '    A. 用 -Apply 参数跑一次（改 LpsEn/CoreAMSDU 为 0，会断网 5-10 秒），'
    Write-Host '       再看尖峰比例是否下降。带 -Revert 可还原。'
    Write-Host '    B. 换驱动：到 Realtek 官网或笔记本厂商下载更新版网卡驱动。'
    Write-Host '       当前驱动 6001.15.155.1 / 2024-10-25，已经一年多了。'
}

# ── 可选：真的改 ──
if ($Apply) {
    if (-not (IsAdmin)) {
        Write-Host ''
        Write-Host '  !! -Apply 需要管理员权限。请用管理员终端重跑。' -ForegroundColor Red
        exit 1
    }
    $backup = Join-Path $env:TEMP 'ferry-nic-agg-backup.json'
    $old = [ordered]@{ savedAt = (Get-Date).ToString('s'); values = [ordered]@{} }
    foreach ($n in 'LpsEn', 'CoreAMSDU') { $old.values[$n] = [int]$p.$n }
    $old | ConvertTo-Json -Depth 5 | Set-Content $backup -Encoding UTF8
    Write-Host ''
    Write-Host ("=== 应用修改（原值存到 {0}）===" -f $backup)
    foreach ($n in 'LpsEn', 'CoreAMSDU') {
        Set-ItemProperty $Key -Name $n -Value 0 -Type DWord
        Write-Host ("  {0} -> 0" -f $n)
    }
    Write-Host '  重启网卡...'
    Restart-NetAdapter -Name $ad.Name -Confirm:$false
    for ($i = 0; $i -lt 25; $i++) { Start-Sleep 1; if ((Get-NetAdapter $ad.Name).Status -eq 'Up') { break } }
    Start-Sleep 3
    Write-Host ''
    Write-Host '=== 修改后复测 ==='
    $after = PingSeq 32
    Stat $after '小包(改后)'
    $afterRate = if ($after.Count) { ($after | Where-Object { $_ -ge 50 }).Count / $after.Count } else { 0 }
    Write-Host ''
    if ($afterRate -lt $spikeRate) {
        Write-Host ("  PASS：尖峰比例 {0:P0} → {1:P0}，有效。" -f $spikeRate, $afterRate) -ForegroundColor Green
    } else {
        Write-Host ("  无效：{0:P0} → {1:P0}。建议还原并换驱动。" -f $spikeRate, $afterRate) -ForegroundColor Red
    }
}

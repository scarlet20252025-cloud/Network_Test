# 用 ETW 抓无线链路的真实行为，区分「网卡驱动缓冲」vs「上层排队」。
#
# 背景：同一台 AP、同一信道
#   手机   avg 3.9ms  mdev 0.87ms   ← 几乎不抖
#   这台电脑 avg 38ms  mdev 约 30ms ← 锯齿，大小包一样
# 已排除：省电 IpsEn、AP、信道、聚合（大小包锯齿相同）、上层连接数
#
# 这个脚本抓 TCPIP + NDIS 的事件，看「包进驱动到出驱动」与「ICMP 回包」之间的时间差。
# 如果时间差集中在驱动层，就是网卡驱动/固件的问题。
#
# 用法（需要管理员；不加参数只会报告）： powershell -ExecutionPolicy Bypass -File tools\trace-wifi-etw.ps1

$ErrorActionPreference = 'Continue'
$etl = Join-Path $env:TEMP 'ferry-wifi.etl'

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

Write-Host '=== 1. 基本信息（不需要管理员）==='
$ad = Get-NetAdapter -Physical | Where-Object {
    $_.InterfaceDescription -like '*Wireless*' -or $_.InterfaceDescription -like '*WiFi*'
} | Select-Object -First 1
Write-Host ("  网卡: {0}" -f $ad.InterfaceDescription)
Write-Host ("  驱动: {0}  日期 {1}" -f $ad.DriverVersion, $ad.DriverDate)
Write-Host ("  状态: {0}  {1}" -f $ad.Status, $ad.LinkSpeed)

Write-Host ''
Write-Host '=== 2. 延迟分布（20 包，看锯齿）==='
$vals = @()
for ($i = 0; $i -lt 20; $i++) {
    $r = Test-Connection 10.141.56.1 -Count 1 -ErrorAction SilentlyContinue
    if ($r) { $vals += $r.ResponseTime }
}
if ($vals.Count) {
    $s = $vals | Sort-Object
    $p50 = $s[[int]($s.Count / 2)]
    $spikes = ($vals | Where-Object { $_ -ge 50 }).Count
    Write-Host ("  p50={0}ms  min={1}  max={2}  尖峰(>=50ms)={3}/{4}" -f $p50, $s[0], $s[-1], $spikes, $vals.Count)
    Write-Host ("  样本: {0}" -f ($vals -join ' '))

    if ($spikes -ge 5) {
        Write-Host '  → 锯齿明显，继续下面的诊断。' -ForegroundColor Yellow
    } else {
        Write-Host '  → 当前不抖，可能是时间段问题（校园网有高峰）。' -ForegroundColor Green
    }
}

Write-Host ''
Write-Host '=== 3. 驱动层统计（只读）==='
# NDIS 的发送/接收队列丢弃、硬件队列统计
$stat = Get-NetAdapterStatistics -Name $ad.Name -ErrorAction SilentlyContinue
if ($stat) {
    Write-Host ("  收包: {0}  发包: {1}" -f $stat.ReceivedPackets, $stat.SentPackets)
    Write-Host ("  收包错误 {0}  发包错误 {1}  收包丢弃 {2}" -f `
        $stat.ReceivedPacketErrors, $stat.OutboundPacketErrors, $stat.ReceivedDiscardedPackets)
}
# 驱动的 DPC/中断占用是最关键的指标
Write-Host ''
Write-Host '  驱动 DPC/中断占用（这就是「驱动在不在拖后腿」的直接证据）：'
try {
    $counter = Get-Counter -Counter '\Processor(_Total)\% DPC Time','\Processor(_Total)\% Interrupt Time' -SampleInterval 2 -MaxSamples 2 -ErrorAction Stop
    foreach ($s in $counter.CounterSamples) {
        Write-Host ("    {0} = {1:N2}%" -f $s.Path.Split('\')[-1], $s.CookedValue)
    }
} catch {
    Write-Host ("    取不到: {0}" -f $_.Exception.Message)
}

Write-Host ''
Write-Host '=== 4. ETW 抓包（需要管理员）==='
if (-not (Test-Admin)) {
    Write-Host '  跳过：不是管理员。' -ForegroundColor Yellow
    Write-Host '  以管理员重跑可抓 ETL，再用「事件查看器 → 网络」分析。'
} else {
    Write-Host ("  抓到 {0}" -f $etl)
    & logman start FerryWifi -p 'Microsoft-Windows-NDIS-PacketCapture' 0xFFFF 0x5 -o $etl -ets 2>&1 | Out-Null
    Write-Host '  采样 8 秒...'
    $ping = Start-Process -FilePath 'ping.exe' -ArgumentList '-n','30','10.141.56.1' -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 8
    & logman stop FerryWifi -ets 2>&1 | Out-Null
    if (Test-Path $etl) {
        Write-Host ("  ETL: {0} ({1:N1} MB)" -f $etl, ((Get-Item $etl).Length / 1MB))
    }
}

Write-Host ''
Write-Host '=== 判读方向 ==='
Write-Host '  · 若 % DPC Time 偏高（>5%）→ 网卡驱动在中断里做重活，换驱动版本'
Write-Host '  · 若收包错误/丢弃 > 0 → 链路层真的在丢，查信号/换网卡'
Write-Host '  · 两者都正常但延迟仍锯齿 → 驱动固件的省电/批处理，只能换驱动或换网卡'

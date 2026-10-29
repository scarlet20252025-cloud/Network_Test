# 关闭无线网卡的「帧聚合」与「LPS 省电」——针对锯齿状延迟抖动。
#
# 为什么：同一台 AP、同一信道，手机 avg 5.2ms，这台电脑 avg 38ms，
# 且呈「爬到 70-79 再掉回 3」的锯齿。这是排队/聚合等待的特征，不是链路差。
# 与延迟直接相关的两个开关还开着：
#     LpsEn     = 2   LEP/LPS 省电（自动）
#     CoreAMSDU = 1   A-MSDU 帧聚合
# 游戏是小包高频流量，聚合对它没有收益，只会让包攒着一起发 → 尾部延迟变长。
#
# 用法（需要管理员，会断网 5-10 秒）：
#   powershell -ExecutionPolicy Bypass -File tools\fix-wifi-aggregation.ps1 -WhatIf
#   powershell -ExecutionPolicy Bypass -File tools\fix-wifi-aggregation.ps1
#   powershell -ExecutionPolicy Bypass -File tools\fix-wifi-aggregation.ps1 -Revert
#
# 改前会把原值存到 %TEMP%\ferry-nic-agg-backup.json，可用 -Revert 一键还原。
param(
    [switch]$WhatIf,
    [switch]$Revert
)

$ErrorActionPreference = 'Stop'
$Key = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0007'
$Backup = Join-Path $env:TEMP 'ferry-nic-agg-backup.json'

# 要改的项：名字 -> 目标值。只动这几个，别的都不碰。
$Targets = [ordered]@{
    'LpsEn'     = 0     # 关闭 LPS 省电
    'CoreAMSDU' = 0     # 关闭 A-MSDU 聚合
}

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function MesureJitter([string]$target = '10.141.56.1') {
    $vals = @()
    for ($i = 0; $i -lt 20; $i++) {
        $r = Test-Connection -ComputerName $target -Count 1 -ErrorAction SilentlyContinue
        if ($r) { $vals += $r.ResponseTime }
    }
    if ($vals.Count -eq 0) { return $null }
    $s = $vals | Sort-Object
    [pscustomobject]@{
        P50 = $s[[int]($s.Count / 2)]
        Min = $s[0]
        Max = $s[-1]
        SpikeCount = ($vals | Where-Object { $_ -ge 50 }).Count
        Raw = ($vals -join ' ')
    }
}

Write-Host '=== 当前值 ==='
$cur = Get-ItemProperty $Key
foreach ($name in $Targets.Keys) {
    $v = $cur.$name
    Write-Host ("  {0,-12} = {1}  →  目标 {2}" -f $name, $(if ($null -eq $v) { '(不存在)' } else { $v }), $Targets[$name])
}

Write-Host ''
Write-Host '=== 改前延迟基线 ==='
$before = MesureJitter
if ($before) {
    Write-Host ("  p50={0}ms  min={1}  max={2}  >=50ms 的次数 {3}/20" -f `
        $before.P50, $before.Min, $before.Max, $before.SpikeCount)
    Write-Host ("  样本: {0}" -f $before.Raw)
}

if ($WhatIf) {
    Write-Host ''
    Write-Host 'WhatIf：只报告，不做任何修改。' -ForegroundColor Yellow
    exit 0
}

if (-not (Test-Admin)) {
    Write-Host ''
    Write-Host '  !! 需要管理员权限（改 HKLM）。请用管理员 PowerShell 运行。' -ForegroundColor Red
    exit 1
}

if ($Revert) {
    if (-not (Test-Path $Backup)) {
        Write-Host '  找不到备份文件，无法还原：' $Backup -ForegroundColor Red
        exit 1
    }
    $old = Get-Content $Backup -Raw | ConvertFrom-Json
    foreach ($name in $old.values.PSObject.Properties.Name) {
        Set-ItemProperty -Path $Key -Name $name -Value $old.values.$name -Type DWord
        Write-Host ("  还原 {0} = {1}" -f $name, $old.values.$name)
    }
} else {
    Write-Host ''
    Write-Host '=== 存档原值 ==='
    $save = [ordered]@{ savedAt = (Get-Date).ToString('s'); values = [ordered]@{} }
    foreach ($name in $Targets.Keys) {
        $save.values[$name] = if ($null -eq $cur.$name) { -1 } else { [int]$cur.$name }
    }
    $save | ConvertTo-Json -Depth 5 | Set-Content $Backup -Encoding UTF8
    Write-Host ("  {0}" -f $Backup)

    Write-Host ''
    Write-Host '=== 写入 ==='
    foreach ($name in $Targets.Keys) {
        Set-ItemProperty -Path $Key -Name $name -Value $Targets[$name] -Type DWord
        $nv = (Get-ItemProperty $Key).$name
        Write-Host ("  {0} -> {1}" -f $name, $nv)
    }
}

Write-Host ''
Write-Host '=== 重启网卡（断网 5-10 秒）==='
$ad = Get-NetAdapter -Physical | Where-Object { $_.InterfaceDescription -like '*Wireless*' -or $_.InterfaceDescription -like '*WiFi*' } | Select-Object -First 1
Write-Host ("  网卡: {0}" -f $ad.Name)
Restart-NetAdapter -Name $ad.Name -Confirm:$false
for ($i = 0; $i -lt 25; $i++) {
    Start-Sleep -Seconds 1
    if ((Get-NetAdapter -Name $ad.Name).Status -eq 'Up') { break }
}
Write-Host ("  状态: {0}" -f (Get-NetAdapter -Name $ad.Name).Status)

Start-Sleep -Seconds 3
Write-Host ''
Write-Host '=== 改后延迟 ==='
$after = MesureJitter
if ($after) {
    Write-Host ("  p50={0}ms  min={1}  max={2}  >=50ms 的次数 {3}/20" -f `
        $after.P50, $after.Min, $after.Max, $after.SpikeCount)
    Write-Host ("  样本: {0}" -f $after.Raw)
}

Write-Host ''
Write-Host '=== 判读 ==='
if ($before -and $after) {
    if ($after.SpikeCount -lt $before.SpikeCount) {
        Write-Host ("  PASS：>=50ms 的次数 {0} → {1}，尖峰减少了。" -f $before.SpikeCount, $after.SpikeCount) -ForegroundColor Green
    } elseif ($after.SpikeCount -eq $before.SpikeCount) {
        Write-Host '  没有变化：聚合不是主因，需要换方向（驱动版本 / 换网卡）。' -ForegroundColor Yellow
    } else {
        Write-Host '  变差了，建议还原：加 -Revert 再跑一次。' -ForegroundColor Red
    }
}
Write-Host ''
Write-Host "还原： powershell -ExecutionPolicy Bypass -File tools\fix-wifi-aggregation.ps1 -Revert"

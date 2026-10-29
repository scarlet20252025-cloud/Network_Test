# 关闭 Realtek 8852BE 无线网卡的省电休眠（IpsEn: 1 -> 0）
#
# 为什么：实测连续发包 p50=3ms，但按 120ms 间隔（游戏发包节奏）发就交替出现
# 50-63ms —— 撞上信标周期（102.4ms）的休眠窗口。IpsEn=1 表示省电模式开启。
# 详见 borrow-net/TASKS.md 与 docs/热点与游戏延迟-20261029.md。
#
# 这个脚本会：
#   1. 存档原值到 %TEMP%\ferry-nic-powersave-backup.json
#   2. 把 IpsEn 改成 0（幂等：已经是 0 就跳过写）
#   3. 重启网卡使设置生效（会断网几秒）
#   4. 复测 120ms 间隔的延迟，打印判读
#
# 用法（需要管理员）：
#   powershell -ExecutionPolicy Bypass -File tools\fix-nic-powersave.ps1
#   powershell -ExecutionPolicy Bypass -File tools\fix-nic-powersave.ps1 -WhatIf   # 只看计划
#   powershell -ExecutionPolicy Bypass -File tools\fix-nic-powersave.ps1 -Revert   # 还原
param(
    [switch]$WhatIf,
    [switch]$Revert
)

$ErrorActionPreference = 'Stop'
$Base = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}'
$BackupFile = Join-Path $env:TEMP 'ferry-nic-powersave-backup.json'

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-WifiAdapter {
    Get-NetAdapter -Physical | Where-Object {
        $_.InterfaceDescription -like '*Wireless*' -or $_.InterfaceDescription -like '*WiFi*'
    } | Select-Object -First 1
}

function Get-NicKey($adapter) {
    # 按 DriverDesc 找类注册表子键（不带反斜杠的那个）
    $desc = $adapter.InterfaceDescription
    Get-ChildItem $Base -ErrorAction SilentlyContinue | Where-Object {
        (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DriverDesc -eq $desc
    } | Select-Object -First 1
}

function Measure-Jitter($target) {
    # 连续发包 vs 120ms 间隔，判断休眠窗口是否还在
    $burst = 1..10 | ForEach-Object {
        (Test-Connection -ComputerName $target -Count 1 -ErrorAction SilentlyContinue).ResponseTime
    } | Where-Object { $_ -ne $null } | Sort-Object
    $paced = 1..10 | ForEach-Object {
        $r = (Test-Connection -ComputerName $target -Count 1 -ErrorAction SilentlyContinue).ResponseTime
        Start-Sleep -Milliseconds 120
        $r
    } | Where-Object { $_ -ne $null } | Sort-Object
    $b = if ($burst.Count) { $burst[[int]($burst.Count / 2)] } else { -1 }
    $p = if ($paced.Count) { $paced[[int]($paced.Count / 2)] } else { -1 }
    [pscustomobject]@{ BurstP50 = $b; PacedP50 = $p; PacedMax = if ($paced.Count) { $paced[-1] } else { -1 } }
}

Write-Host '=== 1. 环境 ==='
if (-not (Test-Admin)) {
    Write-Host '  !! 不是管理员。改 HKLM 需要提权。' -ForegroundColor Red
    Write-Host '     请用管理员 PowerShell 运行本脚本，或在 DSH 里以管理员身份重开。'
    exit 1
}
$ad = Get-WifiAdapter
if (-not $ad) { Write-Host '  !! 找不到无线网卡' -ForegroundColor Red; exit 1 }
$key = Get-NicKey $ad
if (-not $key) { Write-Host "  !! 找不到 $($ad.InterfaceDescription) 的注册表子键" -ForegroundColor Red; exit 1 }
$keyPath = $key.PSPath
Write-Host "  网卡 : $($ad.InterfaceDescription)"
Write-Host "  状态 : $($ad.Status)  $($ad.LinkSpeed)"
Write-Host "  键   : $($keyPath)"

$cur = (Get-ItemProperty $keyPath).IpsEn
Write-Host "  IpsEn: $cur   (1=省电开启，0=关闭)"

if ($WhatIf) {
    Write-Host ''
    Write-Host '=== WhatIf：只报告计划，不做任何修改 ===' -ForegroundColor Yellow
    $m = Measure-Jitter '10.141.56.1'
    Write-Host "  复测：连续 p50=$($m.BurstP50)ms / 120ms 间隔 p50=$($m.PacedP50)ms"
    exit 0
}

Write-Host ''
Write-Host '=== 2. 改前存档 ==='
$bak = [ordered]@{
    savedAt   = (Get-Date).ToString('s')
    adapter   = $ad.InterfaceDescription
    keyPath   = $keyPath
    IpsEn     = $cur
}
$bak | ConvertTo-Json | Set-Content -Path $BackupFile -Encoding UTF8
Write-Host "  已存: $BackupFile"
$before = Measure-Jitter '10.141.56.1'
Write-Host "  改前延迟：连续 p50=$($before.BurstP50)ms / 120ms 间隔 p50=$($before.PacedP50)ms"

if ($Revert) {
    Write-Host ''
    Write-Host '=== Revert：还原 ==='
    $old = Get-Content $BackupFile -Raw | ConvertFrom-Json
    Set-ItemProperty -Path $old.keyPath -Name 'IpsEn' -Value $old.IpsEn -Type DWord
    Write-Host "  IpsEn 还原为 $($old.IpsEn)"
} else {
    if ($cur -eq 0) {
        Write-Host ''
        Write-Host '  IpsEn 已经是 0，不重复写（但仍会重启网卡让设置确保生效）'
    } else {
        Write-Host ''
        Write-Host '=== 3. 写注册表 ==='
        Set-ItemProperty -Path $keyPath -Name 'IpsEn' -Value 0 -Type DWord
        $chk = (Get-ItemProperty $keyPath).IpsEn
        Write-Host "  IpsEn -> $chk"
    }
}

Write-Host ''
Write-Host '=== 4. 重启网卡（会断网几秒）==='
Restart-NetAdapter -Name $ad.Name -Confirm:$false
Start-Sleep -Seconds 8
for ($i = 0; $i -lt 20; $i++) {
    $s = (Get-NetAdapter -Name $ad.Name).Status
    if ($s -eq 'Up') { break }
    Start-Sleep -Seconds 1
}
$ad2 = Get-NetAdapter -Name $ad.Name
Write-Host "  网卡状态: $($ad2.Status)  $($ad2.LinkSpeed)"

Write-Host ''
Write-Host '=== 5. 复测 ==='
$after = Measure-Jitter '10.141.56.1'
Write-Host "  改后延迟：连续 p50=$($after.BurstP50)ms / 120ms 间隔 p50=$($after.PacedP50)ms（最高 $($after.PacedMax)ms）"

Write-Host ''
Write-Host '=== 判读 ==='
$delta = $after.PacedP50 - $after.BurstP50
if ($after.PacedP50 -le 10 -and $delta -le 8) {
    Write-Host '  PASS：120ms 间隔的 p50 已回到 10ms 以内，休眠窗口消失。' -ForegroundColor Green
} elseif ($delta -lt ($before.PacedP50 - $before.BurstP50)) {
    Write-Host "  仍有效果但不彻底：间隔差从 $(($before.PacedP50 - $before.BurstP50))ms 降到 ${delta}ms。" -ForegroundColor Yellow
    Write-Host '  剩下那点可能是驱动自己的节能（PowerSaveMode）或信道拥塞。'
} else {
    Write-Host '  没改善：说明卡顿不来自网卡休眠，要看信道/上游。' -ForegroundColor Yellow
}
Write-Host ''
Write-Host "还原命令： powershell -ExecutionPolicy Bypass -File tools\fix-nic-powersave.ps1 -Revert"

# 查电源计划里的「无线适配器设置」——这是与注册表 IpsEn 完全独立的另一套省电开关。
#
# 背景：同一台 AP、同一信道：
#   手机 → 网关  avg 2.71ms  mdev 0.85ms   ← 一条直线
#   电脑 → 网关  38 3 75 2 67 8 57 32      ← 随机跳变 2-75ms
# 注册表侧已查：IpsEn=0、LpsEn=2、无 TWT、DPC 正常、不丢包。
# 电源计划里还有一个 SUB_WIFI（无线适配器省电模式）没查过：
#   0 = 最高性能（从不省电）
#   1 = 低省电
#   2 = 中等省电（默认）
#   3 = 最高省电
# 如果它是 2 或 3，就是「随机跳变」的合理来源。
#
# 用法（需要管理员才能改，只读查询不需要）：
#   powershell -ExecutionPolicy Bypass -File tools\check-power-wifi.ps1
#   powershell -ExecutionPolicy Bypass -File tools\check-power-wifi.ps1 -SetMaxPerformance

param([switch]$SetMaxPerformance)

$ErrorActionPreference = 'Continue'

# 无线适配器设置的 GUID
$SubWifi = '19cbb8fa-5279-450e-9fac-8a3d5fedd0c1'
$SettingWifi = '12bbebe6-58d6-4636-95bb-3217ef867c1a'

function IsAdmin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

Write-Host '=== 1. 当前电源计划 ==='
$scheme = (powercfg /getactivescheme) -join ' '
Write-Host ("  {0}" -f $scheme)

Write-Host ''
Write-Host '=== 2. 无线适配器省电设置 ==='
$q = (powercfg /query SCHEME_CURRENT $SubWifi $SettingWifi) -join "`n"
# 输出里 AC/DC 各有「当前交流电源设置索引」「当前直流电源设置索引」
$ac = [regex]::Match($q, '(?:当前交流电源设置索引|Current AC Power Setting Index)[^:]*:\s*(0x[0-9a-fA-F]+|\d+)')
$dc = [regex]::Match($q, '(?:当前直流电源设置索引|Current DC Power Setting Index)[^:]*:\s*(0x[0-9a-fA-F]+|\d+)')

function Val($m) {
    if (-not $m.Success) { return $null }
    $v = $m.Groups[1].Value
    if ($v.StartsWith('0x')) { return [Convert]::ToInt32($v.Substring(2), 16) }
    return [int]$v
}

$acV = Val $ac
$dcV = Val $dc
$names = @{ 0 = '最高性能（从不省电）'; 1 = '低省电'; 2 = '中等省电（默认）'; 3 = '最高省电' }

if ($null -ne $acV) {
    Write-Host ("  接电源时: {0}  ({1})" -f $acV, $names[$acV])
} else {
    Write-Host '  接电源时: 取不到'
}
if ($null -ne $dcV) {
    Write-Host ("  用电池时: {0}  ({1})" -f $dcV, $names[$dcV])
} else {
    Write-Host '  用电池时: 取不到'
}

Write-Host ''
Write-Host '=== 3. 判读 ==='
$bad = ($null -ne $acV -and $acV -ge 2) -or ($null -ne $dcV -and $dcV -ge 2)
if ($bad) {
    Write-Host '  ⚠ 无线适配器处于省电模式（2 或 3）—— 这与「随机跳变 2-75ms」吻合。' -ForegroundColor Yellow
    Write-Host '    省电模式下网卡会按 DTIM 周期唤醒，包要等下一次唤醒才能发出去。'
    Write-Host ''
    Write-Host '    改法（需要管理员）：'
    Write-Host ("      powercfg /setacvalueindex SCHEME_CURRENT {0} {1} 0" -f $SubWifi, $SettingWifi)
    Write-Host ("      powercfg /setdcvalueindex SCHEME_CURRENT {0} {1} 0" -f $SubWifi, $SettingWifi)
    Write-Host '      powercfg /setactive SCHEME_CURRENT'
    Write-Host '    或者直接跑本脚本加 -SetMaxPerformance'
} else {
    Write-Host '  无线适配器已经是最高性能，不是这里的问题。' -ForegroundColor Green
    Write-Host '  → 剩下只有换驱动/换网卡一条路。'
}

if ($SetMaxPerformance) {
    if (-not (IsAdmin)) {
        Write-Host ''
        Write-Host '  !! 需要管理员权限。请用管理员终端重跑。' -ForegroundColor Red
        exit 1
    }
    Write-Host ''
    Write-Host '=== 4. 设为最高性能 ==='
    & powercfg /setacvalueindex SCHEME_CURRENT $SubWifi $SettingWifi 0
    & powercfg /setdcvalueindex SCHEME_CURRENT $SubWifi $SettingWifi 0
    & powercfg /setactive SCHEME_CURRENT
    Write-Host '  已设置。重新查询：'
    & powercfg /query SCHEME_CURRENT $SubWifi $SettingWifi | Select-String '索引|Index'
}

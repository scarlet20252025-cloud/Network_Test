# 在 xt 上以「不挂会话」的方式启动 Android 模拟器，并等它开机完成。
#
# 为什么必须走计划任务：
#   AGENTS.md 里记着一次真实事故 —— SSH 会话里用 Start-Process 起的子进程，
#   在 SSH 断开时会被 Windows 的 Job Object 一起回收。模拟器要跑几分钟，
#   而 SSH 命令早就返回了。所以必须用 schtasks（任务不挂在任何会话下）。
#
# ⚠ 第一版失败原因（2026-10-29）：Register-ScheduledTask 不带 -Principal 时，
#   默认主体是「当前用户 + 只使用交互方式（Interactive）」——
#   而 SSH 进来的是非交互会话，任务永远不会真正跑起来。
#   必须显式给 -LogonType S4U（不需要密码，也不需要有人登录）。
#
# 用法（在 xt 上）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File C:\tmp\xt-emu-start.ps1
param(
    [string]$Sdk = 'D:\AndroidSdk',
    [string]$AvdName = 'ferry_test',
    [int]$DeviceTimeoutSec = 180,
    [int]$BootTimeoutSec = 900
)

$ErrorActionPreference = 'Continue'
$taskName = 'ferry-emulator'
$emu = Join-Path $Sdk 'emulator\emulator.exe'
$adb = Join-Path $Sdk 'platform-tools\adb.exe'
$launcher = 'C:\tmp\xt-emu-launch.cmd'

if (-not (Test-Path $emu)) { Write-Output ('!! 找不到 ' + $emu); exit 1 }
if (-not (Test-Path $launcher)) { Write-Output ('!! 找不到 ' + $launcher); exit 1 }

Write-Output '=== 0. 清掉上一轮 ==='
& schtasks /end /tn $taskName 2>&1 | Out-Null
& schtasks /delete /tn $taskName /f 2>&1 | Out-Null
Get-Process qemu-system-x86_64, emulator -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Output ('  结束残留 ' + $_.Name + ' pid=' + $_.Id); try { $_.Kill() } catch {} }
Start-Sleep -Seconds 3

Write-Output '=== 1. 注册任务（S4U，不需要有人登录）==='
$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument ('/c "' + $launcher + '"')
$principal = New-ScheduledTaskPrincipal -UserId ($env:USERNAME) -LogonType S4U -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                                          -ExecutionTimeLimit (New-TimeSpan -Hours 6) `
                                          -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
$info = Get-ScheduledTask -TaskName $taskName
Write-Output ('  主体 LogonType = ' + $info.Principal.LogonType + '  RunLevel = ' + $info.Principal.RunLevel)

Write-Output '=== 2. 运行 ==='
& schtasks /run /tn $taskName 2>&1 | ForEach-Object { Write-Output ('  ' + $_) }
Start-Sleep -Seconds 10
$st = (Get-ScheduledTask -TaskName $taskName | Get-ScheduledTaskInfo).LastTaskResult
Write-Output ('  LastTaskResult = ' + $st)

Write-Output '=== 3. 等 adb 看到设备 ==='
& $adb start-server 2>&1 | Out-Null
$deadline = (Get-Date).AddSeconds($DeviceTimeoutSec)
$serial = $null
while ((Get-Date) -lt $deadline) {
    $out = (& $adb devices 2>&1 | Out-String)
    $m = [regex]::Match($out, '(emulator-\d+)\s+device')
    if ($m.Success) { $serial = $m.Groups[1].Value; break }
    Start-Sleep -Seconds 5
}
if (-not $serial) {
    Write-Output '  adb 没自动发现，尝试 adb connect 127.0.0.1:5555'
    & $adb connect 127.0.0.1:5555 2>&1 | ForEach-Object { Write-Output ('    ' + $_) }
    Start-Sleep -Seconds 3
    $out = (& $adb devices 2>&1 | Out-String)
    $m = [regex]::Match($out, '(emulator-\d+|127\.0\.0\.1:\d+)\s+device')
    if ($m.Success) { $serial = $m.Groups[1].Value }
}
if (-not $serial) {
    Write-Output '  !! 还是没设备。证据：'
    Write-Output '--- emulator-console.log ---'
    if (Test-Path 'C:\tmp\emulator-console.log') {
        Get-Content 'C:\tmp\emulator-console.log' -Tail 40 | ForEach-Object { Write-Output ('    ' + $_) }
    } else { Write-Output '    (没有日志文件 —— 包装脚本没跑起来)' }
    Write-Output '--- 任务状态 ---'
    $i = Get-ScheduledTask -TaskName $taskName | Get-ScheduledTaskInfo
    Write-Output ('    LastTaskResult = ' + $i.LastTaskResult + '  LastRunTime = ' + $i.LastRunTime)
    Get-Process qemu-system-x86_64, emulator -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Output ('    进程 ' + $_.Name + ' pid=' + $_.Id) }
    exit 1
}
Write-Output ('  设备 = ' + $serial)

Write-Output '=== 4. 等开机完成 ==='
$deadline = (Get-Date).AddSeconds($BootTimeoutSec)
$booted = $false
while ((Get-Date) -lt $deadline) {
    $bc = (& $adb -s $serial shell getprop sys.boot_completed 2>&1 | Out-String).Trim()
    if ($bc -eq '1') { $booted = $true; break }
    Start-Sleep -Seconds 10
}
Write-Output ('  sys.boot_completed = ' + $booted)

if ($booted) {
    Write-Output '=== 5. 设备信息 ==='
    foreach ($p in @('ro.build.version.release', 'ro.build.version.sdk', 'ro.product.cpu.abi')) {
        $v = (& $adb -s $serial shell getprop $p 2>&1 | Out-String).Trim()
        Write-Output ('  ' + $p + ' = ' + $v)
    }
    & $adb -s $serial shell settings put global window_animation_scale 0 2>&1 | Out-Null
    & $adb -s $serial shell settings put global transition_animation_scale 0 2>&1 | Out-Null
    & $adb -s $serial shell settings put global animator_duration_scale 0 2>&1 | Out-Null

    Write-Output '=== 6. 模拟器到渡口远端的连通性 ==='
    $t = & $adb -s $serial shell 'ping -c 2 -W 2 10.13.24.1' 2>&1 | Out-String
    Write-Output ($t.Trim() -replace "`r", '')
}

$serial | Out-File -FilePath 'C:\tmp\emu-serial.txt' -Encoding ascii
Write-Output ('=== DONE serial=' + $serial + ' ===')

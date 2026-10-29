# 最后一次尝试：用 -accel off（纯软件模拟，不碰 AEHD）。
#
# 依据：所有带 AEHD 的组合都呈现同一个特征 —— emulator 父进程活着、
# 5554/5555 在听、但**没有 qemu-system-x86_64 子进程**，adb 永远 offline。
# 也就是 qemu 子进程一启动就崩（带图形后端时父进程自己也崩，0xC0000005）。
# 说明问题出在 AEHD 2.2 与 emulator 37.2.12 的配合上（AEHD 2.2 是 2023 年的
# 版本，明显落后于 emulator 37）。
# -accel off 走 TCG 纯软件翻译，完全不加载 AEHD，因此绕开这个不兼容。
# 代价是慢（同架构翻译大约慢 5–10 倍），但能跑起来就有意义。
#
# 另外把「残留锁」一并处理：新建 AVD 也报 same AVD，说明那个判定是**全局**的，
# 不在 AVD 目录里。所以这次全盘搜 *.lock 并删掉。
#
# 用法（在 xt 上）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File C:\tmp\xt-emu-acceloff.ps1
param(
    [string]$Sdk = 'D:\AndroidSdk',
    [string]$AvdName = 'ferry2',
    [int]$BootTimeoutSec = 1800
)

$ErrorActionPreference = 'Continue'
$emu = Join-Path $Sdk 'emulator\emulator.exe'
$adb = Join-Path $Sdk 'platform-tools\adb.exe'
$env:ANDROID_SDK_ROOT = $Sdk
$env:ANDROID_HOME = $Sdk
$env:ANDROID_AVD_HOME = Join-Path $env:USERPROFILE '.android\avd'
$env:ANDROID_EMULATOR_HOME = Join-Path $env:USERPROFILE '.android'
$env:PATH = (Join-Path $Sdk 'platform-tools') + ';' + (Join-Path $Sdk 'emulator') + ';' + $env:PATH

Write-Output '=== 1. 全盘清残留 ==='
& schtasks /end /tn 'ferry-emulator' 2>&1 | Out-Null
& schtasks /delete /tn 'ferry-emulator' /f 2>&1 | Out-Null
foreach ($n in @('qemu-system-x86_64', 'emulator', 'crashpad_handler')) {
    Get-Process $n -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Output ('  杀 ' + $_.Name + ' pid=' + $_.Id); try { $_.Kill() } catch {}
    }
}
Start-Sleep -Seconds 5

Write-Output '=== 2. 找并删所有 lock ==='
$roots = @(
    (Join-Path $env:USERPROFILE '.android'),
    (Join-Path $env:TEMP 'AndroidEmulator'),
    (Join-Path $env:LOCALAPPDATA 'Temp\AndroidEmulator')
)
foreach ($r in $roots) {
    if (-not (Test-Path $r)) { continue }
    Get-ChildItem $r -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '*lock*' } |
        ForEach-Object {
            Write-Output ('  删 ' + $_.FullName)
            Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
}

Write-Output '=== 3. 看端口是否还被占 ==='
foreach ($p in @(5554, 5555)) {
    $c = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue
    Write-Output ('  :' + $p + ' = ' + $(if ($c) { '仍然 LISTEN（可能被别的进程占）' } else { 'free' }))
}

Write-Output '=== 4. 注册并启动（-accel off -gpu swiftshader_indirect）==='
$launcher = 'C:\tmp\xt-emu-acceloff.cmd'
@"
@echo off
set ANDROID_SDK_ROOT=$Sdk
set ANDROID_HOME=$Sdk
set ANDROID_AVD_HOME=%USERPROFILE%\.android\avd
set ANDROID_EMULATOR_HOME=%USERPROFILE%\.android
set PATH=$Sdk\platform-tools;$Sdk\emulator;%PATH%
cd /d $Sdk\emulator
echo === launch %DATE% %TIME% === > C:\tmp\emu-acceloff.log
emulator.exe -avd $AvdName -no-window -no-audio -no-boot-anim -no-snapshot -no-metrics -accel off -gpu swiftshader_indirect -netdelay none -netspeed full -partition-size 1024 >> C:\tmp\emu-acceloff.log 2>&1
echo === exit %ERRORLEVEL% %DATE% %TIME% === >> C:\tmp\emu-acceloff.log
"@ | Out-File -FilePath $launcher -Encoding ascii

$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument ('/c "' + $launcher + '"')
$principal = New-ScheduledTaskPrincipal -UserId ($env:USERNAME) -LogonType S4U -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                                          -ExecutionTimeLimit (New-TimeSpan -Hours 4) `
                                          -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName 'ferry-emulator' -Action $action -Principal $principal -Settings $settings -Force | Out-Null
& schtasks /run /tn 'ferry-emulator' 2>&1 | ForEach-Object { Write-Output ('  ' + $_) }

Write-Output '=== 5. 等 adb 与开机 ==='
& $adb start-server 2>&1 | Out-Null
$deadline = (Get-Date).AddSeconds($BootTimeoutSec)
$state = 'none'
$lastReport = 0
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 15
    $q = Get-Process qemu-system-x86_64 -ErrorAction SilentlyContinue
    $out = (& $adb devices 2>&1 | Out-String)
    $dev = [regex]::Match($out, '(emulator-\d+)\s+device\b')
    $elapsed = [int]((Get-Date) - $deadline.AddSeconds($BootTimeoutSec)).TotalSeconds
    if ($dev.Success) {
        $bc = (& $adb -s $dev.Groups[1].Value shell getprop sys.boot_completed 2>&1 | Out-String).Trim()
        $state = 'device(boot=' + $bc + ')'
        if ($bc -eq '1') { $state = 'booted'; break }
    } else {
        $state = 'no-device'
    }
    if (((Get-Date).TimeOfDay.TotalSeconds % 60) -lt 15) {
        Write-Output ('  t+~' + [int]((Get-Date) - $deadline.AddSeconds($BootTimeoutSec)).TotalSeconds * -1 +
                      's  qemu=' + $(if ($q) { [math]::Round($q.WorkingSet64/1MB,0).ToString() + 'MB' } else { 'none' }) +
                      '  adb=' + $state)
    }
    if (-not $q -and $state -eq 'no-device') {
        Write-Output '  !! qemu 子进程不在且看不到设备，放弃'
        break
    }
}

Write-Output ('=== 最终状态: ' + $state + ' ===')
if (Test-Path 'C:\tmp\emu-acceloff.log') {
    Write-Output '  --- 日志尾部 ---'
    Get-Content 'C:\tmp\emu-acceloff.log' -Tail 15 | ForEach-Object { Write-Output ('    ' + $_) }
}
if ($state -eq 'booted') {
    $serial = 'emulator-5554'
    Write-Output '  --- 设备信息 ---'
    foreach ($p in @('ro.build.version.release', 'ro.build.version.sdk', 'ro.product.cpu.abi')) {
        Write-Output ('    ' + $p + ' = ' + (& $adb -s $serial shell getprop $p 2>&1 | Out-String).Trim())
    }
    $serial | Out-File 'C:\tmp\emu-serial.txt' -Encoding ascii
}
Write-Output '=== ACCELOFF DONE ==='

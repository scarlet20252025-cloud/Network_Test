# 逐个试模拟器启动参数组合，找出哪一组能活到 adb 看到设备。
#
# 背景：AEHD 已确认可用（emulator -accel-check 报 "AEHD (version 2.2) is
# installed and usable."），但一启动就在 "AEHD is operational" 之后
# 以 0xC0000005 (ACCESS_VIOLATION) 崩掉，关掉 Vulkan 也没救。
# 所以把可疑维度穷举一遍，让机器告诉我们哪一组能活：
#   加速方式：aehd / off（纯软件，慢但可能稳）
#   图形后端：swiftshader_indirect / guest / off
#
# 用法（在 xt 上）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File C:\tmp\xt-emu-probe2.ps1
param(
    [string]$Sdk = 'D:\AndroidSdk',
    [int]$PerVariantSec = 100
)

$ErrorActionPreference = 'Continue'
$emu = Join-Path $Sdk 'emulator\emulator.exe'
$adb = Join-Path $Sdk 'platform-tools\adb.exe'
$env:ANDROID_SDK_ROOT = $Sdk
$env:ANDROID_HOME = $Sdk
$env:ANDROID_AVD_HOME = Join-Path $env:USERPROFILE '.android\avd'
$env:ANDROID_EMULATOR_HOME = Join-Path $env:USERPROFILE '.android'
$env:PATH = (Join-Path $Sdk 'platform-tools') + ';' + (Join-Path $Sdk 'emulator') + ';' + $env:PATH

$variants = @(
    @{ n = 'gpu=guest + aehd';        a = @('-gpu', 'guest') },
    @{ n = 'gpu=off + aehd';          a = @('-gpu', 'off') },
    @{ n = 'gpu=guest + accel off';   a = @('-gpu', 'guest', '-accel', 'off') },
    @{ n = 'gpu=swiftshader + aehd';  a = @('-gpu', 'swiftshader_indirect', '-feature', '-Vulkan') },
    @{ n = 'no gpu flag + aehd';      a = @() }
)

& $adb start-server 2>&1 | Out-Null

foreach ($v in $variants) {
    Write-Output ('════ 试：' + $v.n + ' ════')
    Get-Process qemu-system-x86_64, emulator -ErrorAction SilentlyContinue |
        ForEach-Object { try { $_.Kill() } catch {} }
    Start-Sleep -Seconds 3

    $log = 'C:\tmp\emu-try.log'
    if (Test-Path $log) { Remove-Item $log -Force }
    $base = @('-avd', 'ferry_test', '-no-window', '-no-audio', '-no-boot-anim',
              '-no-snapshot', '-no-metrics', '-partition-size', '1024')
    $all = $base + $v.a
    Write-Output ('  参数: ' + ($all -join ' '))

    # 直接 Start-Process 到文件；在这个 SSH 命令还活着的时候它不会被回收
    $p = Start-Process -FilePath $emu -ArgumentList $all -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err" -WindowStyle Hidden
    $alive = $false
    $devSeen = $false
    $deadline = (Get-Date).AddSeconds($PerVariantSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        if (-not $alive -and -not $p.HasExited) { $alive = $true }
        if ($p.HasExited) { break }
        $out = (& $adb devices 2>&1 | Out-String)
        if ($out -match 'emulator-\d+\s+device') { $devSeen = $true; break }
    }

    if ($p.HasExited) {
        Write-Output ('  结果: 进程退出，exit=' + $p.ExitCode + ' (0x' + ('{0:X8}' -f $p.ExitCode) + ')')
    } else {
        Write-Output ('  结果: 进程仍在运行（已 ' + $PerVariantSec + 's）  设备可见=' + $devSeen)
    }

    Write-Output '  --- 日志尾部 ---'
    foreach ($f in @($log, "$log.err")) {
        if (Test-Path $f) {
            Get-Content $f -Tail 12 -ErrorAction SilentlyContinue |
                ForEach-Object { Write-Output ('    ' + $_) }
        }
    }

    if (-not $p.HasExited) {
        Write-Output '  ** 这一组活着，停止穷举，保留进程 **'
        # 留着它继续跑，交给后面的脚本接管
        break
    }
    try { $p.Kill() } catch {}
    Start-Sleep -Seconds 2
}

Write-Output '=== PROBE DONE ==='

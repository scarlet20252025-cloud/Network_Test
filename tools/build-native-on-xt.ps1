# 在 xt（10.113.66.6）上编译 hev-socks5-tunnel 的 native 库
#
# 为什么远端编：NDK 编译要几个 G 的中间产物，本机 E 盘只剩 19G。
#
# 用法（在 xt 上，由 tools/build-native-remote.ps1 调用）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File C:\tmp\build-native.ps1
#
# 流程：
#   1. clone hev-socks5-tunnel（含 4 个子模块）到 D:\hev-src
#   2. ndk-build 出 libhev-socks5-tunnel.so（三个 ABI，见下面的 $Abis）
#   3. 把 .so 复制到 C:\tmp\hev-libs\<abi>\
param(
    [string]$HevRepo = 'D:\hev-src',
    [string]$Ndk = 'D:\AndroidSdk\ndk\27.0.12077973',
    [string]$HevCommit = '',
    # x86_64 是给模拟器用的（没有真机时靠它验证隧道能否起来）。
    # 打进 APK 与否由 app/build.gradle 的 abiFilters 决定：
    # debug 三个 ABI 都打，release 只打两个 arm。
    [string[]]$Abis = @('arm64-v8a', 'armeabi-v7a', 'x86_64')
)

$ErrorActionPreference = 'Continue'
$outRoot = 'C:\tmp\hev-libs'

Write-Output '=== 0. 环境 ==='
Write-Output ('  NDK = ' + $Ndk + '  exists=' + (Test-Path (Join-Path $Ndk 'ndk-build.cmd')))
if (-not (Test-Path (Join-Path $Ndk 'ndk-build.cmd'))) { Write-Output '!! NDK 不在'; exit 1 }
$env:JAVA_HOME = 'C:\Program Files\Java\jdk-21'
$env:ANDROID_HOME = 'D:\AndroidSdk'
$env:ANDROID_SDK_ROOT = 'D:\AndroidSdk'
$env:PATH = "$env:JAVA_HOME\bin;D:\AndroidSdk\platform-tools;$env:PATH"

Write-Output '=== 1. 拉源码 ==='
if (-not (Test-Path (Join-Path $HevRepo '.git'))) {
    if (Test-Path $HevRepo) { Remove-Item $HevRepo -Recurse -Force }
    # 用 HTTP/1.1 强制：本机/远端都有「HTTP/2 卡在 TLS renegotiate」的历史，
    # 症状是 git clone 挂住或直接 Failed to connect。
    & git -c http.version=HTTP/1.1 clone --recursive https://github.com/heiher/hev-socks5-tunnel.git $HevRepo 2>&1 |
        Select-Object -Last 8
} else {
    Write-Output '  已有仓库，检查子模块'
    Push-Location $HevRepo
    & git -c http.version=HTTP/1.1 submodule update --init --recursive 2>&1 | Select-Object -Last 5
    Pop-Location
}

if ($HevCommit -ne '') {
    Write-Output ('  切到指定 commit: ' + $HevCommit)
    Push-Location $HevRepo
    & git checkout -q $HevCommit 2>&1 | Out-Null
    & git -c http.version=HTTP/1.1 submodule update --init --recursive 2>&1 | Select-Object -Last 3
    Pop-Location
}

# 子模块必须都在，否则 ndk-build 报一堆「找不到 lwip/... .h」
Push-Location $HevRepo
foreach ($sub in @('src/core', 'third-part/hev-task-system', 'third-part/lwip', 'third-part/yaml')) {
    $ok = Test-Path (Join-Path $sub 'Android.mk')
    Write-Output ('  submodule ' + $sub + ' = ' + $(if ($ok) { 'OK' } else { 'MISSING' }))
}
Pop-Location

Write-Output '=== 2. ndk-build ==='
Push-Location $HevRepo
$ndkBuild = Join-Path $Ndk 'ndk-build.cmd'
$args2 = @(
    'NDK_PROJECT_PATH=.',
    'NDK_APPLICATION_MK=Application.mk',
    'APP_BUILD_SCRIPT=Android.mk',
    ('APP_ABI=' + ($Abis -join ' ')),
    'APP_PLATFORM=android-26',      # 和 minSdk 26 对齐
    'APP_CFLAGS=-O3',
    'APP_SUPPORT_FLEXIBLE_PAGE_SIZES=true'
)
& $ndkBuild @args2 2>&1 | Tee-Object -FilePath 'C:\tmp\ndk-build.log' | Select-Object -Last 30
$ndkExit = $LASTEXITCODE
Write-Output ('  ndk-build exit = ' + $ndkExit)
Pop-Location

Write-Output '=== 3. 产物 ==='
if (Test-Path $outRoot) { Remove-Item $outRoot -Recurse -Force }
New-Item -ItemType Directory -Path $outRoot -Force | Out-Null
$found = 0
foreach ($abi in $Abis) {
    $so = Join-Path $HevRepo "libs\$abi\libhev-socks5-tunnel.so"
    if (Test-Path $so) {
        $dst = Join-Path $outRoot $abi
        New-Item -ItemType Directory -Path $dst -Force | Out-Null
        Copy-Item $so $dst -Force
        $f = Get-Item $so
        Write-Output ('  OK   ' + $abi + '  ' + [math]::Round($f.Length / 1KB, 1) + ' KB')
        $found++
    } else {
        Write-Output ('  MISS ' + $abi)
    }
}
Write-Output ('=== DONE found=' + $found + ' exit=' + $ndkExit + ' ===')
if ($found -eq 0) { Write-Output '  看 C:\tmp\ndk-build.log' }

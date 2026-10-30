# xt（10.113.66.6）上的 Android 构建脚本
#
# 为什么远端编译：本机 E 盘只剩 19G，Gradle 缓存 + AGP 依赖 + NDK 动辄 10G+。
# xt 的 D 盘有 283G 空闲。
#
# 用法（在 xt 上）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File C:\tmp\build-apk.ps1
#
# 前提：源码已由本地 tools/build-apk-remote.ps1 scp 到 D:\ferry-src
#       （压缩包打的是 android/ 的**内容**，所以解开后 app/ 直接在 D:\ferry-src 下，
#        不是 D:\ferry-src\android\app —— 第一版按后者找，报「源码没传上来」）
param(
    [switch]$Clean
)

$ErrorActionPreference = 'Continue'
$src = 'D:\ferry-src'
$dst = 'D:\ferry-android'
$sdk = 'D:\AndroidSdk'
$jdk = 'C:\Program Files\Java\jdk-21'
$gradleHome = 'D:\gradle\gradle-8.7'
$gradleCmd  = Join-Path $gradleHome 'bin\gradle.bat'

Write-Output '=== 0. 环境 ==='
if (-not (Test-Path $src)) { Write-Output "!! 源码不在 $src"; exit 1 }
$env:JAVA_HOME = $jdk
$env:ANDROID_HOME = $sdk
$env:ANDROID_SDK_ROOT = $sdk
$env:PATH = "$jdk\bin;$sdk\platform-tools;$env:PATH"
Write-Output ('  JAVA_HOME = ' + $jdk)
Write-Output ('  SDK       = ' + $sdk)

# gradle 若未下载则先下（services.gradle.org 在 xt 上直连可达，实测 200）
if (-not (Test-Path $gradleCmd)) {
    Write-Output '=== 0b. 下载 gradle 8.7 ==='
    New-Item -ItemType Directory -Path 'D:\gradle' -Force | Out-Null
    $zip = 'D:\gradle\gradle-8.7-bin.zip'
    if (-not (Test-Path $zip)) {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri 'https://services.gradle.org/distributions/gradle-8.7-bin.zip' -OutFile $zip -TimeoutSec 900
    }
    Expand-Archive -Path $zip -DestinationPath 'D:\gradle' -Force
}
Write-Output ('  gradle = ' + $gradleCmd + '  exists=' + (Test-Path $gradleCmd))

Write-Output '=== 1. 同步源码 ==='
if ($Clean -and (Test-Path $dst)) { Remove-Item $dst -Recurse -Force }
if (-not (Test-Path $dst)) { New-Item -ItemType Directory -Path $dst -Force | Out-Null }
# robocopy 退出码 0-7 都算成功（8+ 才是失败）
#
# ⚠ 先把 $src 的时间戳刷成「现在」。robocopy 默认按「时间戳 + 大小」判断，
# 源比目标旧就跳过；而 Expand-Archive 解压时保留的是 zip 里的 mtime
# （本地 CreateFromDirectory 打进去的打包时刻），常常比 D:\ferry-android 里
# 上次编译留下的文件还旧 —— 于是源码明明改了也不覆盖，Gradle 判定
# UP-TO-DATE，拉回来的还是旧 APK。
# 2026-10-30 实际栽过：加了一整段授权检查 + 测试入口，APK 字节数一模一样
# （6,291,196 两版完全相同），排查了半天以为是 Gradle 缓存。
Get-ChildItem -Path $src -Recurse -File -ErrorAction SilentlyContinue |
    ForEach-Object { $_.LastWriteTime = Get-Date }
& robocopy $src $dst /MIR /NFL /NDL /NJH /NJS /NP /XD build .gradle .idea third-part /XF local.properties | Out-Null
Write-Output ('  robocopy exit = ' + $LASTEXITCODE)

Write-Output '=== 2. local.properties ==='
'sdk.dir=D:\\AndroidSdk' | Out-File -FilePath (Join-Path $dst 'local.properties') -Encoding ascii

Write-Output '=== 3. 构建 assembleDebug ==='
Push-Location $dst
& $gradleCmd --no-daemon --console=plain assembleDebug 2>&1 |
    Tee-Object -FilePath 'C:\tmp\gradle-build.log' |
    Select-Object -Last 50
$buildExit = $LASTEXITCODE
Write-Output ('  build exit = ' + $buildExit)

Write-Output '=== 4. 产物 ==='
$apk = Get-ChildItem -Path $dst -Recurse -Filter '*.apk' -ErrorAction SilentlyContinue |
       Where-Object { $_.Name -like '*debug*' } | Select-Object -First 1
if ($apk) {
    Copy-Item $apk.FullName 'C:\tmp\ferry-debug.apk' -Force
    Write-Output ('  APK = ' + $apk.FullName)
    Write-Output ('  大小 = ' + [math]::Round($apk.Length / 1MB, 2) + ' MB')
    Write-Output '  已复制到 C:\tmp\ferry-debug.apk'
} else {
    Write-Output '  !! 没找到 APK，检查 C:\tmp\gradle-build.log'
}
Pop-Location
Write-Output ('=== DONE exit=' + $buildExit + ' ===')

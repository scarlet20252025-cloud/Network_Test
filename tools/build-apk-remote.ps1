# 把本地 android/ 源码打包传到 xt 并在那边编译 APK，再把 APK 拉回本地。
#
# 为什么远端编译：本机 E 盘只剩 19G，Gradle 缓存 + AGP 依赖 + NDK 动辄 10G+。
# xt 的 D 盘有 283G。
#
# 用法（在本机，仓库根目录）：
#   powershell -ExecutionPolicy Bypass -File tools\build-apk-remote.ps1
#   powershell -ExecutionPolicy Bypass -File tools\build-apk-remote.ps1 -Clean
param(
    # 不用 $Host —— 那是 PowerShell 的保留自动变量
    [string]$Target = 'xt',
    [switch]$Clean,
    [switch]$KeepRemote
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$android = Join-Path $repo 'android'
if (-not (Test-Path $android)) { throw "找不到 android/ 目录：$android" }

Write-Output '=== 1. 打包源码（排除 third-part，那边只用来查参考）==='
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$zip = Join-Path $env:TEMP "ferry-android-$stamp.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

# 用 .NET 的 ZipFile，避免依赖外部 tar（Windows 自带 tar 对中文路径不稳）
Add-Type -AssemblyName System.IO.Compression.FileSystem
$tmp = Join-Path $env:TEMP "ferry-android-src-$stamp"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

# 只拷需要的东西
foreach ($item in @('app', 'build.gradle', 'settings.gradle', 'gradle.properties')) {
    $p = Join-Path $android $item
    if (-not (Test-Path $p)) { continue }
    Copy-Item $p -Destination $tmp -Recurse -Force
}
# 清掉不该上远端的
Get-ChildItem $tmp -Recurse -Directory -Include 'build', '.gradle', '.idea' -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
Get-ChildItem $tmp -Recurse -File -Include 'local.properties', '*.apk' -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }

[System.IO.Compression.ZipFile]::CreateFromDirectory($tmp, $zip)
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Output ("  zip = $zip  (" + [math]::Round((Get-Item $zip).Length / 1KB, 1) + " KB)")

Write-Output '=== 2. 传到 xt ==='
& scp -o ConnectTimeout=10 -o BatchMode=yes $zip "${Target}:D:/ferry-src.zip"
if ($LASTEXITCODE -ne 0) { throw 'scp 失败' }
Write-Output '  上传完成'

Write-Output '=== 3. xt 上解压 ==='
$unzip = @"
`$ErrorActionPreference='Stop'
if (Test-Path 'D:\ferry-src') { Remove-Item 'D:\ferry-src' -Recurse -Force }
Expand-Archive -Path 'D:\ferry-src.zip' -DestinationPath 'D:\ferry-src' -Force
Write-Output ('extracted to D:\ferry-src')
Get-ChildItem 'D:\ferry-src' | ForEach-Object { Write-Output ('  ' + `$_.Name) }
"@
$unzipPath = Join-Path $env:TEMP "unzip-ferry-$stamp.ps1"
$unzip | Out-File -FilePath $unzipPath -Encoding UTF8
& scp -o ConnectTimeout=10 -o BatchMode=yes $unzipPath "${Target}:C:/tmp/unzip-ferry.ps1"
& ssh -o ConnectTimeout=15 -o BatchMode=yes $Target "powershell -NoProfile -ExecutionPolicy Bypass -File C:\tmp\unzip-ferry.ps1"
if ($LASTEXITCODE -ne 0) { throw '解压失败' }

# 远端构建脚本随仓库走，每次都传，避免两边不一致
$remoteBuild = Join-Path $PSScriptRoot 'build-apk-on-xt.ps1'
if (-not (Test-Path $remoteBuild)) { throw '缺少 tools/build-apk-on-xt.ps1' }
& scp -o ConnectTimeout=10 -o BatchMode=yes $remoteBuild "${Target}:C:/tmp/build-apk.ps1"
if ($LASTEXITCODE -ne 0) { throw '远端构建脚本上传失败' }

Write-Output '=== 4. xt 上构建 ==='
$buildArgs = if ($Clean) { '-Clean' } else { '' }
& ssh -o ConnectTimeout=15 -o BatchMode=yes -o ServerAliveInterval=20 $Target "powershell -NoProfile -ExecutionPolicy Bypass -File C:\tmp\build-apk.ps1 $buildArgs"
$buildExit = $LASTEXITCODE
Write-Output ("  远端构建 exit = $buildExit")

Write-Output '=== 5. 拉回 APK ==='
$outApk = Join-Path $repo 'dist\ferry-debug.apk'
$outDir = Split-Path -Parent $outApk
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
& scp -o ConnectTimeout=10 -o BatchMode=yes "${Target}:C:/tmp/ferry-debug.apk" $outApk
if ($LASTEXITCODE -eq 0 -and (Test-Path $outApk)) {
    Write-Output ("  APK 已拉回：$outApk  (" + [math]::Round((Get-Item $outApk).Length / 1MB, 2) + " MB)")
} else {
    Write-Output '  !! APK 没拉到，看远端 C:\tmp\gradle-build.log'
}

# 清理本地临时
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Remove-Item $unzipPath -Force -ErrorAction SilentlyContinue

if (-not $KeepRemote) {
    Write-Output '=== 6. 清理远端源码（保留 build 缓存，下次更快）==='
    # 清理命令写成远端 .ps1，避免多层引号转义（PowerShell 里嵌 ssh 再嵌 powershell
    # 是最容易写出解析错误的地方，第一次就栽在这）
    $cleanPs = @'
Remove-Item 'D:\ferry-src.zip' -Force -ErrorAction SilentlyContinue
Remove-Item 'D:\ferry-src' -Recurse -Force -ErrorAction SilentlyContinue
Write-Output 'cleaned'
'@
    $cleanPath = Join-Path $env:TEMP "clean-ferry-$stamp.ps1"
    $cleanPs | Out-File -FilePath $cleanPath -Encoding UTF8
    & scp -o ConnectTimeout=10 -o BatchMode=yes $cleanPath "${Target}:C:/tmp/clean-ferry.ps1" | Out-Null
    & ssh -o ConnectTimeout=10 -o BatchMode=yes $Target 'powershell -NoProfile -ExecutionPolicy Bypass -File C:\tmp\clean-ferry.ps1'
    Remove-Item $cleanPath -Force -ErrorAction SilentlyContinue
}

Write-Output "=== DONE (build exit $buildExit) ==="
exit $buildExit

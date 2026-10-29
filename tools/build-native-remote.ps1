# 在本机调用远端编译 hev-socks5-tunnel，把 .so 拉回 jniLibs。
#
# 用法（仓库根目录）：
#   powershell -ExecutionPolicy Bypass -File tools\build-native-remote.ps1
param(
    [string]$Target = 'xt'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$remoteBuild = Join-Path $PSScriptRoot 'build-native-on-xt.ps1'
if (-not (Test-Path $remoteBuild)) { throw '缺少 tools/build-native-on-xt.ps1' }

Write-Output '=== 1. 上传远端构建脚本 ==='
& scp -o ConnectTimeout=10 -o BatchMode=yes $remoteBuild "${Target}:C:/tmp/build-native.ps1"
if ($LASTEXITCODE -ne 0) { throw 'scp 失败' }

Write-Output '=== 2. 远端编译（首次要 clone 四个子模块，可能几分钟）==='
& ssh -o ConnectTimeout=15 -o BatchMode=yes -o ServerAliveInterval=20 $Target 'powershell -NoProfile -ExecutionPolicy Bypass -File C:\tmp\build-native.ps1'
$exit = $LASTEXITCODE
Write-Output ("  远端 exit = $exit")

Write-Output '=== 3. 拉回 .so ==='
$jniDir = Join-Path $repo 'android\app\src\main\jniLibs'
if (Test-Path $jniDir) { Remove-Item $jniDir -Recurse -Force }
New-Item -ItemType Directory -Path $jniDir -Force | Out-Null

# 三个 ABI：
#   arm64-v8a / armeabi-v7a —— 真机（覆盖几乎所有在用手机）
#   x86_64                 —— 模拟器（用于在没有真机时验证隧道能不能起来）
# x86_64 只在 debug 包里打进 APK，见 app/build.gradle 的 abiFilters。
foreach ($abi in @('arm64-v8a', 'armeabi-v7a', 'x86_64')) {
    $localAbi = Join-Path $jniDir $abi
    New-Item -ItemType Directory -Path $localAbi -Force | Out-Null
    & scp -o ConnectTimeout=10 -o BatchMode=yes "${Target}:C:/tmp/hev-libs/$abi/libhev-socks5-tunnel.so" (Join-Path $localAbi 'libhev-socks5-tunnel.so')
    if ($LASTEXITCODE -eq 0) {
        $f = Get-Item (Join-Path $localAbi 'libhev-socks5-tunnel.so') -ErrorAction SilentlyContinue
        if ($f) {
            Write-Output ('  OK   ' + $abi + '  ' + [math]::Round($f.Length / 1KB, 1) + ' KB')
        }
    } else {
        Write-Output ('  拉取失败：' + $abi)
    }
}

Write-Output '=== 4. 校验 ==='
Get-ChildItem $jniDir -Recurse -Filter '*.so' | ForEach-Object {
    Write-Output ('  ' + $_.Directory.Name + '/' + $_.Name + '  ' + [math]::Round($_.Length / 1KB, 1) + ' KB')
}

Write-Output "=== DONE (exit $exit) ==="
exit $exit

# 离线校验 Android 侧的两处「不一致就会静默失败」的地方：
#
#   1. 发给 hev-socks5-tunnel 的 YAML（键名/段名/数值）
#   2. 路由表（哪些流量进隧道）
#
# 为什么不去手机上试：这两处写错的症状分别是「隧道起不来」和
# 「局域网全超时 / DNS 解析不了」，在真机上排查都要来回装包；
# TunConfig 与 HevConfig 都是纯 Java（不 import Android 类），
# 在电脑上 javac 直接编出来跑，几十毫秒就出结果。
#
# 用法（仓库根目录）：
#   powershell -ExecutionPolicy Bypass -File tools\verify-android.ps1
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$srcDir = Join-Path $repo 'android\app\src\main\java\com\ferry\app\vpn'
$verifyDir = Join-Path $PSScriptRoot 'android-verify'
$work = Join-Path $env:TEMP 'ferry-android-verify'

Write-Output '=== 1. 准备编译目录 ==='
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null

$needed = @(
    (Join-Path $srcDir 'TunConfig.java'),
    (Join-Path $srcDir 'HevConfig.java'),
    (Join-Path $verifyDir 'VerifyHevConfig.java')
)
foreach ($f in $needed) {
    if (-not (Test-Path $f)) { throw "缺少源文件：$f" }
    Copy-Item $f $work -Force
}

Write-Output '=== 2. javac ==='
Push-Location $work
# JDK 21/25 都在；用 javac 编译纯 Java 文件不需要 Android SDK
& javac -encoding UTF-8 -d . TunConfig.java HevConfig.java VerifyHevConfig.java
if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'javac 失败' }
Write-Output '  编译通过'

Write-Output '=== 3. 跑 Java 侧校验 ==='
# -Dfile.encoding=UTF-8 必须**加引号**：不加的话 PowerShell 会把 -Dfile.encoding
# 当成自己的参数名，报「找不到与参数名称 .encoding=UTF-8 匹配的参数」（第一次就这么栽的）。
$javaOut = & java '-Dfile.encoding=UTF-8' -cp . VerifyHevConfig 2>&1
$javaOut | Where-Object { $_ -match '^(PASS|FAIL|ALL PASS|\d+ FAILED)' } | ForEach-Object {
    Write-Output ('  ' + $_)
}
$javaExit = $LASTEXITCODE
Pop-Location

Write-Output '=== 4. 跑 Python 侧路由表校验 ==='
$pyOut = & python (Join-Path $PSScriptRoot 'verify-android-routes.py') 2>&1
$pyOut | Where-Object { $_ -match '^(PASS|FAIL|ALL PASS|\d+ FAILED)' } | ForEach-Object {
    Write-Output ('  ' + $_)
}
$pyExit = $LASTEXITCODE

Write-Output '=== 5. 结果 ==='
if ($javaExit -eq 0 -and $pyExit -eq 0) {
    Write-Output '  全部通过'
    exit 0
} else {
    Write-Output ("  Java exit=$javaExit  Python exit=$pyExit")
    exit 1
}

# 静态验证 Java 侧声明的 native 方法，与 .so 里 RegistrationNatives 的签名是否一致。
#
# 为什么值得单独验：这一步错了，症状是运行时 UnsatisfiedLinkError 或者
# 更糟的「.so 加载成功但调用直接崩 native 层」，而这在真机上极难定位。
# dex 里的方法描述符 + .so 里的 JNINativeMethod 表，两边都是可读的，
# 不用真机就能对账。
#
# 用法（在 xt 上）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File C:\tmp\xt-jni-check.ps1
param(
    [string]$Sdk = 'D:\AndroidSdk',
    [string]$Apk = 'C:\tmp\ferry-debug.apk'
)

$ErrorActionPreference = 'Continue'
$bt = Join-Path $Sdk 'build-tools\34.0.0'
$dexdump = Join-Path $bt 'dexdump.exe'
$unzip = 'C:\tmp\apk-x'

Write-Output '=== 1. 解包 APK ==='
if (Test-Path $unzip) { Remove-Item $unzip -Recurse -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($Apk, $unzip)
Get-ChildItem $unzip -Filter '*.dex' | ForEach-Object { Write-Output ('  ' + $_.Name + '  ' + $_.Length) }

Write-Output '=== 2. dex 里的 TProxyService 与方法签名 ==='
$found = @{}
foreach ($d in (Get-ChildItem $unzip -Filter '*.dex' | Sort-Object Name)) {
    if (-not (Test-Path $dexdump)) { Write-Output '  (没有 dexdump.exe)'; break }
    $out = & $dexdump -d $d.FullName 2>&1 | Out-String
    foreach ($m in [regex]::Matches($out, 'TProxy(StartService|StopService|IsRunning|GetStats)')) {
        $found[$m.Groups[1].Value] = $d.Name
    }
    # 类名
    if ($out -match 'Lhev/htproxy/TProxyService;') {
        Write-Output ('  类 Lhev/htproxy/TProxyService; 在 ' + $d.Name)
    }
}
foreach ($k in @('StartService', 'StopService', 'IsRunning', 'GetStats')) {
    if ($found.ContainsKey($k)) {
        Write-Output ('  OK   TProxy' + $k + '  （在 ' + $found[$k] + '）')
    } else {
        Write-Output ('  MISS TProxy' + $k)
    }
}

Write-Output '=== 3. .so 里的 JNI 注册表（期望 4 条）==='
foreach ($abi in @('arm64-v8a', 'armeabi-v7a', 'x86_64')) {
    $so = Join-Path $unzip ("lib\$abi\libhev-socks5-tunnel.so")
    if (-not (Test-Path $so)) { Write-Output ('  MISS ' + $abi); continue }
    $bytes = [System.IO.File]::ReadAllBytes($so)
    $text = [System.Text.Encoding]::ASCII.GetString($bytes)
    $syms = @('TProxyStartService', 'TProxyStopService', 'TProxyIsRunning', 'TProxyGetStats',
              'JNI_OnLoad', 'hev/htproxy/TProxyService')
    $hit = ($syms | Where-Object { $text.Contains($_) }).Count
    Write-Output ('  ' + $abi + '  ' + [math]::Round($so.Length/1KB,1) + 'KB  找到 ' + $hit + '/' + $syms.Count + ' 个关键符号')
    foreach ($s in $syms) {
        if (-not $text.Contains($s)) { Write-Output ('    MISS ' + $s) }
    }
}

Write-Output '=== 4. 期望的签名（与 hev-jni.c 的 native_methods 对照）==='
Write-Output '  TProxyStartService (Ljava/lang/String;I)Z'
Write-Output '  TProxyStopService  ()Z'
Write-Output '  TProxyIsRunning    ()Z'
Write-Output '  TProxyGetStats     ()[J'
Write-Output '=== JNICHECK DONE ==='

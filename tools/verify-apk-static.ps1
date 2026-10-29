$ErrorActionPreference = 'Continue'
$sdk = 'D:\AndroidSdk'
$apk = 'C:\tmp\ferry-debug.apk'

Write-Output '=== 1. 签名校验（能不能被系统安装）==='
$apksigner = Join-Path $sdk 'build-tools\34.0.0\apksigner.bat'
if (Test-Path $apksigner) {
    & $apksigner verify --verbose --print-certs $apk 2>&1 | Select-Object -First 20 |
        ForEach-Object { Write-Output ('  ' + $_) }
} else {
    Write-Output '  (没有 apksigner)'
}

Write-Output '=== 2. badging ==='
$aapt = Join-Path $sdk 'build-tools\34.0.0\aapt2.exe'
& $aapt dump badging $apk 2>&1 |
    Select-String -Pattern "package:|sdkVersion|targetSdkVersion|native-code|application-label|launchable-activity|uses-permission" |
    ForEach-Object { Write-Output ('  ' + $_.Line) }

Write-Output '=== 3. .so 与 dex ==='
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::OpenRead($apk)
$so = 0; $dex = 0
foreach ($e in $z.Entries) {
    if ($e.FullName -like '*.so') { $so++; Write-Output ('  ' + $e.FullName + '  ' + $e.Length) }
    if ($e.FullName -like '*.dex') { $dex++ }
}
Write-Output ('  dex 数 = ' + $dex)
$z.Dispose()

Write-Output '=== 4. APK 大小 ==='
Write-Output ('  ' + [math]::Round((Get-Item $apk).Length / 1MB, 2) + ' MB')

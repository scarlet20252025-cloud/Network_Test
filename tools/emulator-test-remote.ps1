# 在 xt 的模拟器上装 APK、起隧道、收集证据。
#
# 为什么需要：真机没有，模拟器是唯一能做运行时验证的地方。
# 而「隧道到底通没通」不能靠界面截图判断，必须拿硬证据：
#   · tun 网卡有没有 198.18.0.1
#   · hev 的 native 有没有在跑
#   · 自检五步的结果（会落到 /data/data/.../files/selfcheck.txt）
#
# 用法（在 xt 上，模拟器已 boot_completed）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File C:\tmp\xt-emu-test.ps1
param(
    [string]$Sdk = 'D:\AndroidSdk',
    [string]$Apk = 'C:\tmp\ferry-debug.apk',
    [string]$Pkg = 'com.ferry.app.debug',
    [string]$Activity = 'com.ferry.app.MainActivity'
)

$ErrorActionPreference = 'Continue'
$adb = Join-Path $Sdk 'platform-tools\adb.exe'

if (-not (Test-Path $adb)) { Write-Output ('!! 找不到 ' + $adb); exit 1 }
if (-not (Test-Path $Apk)) { Write-Output ('!! 找不到 ' + $Apk); exit 1 }

# 找到设备
$serial = $null
if (Test-Path 'C:\tmp\emu-serial.txt') {
    $serial = (Get-Content 'C:\tmp\emu-serial.txt' -Raw).Trim()
}
if (-not $serial) {
    $out = (& $adb devices 2>&1 | Out-String)
    $m = [regex]::Match($out, '(emulator-\d+)\s+device')
    if ($m.Success) { $serial = $m.Groups[1].Value }
}
if (-not $serial) { Write-Output '!! 没有可用设备'; & $adb devices; exit 1 }
Write-Output ('=== 设备 ' + $serial + ' ===')

Write-Output '=== 1. 安装 APK ==='
& $adb -s $serial install -r -g $Apk 2>&1 | ForEach-Object { Write-Output ('  ' + $_) }

Write-Output '=== 2. 预授权 VPN（跳掉系统授权对话框）==='
# 系统 VPN 授权框要人手点，自动化环境点不了。
# appops 的 ACTIVATE_VPN 就是这个框背后的开关 —— 设成 allow 后
# VpnService.prepare() 直接返回 null，不再弹框。
& $adb -s $serial shell appops set $Pkg ACTIVATE_VPN allow 2>&1 | ForEach-Object { Write-Output ('  ' + $_) }
$op = & $adb -s $serial shell appops get $Pkg ACTIVATE_VPN 2>&1 | Out-String
Write-Output ('  当前值: ' + $op.Trim())

Write-Output '=== 3. 写调试用配置（跳过激活）==='
# App 检查 cfg.hasToken() 才让开始借网。测试时直接塞一个假 token 进去，
# 免得为了跑通隧道去动真实激活流程（那是 M3 的事）。
$prefsDir = '/data/data/' + $Pkg + '/shared_prefs'
$xml = @'
<?xml version='1.0' encoding='utf-8' standalone='yes' ?>
<map>
    <string name="portalUrl">http://10.13.24.1:8080</string>
    <string name="token">debug-test-token</string>
    <string name="code">debug-test</string>
    <string name="remoteHost">10.13.24.1</string>
    <int name="remotePort" value="1080" />
</map>
'@
$tmpXml = Join-Path $env:TEMP 'ferry-prefs.xml'
# ADB 的 shared_prefs 必须是 UTF-8 无 BOM
[System.IO.File]::WriteAllText($tmpXml, $xml, (New-Object System.Text.UTF8Encoding($false)))
& $adb -s $serial push $tmpXml /data/local/tmp/ferry-prefs.xml 2>&1 | Out-Null
& $adb -s $serial shell "run-as $Pkg mkdir -p shared_prefs" 2>&1 | Out-Null
& $adb -s $serial shell "run-as $Pkg cp /data/local/tmp/ferry-prefs.xml shared_prefs/ferry.xml" 2>&1 | ForEach-Object { Write-Output ('  ' + $_) }
$ls = & $adb -s $serial shell "run-as $Pkg ls -l shared_prefs" 2>&1 | Out-String
Write-Output ('  ' + $ls.Trim().Replace("`n", "`n  "))

Write-Output '=== 4. 清日志并启动 App（自动借网 + 自动自检）==='
& $adb -s $serial logcat -c 2>&1 | Out-Null
& $adb -s $serial shell "am force-stop $Pkg" 2>&1 | Out-Null
Start-Sleep -Seconds 1
& $adb -s $serial shell "am start -n $Pkg/$Activity --ez autostart true --ez selfcheck true" 2>&1 |
    ForEach-Object { Write-Output ('  ' + $_) }

Write-Output '=== 5. 等隧道建立 ==='
Start-Sleep -Seconds 25

Write-Output '=== 6. 证据收集 ==='

Write-Output '--- 6.1 tun 网卡 ---'
$ip = & $adb -s $serial shell "ip -4 addr show tun0" 2>&1 | Out-String
Write-Output ($ip.Trim() -replace "`r", '')

Write-Output '--- 6.2 VPN 是否被系统认作默认网络 ---'
$conn = & $adb -s $serial shell "dumpsys connectivity --short 2>/dev/null | head -12" 2>&1 | Out-String
Write-Output ($conn.Trim() -replace "`r", '')

Write-Output '--- 6.3 应用日志（FerryUi / FerryVpn / FerrySelfCheck）---'
$log = & $adb -s $serial logcat -d -s FerryUi:V FerryVpn:V FerrySelfCheck:V FerryApi:V 2>&1 | Out-String
Write-Output ($log.Trim() -replace "`r", '')

Write-Output '--- 6.4 自检结果文件 ---'
$sc = & $adb -s $serial shell "run-as $Pkg cat files/selfcheck.txt" 2>&1 | Out-String
Write-Output ($sc.Trim() -replace "`r", '')

Write-Output '--- 6.5 hev 自己的日志（最后 40 行）---'
$hev = & $adb -s $serial shell "run-as $Pkg tail -40 files/hev/hev.log" 2>&1 | Out-String
Write-Output ($hev.Trim() -replace "`r", '')

Write-Output '--- 6.6 原生崩溃痕迹（如果有）---'
$crash = & $adb -s $serial logcat -d -b crash 2>&1 | Out-String
Write-Output ($crash.Trim() -replace "`r", '')

Write-Output '=== DONE ==='

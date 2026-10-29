# 同 AP 多 SSID 干扰验证：GDPZ-student-5G 与 GDPZ-teacher-5G 共用一个射频。
#
# 判据：如果两个 SSID 来自同一个物理 AP（BSSID 只差最后几字节、同信道、
# 同 Radio type），那它们共用射频。教师网上有人下载/看视频时，
# 学生网的 airtime 会被吃掉 —— 表现就是「带宽看着够、延迟随机抖」。
#
# 用法： powershell -ExecutionPolicy Bypass -File tools\check-shared-radio.ps1

$ErrorActionPreference = 'Continue'
$out = netsh wlan show networks mode=bssid 2>&1 | Out-String -Width 300

$entries = @()
$blocks = $out -split '(?=SSID \d+ : )'
foreach ($b in $blocks) {
    $n = [regex]::Match($b, 'SSID \d+ : (.+)')
    if (-not $n.Success) { continue }
    $name = $n.Groups[1].Value.Trim()
    $rx = [regex]::Matches($b, 'BSSID \d+\s*:\s*([0-9a-f:]+)\s*\n\s*Signal\s*:\s*(\d+)%[\s\S]*?Channel\s*:\s*(\d+)')
    foreach ($m in $rx) {
        $entries += [pscustomobject]@{
            SSID    = $name
            BSSID   = $m.Groups[1].Value
            Signal  = [int]$m.Groups[2].Value
            Channel = $m.Groups[3].Value
        }
    }
}

Write-Host '=== 全部 BSSID ==='
$entries | Sort-Object Channel, SSID | Format-Table -AutoSize

Write-Host ''
Write-Host '=== 按信道分组：同信道的 SSID 会互相抢 airtime ==='
$entries | Group-Object Channel | Sort-Object Name | ForEach-Object {
    $strong = $_.Group | Where-Object { $_.Signal -ge 70 }
    Write-Host ("信道 {0}: 共 {1} 个 BSSID，其中强信号(>=70%) {2} 个" -f $_.Name, $_.Count, $strong.Count)
    foreach ($s in ($_.Group | Sort-Object Signal -Descending)) {
        $mark = if ($s.Signal -ge 70) { '  <<< 会互相干扰' } else { '' }
        Write-Host ("    {0,-28} {1}  信号 {2,3}%{3}" -f $s.SSID, $s.BSSID, $s.Signal, $mark)
    }
}

Write-Host ''
Write-Host '=== 判读 ==='
# 找「不同 SSID 但 BSSID 高度相似」的组合 —— 那是同一个物理 AP 的多个 SSID
$byMac = $entries | ForEach-Object {
    $parts = $_.BSSID -split ':'
    [pscustomobject]@{
        SSID = $_.SSID
        BSSID = $_.BSSID
        Prefix = ($parts[0..2] -join ':')
        Tail = ($parts[3..5] -join ':')
        Signal = $_.Signal
        Channel = $_.Channel
    }
}
$shared = $byMac | Group-Object Prefix | Where-Object { $_.Count -ge 4 }
foreach ($g in $shared) {
    $ssids = $g.Group | Select-Object -ExpandProperty SSID -Unique
    if ($ssids.Count -ge 2) {
        Write-Host ("  ⚠ 厂商前缀 {0} 下同时有 {1} 个 SSID：{2}" -f $g.Name, $ssids.Count, ($ssids -join ' / ')) -ForegroundColor Yellow
        Write-Host '    同一 AP 设备广播多个 SSID = 共用射频（同信道时）。'
        Write-Host '    别人的流量会抢你的 airtime：带宽够、延迟照样抖。'
    }
}

$myAp = $entries | Where-Object { $_.Signal -eq 100 -and $_.SSID -like '*student*' } | Select-Object -First 1
if ($myAp) {
    $peers = $entries | Where-Object { $_.Channel -eq $myAp.Channel -and $_.SSID -ne $myAp.SSID -and $_.Signal -ge 70 }
    Write-Host ''
    Write-Host ("  你连的是 {0}（信道 {1}）" -f $myAp.SSID, $myAp.Channel)
    if ($peers) {
        Write-Host ("  同信道、强信号的其它 SSID: {0}" -f (($peers | ForEach-Object { $_.SSID }) -join ', ')) -ForegroundColor Yellow
        Write-Host '  → 它们和你在抢同一个信道时间。这是你在本机侧无法消除的。'
    } else {
        Write-Host '  同信道没有其它强信号 SSID。'
    }
}

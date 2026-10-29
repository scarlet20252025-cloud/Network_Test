# 统计周边 AP 的信道分布 —— 判断 5G 频段是否拥挤（抖动可能来自干扰）。
# 只读，不碰任何配置。
$out = netsh wlan show networks mode=bssid 2>&1 | Out-String -Width 300

$blocks = $out -split '(?=SSID \d+ )' | Where-Object { $_ -match 'BSSID' }
Write-Host ("可见网络(BSSID)总数: {0}" -f $blocks.Count)

# 每个块里抓第一个 BSSID 行和通道行
$chanMap = @{}
foreach ($b in $blocks) {
    $m = [regex]::Match($b, '通道\s*:\s*(\d+)')
    if (-not $m.Success) { $m = [regex]::Match($b, 'Channel\s*:\s*(\d+)') }
    if ($m.Success) {
        $ch = $m.Groups[1].Value
        if (-not $chanMap.ContainsKey($ch)) { $chanMap[$ch] = 0 }
        $chanMap[$ch]++
    }
}

Write-Host ''
Write-Host '信道分布（数量降序）：'
$chanMap.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
    $band = if ([int]$_.Key -le 14) { '2.4G' } else { '5G' }
    Write-Host ("  信道 {0,-4} ({1}): {2} 个 AP" -f $_.Key, $band, $_.Value)
}

# 当前信道
$cur = ($out -split "`n" | Select-String 'GDPZ-student-5G' -Context 0,8 | Out-String)
$cm = [regex]::Match($cur, '通道\s*:\s*(\d+)')
Write-Host ''
if ($cm.Success) {
    $c = $cm.Groups[1].Value
    Write-Host ("当前连接的信道: {0}" -f $c)
    $same = if ($chanMap.ContainsKey($c)) { $chanMap[$c] } else { 1 }
    Write-Host ("同信道 AP 数（含自己）: {0}" -f $same)
    if ($same -ge 5) {
        Write-Host '  ⚠ 信道较拥挤 —— 抖动很可能来自同频干扰。换信道/换频段或许立竿见影。' -ForegroundColor Yellow
    } elseif ($same -le 2) {
        Write-Host '  信道不拥挤，干扰不是主因。' -ForegroundColor Green
    }
}

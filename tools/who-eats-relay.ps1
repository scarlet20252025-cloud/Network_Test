# 看占用中继最大的那个客户端在干什么。
$log = 'C:\Users\PCserver\AppData\Local\Ferry\logs\udp-relay.log'
$target = if ($args[0]) { $args[0] } else { '10.112.70.14' }

Write-Host ("=== 客户端 {0} 最近在访问什么 ===" -f $target)
$lines = Select-String -Path $log -Pattern ([regex]::Escape($target)) -ErrorAction SilentlyContinue |
    Select-Object -Last 40
foreach ($l in $lines) {
    $t = $l.Line
    if ($t.Length -gt 130) { $t = $t.Substring(0, 130) }
    Write-Host ('  ' + $t)
}

Write-Host ''
Write-Host '=== 目标域名统计（最近 3000 行）==='
$tail = Get-Content $log -Tail 3000 -ErrorAction SilentlyContinue |
    Where-Object { $_ -match [regex]::Escape($target) }
$domains = @{}
foreach ($t in $tail) {
    $m = [regex]::Match($t, '->\s*([^\s:]+)')
    if ($m.Success) {
        $d = $m.Groups[1].Value
        if (-not $domains.ContainsKey($d)) { $domains[$d] = 0 }
        $domains[$d]++
    }
}
if ($domains.Count) {
    $domains.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 15 | ForEach-Object {
        Write-Host ("  {0,-45} {1}" -f $_.Key, $_.Value)
    }
} else {
    Write-Host '  没有解析到域名（可能是纯 IP 连接，比如游戏/BT）'
}

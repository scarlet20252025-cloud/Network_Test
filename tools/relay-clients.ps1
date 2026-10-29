# 统计 UDP 中继的并发与客户端数 —— 判断是否被其他用户挤占。
$log = 'C:\Users\PCserver\AppData\Local\Ferry\logs\udp-relay.log'

Write-Host '=== 1. 客户端 IP 去重统计 ==='
$tail = Get-Content $log -Tail 3000 -ErrorAction SilentlyContinue
$ips = @{}
foreach ($line in $tail) {
    $m = [regex]::Match($line, '^\[\d\d:\d\d:\d\d\]\s+(\d+\.\d+\.\d+\.\d+)')
    if ($m.Success) {
        $ip = $m.Groups[1].Value
        if (-not $ips.ContainsKey($ip)) { $ips[$ip] = 0 }
        $ips[$ip]++
    }
}
Write-Host ("  最近 3000 行里出现的客户端 IP: {0} 个" -f $ips.Count)
$ips.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 12 | ForEach-Object {
    Write-Host ("    {0,-16} {1} 次" -f $_.Key, $_.Value)
}

Write-Host ''
Write-Host '=== 2. 并发关联数（提取 ASSOCIATE 行里的数字）==='
$assoc = $tail | Where-Object { $_ -match 'ASSOCIATE' } | Select-Object -Last 15
foreach ($a in $assoc) {
    $nums = [regex]::Matches($a, '(\d+)\s*$')
    $last = if ($nums.Count) { $nums[$nums.Count-1].Groups[1].Value } else { '?' }
    Write-Host ("    " + $a.Substring(0, [Math]::Min(70, $a.Length)) + "   [末尾数字=" + $last + "]")
}

Write-Host ''
Write-Host '=== 3. 每分钟请求量（看负载）==='
$perMin = @{}
foreach ($line in $tail) {
    $m = [regex]::Match($line, '^\[\d\d:(\d\d):')
    if ($m.Success) {
        $k = $m.Groups[1].Value
        if (-not $perMin.ContainsKey($k)) { $perMin[$k] = 0 }
        $perMin[$k]++
    }
}
Write-Host '  最近若干分钟的日志行数：'
$perMin.GetEnumerator() | Sort-Object Name -Descending | Select-Object -First 8 | ForEach-Object {
    Write-Host ("    {0} 分: {1} 行" -f $_.Key, $_.Value)
}

Write-Host ''
Write-Host '=== 4. 自己这台机器（10.141.59.235）的记录 ==='
$mine = $tail | Where-Object { $_ -match '10\.141\.59\.235' } | Select-Object -Last 10
if ($mine) { $mine | ForEach-Object { Write-Host ('    ' + $_) } }
else { Write-Host '    最近 3000 行里没有本机记录（说明本机流量不大）' }

Write-Host ''
Write-Host '=== 5. python 进程 ==='
Get-Process python* -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host ("  PID {0}  CPU {1:N1}s  内存 {2:N0}MB  线程 {3}  启动 {4}" -f `
        $_.Id, $_.CPU, ($_.WorkingSet64 / 1MB), $_.Threads.Count, $_.StartTime)
}

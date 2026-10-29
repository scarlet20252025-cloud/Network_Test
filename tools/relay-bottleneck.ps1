# 检查 pcs 上 UDP 中继是否成为瓶颈 —— 并发 473 个关联的处理能力。
$log = 'C:\Users\PCserver\AppData\Local\Ferry\logs\udp-relay.log'

Write-Host '=== 1. Python 进程资源（中继本体）==='
Get-Process python* -ErrorAction SilentlyContinue | ForEach-Object {
    $p = $_
    $cpu1 = $p.CPU
    Start-Sleep -Milliseconds 2000
    $p2 = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
    $cpu2 = if ($p2) { $p2.CPU } else { $cpu1 }
    $pct = [math]::Round(($cpu2 - $cpu1) / 2 * 100, 1)
    Write-Host ("  PID {0}  CPU累计 {1:N1}s  当前占用 {2}%  内存 {3:N0}MB  线程 {4}" -f `
        $p.Id, $cpu2, $pct, ($p.WorkingSet64 / 1MB), $p.Threads.Count)
    Write-Host ("    命令行: " + (Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)").CommandLine)
}

Write-Host ''
Write-Host '=== 2. 日志增长速率（每秒多少行 = 多少事务）==='
$a = (Get-Item $log).Length
Start-Sleep -Seconds 5
$b = (Get-Item $log).Length
Write-Host ("  5 秒增长 {0:N0} 字节 → 约 {1:N0} 字节/秒" -f ($b - $a), (($b - $a) / 5))

Write-Host ''
Write-Host '=== 3. 当前并发关联数（取最后一条）==='
$last = Select-String -Path $log -Pattern 'ASSOCIATE' -ErrorAction SilentlyContinue | Select-Object -Last 1
if ($last) {
    $m = [regex]::Match($last.Line, '(\d+)\s*\S*\s*$')
    Write-Host ('  最后一条: ' + $last.Line.Substring(0, [Math]::Min(90, $last.Line.Length)))
}

Write-Host ''
Write-Host '=== 4. 按客户端 IP 统计当前份额（最后 2000 行）==='
$tail = Get-Content $log -Tail 2000 -ErrorAction SilentlyContinue
$stat = @{}
foreach ($l in $tail) {
    $m = [regex]::Match($l, '^\[\d\d:\d\d:\d\d\]\s+(\d+\.\d+\.\d+\.\d+)')
    if ($m.Success) {
        $ip = $m.Groups[1].Value
        if (-not $stat.ContainsKey($ip)) { $stat[$ip] = 0 }
        $stat[$ip]++
    }
}
$total = ($stat.Values | Measure-Object -Sum).Sum
Write-Host ('  总行数: ' + $total)
$stat.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 10 | ForEach-Object {
    $pct = [math]::Round($_.Value * 100.0 / $total, 1)
    Write-Host ("    {0,-16} {1,5} 行  {2,5}%" -f $_.Key, $_.Value, $pct)
}

Write-Host ''
Write-Host '=== 5. 判断 ==='
$maxAssoc = 0
$assocLine = Select-String -Path $log -Pattern 'ASSOCIATE' -ErrorAction SilentlyContinue | Select-Object -Last 30
foreach ($a in $assocLine) {
    $nums = [regex]::Matches($a.Line, '(\d+)\s*\S*\s*$')
    if ($nums.Count) {
        $v = [int]$nums[$nums.Count-1].Groups[1].Value
        if ($v -gt $maxAssoc) { $maxAssoc = $v }
    }
}
Write-Host ("  观察到的最大并发: {0}" -f $maxAssoc)
if ($maxAssoc -gt 300) {
    Write-Host '  → 并发超过 300，单进程 Python 中继很可能在排队。' -ForegroundColor Yellow
    Write-Host '    这就是客户端侧延迟固定的来源。'
}

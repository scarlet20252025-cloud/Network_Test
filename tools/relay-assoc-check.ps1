# 查远端 UDP 中继的关联数与拒绝情况 —— 定位「UDP 关联失败」的来源。
$log = 'C:\Users\PCserver\AppData\Local\Ferry\logs\udp-relay.log'

Write-Host '=== 1. 硬上限拒绝记录 ==='
$hard = Select-String -Path $log -Pattern 'HARD_LIMIT|hard limit|3000' -ErrorAction SilentlyContinue
Write-Host ("  匹配 {0} 条" -f $hard.Count)
$hard | Select-Object -Last 10 | ForEach-Object { Write-Host ('  ' + $_.Line) }

Write-Host ''
Write-Host '=== 2. ASSOCIATE 失败记录 ==='
$fail = Select-String -Path $log -Pattern 'ASSOCIATE.*(fail|FAIL|拒绝|error)' -ErrorAction SilentlyContinue
Write-Host ("  匹配 {0} 条" -f $fail.Count)
$fail | Select-Object -Last 10 | ForEach-Object { Write-Host ('  ' + $_.Line) }

Write-Host ''
Write-Host '=== 3. 最近 20 条 ASSOCIATE（看并发数）==='
Select-String -Path $log -Pattern 'ASSOCIATE' -ErrorAction SilentlyContinue |
    Select-Object -Last 20 | ForEach-Object {
        $l = $_.Line
        if ($l.Length -gt 100) { $l = $l.Substring(0, 100) }
        Write-Host ('  ' + $l)
    }

Write-Host ''
Write-Host '=== 4. 日志总量与增长 ==='
$fi = Get-Item $log
Write-Host ("  {0:N1} MB  最后写入 {1}" -f ($fi.Length / 1MB), $fi.LastWriteTime)
$a = $fi.Length
Start-Sleep -Seconds 5
$b = (Get-Item $log).Length
Write-Host ("  5 秒增长 {0:N0} 字节" -f ($b - $a))

Write-Host ''
Write-Host '=== 5. 中继进程内存与线程 ==='
Get-Process python* -ErrorAction SilentlyContinue | Where-Object { $_.WorkingSet64 -gt 50MB } | ForEach-Object {
    Write-Host ("  PID {0}  CPU {1:N0}s  内存 {2:N0}MB  线程 {3}  句柄 {4}" -f `
        $_.Id, $_.CPU, ($_.WorkingSet64 / 1MB), $_.Threads.Count, $_.HandleCount)
}

# 分析 pcs 上 UDP 中继的负载情况 —— 判断延迟是否来自中继排队。
#
# 背景：渡口的 UDP 走 pcs 的 ferry-socks5-server --udp。日志显示它同时服务
# 大量不同来源 IP（10.112.x / 10.113.x / 10.114.x），是全校共享出口。
# 如果并发 UDP 关联很多，每个包的转发就会排队。
#
# 用法（在 pcs 上跑）： powershell -ExecutionPolicy Bypass -File analyze-relay.ps1
$log = 'C:\Users\PCserver\AppData\Local\Ferry\logs\udp-relay.log'

Write-Host '=== 1. 日志规模 ==='
$fi = Get-Item $log -ErrorAction SilentlyContinue
if (-not $fi) { Write-Host '找不到日志'; exit 1 }
Write-Host ("  {0:N0} 字节  最后写入 {1}" -f $fi.Length, $fi.LastWriteTime)

Write-Host ''
Write-Host '=== 2. UDP ASSOCIATE（当前并发）==='
$assoc = Select-String -Path $log -Pattern 'UDP ASSOCIATE' -ErrorAction SilentlyContinue
Write-Host ("  累计 ASSOCIATE 次数: {0}" -f $assoc.Count)
$assoc | Select-Object -Last 5 | ForEach-Object { Write-Host ('    ' + $_.Line) }

Write-Host ''
Write-Host '=== 3. UDP 关联关闭统计（最近 10 条）==='
Select-String -Path $log -Pattern 'UDP .*close|UDP .*关闭' -ErrorAction SilentlyContinue |
    Select-Object -Last 10 | ForEach-Object { Write-Host ('    ' + $_.Line) }

Write-Host ''
Write-Host '=== 4. 不同客户端 IP 数（看是不是共享出口）==='
$ips = Select-String -Path $log -Pattern '^\[\d\d:\d\d:\d\d\] (10\.\d+\.\d+\.\d+)' |
    ForEach-Object { $_.Matches[0].Groups[1].Value } |
    Sort-Object -Unique
Write-Host ("  日志里出现过的客户端 IP: {0} 个" -f $ips.Count)
$ips | Select-Object -First 15 | ForEach-Object { Write-Host ('    ' + $_) }

Write-Host ''
Write-Host '=== 5. 最近的吞吐（每秒多少行 = 多少请求）==='
$lines = Get-Content $log -Tail 400
$times = $lines | ForEach-Object {
    $m = [regex]::Match($_, '^\[(\d\d:\d\d:\d\d)\]')
    if ($m.Success) { $m.Groups[1].Value }
} | Group-Object | Sort-Object Name -Descending | Select-Object -First 5
Write-Host '  最近几个整秒的日志行数（近似 QPS）：'
$times | ForEach-Object { Write-Host ("    {0}  {1} 行" -f $_.Name, $_.Count) }

Write-Host ''
Write-Host '=== 6. python 进程资源占用 ==='
Get-Process python* -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host ("  PID {0}  CPU {1:N1}s  内存 {2:N0}MB  线程 {3}" -f `
        $_.Id, $_.CPU, ($_.WorkingSet64 / 1MB), $_.Threads.Count)
}

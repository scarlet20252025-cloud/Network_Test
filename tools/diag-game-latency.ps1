# 游戏延迟排查：找出「谁在抢无线链路」。
#
# 为什么是连接数而不是流量：无线链路是按 airtime 分配的。44 个长连接
# 每秒钟哪怕只各发几个小包，也会把信道切成很多碎片，游戏 UDP 的包就得排队。
# 这时总带宽可能只有 120KB/s（看起来「没在跑什么」），延迟照样抖 ——
# 这是「带宽充足但延迟差」最典型的原因。
#
# 用法： powershell -ExecutionPolicy Bypass -File tools\diag-game-latency.ps1
# 只读，不改任何设置。

$ErrorActionPreference = 'Continue'

Write-Host '=== 1. 当前延迟基线 ==='
$ping = 1..10 | ForEach-Object {
    $r = Test-Connection 10.141.56.1 -Count 1 -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 150
    if ($r) { $r.ResponseTime }
}
if ($ping) {
    $sorted = $ping | Sort-Object
    $p50 = $sorted[[int]($sorted.Count / 2)]
    Write-Host ("  网关 p50={0}ms  min={1}  max={2}" -f $p50, $sorted[0], $sorted[-1])
    Write-Host ("  样本: {0}" -f ($ping -join ' '))
}

Write-Host ''
Write-Host '=== 2. 谁的连接最多（按进程） ==='
$byProc = Get-NetTCPConnection -ErrorAction SilentlyContinue |
    Where-Object { $_.State -eq 'Established' } |
    Group-Object OwningProcess |
    Sort-Object Count -Descending
$total = 0
foreach ($g in $byProc) {
    $p = Get-Process -Id $g.Name -ErrorAction SilentlyContinue
    if ($p) {
        $total += $g.Count
        if ($g.Count -ge 3) {
            Write-Host ("  {0,-24} {1,4} 个" -f $p.ProcessName, $g.Count)
        }
    }
}
Write-Host ("  合计已建立连接: {0}" -f $total)

Write-Host ''
Write-Host '=== 3. 各网卡 3 秒吞吐 ==='
$nics = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' }
$before = @{}
foreach ($n in $nics) {
    $s = Get-NetAdapterStatistics -Name $n.Name -ErrorAction SilentlyContinue
    if ($s) { $before[$n.Name] = @($s.ReceivedBytes, $s.SentBytes) }
}
Start-Sleep -Seconds 3
Write-Host ("  {0,-20} {1,>12} {2,>12}" -f '网卡', '收 KB/s', '发 KB/s')
foreach ($n in $before.Keys) {
    $s = Get-NetAdapterStatistics -Name $n -ErrorAction SilentlyContinue
    if ($s) {
        $rx = [math]::Round(($s.ReceivedBytes - $before[$n][0]) / 3 / 1024, 1)
        $tx = [math]::Round(($s.SentBytes - $before[$n][1]) / 3 / 1024, 1)
        Write-Host ("  {0,-20} {1,12} {2,12}" -f $n, $rx, $tx)
    }
}

Write-Host ''
Write-Host '=== 4. 判读 ==='
$clash = $byProc | Where-Object { (Get-Process -Id $_.Name -ErrorAction SilentlyContinue).ProcessName -match 'mihomo|clash' }
if ($clash -and $clash.Count -ge 20) {
    Write-Host ("  ⚠ 代理程序占了 {0} 个连接。打游戏时它会把无线链路切成很多碎片，" -f $clash.Count) -ForegroundColor Yellow
    Write-Host '    即使总流量很小，游戏的 UDP 包也要排队 → 延迟抖动。'
    Write-Host '    建议：打游戏时把 Clash 切「直连模式」或退出，再测一次对比。'
}
if ($total -ge 60) {
    Write-Host ("  ⚠ 已建立连接共 {0} 个，偏多。" -f $total) -ForegroundColor Yellow
}
Write-Host ''
Write-Host '  对照方法：关掉代理/浏览器后再跑一次本脚本，看 p50 和 max 是否收敛。'

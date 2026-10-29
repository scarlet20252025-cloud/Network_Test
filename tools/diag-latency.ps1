# 网络延迟诊断：区分「链路慢」和「网卡省电抖动」。
#
# 为什么需要这个工具（2026-10-29 排查游戏延迟时踩出来的）：
#   用户报「打游戏延迟高」，第一反应会去看 ping。但 ping 的 min/avg/max
#   说明不了问题 —— 本机实测到网关 min=1ms 而 p50=47ms，avg 完全被
#   少数离群值拉偏。必须看**分布**和**发包节奏的关系**才能定性。
#
# 三种发包节奏的意义：
#   连续  ：网卡保持唤醒，测的是真实链路 RTT
#   120ms ：正好落在信标周期附近，会撞上无线省电模式（PSM）的休眠窗口
#   500ms ：比休眠窗口长，多数时候网卡已经醒了
#   如果「连续快、120ms 慢」，就是省电模式；如果三种都慢，才是带宽/空口竞争。
param(
    [string]$Target = '10.141.56.1',
    [int]$Count = 30
)

$ErrorActionPreference = 'Continue'
$p = New-Object System.Net.NetworkInformation.Ping

function Stat($label, $gapMs, $n) {
    $t = New-Object System.Collections.ArrayList
    $lost = 0
    for ($i = 0; $i -lt $n; $i++) {
        try {
            $r = $p.Send($Target, 2000)
            if ($r.Status -eq 'Success') { [void]$t.Add($r.RoundtripTime) } else { $lost++ }
        } catch { $lost++ }
        if ($gapMs -gt 0) { Start-Sleep -Milliseconds $gapMs }
    }
    if ($t.Count -eq 0) {
        Write-Output ("  {0,-20} 全部失败（丢 {1}/{2}）" -f $label, $lost, $n)
        return $null
    }
    $s = $t | Sort-Object
    $p50 = $s[[math]::Floor($s.Count * 0.5)]
    $p95 = $s[[math]::Min($s.Count - 1, [math]::Floor($s.Count * 0.95))]
    $avg = [math]::Round(($s | Measure-Object -Average).Average, 1)
    Write-Output ("  {0,-20} n={1,2} min={2,3} p50={3,3} p95={4,3} avg={5,5} max={6,3}  丢{7}" -f `
        $label, $s.Count, $s[0], $p50, $p95, $avg, $s[$s.Count-1], $lost)
    return @{ p50 = $p50; p95 = $p95 }
}

Write-Output ("=== 延迟分布  目标 " + $Target + " ===")
$a = Stat '连续 (0ms 间隔)' 0 $Count
$b = Stat '120ms 间隔' 120 $Count
$c = Stat '500ms 间隔' 500 ([math]::Floor($Count * 0.7))

Write-Output ''
Write-Output '=== 判读 ==='
if ($a -and $b) {
    $diff = $b.p50 - $a.p50
    if ($diff -ge 10) {
        Write-Output ("  连续 p50={0}ms 但 120ms 间隔 p50={1}ms —— 差 {2}ms" -f $a.p50, $b.p50, $diff)
        Write-Output '  → 典型的**无线网卡省电模式**：静默 >信标周期 的发包会撞上休眠窗口。'
        Write-Output '    处理：设备管理器 → 网卡 → 电源管理，或注册表把 IpsEn 设成 0。'
    } elseif ($a.p50 -ge 30) {
        Write-Output ("  三种节奏都慢（连续 p50={0}ms）→ 不是省电，是链路本身拥塞或跨段。" -f $a.p50)
    } else {
        Write-Output ("  延迟正常（连续 p50={0}ms，120ms 间隔 p50={1}ms）" -f $a.p50, $b.p50)
    }
    if ($a.p95 - $a.p50 -ge 30) {
        Write-Output ("  注意：p95={0}ms 比 p50 高很多，存在偶发尖峰（不影响均值判断，但体感会卡顿）" -f $a.p95)
    }
}

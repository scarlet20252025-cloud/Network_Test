# 验证 1.0.48 的界面卡顿修复 —— 只读测量，不碰用户正在打的游戏。
#
# 用法（管理员或普通权限都行）：
#   powershell -ExecutionPolicy Bypass -File tools\verify-ui-cpu.ps1
#
# 背景：1.0.47 在「直连、什么都没做」的状态下，UI 线程 5 秒吃掉 4.09 秒 CPU
# （约 82% 单核），点界面上任何东西都要等很久。根因是 RefreshUi() 每秒给
# 40 个 TextBlock 的 Text 和 37 个 Foreground 无条件赋值，而 Palette 的
# Brush 每次 get 都 new 一个新对象 —— WPF 判等失败，每秒触发全套 measure/
# arrange/重绘。修法见 Palette.cs 与 MainWindow.SetIfChanged 的注释。
#
# 本脚本输出：
#   1. 当前运行的客户端进程信息
#   2. 10 秒内的 CPU 增量（修复后应该是接近 0）
#   3. 主线程是否一直处于 Running（空转的标志）
param(
    [int]$Seconds = 10
)

$ErrorActionPreference = 'Continue'

Write-Host '=== 1. 找到渡口助手 ==='
$proc = Get-Process | Where-Object { $_.ProcessName -like '*渡口*' } | Select-Object -First 1
if (-not $proc) {
    Write-Host '  客户端没在运行。先把它打开再跑本脚本。' -ForegroundColor Yellow
    exit 1
}
$ver = $proc.MainModule.FileVersionInfo.FileVersion
Write-Host ("  PID={0}  版本={1}  Mem={2}MB  启动于={3}" -f `
    $proc.Id, $ver, [math]::Round($proc.WorkingSet64 / 1MB), $proc.StartTime)

if ($ver -notlike '1.0.48*') {
    Write-Host ("  ⚠ 当前跑的不是 1.0.48（是 $ver），测的是旧版本") -ForegroundColor Yellow
}

Write-Host ''
Write-Host ("=== 2. {0} 秒 CPU 增量 ===" -f $Seconds)
$a = (Get-Process -Id $proc.Id).CPU
Start-Sleep -Seconds $Seconds
$b = (Get-Process -Id $proc.Id).CPU
$delta = [math]::Round($b - $a, 2)
$pct = [math]::Round(($b - $a) / $Seconds * 100, 1)
Write-Host ("  {0} 秒内消耗 {1} 秒 CPU  →  平均 {2}% 单核" -f $Seconds, $delta, $pct)

Write-Host ''
Write-Host '=== 3. 主线程状态 ==='
$running = ($proc.Threads | Where-Object { $_.ThreadState -eq 'Running' } | Measure-Object).Count
Write-Host ("  处于 Running 的线程数: {0}" -f $running)
Write-Host ("  响应状态: {0}" -f $proc.Responding)

Write-Host ''
Write-Host '=== 判读 ==='
# 修复前实测：空闲直连状态下约 82%
if ($pct -le 5) {
    Write-Host ("  PASS：{0}% —— 界面线程已经空闲下来了。" -f $pct) -ForegroundColor Green
} elseif ($pct -le 25) {
    Write-Host ("  基本正常：{0}%。界面在刷新，但没有死循环。" -f $pct) -ForegroundColor Green
} else {
    Write-Host ("  FAIL：{0}% —— 仍然偏高。对照基线：1.0.47 空闲时是 82%。" -f $pct) -ForegroundColor Red
    Write-Host '  如果跑的是 1.0.48 还这么高，说明还有别的每帧重活（把本脚本的输出发出来）。'
}

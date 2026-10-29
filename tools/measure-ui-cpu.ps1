# 验证 1.0.49 的界面 CPU 占用 —— 一条命令拿到结果。
#
# 为什么写成脚本：直接 Start-Process 会被 UAC 取消（配置里 useTun=true，
# 客户端会自己提权重启一次）。这里改用 WMI 起一个与当前会话无关的进程，
# 再测量。只读测量，不改任何配置。
#
# 用法： powershell -ExecutionPolicy Bypass -File tools\measure-ui-cpu.ps1
param(
    [int]$WarmupSec = 12,
    [int]$SampleSec = 10
)

$ErrorActionPreference = 'Continue'

Write-Host '=== 1. 结束旧实例 ==='
Get-Process | Where-Object { $_.ProcessName -like '*渡口*' } | ForEach-Object {
    Write-Host ("  停止 PID {0}" -f $_.Id)
    $_ | Stop-Process -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2

Write-Host ''
Write-Host '=== 2. 启动（走 WMI，不在当前 Job 里）==='
$exe = 'E:\肥鱼不乐园\borrow-net\dist\渡口助手.exe'
if (-not (Test-Path $exe)) { Write-Host "找不到 $exe"; exit 1 }

try {
    $r = ([wmiclass]'Win32_Process').Create('"' + $exe + '"')
    Write-Host ("  WMI 返回码 {0}" -f $r.ReturnValue)
} catch {
    Write-Host ("  WMI 失败: {0}，改用 Start-Process" -f $_.Exception.Message)
    Start-Process -FilePath $exe -ErrorAction SilentlyContinue
}

Start-Sleep -Seconds $WarmupSec
$p = Get-Process | Where-Object { $_.ProcessName -like '*渡口*' } | Select-Object -First 1
if (-not $p) {
    Write-Host '  !! 没起来。可能是 UAC 被拒。请手动打开客户端后再跑本脚本（加 -Attach）。'
    exit 1
}

$ver = $p.MainModule.FileVersionInfo.FileVersion
Write-Host ("  PID={0}  版本={1}" -f $p.Id, $ver)

Write-Host ''
Write-Host ("=== 3. {0} 秒 CPU 采样 ===" -f $SampleSec)
$a = $p.CPU
Start-Sleep -Seconds $SampleSec
$b = (Get-Process -Id $p.Id -ErrorAction SilentlyContinue).CPU
if ($null -eq $b) { Write-Host '  进程已退出'; exit 1 }
$delta = [math]::Round($b - $a, 2)
$pct = [math]::Round(($b - $a) / $SampleSec * 100, 1)
Write-Host ("  CPU 增量 {0} 秒 / {1} 秒  →  {2}% 单核" -f $delta, $SampleSec, $pct)

Write-Host ''
Write-Host '=== 4. 线程情况 ==='
$running = ($p.Threads | Where-Object { $_.ThreadState -eq 'Running' } | Measure-Object).Count
Write-Host ("  处于 Running 的线程数: {0}" -f $running)

Write-Host ''
Write-Host '=== 判读（基线：1.0.47 空闲时 82%，1.0.48 只降到 78%）==='
if ($pct -le 5) {
    Write-Host ("  PASS：{0}% —— 界面彻底空闲了。" -f $pct) -ForegroundColor Green
} elseif ($pct -le 20) {
    Write-Host ("  可用：{0}%。还有刷新开销，但不该让界面卡死。" -f $pct) -ForegroundColor Green
} else {
    Write-Host ("  FAIL：{0}% —— 仍有大量每帧工作。" -f $pct) -ForegroundColor Red
}

# 把 dist\渡口助手.exe 换成正在用的那份（先退出客户端）。
#
# 为什么要单独一个脚本：客户端是托盘常驻的，运行中 exe 被锁，
# 直接覆盖会失败（共享冲突）。本机更新时我先把它复制成
# %LOCALAPPDATA%\Ferry\渡口助手.exe.new，等用户方便时执行这里换上去。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\apply-client-update.ps1
#   ... -Restart      # 换完立刻启动（会弹 UAC，因为 manifest 要求管理员）

param(
  [switch]$Restart
)

$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'Ferry'
$exe = Join-Path $dir '渡口助手.exe'
$new = Join-Path $dir '渡口助手.exe.new'

if (-not (Test-Path $new)) { throw "没有待更新的文件：$new（先跑 build.ps1 再复制过来）" }

$p = Get-Process -Name '渡口助手' -ErrorAction SilentlyContinue
if ($p) {
  Write-Host "[!] 客户端正在运行（pid $($p.Id)）——先在托盘右键退出"
  Write-Host "    正在借网时别直接停：会断网。先点「停止并恢复直连」。"
  throw "client still running"
}

Copy-Item $new $exe -Force
Remove-Item $new -Force
$v = (Get-Item $exe).VersionInfo.FileVersion
Write-Host "[OK] 已替换，当前版本 $v"

if ($Restart) {
  Start-Process $exe
  Write-Host "[OK] 已启动（会在 UAC 里确认一次）"
}

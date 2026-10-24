# 渡口服务端 · 一键控制
#
# 双击运行。会自己提权（因为注册计划任务需要管理员）。
#
# 为什么需要提权：
#   注册 RunLevel=HighestAvailable 的计划任务必须管理员。
#   之前用普通权限跑，报「错误: 拒绝访问」，而且是在**重启中继**时
#   才发现的 —— 用户以为快捷方式坏了。
#
# 为什么要一个菜单而不是几个快捷方式：
#   服务器上要做的事就那几件（看状态/启动/重启/注册自启），
#   一个菜单选一下比桌面摆四个图标清楚。

$ErrorActionPreference = 'Continue'

# ── 自我提权 ──
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$pr = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  Write-Host '需要管理员权限（注册开机自启的任务需要），正在提权…' -ForegroundColor Yellow
  $self = $MyInvocation.MyCommand.Path
  try {
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @(
      '-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$self`""
    ) | Out-Null
  } catch {
    Write-Host '提权被拒绝。请右键 → 以管理员身份运行。' -ForegroundColor Red
    Read-Host '按回车退出'
  }
  exit
}

$All = Join-Path $PSScriptRoot 'ferry-server-all.ps1'
if (-not (Test-Path $All)) {
  Write-Host "找不到 $All" -ForegroundColor Red
  Read-Host '按回车退出'
  exit 1
}

function Show-Menu {
  Clear-Host
  Write-Host ''
  Write-Host '  ╔══════════════════════════════════════════╗' -ForegroundColor Cyan
  Write-Host '  ║      渡口 Ferry · 服务端控制台           ║' -ForegroundColor Cyan
  Write-Host '  ╚══════════════════════════════════════════╝' -ForegroundColor Cyan
  Write-Host ''
  Write-Host '   1  查看状态（服务 + 端口 + 限速 + 任务）'
  Write-Host '   2  启动全部服务'
  Write-Host '   3  重启全部服务'
  Write-Host '   4  停止全部服务'
  Write-Host '   5  注册开机自启（重启服务器后自动拉起）'
  Write-Host '   6  卸载（移除自启 + 停服务）'
  Write-Host '   7  打开管理面板（浏览器）'
  Write-Host '   0  退出'
  Write-Host ''
}

while ($true) {
  Show-Menu
  $c = Read-Host '  请选择'
  switch ($c) {
    '1' { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $All -Action Status; Read-Host '  按回车返回菜单' }
    '2' { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $All -Action Start; Read-Host '  按回车返回菜单' }
    '3' { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $All -Action Restart; Read-Host '  按回车返回菜单' }
    '4' { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $All -Action Stop; Read-Host '  按回车返回菜单' }
    '5' { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $All -Action Install; Read-Host '  按回车返回菜单' }
    '6' {
      Write-Host ''
      $ok = Read-Host '  确认卸载？会停掉服务并移除自启 (y/N)'
      if ($ok -eq 'y' -or $ok -eq 'Y') {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $All -Action Uninstall
      } else { Write-Host '  已取消' }
      Read-Host '  按回车返回菜单'
    }
    '7' { Start-Process 'http://10.13.24.1:8080/admin'; Start-Sleep -Seconds 1 }
    '0' { exit }
    default { }
  }
}

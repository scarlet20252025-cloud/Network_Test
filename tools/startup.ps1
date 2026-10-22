#requires -Version 5.1
<#
  startup.ps1 — 管理「本机登录自启」的隧道保活

  为什么不用计划任务：
    Register-ScheduledTask / schtasks 都需要管理员权限（实测当前会话
    Administrator=False，两者均报 Access is denied）。
    启动文件夹（%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup）
    普通用户可写，登录时由 explorer 自动拉起，效果等同且零权限要求。

  用法：
    .\startup.ps1 Status      查看是否已装
    .\startup.ps1 Install     装到启动文件夹
    .\startup.ps1 Uninstall   移除
    .\startup.ps1 Test        真实模拟一次登录启动并验证 SOCKS 可用
#>
[CmdletBinding()]
param(
  [ValidateSet('Status', 'Install', 'Uninstall', 'Test')]
  [string]$Action = 'Status',
  [string]$RemoteHost = 'pcs',
  [int]$SocksPort = 10808
)

$ErrorActionPreference = 'Stop'

$KeeperScript = Join-Path $PSScriptRoot 'ssh-tunnel-keeper.ps1'
$StartupDir   = [Environment]::GetFolderPath('Startup')
$LnkPath      = Join-Path $StartupDir 'BorrowNetKeeper.lnk'

function Write-Ok($m)   { Write-Host "  $m" -ForegroundColor Green }
function Write-Warn($m) { Write-Host "  $m" -ForegroundColor Yellow }
function Write-Bad($m)  { Write-Host "  $m" -ForegroundColor Red }

function New-KeeperShortcut {
  $ws = New-Object -ComObject WScript.Shell
  $s = $ws.CreateShortcut($LnkPath)
  $s.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
  $s.Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$KeeperScript`" -RemoteHost $RemoteHost -SocksPort $SocksPort"
  $s.WorkingDirectory = $PSScriptRoot
  $s.WindowStyle = 7          # 最小化启动，不闪黑窗
  $s.Description = '借网 SSH SOCKS 隧道保活（登录自启）'
  $s.Save()
}

function Get-SocksListening {
  $n = (Get-NetTCPConnection -LocalPort $SocksPort -State Listen -ErrorAction SilentlyContinue | Measure-Object).Count
  return ($n -gt 0)
}

switch ($Action) {

  'Status' {
    Write-Host '--- 登录自启状态 ---'
    if (Test-Path $LnkPath) {
      Write-Ok "已安装: $LnkPath"
      $ws = New-Object -ComObject WScript.Shell
      $s = $ws.CreateShortcut($LnkPath)
      Write-Host "    目标: $($s.TargetPath)"
      Write-Host "    参数: $($s.Arguments)"
    } else {
      Write-Warn "未安装（$LnkPath 不存在）"
      Write-Host '    装: .\startup.ps1 Install'
    }

    Write-Host ''
    Write-Host '--- 当前运行状态 ---'
    $kp = Get-CimInstance Win32_Process -Filter "Name='powershell.exe' or Name='pwsh.exe'" -ErrorAction SilentlyContinue |
          Where-Object { $_.CommandLine -like '*ssh-tunnel-keeper.ps1*' }
    if ($kp) { Write-Ok "keeper 运行中: PID $(($kp.ProcessId) -join ',')" }
    else     { Write-Bad 'keeper 未运行' }

    $ssh = Get-Process ssh -ErrorAction SilentlyContinue
    if ($ssh) {
      foreach ($s in $ssh) {
        $age = [int]((Get-Date) - $s.StartTime).TotalMinutes
        Write-Ok "ssh 运行中: PID $($s.Id)，已存活 $age 分钟"
      }
    } else { Write-Bad 'ssh 未运行' }

    if (Get-SocksListening) { Write-Ok "SOCKS 127.0.0.1:$SocksPort 监听中" }
    else                     { Write-Bad "SOCKS 127.0.0.1:$SocksPort 未监听" }
  }

  'Install' {
    Write-Host '--- 安装登录自启 ---'
    if (-not (Test-Path $KeeperScript)) { throw "找不到 $KeeperScript" }
    New-KeeperShortcut
    if (Test-Path $LnkPath) { Write-Ok "已写入启动文件夹: $LnkPath" }
    else { throw '写入失败' }
    Write-Host ''
    Write-Host '下次登录 Windows 时自动启动隧道保活（只建隧道，不自动切系统代理）。' -ForegroundColor Cyan
    Write-Host '断网时再跑 borrow-net.ps1 On 或双击「借网助手」切代理。' -ForegroundColor Cyan
  }

  'Uninstall' {
    Write-Host '--- 移除登录自启 ---'
    if (Test-Path $LnkPath) {
      Remove-Item $LnkPath -Force
      Write-Ok '已移除'
    } else { Write-Warn '本来就没装' }
  }

  'Test' {
    Write-Host '--- 真实模拟一次登录启动 ---'

    Write-Host '[1/5] 停掉现有 keeper 与 ssh'
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe' or Name='pwsh.exe'" -ErrorAction SilentlyContinue |
      Where-Object { $_.CommandLine -like '*ssh-tunnel-keeper.ps1*' } |
      ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; Write-Host "      停 keeper $($_.ProcessId)" }
    Start-Sleep -Milliseconds 1000
    Get-Process ssh -ErrorAction SilentlyContinue |
      ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue; Write-Host "      停 ssh $($_.Id)" }
    Remove-Item (Join-Path $PSScriptRoot 'borrow-net.keeper.lock') -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    if (Get-SocksListening) { throw '清理不干净，SOCKS 仍在监听' }
    Write-Ok '已清理'

    Write-Host '[2/5] 从启动文件夹的 .lnk 启动'
    if (-not (Test-Path $LnkPath)) { throw "先跑 Install：$LnkPath 不存在" }
    Start-Process $LnkPath
    Start-Sleep -Seconds 8

    Write-Host '[3/5] 检查进程'
    $kp = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
          Where-Object { $_.CommandLine -like '*ssh-tunnel-keeper.ps1*' }
    if (-not $kp) { throw 'keeper 没起来' }
    Write-Ok "keeper PID $(($kp.ProcessId) -join ',')"
    $ssh = Get-Process ssh -ErrorAction SilentlyContinue
    if (-not $ssh) { throw 'ssh 没起来' }
    Write-Ok "ssh PID $(($ssh.Id) -join ',')"

    Write-Host '[4/5] 检查 SOCKS 端口'
    if (-not (Get-SocksListening)) { throw "SOCKS $SocksPort 未监听" }
    Write-Ok "监听中"

    Write-Host '[5/5] 实际过代理验证'
    $code = & curl.exe -s -o NUL -w '%{http_code}' --proxy "socks5h://127.0.0.1:$SocksPort" --max-time 12 'http://www.baidu.com' 2>$null
    if ($code -ne '200') { throw "百度经代理返回 $code" }
    Write-Ok "百度经代理 code=$code"
    $ip = & curl.exe -s --proxy "socks5h://127.0.0.1:$SocksPort" --max-time 12 'http://myip.ipip.net' 2>$null
    Write-Ok "借网出口: $ip"

    Write-Host ''
    Write-Host '✅ 登录自启链路完整可用' -ForegroundColor Green
  }
}

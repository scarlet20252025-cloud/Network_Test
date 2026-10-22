#requires -Version 5.1
<#
  ssh-tunnel-keeper.ps1 — 常驻保活：保持 ssh -D SOCKS 隧道连通远端办公网

  由 borrow-net.ps1 -Action Start 启动，或由计划任务登录时自启。
  设计要点：
    * 本进程前台跑 ssh，ssh 退出（断线/重启）自动重启，5s 间隔防风暴
    * 本进程被杀会带走子 ssh（父子进程），不会留孤儿
    * 单例锁 borrow-net.keeper.lock 防止重复启动
    * 只建立隧道，不改系统代理、不改路由 —— 要不要走代理由 borrow-net.ps1 决定

  参数可与主脚本不一致，主脚本会按当前配置传参。
#>
[CmdletBinding()]
param(
  [string]$RemoteHost = 'pcs',
  [int]$SocksPort   = 10808
)

$ErrorActionPreference = 'Stop'

$sshArgs = @(
  '-N'
  '-o', 'BatchMode=yes'
  '-o', 'ExitOnForwardFailure=yes'
  '-o', 'ServerAliveInterval=30'
  '-o', 'ServerAliveCountMax=3'
  $RemoteHost
  '-D', "127.0.0.1:$SocksPort"
)

$lockPath = Join-Path $PSScriptRoot 'borrow-net.keeper.lock'

# --- 单例检查 ---
if (Test-Path $lockPath) {
  $oldPid = (Get-Content $lockPath -Raw -ErrorAction SilentlyContinue)
  if ($oldPid) {
    $oldPid = "$oldPid".Trim()
    if ($oldPid -and $oldPid -ne "$PID") {
      $proc = Get-Process -Id $oldPid -ErrorAction SilentlyContinue
      if ($proc -and $proc.ProcessName -match 'powershell|pwsh') {
        Write-Host "keeper already running (pid $oldPid), nothing to do"
        exit 0
      }
    }
  }
}
Set-Content -Path $lockPath -Value "$PID" -Encoding ASCII

try {
  while ($true) {
    Write-Host ("[{0}] keeper: ssh -D {1} -> {2}" -f (Get-Date -Format 'HH:mm:ss'), $SocksPort, $RemoteHost)
    & ssh @sshArgs
    $code = $LASTEXITCODE
    Write-Host ("[{0}] keeper: ssh exited ({1}), restart in 5s" -f (Get-Date -Format 'HH:mm:ss'), $code)
    Start-Sleep -Seconds 5
  }
}
finally {
  Remove-Item $lockPath -Force -ErrorAction SilentlyContinue
}

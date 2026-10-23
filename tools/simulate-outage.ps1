#requires -Version 5.1
<#
  simulate-outage.ps1 — 模拟「学校断网」：禁用上网能力但保留局域网

  为什么按这个规则模拟：
    学校的断网方式是「禁用学生账号的上网权限，局域网仍然互通」。
    也就是说：
      - WLAN 接入正常、能拿到 IP、能到网关、能到校内其他机器（含远端）
      - 但去公网的流量被挡
    渡口之所以能生效，正是因为 SSH 走的是**局域网**（到远端 10.x），
    根本不需要公网出口；出公网那一段由远端办公网承担。

  本脚本用 Windows 防火墙出站规则复刻这个状态：
      - 放行 10.0.0.0/8、172.16.0.0/12、192.168.0.0/16（局域网）
      - 放行 127.0.0.0/8（回环：dual-proxy 与 ssh -D 都在本机）
      - 阻断其余全部出站（= 公网被禁）

  ⚠️ 需要管理员权限。执行期间本机无法访问公网（但 SSH 到远端仍通），属预期。
  ⚠️ 万一脚本异常退出没回滚，手动执行：
       Remove-NetFirewallRule -DisplayName 'Ferry-OutageSim-*'

  用法：
    # 管理员 PowerShell
    .\simulate-outage.ps1 -Action Start
    # 验证
    curl.exe -s -o NUL -w "%{http_code}" --noproxy "*" http://myip.ipip.net   # 应失败 = 公网被禁
    curl.exe -s -x http://127.0.0.1:10809 http://myip.ipip.net                # 应成功 = 渡口有效
    # 恢复
    .\simulate-outage.ps1 -Action Stop
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [ValidateSet('Start', 'Stop', 'Status')]
  [string]$Action,
  [string]$RemoteHost = 'pcs',
  [int]$SocksPort = 0,
  [int]$HttpPort = 10809
)

# ---- 端口解析（与 ferry.ps1 / 渡口助手.ps1 共用同一入口）----
# 不自带默认值：写死端口会让本脚本在「开了端口自适应」的环境里探错端口。
. (Join-Path $PSScriptRoot 'Get-FerryPorts.ps1')
$_fp = Get-FerryPorts -SocksPort $SocksPort -HttpPort $(if ($PSBoundParameters.ContainsKey('HttpPort')) { $HttpPort } else { 0 })
$SocksPort = $_fp.SocksPort
if ($PSBoundParameters.ContainsKey('HttpPort')) { $HttpPort = $_fp.HttpPort }

$ErrorActionPreference = 'Stop'
$Tag = 'Ferry-OutageSim'

function Test-Admin {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-Direct {
  $r = & curl.exe -s -o NUL -w '%{http_code}' --noproxy '*' --max-time 6 'http://myip.ipip.net' 2>$null
  return ("$r" -match '^2')
}

function Test-Borrow {
  $r = & curl.exe -s -o NUL -w '%{http_code}' --max-time 8 -x "http://127.0.0.1:$HttpPort" 'http://myip.ipip.net' 2>$null
  return ("$r" -match '^2')
}

function Show-State {
  $sshOk = ("$(ssh -o ConnectTimeout=5 -o BatchMode=yes $RemoteHost 'echo ok' 2>$null)" -match 'ok')
  $d = Test-Direct
  $b = Test-Borrow
  Write-Host ("  SSH 到 {0,-5} : {1}" -f $RemoteHost, $(if ($sshOk) { '通' } else { '断' }))
  Write-Host ("  直连出公网     : {0}" -f $(if ($d) { '通' } else { '断（符合预期）' }))
  Write-Host ("  渡口出公网     : {0}" -f $(if ($b) { '通' } else { '断' }))
  Write-Host ''
  if (-not $d -and $b) {
    Write-Host '✅ 这就是「断网但渡口有效」的状态' -ForegroundColor Green
  }
  elseif (-not $d -and -not $b) {
    if (-not $sshOk) {
      Write-Host '❌ 渡口也断了，因为连不上远端 —— 检查隧道 / keeper' -ForegroundColor Red
    }
    else {
      Write-Host '⚠️ SSH 通但渡口不通 —— 检查 dual-proxy 与系统代理' -ForegroundColor Yellow
    }
  }
  elseif ($d -and $b) {
    Write-Host '（两条都通 = 当前不在断网状态）' -ForegroundColor DarkGray
  }
}

switch ($Action) {

  'Status' {
    $rules = Get-NetFirewallRule -DisplayName "$Tag-*" -ErrorAction SilentlyContinue
    if ($rules) {
      Write-Host '模拟断网：已启用' -ForegroundColor Yellow
      $rules | ForEach-Object { Write-Host "  $($_.DisplayName)  $($_.Direction)  $($_.Action)  Enabled=$($_.Enabled)" }
    }
    else {
      Write-Host '模拟断网：未启用' -ForegroundColor Green
    }
    Write-Host ''
    Show-State
  }

  'Start' {
    if (-not (Test-Admin)) { throw '需要管理员权限（改防火墙）' }
    $old = Get-NetFirewallRule -DisplayName "$Tag-*" -ErrorAction SilentlyContinue
    if ($old) { $old | Remove-NetFirewallRule }
    Write-Host '⚠️ 接下来本机无法访问公网（局域网与 SSH 保持），用 -Action Stop 恢复。' -ForegroundColor Yellow
    Write-Host ''

    foreach ($cidr in @('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16')) {
      $safe = $cidr -replace '[./]', '_'
      New-NetFirewallRule -DisplayName "$Tag-Allow-LAN-$safe" -Direction Outbound `
        -RemoteAddress $cidr -Action Allow -Profile Any | Out-Null
    }
    New-NetFirewallRule -DisplayName "$Tag-Allow-Loopback" -Direction Outbound `
      -RemoteAddress '127.0.0.0/8' -Action Allow -Profile Any | Out-Null
    New-NetFirewallRule -DisplayName "$Tag-Block-Public" -Direction Outbound `
      -Action Block -Profile Any | Out-Null

    Write-Host '模拟断网已启用（局域网通、公网断）' -ForegroundColor Red
    Start-Sleep -Seconds 2
    Show-State
  }

  'Stop' {
    if (-not (Test-Admin)) { throw '需要管理员权限' }
    $rules = Get-NetFirewallRule -DisplayName "$Tag-*" -ErrorAction SilentlyContinue
    if ($rules) {
      $rules | Remove-NetFirewallRule
      Write-Host '已移除模拟断网规则，公网恢复' -ForegroundColor Green
    }
    else {
      Write-Host '没有需要清理的规则' -ForegroundColor Green
    }
    Start-Sleep -Seconds 2
    Show-State
  }
}

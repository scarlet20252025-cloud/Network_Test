#requires -Version 5.1
<#
  setup-client.ps1 — 在一台「使用者机器」上配置借网接入

  为什么要这个脚本（定位修正）：
    校园网对学生账号之间有隔离（实测：从另一账号的机器访问本机，
    22 端口不通、445 通、10803 通 —— 是端口级过滤，不是简单的账号隔离）。
    所以「把本机代理共享出去」这个方案不可靠。

    正确做法：**每台机器自己建隧道直连远端**。远端才是共享点，
    它天然就是给多人用的（一台机器、一个出口、N 条 SSH 隧道）。

  这个脚本在使用者机器上跑一次，做四件事：
    1. 检测/生成 SSH 密钥（没有就建，密码短语留空）
    2. 把公钥贴出来，让你去远端 authorized_keys 加一下（一条命令搞定）
    3. 测试直连远端
    4. 启动本机隧道 + 双协议代理（端口自动探测，不写死）

  用法（在使用者机器上，普通用户即可，不需要管理员）：
    powershell -ExecutionPolicy Bypass -File setup-client.ps1 -RemoteHost pcs
    powershell -ExecutionPolicy Bypass -File setup-client.ps1 -RemoteHost 10.13.24.1 -RemoteUser pcserver

  远端是 Linux 的话把 -RemoteUser 改成对应用户。
#>
[CmdletBinding()]
param(
  [string]$RemoteHost = 'pcs',
  [string]$RemoteUser = 'pcserver',
  [string]$RemotePort = '22',
  # 0 = 自动探测（推荐）
  [int]$SocksPort = 0,
  [int]$HttpPort = 0
)

$ErrorActionPreference = 'Continue'

function Ok($m)   { Write-Host "  [OK] $m" -ForegroundColor Green }
function Bad($m)  { Write-Host "  [X]  $m" -ForegroundColor Red }
function Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Hint($m) { Write-Host "     $m" -ForegroundColor DarkGray }

$SshDir = "$env:USERPROFILE\.ssh"
$KeyPub = Join-Path $SshDir 'id_ed25519.pub'

Step '环境检查'
$sshExe = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh.exe'
$sshKeygen = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh-keygen.exe'
if (-not (Test-Path $sshExe)) {
  # Git 自带 ssh 兜底（很多开发机没装 OpenSSH 客户端）
  $gitSsh = 'C:\Program Files\Git\usr\bin\ssh.exe'
  if (Test-Path $gitSsh) { $sshExe = $gitSsh; Ok "用 Git 自带 ssh: $sshExe" }
  else { Bad '找不到 ssh.exe（OpenSSH 或 Git）'; exit 1 }
} else { Ok "ssh: $sshExe" }

$node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
if (-not $node) { Bad '找不到 node.exe，dual-proxy 无法运行'; exit 1 }
Ok "node: $node"

Step 'SSH 密钥'
if (-not (Test-Path $KeyPub)) {
  Write-Host '  生成新密钥（密码短语直接回车留空）' -ForegroundColor Yellow
  & $sshKeygen -t ed25519 -f "$SshDir\id_ed25519" -N '""' 2>&1 | Out-Null
  if (Test-Path $KeyPub) { Ok '已生成 id_ed25519' } else { Bad '密钥生成失败'; exit 1 }
} else { Ok '已有密钥' }

$pub = (Get-Content $KeyPub -Raw).Trim()
Write-Host ''
Write-Host '  你的公钥（下一步要用）：' -ForegroundColor Yellow
Write-Host "  $pub" -ForegroundColor White

Step '测试能否直连远端'
$test = & $sshExe -o ConnectTimeout=8 -o BatchMode=yes -o StrictHostKeyChecking=accept-new -p $RemotePort "$RemoteUser@$RemoteHost" 'echo CONNECT_OK; hostname' 2>&1
if ("$test" -match 'CONNECT_OK') {
  Ok "已能免密直连 $RemoteUser@${RemoteHost}:$RemotePort"
} else {
  Write-Host ''
  Write-Host '  还不能免密登录。请在**能管理远端的那台机器**上执行：' -ForegroundColor Yellow
  Write-Host ''
  Write-Host "     # 把下面这一行追加到远端的 authorized_keys" -ForegroundColor DarkGray
  Write-Host "     echo '$pub' >> ~/.ssh/authorized_keys" -ForegroundColor White
  Write-Host ''
  Write-Host '  Windows 远端（管理员 PowerShell）：' -ForegroundColor DarkGray
  Write-Host "     Add-Content C:\ProgramData\ssh\administrators_authorized_keys '$pub'" -ForegroundColor White
  Write-Host ''
  Write-Host '  加完后重新运行本脚本。' -ForegroundColor Yellow
  exit 2
}

Step '启动隧道 + 双协议代理'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$keeper = Join-Path $here 'ssh-tunnel-keeper.ps1'
$bridge = Join-Path $here 'dual-proxy.mjs'
if (-not (Test-Path $bridge)) { Bad "找不到 $bridge"; exit 1 }

# keeper（保活 + 断线重连）
$arg = "-NoProfile -ExecutionPolicy Bypass -File `"$keeper`" -RemoteHost $RemoteHost -SocksPort 0"
$null = Start-Process powershell.exe -ArgumentList $arg -WindowStyle Minimized
Ok 'keeper 已启动（端口自动）'
Start-Sleep -Seconds 6

# dual-proxy（双协议单端口）
$barg = "`"$bridge`" --keeper `"$keeper`" --remote $RemoteHost"
$null = Start-Process $node -ArgumentList $barg -WindowStyle Minimized
Ok 'dual-proxy 已启动（端口自动）'
Start-Sleep -Seconds 5

# 读实际端口
$portsFile = Join-Path (Split-Path -Parent $here) '.proxy-state\ports.json'
if (Test-Path $portsFile) {
  $pj = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($portsFile)) | ConvertFrom-Json
  $HttpPort = [int]$pj.http
  $SocksPort = [int]$pj.socks
} else {
  Bad '没读到端口状态文件，dual-proxy 可能没起来'
  exit 1
}

Step '验证'
$t1 = & curl.exe -s -o NUL -w '%{http_code}' --max-time 15 --socks5-hostname "127.0.0.1:$HttpPort" https://www.baidu.com 2>$null
if ("$t1" -match '^[23]') { Ok "SOCKS5 127.0.0.1:$HttpPort 可用" } else { Bad "SOCKS5 不通（$t1）" }

$t2 = & curl.exe -s -o NUL -w '%{http_code}' --max-time 15 -x "http://127.0.0.1:$HttpPort" https://www.baidu.com 2>$null
if ("$t2" -match '^[23]') { Ok "HTTP 127.0.0.1:$HttpPort 可用" } else { Bad "HTTP 不通（$t2）" }

Write-Host ''
Write-Host '===========================================' -ForegroundColor Cyan
Write-Host '  配置完成' -ForegroundColor Cyan
Write-Host "  代理地址 : 127.0.0.1:$HttpPort （HTTP 与 SOCKS5 同端口）" -ForegroundColor White
Write-Host "  SOCKS 端口: $SocksPort" -ForegroundColor White
Write-Host ''
Write-Host '  浏览器 / 应用里填 :' -ForegroundColor Yellow
Write-Host "     socks5  127.0.0.1  $HttpPort" -ForegroundColor White
Write-Host "     或 http 127.0.0.1  $HttpPort" -ForegroundColor White
Write-Host ''
Write-Host '  注意：端口是自动选的。重启后会变，' -ForegroundColor DarkGray
Write-Host '  以 .proxy-state\ports.json 里的值为准。' -ForegroundColor DarkGray
Write-Host '===========================================' -ForegroundColor Cyan

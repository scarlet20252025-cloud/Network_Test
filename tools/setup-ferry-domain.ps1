#requires -Version 5.1
<#
  setup-ferry-domain.ps1 — 让 ferry.internal 能打开门户

  为什么需要它：
    浏览器访问 http://ferry.internal 要能通，取决于两件事：
      1. 这个域名要能解析成远端 IP（hosts 文件）
      2. 浏览器不能把它交给代理（Clash 解析不了 .internal，会回 502）

    第 2 点已自动处理（写 HKCU 的 ProxyOverride，不需要管理员）。
    第 1 点改 hosts 需要管理员 —— 所以单独做成脚本。

  用法（右键「以管理员身份运行 PowerShell」）：
    powershell -ExecutionPolicy Bypass -File setup-ferry-domain.ps1
    powershell -ExecutionPolicy Bypass -File setup-ferry-domain.ps1 -Remove
#>
[CmdletBinding()]
param(
  [string]$RemoteHost = '10.13.24.1',
  [string]$Domain = 'ferry.internal',
  [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$hostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$marker = '# Ferry 渡口'

function Test-Admin {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ── 1. 代理绕行（不需要管理员，先做）──
Write-Host '==> 配置代理绕行（不需要管理员）'
$k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$ov = (Get-ItemProperty $k -Name ProxyOverride -ErrorAction SilentlyContinue).ProxyOverride
if ($null -eq $ov) { $ov = '' }
$parts = $ov -split ';' | Where-Object { $_ }
$need = @($Domain, '*.internal')
$added = @()
foreach ($n in $need) {
  if ($parts -notcontains $n) { $parts += $n; $added += $n }
}
if ($added.Count -gt 0) {
  Set-ItemProperty $k -Name ProxyOverride -Value ($parts -join ';') -Type String
  Write-Host "  已加入绕行列表: $($added -join ', ')"
} else {
  Write-Host '  已在绕行列表中'
}

# 广播设置变更，让已在运行的浏览器立刻感知
$sig = @'
using System;
using System.Runtime.InteropServices;
public class InetRefresh2 {
  [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
  public static extern bool InternetSetOption(IntPtr h, int o, IntPtr b, int l);
  public static void Go() {
    InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
    InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
  }
}
'@
try {
  if (-not ('InetRefresh2' -as [type])) {
    Add-Type -TypeDefinition $sig -Language CSharp -ErrorAction SilentlyContinue
  }
  [InetRefresh2]::Go()
} catch {}

# ── 2. hosts ──
Write-Host ''
Write-Host '==> 配置 hosts'
if (-not (Test-Admin)) {
  Write-Host '  ✗ 需要管理员权限' -ForegroundColor Red
  Write-Host ''
  Write-Host '  请右键 PowerShell →「以管理员身份运行」，再执行本脚本：' -ForegroundColor Yellow
  Write-Host "    powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -ForegroundColor Yellow
  Write-Host ''
  Write-Host '  或者手动往 hosts 里加一行（记事本以管理员打开）：'
  Write-Host "    $RemoteHost  $Domain" -ForegroundColor Cyan
  Write-Host "  文件位置: $hostsPath"
  exit 2
}

# 先清掉本脚本以前加的条目，避免重复/过时
$lines = @(Get-Content $hostsPath -ErrorAction Stop)
$kept = $lines | Where-Object { $_ -notmatch [regex]::Escape($marker) }
if ($Remove) {
  if ($kept.Count -ne $lines.Count) {
    Set-Content -Path $hostsPath -Value $kept -Encoding ASCII
    Write-Host "  已移除 $Domain 条目"
  } else {
    Write-Host '  没有需要移除的条目'
  }
  ipconfig /flushdns | Out-Null
  Write-Host '  已刷新 DNS 缓存'
  exit 0
}

$entry = "$RemoteHost`t$Domain`t$marker"
$kept += $entry
Set-Content -Path $hostsPath -Value $kept -Encoding ASCII
Write-Host "  已写入: $entry"

ipconfig /flushdns | Out-Null
Write-Host '  已刷新 DNS 缓存'

# ── 3. 验证 ──
Write-Host ''
Write-Host '==> 验证'
Start-Sleep -Seconds 1
try {
  $r = Invoke-WebRequest -Uri "http://$Domain/" -UseBasicParsing -TimeoutSec 8 -ErrorAction Stop
  Write-Host "  ✓ http://$Domain/ 返回 $($r.StatusCode)" -ForegroundColor Green
  Write-Host "  标题: $(if ($r.Content -match '<title>(.*?)</title>') { $matches[1] } else { '(无)' })"
} catch {
  Write-Host "  ✗ http://$Domain/ 失败: $($_.Exception.Message)" -ForegroundColor Red
  Write-Host '  排查顺序：'
  Write-Host '    1. 门户在跑吗？  ssh pcs "Get-NetTCPConnection -LocalPort 80 -State Listen"'
  Write-Host "    2. 直连远端通吗？curl --noproxy `"*`" http://$RemoteHost/"
  exit 1
}

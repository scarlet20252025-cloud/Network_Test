# 把渡口的自签名根证书装进「受信任的根证书颁发机构」。
#
# 装完之后：
#   · exe 属性页显示「发布者：Ferry Network Tool」而不是「未知」
#   · Windows SmartScreen 对签名过的版本基本不再弹
#
# **需要管理员权限**（要写 LocalMachine 的证书库）。
#
# 用法（右键「使用 PowerShell 运行」或以管理员身份打开 PowerShell）：
#   powershell -ExecutionPolicy Bypass -File trust-cert.ps1
#   powershell -ExecutionPolicy Bypass -File trust-cert.ps1 -Uninstall   # 撤销信任

param(
  [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$cerPath = Join-Path $here 'ferry-sign.cer'

# 检查管理员
$isAdmin = ([Security.Principal.WindowsPrincipal] `
  [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
  Write-Host '需要管理员权限。请右键此脚本 →「使用 PowerShell 运行」并同意提权，'
  Write-Host '或者以管理员身份打开 PowerShell 后重跑。'
  Read-Host '按回车退出'
  exit 1
}

if ($Uninstall) {
  $found = Get-ChildItem Cert:\LocalMachine\Root |
           Where-Object { $_.Subject -like '*O=Ferry Network Tool*' }
  if (-not $found) { Write-Host '没有找到渡口的根证书，无需撤销。'; Read-Host '按回车退出'; exit 0 }
  foreach ($c in $found) {
    Remove-Item -Path ('Cert:\LocalMachine\Root\' + $c.Thumbprint) -Force
    Write-Host ('已移除: ' + $c.Subject)
  }
  Read-Host '完成，按回车退出'
  exit 0
}

if (-not (Test-Path $cerPath)) {
  throw "找不到 $cerPath。请把 ferry-sign.cer 和本脚本放在同一目录。"
}

$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cerPath
Write-Host ('即将信任这张证书：')
Write-Host ('    主题: ' + $cert.Subject)
Write-Host ('    有效期至: ' + $cert.NotAfter)
Write-Host ('    指纹: ' + $cert.Thumbprint)
Write-Host ''
Write-Host '⚠️  装根证书 = 从此信任这家「发布者」签的所有程序。'
Write-Host '    只在你确认这个 .cer 来自渡口管理员时才继续。'
Write-Host ''
$ans = Read-Host '继续？(y/N)'
if ($ans -notmatch '^[Yy]') { Write-Host '已取消'; exit 0 }

$store = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'LocalMachine')
$store.Open('ReadWrite')
$store.Add($cert)
$store.Close()

Write-Host ''
Write-Host '✅ 已装入「受信任的根证书颁发机构」'
Write-Host '   现在双击 Ferry-Setup 应该不再弹 SmartScreen，或者显示「发布者：Ferry」。'
Write-Host '   如果仍然弹一次：点「更多信息」→「仍要运行」，之后再也不会问。'
Read-Host '按回车退出'

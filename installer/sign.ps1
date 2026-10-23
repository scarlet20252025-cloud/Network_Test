# 给渡口的 exe 和安装包签名。
#
# 顺序很重要：
#   1) build.ps1 编译出 dist\渡口助手.exe
#   2) **先签 exe**（否则安装包里内嵌的是没签名的版本）
#   3) ISCC.exe ferry.iss 打安装包
#   4) 再签安装包
#
# 用 Set-AuthenticodeSignature 而不是 signtool.exe ——
# 前者 Windows PowerShell 自带，后者要装 Windows SDK。
#
# 时间戳很重要：不盖的话，证书一过期，之前签的 exe 全部失效；
# 盖了时间戳，签名在「签名那一刻」有效即可长期使用。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File sign.ps1 -Password "你的密码"
#   powershell -ExecutionPolicy Bypass -File sign.ps1 -SkipTimestamp   # 无网时

param(
  [switch]$SkipTimestamp,
  [string]$Password = ''   # 私钥密码：不入库，调用时用 -Password 传
)

$ErrorActionPreference = 'Stop'
if (-not $Password) { throw "必须用 -Password 指定私钥密码（不要写死在脚本里）" }
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$pfxPath = Join-Path $here 'ferry-sign.pfx'

if (-not (Test-Path $pfxPath)) {
  throw "找不到 $pfxPath。先跑 make-cert.ps1 生成。"
}

# 直接从 pfx 加载带私钥的证书对象，不导入证书库 ——
# 少一步副作用，也不受 CryptoAPI 密钥容器的策略限制。
$sec = ConvertTo-SecureString $Password -Force -AsPlainText
$flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable -bor `
         [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfxPath, $sec, $flags)
if (-not $cert.HasPrivateKey) { throw '证书没有私钥，无法签名。pfx 可能导出错了。' }

Write-Host ('使用证书: ' + $cert.Subject)
Write-Host ('        指纹: ' + $cert.Thumbprint)
Write-Host ''

$targets = @(
  (Join-Path $root 'dist\渡口助手.exe'),
  (Join-Path $root 'dist\Ferry-Setup-0.8.0.exe')
)

$ok = 0
foreach ($t in $targets) {
  if (-not (Test-Path $t)) { Write-Host ('  跳过（不存在）: ' + (Split-Path -Leaf $t)); continue }
  $leaf = Split-Path -Leaf $t
  Write-Host ('  签名: ' + $leaf)
  try {
    $p = @{ FilePath = $t; Certificate = $cert; HashAlgorithm = 'SHA256' }
    if (-not $SkipTimestamp) { $p.TimestampServer = 'http://timestamp.digicert.com' }
    $r = Set-AuthenticodeSignature @p -EA Stop
    # 注意：Status 只有在**本机信任了这张根证书**时才是 Valid。
    # 没装根证书时会显示 UnknownError「在不受信任的根证书中终止」——
    # 这**不代表签名失败**，签名已经嵌进去了，只要看 SignerCertificate 有没有值。
    if ($r.SignerCertificate) {
      Write-Host ('    OK 已嵌入签名（本机信任状态: ' + $r.Status + '）')
      $ok++
    } else {
      Write-Host ('    FAIL ' + $r.Status + ' — ' + $r.StatusMessage)
    }
  } catch {
    Write-Host ('    ERROR ' + $_.Exception.Message)
  }
}

Write-Host ''
Write-Host ('完成：' + $ok + ' / ' + $targets.Count + ' 个文件已签名')
Write-Host ''
Write-Host '查看签名信息（Signer 有值就说明签上了）：'
Write-Host ('  Get-AuthenticodeSignature "' + $targets[1] + '" | Format-List Status, @{n="Signer";e={$_.SignerCertificate.Subject}}')
Write-Host ''
Write-Host '要在本机看到「发布者：Ferry」，需要先装根证书：'
Write-Host '  powershell -ExecutionPolicy Bypass -File trust-cert.ps1   （管理员）'

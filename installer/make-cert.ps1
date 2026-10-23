# 生成渡口的代码签名证书（自签名）。
#
# 为什么不用 New-SelfSignedCertificate：
#   在部分受限环境（本机就是这样）它会失败：
#     CertEnroll::CX509Enrollment::_CreateRequest: 拒绝访问。0x80090010 (NTE_PERM)
#   原因是它要把密钥写进 CryptoAPI 的密钥容器，而容器受策略限制。
#   改用 Git 自带的 openssl 在内存里生成 —— 不碰密钥容器，纯文件操作。
#
# 生成两样东西：
#   ferry-sign.cer  ← 发给客户端，装进「受信任的根证书颁发机构」
#   ferry-sign.pfx  ← 私钥，签名时用。**不要提交 git、不要外发**
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File make-cert.ps1 -Password "你的密码"
#   powershell -ExecutionPolicy Bypass -File make-cert.ps1 -Force

param(
  [switch]$Force,
  [string]$Password = '',   # 私钥密码：不入库，调用时用 -Password 传
  [int]$Years = 5
)

$ErrorActionPreference = 'Stop'
if (-not $Password) { throw "必须用 -Password 指定私钥密码（不要写死在脚本里）" }
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$cerPath = Join-Path $here 'ferry-sign.cer'
$pfxPath = Join-Path $here 'ferry-sign.pfx'

if ((Test-Path $cerPath) -and (Test-Path $pfxPath) -and -not $Force) {
  Write-Host '证书已存在，跳过生成（要重新生成加 -Force）'
  Write-Host ('  ' + $cerPath)
  Write-Host ('  ' + $pfxPath)
  exit 0
}

$ossl = @(
  'C:\Program Files\Git\usr\bin\openssl.exe',
  'C:\Program Files\Git\mingw64\bin\openssl.exe',
  'C:\Program Files\OpenSSL-Win64\bin\openssl.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $ossl) { throw '找不到 openssl.exe（装 Git for Windows 即可，它自带 openssl）' }
Write-Host ('使用 openssl: ' + $ossl)

$work = Join-Path $env:TEMP ('ferrycert-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $work | Out-Null
Push-Location $work
try {
  Write-Host '==> 生成 RSA 2048 自签名证书'
  & $ossl req -x509 -newkey rsa:2048 -keyout key.pem -out cert.pem `
      -days ($Years * 365) -nodes `
      -subj '/CN=Ferry/O=Ferry Network Tool/C=CN' `
      -addext 'extendedKeyUsage=codeSigning' `
      -addext 'keyUsage=digitalSignature' 2>&1 | Out-Null
  if (-not (Test-Path 'cert.pem')) { throw 'openssl 生成失败' }

  Write-Host '==> 导出公钥证书（客户端要装这个）'
  & $ossl x509 -in cert.pem -outform DER -out $cerPath 2>&1 | Out-Null

  Write-Host '==> 导出 pfx（签名用，含私钥）'
  # 用传统算法（3DES/SHA1）而不是 OpenSSL 3 默认的 PBES2：
  # .NET Framework 的 X509Certificate2 对 PBES2 支持不稳，会报「密码无效」。
  & $ossl pkcs12 -export -certpbe PBE-SHA1-3DES -keypbe PBE-SHA1-3DES -macalg sha1 `
      -out $pfxPath -inkey key.pem -in cert.pem `
      -passout ("pass:" + $Password) 2>&1 | Out-Null
  if (-not (Test-Path $pfxPath)) { throw 'pfx 导出失败' }

  Write-Host ''
  Write-Host ('  OK ' + $cerPath + '  (' + (Get-Item $cerPath).Length + ' 字节)')
  Write-Host ('  OK ' + $pfxPath + '  (' + (Get-Item $pfxPath).Length + ' 字节，密码 ' + $Password + ')')
} finally {
  Pop-Location
  Remove-Item $work -Recurse -Force -EA SilentlyContinue
}

Write-Host ''
Write-Host '==> 下一步：'
Write-Host '    1) 签名：powershell -ExecutionPolicy Bypass -File sign.ps1'
Write-Host '    2) 客户端装根证书：把 ferry-sign.cer + trust-cert.ps1 发给使用者，'
Write-Host '       右键 trust-cert.ps1 -> 使用 PowerShell 运行（会弹 UAC 和证书确认框）'
Write-Host ''
Write-Host '注意：ferry-sign.pfx 是私钥，别提交 git、别外发。'

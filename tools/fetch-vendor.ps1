# 取 mihomo 二进制到 vendor/ —— 打包前跑一次。
#
# 为什么不把 mihomo.exe 提交进 Git：
#   61 MB 的二进制进版本库会让 clone 变慢、每次改代码都要传一遍，
#   而且它有自己的上游（MetaCubeX/mihomo）和发布节奏，我们只是消费方。
#   需要时现取，取完校验哈希。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\fetch-vendor.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\fetch-vendor.ps1 -From "D:\Clash Verge\verge-mihomo.exe"

param(
  # 本机已有的 mihomo 路径（默认从 Clash Verge 取）。
  # 为什么优先本地取而不是下载：断网/校园网环境下 GitHub 经常不通，
  # 而开发机上通常已有 Clash Verge。
  [string]$From = 'D:\Clash Verge\verge-mihomo.exe',

  # 没找到本地文件时，从这个 URL 下载（需要能上 GitHub）
  [string]$Url = 'https://github.com/MetaCubeX/mihomo/releases/latest/download/mihomo-windows-amd64-compatible-v1.19.32.zip'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$vendor = Join-Path $root 'vendor'
$dest = Join-Path $vendor 'mihomo.exe'

if (-not (Test-Path $vendor)) { $null = New-Item -ItemType Directory -Path $vendor -Force }

if (Test-Path $From) {
  Write-Host "从本机拷贝: $From"
  Copy-Item $From $dest -Force
} else {
  Write-Host "本地没有，尝试下载: $Url"
  try {
    $zip = Join-Path $env:TEMP 'mihomo.zip'
    $tmp = Join-Path $env:TEMP 'mihomo-extract'
    Invoke-WebRequest -Uri $Url -OutFile $zip -TimeoutSec 180
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    $exe = Get-ChildItem $tmp -Filter '*.exe' -Recurse | Select-Object -First 1
    if (-not $exe) { throw "压缩包里没找到 exe" }
    Copy-Item $exe.FullName $dest -Force
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
  } catch {
    Write-Host "下载失败: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "请先装 Clash Verge，或用 -From 指定已有的 mihomo.exe 路径" -ForegroundColor Yellow
    exit 1
  }
}

$f = Get-Item $dest
$hash = (Get-FileHash $dest -Algorithm SHA256).Hash
Write-Host ''
Write-Host ("已就绪: {0}" -f $f.FullName)
Write-Host ("大小: {0:N1} MB" -f ($f.Length / 1MB))
Write-Host ("SHA256: {0}" -f $hash)

# 版本自检：mihomo -v 会打印版本，能跑起来说明架构匹配
try {
  $v = & $dest -v 2>&1 | Select-Object -First 1
  Write-Host "版本: $v"
} catch {
  Write-Host "警告：无法执行 mihomo（可能是 arm 版本的机器或文件损坏）" -ForegroundColor Yellow
}

Write-Host ''
Write-Host '现在可以编译安装包了：' -ForegroundColor Cyan
Write-Host '  iscc installer\ferry.iss'

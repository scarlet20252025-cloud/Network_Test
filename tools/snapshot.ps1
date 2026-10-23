# 存档：把当前构建快照到 versions/<时间戳>/
#
# 用法（在 borrow-net 根目录）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\snapshot.ps1 -Label "WG 实验前"
param(
  [string]$Label = '',
  [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$ts = Get-Date -Format 'yyyyMMdd-HHmm'
$dst = Join-Path $Root "versions\$ts"
New-Item -ItemType Directory -Force -Path $dst | Out-Null

function Copy-IfExists($src, $name) {
  if (Test-Path $src) {
    Copy-Item $src (Join-Path $dst $name) -Force
    Write-Host ("  + " + $name)
  } else {
    Write-Host ("  - " + $name + "（不存在，跳过）")
  }
}

Write-Host "快照到 $dst"
Copy-IfExists (Join-Path $Root 'dist\渡口助手.exe') '渡口助手.exe'
Copy-IfExists (Join-Path $Root 'dist\Ferry-Setup-0.8.0.exe') 'Ferry-Setup-0.8.0.exe'
Copy-IfExists (Join-Path $Root 'portal\portal-server.py') 'portal-server.py'
Copy-IfExists (Join-Path $Root 'installer\ferry.iss') 'ferry.iss'

# 源码打成 zip —— 回退时不用猜是哪个 commit
$srcDir = Join-Path $Root 'ferry-client'
if (Test-Path $srcDir) {
  $zip = Join-Path $dst 'ferry-client-src.zip'
  if (Test-Path $zip) { Remove-Item $zip -Force }
  Compress-Archive -Path (Join-Path $srcDir '*') -DestinationPath $zip -Force
  Write-Host "  + ferry-client-src.zip"
}

# META
$commit = ''
try { $commit = (git -C $Root rev-parse --short HEAD 2>$null) } catch {}
$ver = ''
$exe = Join-Path $Root 'dist\渡口助手.exe'
if (Test-Path $exe) { $ver = (Get-Item $exe).VersionInfo.FileVersion }

$meta = @()
$meta += "时间: $ts"
$meta += "标签: $Label"
$meta += "客户端版本: $ver"
$meta += "git commit: $commit"
$meta += ""
foreach ($f in (Get-ChildItem $dst -File)) {
  $h = (Get-FileHash $f.FullName -Algorithm MD5).Hash.ToLower()
  $meta += ("{0}  {1}  {2} B  md5={3}" -f $f.Name, '', $f.Length, $h)
}
Set-Content -Path (Join-Path $dst 'META.txt') -Value ($meta -join "`r`n") -Encoding UTF8
Write-Host "  + META.txt"

# git tag（失败不阻塞）
try {
  git -C $Root tag -f "snapshot-$ts" 2>$null | Out-Null
  Write-Host "  + git tag snapshot-$ts"
} catch {
  Write-Host "  - git tag 失败（可能是没配 git 或无改动）"
}

Write-Host ""
Write-Host "完成。回退方法见 VERSIONS.md"
Write-Host ("  目录: " + $dst)

# 编译渡口客户端（WPF 版）。
#
# 为什么从 WinForms 换成 WPF：
#   WinForms 的布局是「坐标 + 固定像素」——控件位置和高度都要手算，
#   字体却随 DPI 缩放。这两件事天然冲突，我们为此修了六七轮：
#   裁字、重叠、按钮文字消失、MinimumSize 压制 AutoSize……
#   每次都是「算准了」，换个 DPI 又崩。
#
#   WPF 的布局是「测量 → 排列」两阶段：控件高度由内容决定，
#   TextWrapping 自动换行，Grid/StackPanel 自动分配空间。
#   同一个界面在 100% / 125% / 150% 下**不需要任何特殊处理**。
#
# 仍然用 csc 直接编译（不引 .NET SDK / 不用 XAML）：
#   · 目标机器只需 .NET Framework 4.8（Win10/11 自带）
#   · 纯 C# 代码构建 UI，没有 XAML 编译步骤，改一行就能编
#
# 用法：powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.0.0

param(
  [string]$Version = '1.0.0',
  [string]$OutDir  = ''
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$csc = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "找不到 csc.exe：$csc" }

# 版本号写进 AssemblyInfo（客户端靠它判断要不要提示更新）
$ai = Join-Path $here 'src\AssemblyInfo.cs'
$aiText = Get-Content -Raw -Encoding UTF8 $ai
$full = if ($Version -match '^\d+\.\d+\.\d+$') { "$Version.0" } else { $Version }
$aiText = [regex]::Replace($aiText, 'AssemblyVersion\("[^"]*"\)', "AssemblyVersion(`"$full`")")
$aiText = [regex]::Replace($aiText, 'AssemblyFileVersion\("[^"]*"\)', "AssemblyFileVersion(`"$full`")")
[IO.File]::WriteAllText($ai, $aiText, (New-Object Text.UTF8Encoding $true))

$out = Join-Path $OutDir '渡口助手.exe'
$srcs = Get-ChildItem (Join-Path $here 'src') -Filter *.cs | ForEach-Object { $_.FullName }

# WPF 的程序集在 GAC 里，不在标准引用路径 —— 必须给绝对路径。
# PresentationCore 在 GAC_64（有原生代码），其余在 GAC_MSIL。
$msil = 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL'
$g64  = 'C:\Windows\Microsoft.NET\assembly\GAC_64'
$refs = @(
  'System.dll', 'System.Core.dll', 'System.Management.dll',
  "$msil\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll",
  "$g64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll",
  "$msil\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll",
  "$msil\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll"
) | ForEach-Object { "/r:$_" }

$icon = Join-Path $here 'app.ico'
$iconArg = if (Test-Path $icon) { "/win32icon:$icon" } else { '' }

$manifest = Join-Path $here 'src\app.manifest'
if (-not (Test-Path $manifest)) { throw "缺少 manifest：$manifest" }

$cscArgs = @(
  '/nologo', '/target:winexe', "/out:$out", '/codepage:65001',
  '/optimize+', '/platform:anycpu', '/warn:2',
  "/win32manifest:$manifest"
) + $refs + @($iconArg, '') + $srcs
$cscArgs = $cscArgs | Where-Object { $_ -ne '' }

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }

$f = Get-Item $out
Write-Host ("Compiled: " + $f.FullName + "  (" + $f.Length + " bytes)")

# 编译渡口客户端。用 Windows 自带的 csc.exe（.NET Framework 4.8 的一部分），
# 不引入任何 NuGet、不需要 dotnet SDK —— 产物 ~200 KB，Win10/11 双击即用。
#
# 为什么用 .NET Framework 而不是 .NET 6/8：
#   · Win10/11 自带 4.8 运行时，目标机器**什么都不用装**
#   · self-contained 的 .NET 8 单文件要 60 MB+，对一个改代理的小工具太重
#   · 代价是只能用 C# 5 语法（没有字符串插值、没有 ?.），代码里已经避开
#
# 用法：powershell -ExecutionPolicy Bypass -File build.ps1 [-Version 0.7.0]

param(
  [string]$Version = '0.8.0',
  [string]$OutDir  = ''
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$csc = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "找不到 csc.exe：$csc" }

# 版本号同时写进 AssemblyInfo，客户端靠 Application.ProductVersion 判断要不要提示更新
$ai = Join-Path $here 'src\AssemblyInfo.cs'
$aiText = Get-Content -Raw -Encoding UTF8 $ai
$full = if ($Version -match '^\d+\.\d+\.\d+$') { "$Version.0" } else { $Version }
$aiText = [regex]::Replace($aiText, 'AssemblyVersion\("[^"]*"\)', "AssemblyVersion(`"$full`")")
$aiText = [regex]::Replace($aiText, 'AssemblyFileVersion\("[^"]*"\)', "AssemblyFileVersion(`"$full`")")
[IO.File]::WriteAllText($ai, $aiText, (New-Object Text.UTF8Encoding $true))

$out = Join-Path $OutDir '渡口助手.exe'
$srcs = Get-ChildItem (Join-Path $here 'src') -Filter *.cs | ForEach-Object { $_.FullName }

$refs = @(
  'System.dll', 'System.Core.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll',
  'System.Management.dll'
) | ForEach-Object { "/r:$_" }

$icon = Join-Path $here 'app.ico'
$iconArg = if (Test-Path $icon) { "/win32icon:$icon" } else { '' }

# /codepage:65001 —— 源码里全是中文注释和界面文案，不指定代码页会按 ANSI 读，直接乱码
#
# /win32manifest —— 嵌入 app.manifest，让 exe 默认以管理员身份启动。
# 为什么需要：TUN 模式要建虚拟网卡 + 改路由表，必须提权。
# 没有 manifest 时用户得手动「右键→以管理员身份运行」，从快捷方式启动时
# 那个选项还不生效，体验很差。
# 注意 csc 的 /win32manifest 不接受空值，文件不存在就直接报错，
# 不能像 icon 那样静默跳过 —— 静默跳过的结果是「以为嵌入了其实没有」。
$manifest = Join-Path $here 'src\app.manifest'
if (-not (Test-Path $manifest)) { throw "缺少 manifest：$manifest（TUN 需要它来提权）" }

$args = @(
  '/nologo', '/target:winexe', "/out:$out", '/codepage:65001',
  '/optimize+', '/platform:anycpu', '/warn:2',
  "/win32manifest:$manifest"
) + $refs + @($iconArg, '') + $srcs
$args = $args | Where-Object { $_ -ne '' }

& $csc $args
if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }

$f = Get-Item $out
Write-Host ("编译成功：{0}  ({1:N0} 字节, {2} KB)" -f $f.FullName, $f.Length, [math]::Round($f.Length / 1KB), '')

# 编译并运行 tools\verify-tunconfig.cs。
#
# 为什么不用 tools\verify.cmd：它只把 Proxy.cs + CnIpRanges.cs 一起编，
# 而这个验证器要用反射调 TunMode.BuildConfig，必须把整个 src\*.cs 编进来；
# src 里另有程序入口，所以还得用 /main:TunCfgCheck 指定验证器的 Main。
# 1.1.0 起 BuildConfig 的 udpHost 会写进配置，这个结构断言必须能真跑起来。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\verify-tunconfig.ps1

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$compiler = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $compiler)) { throw "找不到编译器：$compiler" }

# 与 build-wpf.ps1 相同的引用集：src 里有 WPF 窗口类，缺这些 GAC 引用会编不过。
$msil = 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL'
$g64  = 'C:\Windows\Microsoft.NET\assembly\GAC_64'
$refs = @(
  'System.dll', 'System.Core.dll', 'System.Management.dll',
  "$msil\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll",
  "$g64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll",
  "$msil\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll",
  "$msil\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll"
) | ForEach-Object { "/r:$_" }

$outDir = Join-Path $env:TEMP 'ferry-verify'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$exe = Join-Path $outDir 'verify-tunconfig.exe'
$srcs = Get-ChildItem (Join-Path $root 'ferry-client\src') -Filter *.cs |
        ForEach-Object { $_.FullName }

$argList = @('/nologo', '/target:exe', '/main:TunCfgCheck', '/codepage:65001',
             "/out:$exe") + $refs + @((Join-Path $here 'verify-tunconfig.cs')) + $srcs

& $compiler $argList
if ($LASTEXITCODE -ne 0) { throw "验证器编译失败（exit $LASTEXITCODE）" }
& $exe
if ($LASTEXITCODE -ne 0) { throw "结构断言未全过（exit $LASTEXITCODE）" }

# 编译并运行「生命周期」验证（借网/直连反复切换、Clash 开与关、UDP、残局）。
#
# 为什么要有它：客户端最怕的不是功能不对，而是**状态收不干净** ——
# 停不掉、代理还原不回去、下次启动被上次的残骸挡死。这些都只有反复启停才暴露。
#
# 注意：这个测试会真的借网（走 RemoteHost 的 SOCKS），也会改本机的系统代理设置，
# 所以**不要在用户正在用的机器上跑**，统一在测试机（10.113.66.6）上跑。
# 结束后会把系统代理设成 127.0.0.1:7897（测试机上是无害值）。

param(
    [string]$Out = "$env:TEMPerry-lifecycle-test.exe"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'ferry-client\src'
$csc = "$env:SystemRoot\Microsoft.NET\Framework644.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc not found: $csc" }

$srcs = Get-ChildItem $src -Filter *.cs | Where-Object { $_.Name -ne 'ProgramWpf.cs' } |
        ForEach-Object { $_.FullName }
$srcs += (Join-Path $PSScriptRoot 'verify-lifecycle.cs')

$msil = 'C:\Windows\Microsoft.NETssembly\GAC_MSIL'
$g64 = 'C:\Windows\Microsoft.NETssembly\GAC_64'
$refs = @(
    'System.dll', 'System.Core.dll', 'System.Management.dll',
    'System.Windows.Forms.dll', 'System.Drawing.dll',
    "$msil\WindowsBase4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll",
    "$g64\PresentationCore4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll",
    "$msil\PresentationFramework4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll",
    "$msil\System.Xaml4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll"
) | ForEach-Object { "/r:$_" }

& $csc /nologo /target:exe "/out:$Out" /codepage:65001 /platform:anycpu $refs $srcs
if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }

Write-Host "built: $Out"
& $Out
exit $LASTEXITCODE

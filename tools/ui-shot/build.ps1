param([string]$Out = "")
$ErrorActionPreference = 'Stop'
$base = $PSScriptRoot
if (-not $Out) { $Out = Join-Path $base 'shot.exe' }
$csc = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$srcs = Get-ChildItem (Join-Path $base 'src') -Filter *.cs |
  Where-Object { $_.Name -ne 'ProgramWpf.cs' } |
  ForEach-Object { $_.FullName }
$srcs += (Join-Path $base 'shot.cs')

# WPF assemblies live in the GAC, not on the default reference path.
$msil = 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL'
$g64  = 'C:\Windows\Microsoft.NET\assembly\GAC_64'
$refs = @(
  'System.dll', 'System.Core.dll', 'System.Management.dll', 'System.Drawing.dll',
  'System.Windows.Forms.dll',
  "$msil\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll",
  "$g64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll",
  "$msil\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll",
  "$msil\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll"
) | ForEach-Object { "/r:$_" }

& $csc /nologo /target:exe "/out:$Out" /codepage:65001 /platform:anycpu $refs $srcs
if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }
Write-Host ("OK: " + $Out)

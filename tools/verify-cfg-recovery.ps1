param([string]$Out = "$env:TEMP\ferry-cfg-recovery-test.exe")

# verify-cfg-recovery.ps1 -- build + run tools\verify-cfg-recovery.cs.
#
# Checks the three layers that keep the activation token alive across
# uninstall / reinstall / new install dir:
#   1. Cfg.Save mirrors the full config to %APPDATA%\Ferry\config.backup.json
#   2. startup Cfg.RecoverFromRoamingBackup restores it
#   3. the installer also reads that backup (see installer/ferry.iss)
#
# Pure ASCII on purpose (PS 5.1 reads BOM-less UTF-8 as ANSI and a stray
# Chinese comment then breaks parsing at a misleading line).

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$src  = Join-Path $root 'ferry-client\src'
$csc  = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

# ProgramWpf.cs owns Main() -- exclude it so the test can be the entry point.
$srcs = Get-ChildItem $src -Filter *.cs |
  Where-Object { $_.Name -ne 'ProgramWpf.cs' } |
  ForEach-Object { $_.FullName }
$srcs += (Join-Path $here 'verify-cfg-recovery.cs')

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

& $Out
$code = $LASTEXITCODE
Remove-Item $Out -Force -ErrorAction SilentlyContinue
exit $code

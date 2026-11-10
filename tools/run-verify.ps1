# Compile and run a tools\*.cs verifier together with ALL of ferry-client\src\*.cs.
#
# Why not tools\verify.cmd: it only compiles Proxy.cs + CnIpRanges.cs, so any
# verifier referencing FerryRuntime / TunMode / Cfg / FerryCore fails with CS0246.
# src also contains the app entry point, so /main: must name the verifier class.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\run-verify.ps1 -Name verify-scenarios
#
# NOTE: keep this file pure ASCII, no BOM tricks. Windows PowerShell 5.1 reads
# BOM-less UTF-8 scripts as ANSI; a mangled Chinese comment can swallow the next
# line and silently blank out variables used in the csc reference list.
#
# The entry class is the first "class Xxx" (static or not) in the verifier.
# Compile failure throws instead of being reported as a PASS.

param(
  [Parameter(Mandatory = $true)][string]$Name
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$compiler = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $compiler)) { throw "csc not found: $compiler" }

$verifier = Join-Path $here ($Name + '.cs')
if (-not (Test-Path $verifier)) { throw "verifier not found: $verifier" }

$text = Get-Content $verifier -Raw -Encoding UTF8
$m = [regex]::Match($text, '(?m)^\s*(?:(?:public|internal)\s+)?(?:static\s+)?class\s+(\w+)')
if (-not $m.Success) { throw "entry class not found: $verifier" }
$mainClass = $m.Groups[1].Value

# Same reference set as build-wpf.ps1. Paths are literal: variable interpolation
# inside a double-quoted path was the source of a broken /r: argument.
$refs = @(
  'System.dll', 'System.Core.dll', 'System.Management.dll',
  'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll',
  'C:\Windows\Microsoft.NET\assembly\GAC_64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll',
  'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll',
  'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll'
) | ForEach-Object { "/r:$_" }

$outDir = Join-Path $env:TEMP 'ferry-verify'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$exe = Join-Path $outDir ($Name + '.exe')
$srcs = Get-ChildItem (Join-Path $root 'ferry-client\src') -Filter *.cs |
        ForEach-Object { $_.FullName }

$argList = @('/nologo', '/target:exe', "/main:$mainClass", '/codepage:65001',
             "/out:$exe") + $refs + @($verifier) + $srcs
& $compiler $argList
if ($LASTEXITCODE -ne 0) { throw "compile failed: $Name (exit $LASTEXITCODE)" }

# Some verifiers (verify-regression-guard etc.) read source/doc files by paths
# relative to Environment.CurrentDirectory. The exe lives in %TEMP%, so run it
# from the project root; otherwise every Read() returns null and the verifier
# dies with a bare NullReferenceException instead of a meaningful FAIL.
Push-Location $root
try {
  # A native command writing to stderr becomes a terminating PowerShell error
  # under $ErrorActionPreference='Stop' even when it exits 0. Judge only by
  # the exit code; otherwise a stray CLR diagnostic shows up as a fake THREW.
  $prev = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  & $exe
  $code = $LASTEXITCODE
  $ErrorActionPreference = $prev
} finally {
  Pop-Location
}

# No exit here: "exit" in a script called with & terminates the whole host
# session and would break batch runs. Success returns normally, failure throws.
if ($code -ne 0) { throw "verifier failed: $Name (exit $code)" }

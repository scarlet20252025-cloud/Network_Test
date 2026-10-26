param(
  [string]$RemoteHost = 'xt',
  [string]$OutDir = "$env:TEMP\ferry-ui-shots",
  [double]$Scale = 1.0
)

# render-ui.ps1 -- render the WPF client UI on a REMOTE test machine.
#
# Why remote: the only honest check for layout bugs is a real render, but the
# user's desktop is usually occupied by other programs and he does not want
# test windows popping up on his own machine. 10.113.66.6 is a spare box.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\render-ui.ps1
#   powershell -ExecutionPolicy Bypass -File tools\render-ui.ps1 -Scale 1.25
#
# Output: <OutDir>\shot-main.png / shot-settings.png / shot-feedback.png
#
# NOTE: this file is pure ASCII on purpose -- Windows PowerShell 5.1 reads a
# UTF-8 file without BOM as ANSI, and a stray Chinese comment then breaks
# parsing in a way that points at the wrong line. Keep it ASCII.

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$src  = Join-Path $root 'ferry-client\src'
$kit  = Join-Path $here 'ui-shot'
$rdir = 'C:/ferry-ui'

$tmp = Join-Path $env:TEMP ('ferry-ui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
Copy-Item $src (Join-Path $tmp 'src') -Recurse
Copy-Item (Join-Path $kit 'shot.cs')   $tmp
Copy-Item (Join-Path $kit 'build.ps1') $tmp

$zip = Join-Path $env:TEMP 'ferry-ui.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
# Use .NET ZipFile instead of tar: MSYS tar treats "C:\..." as "host:path"
# ("Cannot connect to C"). Zip + Expand-Archive is portable across both ends.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($tmp, $zip)

Write-Host '==> uploading sources to ' $RemoteHost
& scp -o BatchMode=yes -o ConnectTimeout=10 $zip "${RemoteHost}:C:/ferry-ui.zip"
if ($LASTEXITCODE -ne 0) { throw 'scp upload failed' }

Write-Host '==> unpack + build + render'
$remote = @'
if (Test-Path C:\ferry-ui) { Remove-Item C:\ferry-ui -Recurse -Force }
New-Item -ItemType Directory C:\ferry-ui | Out-Null
Expand-Archive -Path C:\ferry-ui.zip -DestinationPath C:\ferry-ui -Force
powershell -ExecutionPolicy Bypass -NoProfile -File C:\ferry-ui\build.ps1
New-Item -ItemType Directory -Force C:\ferry-ui\out | Out-Null
& C:\ferry-ui\shot.exe C:\ferry-ui\out SCALE C:\ferry-ui\appdata
'@
$remote = $remote.Replace('SCALE', $Scale.ToString([System.Globalization.CultureInfo]::InvariantCulture))
& ssh -o BatchMode=yes -o ConnectTimeout=10 $RemoteHost $remote
if ($LASTEXITCODE -ne 0) { throw 'remote build/render failed' }

New-Item -ItemType Directory -Force $OutDir | Out-Null
& scp -o BatchMode=yes "${RemoteHost}:C:/ferry-ui/out/*.png" $OutDir
if ($LASTEXITCODE -ne 0) { throw 'scp download failed' }

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ''
Write-Host ('done -> ' + $OutDir)
Get-ChildItem $OutDir -Filter *.png | Select-Object Name, Length | Format-Table -AutoSize

# Deploy portal-server.py to the remote host (pcs / 10.13.24.1).
# All output is ASCII so this file stays safe even without a BOM.
$ErrorActionPreference = 'Continue'
$dir  = 'C:\ferry-portal'
$new  = Join-Path $dir 'portal-server.py.new'
$cur  = Join-Path $dir 'portal-server.py'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

if (-not (Test-Path $new)) { throw "missing $new" }
Write-Host "[1/4] stop running portal"
Get-CimInstance Win32_Process -Filter "Name='python.exe'" | ForEach-Object {
  if ($_.CommandLine -and $_.CommandLine.Contains('portal-server')) {
    Write-Host "      stop pid=$($_.ProcessId)"
    Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
  }
}
Start-Sleep -Seconds 2

Write-Host "[2/4] backup + swap"
if (Test-Path $cur) {
  $bak = Join-Path $dir ("portal-server.py.bak-" + $stamp)
  Copy-Item $cur $bak -Force
  Write-Host "      backup -> $bak"
}
Move-Item $new $cur -Force
Write-Host "      new size = $((Get-Item $cur).Length)"

Write-Host "[3/4] start scheduled task FerryPortal"
schtasks /Run /TN FerryPortal | Out-Null
Start-Sleep -Seconds 5

Write-Host "[4/4] status"
$p = Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
     Where-Object { $_.CommandLine -like '*portal-server*' }
if ($p) { Write-Host "      running pid=$($p.ProcessId)" }
else    { Write-Host "      NOT RUNNING" }
$r = try { (Invoke-WebRequest 'http://127.0.0.1:8080/' -TimeoutSec 10 -UseBasicParsing).StatusCode } catch { 'ERR' }
Write-Host "      local http 8080 = $r"

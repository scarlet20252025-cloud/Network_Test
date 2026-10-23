@echo off
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
"C:\Program Files\WireGuard\wireguard.exe" /uninstalltunnelservice ferry-wg
echo ÒÑÍ£Ö¹ ferry-wg ËíµÀ
pause
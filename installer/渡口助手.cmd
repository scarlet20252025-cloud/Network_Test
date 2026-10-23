@echo off
chcp 65001 >nul 2>&1
setlocal
set "P=%~dp0渡口助手.ps1"
if not exist "%P%" (
  echo 找不到 渡口助手.ps1，安装包可能不完整。
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%P%"
endlocal

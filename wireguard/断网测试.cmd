@echo off
setlocal
title 渡口 · 断网测试
net session >nul 2>&1
if errorlevel 1 (
  echo 需要管理员权限，正在请求提权……
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0断网测试.ps1"
echo.
pause
@echo off
chcp 65001 >nul 2>&1
setlocal
title 渡口 · WireGuard 线路测试

net session >nul 2>&1
if errorlevel 1 (
  echo 需要管理员权限，正在请求提权……
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

set "WG=C:\Program Files\WireGuard\wireguard.exe"
set "TOOL=C:\Program Files\WireGuard\wg.exe"
set "CONF=%~dp0ferry-wg.conf"

if not exist "%WG%" ( echo 找不到 WireGuard & pause & exit /b 1 )
if not exist "%CONF%" ( echo 找不到配置 %CONF% & pause & exit /b 1 )

echo.
echo === 清理旧的 ferry-wg 隧道 ===
"%WG%" /uninstalltunnelservice ferry-wg 2>nul

echo === 安装隧道 ferry-wg ===
"%WG%" /installtunnelservice "%CONF%"
if errorlevel 1 ( echo   安装失败 & pause & exit /b 1 )

echo === 等 8 秒看握手 ===
timeout /t 8 /nobreak >nul
"%TOOL%" show ferry-wg
echo.
echo === ping 隧道对端 10.66.67.1 ===
ping -n 3 10.66.67.1
echo.
echo 判读：
echo   有 "latest handshake: N seconds ago" + ping 通  = UDP 通，线路可用
echo   只有 "transfer: 0 B received" 且无握手           = UDP 被拦
echo.
echo 退出测试：运行  stop-wg.cmd
pause
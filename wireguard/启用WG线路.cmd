@echo off
setlocal
title 渡口 · WireGuard 低延迟线路

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
if not exist "%CONF%" ( echo 找不到配置 & pause & exit /b 1 )

echo.
echo === 1) 清理旧隧道 ===
"%WG%" /uninstalltunnelservice ferry-wg 2>nul

echo === 2) 安装隧道 ===
"%WG%" /installtunnelservice "%CONF%"
if errorlevel 1 ( echo   失败 & pause & exit /b 1 )

echo === 3) 等握手 ===
timeout /t 8 /nobreak >nul
"%TOOL%" show ferry-wg
echo.

echo === 4) 测隧道内 SOCKS5 ===
echo   远端 10.66.67.1:1080 上的 SOCKS5 服务
curl.exe -s --max-time 10 --socks5-hostname 10.66.67.1:1080 "http://www.baidu.com" -o nul -w "  HTTP: %%{http_code}  %%{time_total}s  %%{size_download}B\n"
curl.exe -s --max-time 10 --socks5-hostname 10.66.67.1:1080 "https://www.baidu.com" -o nul -w "  HTTPS: %%{http_code}  %%{time_total}s  %%{size_download}B\n"
echo.

echo ============================================================
echo  怎么用：
echo.
echo  浏览器如果支持 SOCKS5，直接填：
echo     地址 10.66.67.1    端口 1080    SOCKS5（不是 HTTP）
echo.
echo  Chrome / Edge 不支持单独配 SOCKS5，可以启动参数：
echo    chrome.exe --proxy-server="socks5://10.66.67.1:1080"
echo.
echo  Firefox 可以直接在 设置-网络 里配 SOCKS5。
echo.
echo  注意：这条路只对「认代理」的程序有效。
echo        系统代理本身不支持 SOCKS5；游戏走 UDP，这条路覆盖不到。
echo ============================================================
echo.
echo 退出：运行 stop-wg.cmd
pause
@echo off
rem 装 1.0.47 —— 请先退出火绒再运行本文件。
rem
rem 为什么需要你手动做：
rem   1.0.47 的安装器在这台机器上创建 %TEMP%\is-XXXX.tmp 时被拒（错误 5）。
rem   已实测排除：Temp 目录 ACL、磁盘空间、Zone.Identifier、
rem   Smart App Control、AppLocker、以及「从 %TEMP% 执行程序」本身
rem   （手动 CreateProcess 一个放在 %TEMP% 里的 exe 是成功的）。
rem   火绒 HipsDaemon 在跑，剩下最可能就是它的「软件安装拦截」。
rem
rem 步骤：
rem   1. 右键火绒托盘图标 → 退出（或临时关闭防护）
rem   2. 双击本文件
rem   3. 装完把火绒打开
rem
rem 装好后本脚本会自动验证版本号并打印结论。
setlocal
cd /d "%~dp0.."
set SETUP=dist\Ferry-Setup-1.0.47.exe
if not exist "%SETUP%" (
  echo 找不到 %SETUP%
  pause
  exit /b 1
)
echo 正在安装 1.0.47（静默 + 日志 + 关闭占用程序）...
"%SETUP%" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /LOG="%TEMP%\ferry-1.0.47-install.log"
echo.
echo === 安装退出码: %ERRORLEVEL% ===
echo 日志: %TEMP%\ferry-1.0.47-install.log
echo.
echo === 检查装上的版本 ===
powershell -NoProfile -Command "$v=(Get-Item \"$env:LOCALAPPDATA\Ferry\渡口助手.exe\" -ErrorAction SilentlyContinue).VersionInfo; if($v){'已装版本: '+$v.FileVersion}else{'没找到 exe'}"
echo.
echo 如果是 1.0.47.0 就成功了。
pause

@echo off
rem Compile + run a tools\*.cs verifier against the current sources.
rem
rem Usage: verify.cmd <tool.cs> [extra source, relative to repo root]
rem   verify.cmd verify-udp-upstream.cs
rem   verify.cmd verify-hotspot-picker.cs ferry-client/src/Hotspot.cs
rem
rem Why this file exists: the repo lives under E:\ with Chinese characters, and
rem cmd.exe mangles UTF-8 arguments handed to csc (the source path turns into
rem mojibake and csc reports "找不到源文件"). Keeping the paths inside a UTF-8
rem (BOM) batch file is the only reliable way on this box.
rem
rem Proxy.cs + CnIpRanges.cs are always compiled in because every verifier so
rem far needs ProxyServer; the second argument adds one more source file.
setlocal
chcp 65001 > nul
set ROOT=E:\肥鱼不乐园\borrow-net
set OUT=%TEMP%\ferry-verify
if not exist "%OUT%" mkdir "%OUT%"
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set SRC=%ROOT%\tools\%~1
set EXTRA=%~2
set EXE=%OUT%\%~n1.exe

rem Paths are written literally instead of via "%ROOT%\%EXTRA%".
rem Why: cmd's %~2 strips nothing useful but joining variables with a
rem subdirectory produces "…\borrow-netHotspot.cs"-style garbage when the
rem variable ends up empty. Literal paths never do that.
if not "%EXTRA%"=="" goto with_extra
"%CSC%" -nologo -out:"%EXE%" -r:System.dll "%SRC%" "E:\肥鱼不乐园\borrow-net\ferry-client\src\Proxy.cs" "E:\肥鱼不乐园\borrow-net\ferry-client\src\CnIpRanges.cs"
goto run

:with_extra
if "%EXTRA%"=="ferry-client/src/Hotspot.cs" goto extra_hotspot
echo unknown EXTRA: %EXTRA%
exit /b 2

:extra_hotspot
"%CSC%" -nologo -out:"%EXE%" -r:System.dll "%SRC%" "E:\肥鱼不乐园\borrow-net\ferry-client\src\Proxy.cs" "E:\肥鱼不乐园\borrow-net\ferry-client\src\CnIpRanges.cs" "E:\肥鱼不乐园\borrow-net\ferry-client\src\Hotspot.cs"

:run
if errorlevel 1 exit /b 1
"%EXE%"
exit /b %errorlevel%

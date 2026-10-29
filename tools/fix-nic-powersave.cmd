@echo off
rem 以管理员身份运行 fix-nic-powersave.ps1（会弹 UAC）。
rem
rem 用法：双击本文件，或在 cmd 里执行 tools\fix-nic-powersave.cmd
rem 作用：关掉 Realtek 8852BE 的省电休眠（IpsEn 1 -> 0），重启网卡，复测延迟。
rem 会断网几秒，属正常。
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','%~dp0fix-nic-powersave.ps1'"

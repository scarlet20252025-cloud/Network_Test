@echo off
rem 以管理员身份运行 fix-wifi-aggregation.ps1（关掉 A-MSDU 聚合 + LPS 省电）。
rem
rem 用法：双击本文件，UAC 弹窗点「是」。
rem 它会：存档原值 -> 改两个注册表项 -> 重启网卡（断网 5-10 秒）-> 复测判读。
rem 想还原：右键终端(管理员)，跑
rem   powershell -ExecutionPolicy Bypass -File tools\fix-wifi-aggregation.ps1 -Revert
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','%~dp0fix-wifi-aggregation.ps1'"

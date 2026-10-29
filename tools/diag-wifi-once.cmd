@echo off
rem 一次跑完无线抖动诊断（只读）。
rem 想同时应用修改（关 LpsEn/CoreAMSDU）就在后面加 -Apply：
rem   tools\diag-wifi-once.cmd -Apply
cd /d "%~dp0"
echo 正在请求管理员权限（诊断本身只读，但读注册表高级项需要提权）...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','%~dp0diag-wifi-once.ps1','%*'"

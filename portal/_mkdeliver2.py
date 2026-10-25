import io
import os
import shutil

ws = r'E:\肥鱼不乐园'
desk = r'C:\Users\63526\Desktop'
src_exe = os.path.join(ws, '渡口助手-0.9.5.exe')

# 清掉上一版交付物，避免点错
for name in ('渡口助手-0.9.4.exe', '覆盖更新-0.9.4.cmd'):
    for base in (ws, desk):
        f = os.path.join(base, name)
        if os.path.exists(f):
            os.remove(f)
            print('removed', f)

shutil.copy2(src_exe, os.path.join(desk, '渡口助手-0.9.5.exe'))

cmd = r'''@echo off
chcp 936 >nul
title 渡口 0.9.5 覆盖更新
setlocal
set "DST=%LOCALAPPDATA%\Ferry\渡口助手.exe"
set "SRC=%~dp0渡口助手-0.9.5.exe"

if not exist "%SRC%" (
  echo [X] 找不到 %SRC%
  echo     本脚本要和 exe 放在同一个文件夹里。
  pause & exit /b 1
)
if not exist "%LOCALAPPDATA%\Ferry" (
  echo [X] 没找到安装目录 %LOCALAPPDATA%\Ferry
  echo     这台机器可能没装过渡口。
  pause & exit /b 1
)

echo [1/4] 关闭正在运行的客户端...
taskkill /IM 渡口助手.exe /F >nul 2>&1
timeout /t 2 /nobreak >nul

if exist "%DST%" (
  echo [2/4] 备份旧版到 渡口助手.exe.bak
  copy /Y "%DST%" "%DST%.bak" >nul
)

echo [3/4] 覆盖...
copy /Y "%SRC%" "%DST%" >nul
if errorlevel 1 (
  echo [X] 覆盖失败：客户端还在运行。
  echo     先右键托盘图标退出，再用管理员身份重新运行本脚本。
  pause & exit /b 1
)

echo [4/4] 启动（会弹一次 UAC，点“是”）
start "" "%DST%"
echo.
echo 完成。设置页底部「当前版本」应显示 0.9.5
timeout /t 3 /nobreak >nul
'''

for target in (os.path.join(ws, '覆盖更新-0.9.5.cmd'),
               os.path.join(desk, '覆盖更新-0.9.5.cmd')):
    with io.open(target, 'w', encoding='gbk', newline='\r\n') as f:
        f.write(cmd)
    print('wrote', target)

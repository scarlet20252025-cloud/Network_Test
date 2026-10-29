@echo off
rem Emulator launcher wrapper for xt.
rem
rem Why a wrapper: a scheduled task does NOT inherit environment variables.
rem ANDROID_SDK_ROOT decides where emulator finds the system image;
rem ANDROID_AVD_HOME decides where it finds the AVD definition.
rem
rem Keep this file ASCII-only with CRLF line endings: cmd.exe mis-parses
rem UTF-8 Chinese comments and mishandles LF-only caret continuations
rem (first attempt died with "'ize' is not recognized" -- the caret split
rem the "-partition-size" argument). Chinese explanation lives in
rem tools/emulator-start-remote.ps1 instead.
rem
rem Flag choices, each one paid for by a crash (2026-10-29):
rem   -no-window        only option without a desktop session
rem   -gpu off          THE important one. With swiftshader_indirect or guest,
rem                     the emulator dies with 0xC0000005 (ACCESS_VIOLATION)
rem                     right after "AEHD is operational"; -feature -Vulkan
rem                     does NOT help. -gpu off is the only variant that
rem                     survived a 100s probe. Probed variants and results:
rem                       gpu=guest      -> crash 0xC0000005
rem                       gpu=off        -> alive
rem                       (swiftshader+Vulkan-off -> crash)
rem   -no-metrics       don't call Google telemetry endpoints
rem   -partition-size 1024   max allowed is 2047; 2048 aborts immediately
set ANDROID_SDK_ROOT=D:\AndroidSdk
set ANDROID_HOME=D:\AndroidSdk
set ANDROID_AVD_HOME=%USERPROFILE%\.android\avd
set ANDROID_EMULATOR_HOME=%USERPROFILE%\.android
set PATH=D:\AndroidSdk\platform-tools;D:\AndroidSdk\emulator;%PATH%

cd /d D:\AndroidSdk\emulator
echo === launch %DATE% %TIME% === > C:\tmp\emulator-console.log
echo USERPROFILE=%USERPROFILE% >> C:\tmp\emulator-console.log
echo ANDROID_AVD_HOME=%ANDROID_AVD_HOME% >> C:\tmp\emulator-console.log

emulator.exe -avd ferry_test -no-window -no-audio -no-boot-anim -no-snapshot -no-metrics -gpu off -netdelay none -netspeed full -partition-size 1024 >> C:\tmp\emulator-console.log 2>&1
echo === exit %ERRORLEVEL% %DATE% %TIME% === >> C:\tmp\emulator-console.log

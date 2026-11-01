@echo off
rem TCP echo server on pcs (port 19100) for tools/measure-64hz.py
rem Wrapper needed: schtasks /tr mangles nested quotes, so put the
rem full python path here instead of on the task command line.
set PY=C:\Users\PCserver\AppData\Local\Python\pythoncore-3.14-64\python.exe
"%PY%" C:\Windows\Temp\echo_srv.py 19100

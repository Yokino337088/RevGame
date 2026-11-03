@echo off
setlocal
rem Use the built-in Windows PowerShell; paths come from this script, not the current directory.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start_Unity.ps1" %*
set "launcher_exit=%errorlevel%"
if not "%launcher_exit%"=="0" pause
exit /b %launcher_exit%

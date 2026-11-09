@echo off
setlocal
chcp 65001 >nul
title RevGame Server - Fantasy KCP

rem Use the same launcher as PowerShell.
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0start-server.ps1" %*
set "revgame_exit_code=%errorlevel%"
echo.
pause
exit /b %revgame_exit_code%

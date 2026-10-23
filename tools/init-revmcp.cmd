@echo off
rem RevMCP one-click setup. Double-click this file.
rem All logic is in init-revmcp.ps1; this wrapper only starts PowerShell with a safe execution policy.
rem Keep this file ASCII-only: cmd reads .bat/.cmd in the OEM code page, so non-ASCII here turns into mojibake.
setlocal
cd /d "%~dp0.."

where powershell >nul 2>nul
if errorlevel 1 (
    echo [XX] Windows PowerShell not found. Run tools\init-revmcp.ps1 manually.
    goto :done
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0init-revmcp.ps1" %*

:done
echo.
pause

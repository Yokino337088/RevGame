@echo off
chcp 65001 >nul
setlocal

REM ============================================================
REM  RevGame 服务端 · Fantasy 开发环境一键部署（双击即可运行）
REM  真正的逻辑在 setup-fantasy-env.ps1，这里只负责：
REM   1) 切到 UTF-8 代码页，保证中文不乱码
REM   2) 用 -ExecutionPolicy Bypass 绕过脚本策略，不需要改系统设置
REM   3) 把参数原样转交给 PowerShell
REM ============================================================

set "SCRIPT_DIR=%~dp0"
set "PS_SCRIPT=%SCRIPT_DIR%setup-fantasy-env.ps1"

if not exist "%PS_SCRIPT%" (
    echo [ERROR] 找不到 setup-fantasy-env.ps1，请确认本文件和脚本在同一目录。
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%" %*
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if "%EXIT_CODE%"=="0" (
    echo 环境检查完成。
) else (
    echo 环境检查发现阻塞项，请查看上面的 FAIL 行。
)

REM 从资源管理器双击运行时保持窗口，方便看结果；带参数调用（CI/脚本）时不留
if "%~1"=="" pause
exit /b %EXIT_CODE%

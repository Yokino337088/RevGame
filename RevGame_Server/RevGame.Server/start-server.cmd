@echo off
setlocal
chcp 65001 >nul
title RevGame Server - Fantasy KCP

rem 固定工作目录，双击或从其它目录调用都能找到配置和项目。
pushd "%~dp0"
if errorlevel 1 goto directory_error

where dotnet >nul 2>&1
if errorlevel 1 goto dotnet_error

set "revgame_output=Server\Main\bin\Debug\net8.0"
rem 启动沿用已有 Debug 产物。修改源码后可手动执行 dotnet build 更新产物。
for %%F in (Main.dll Main.deps.json Main.runtimeconfig.json Entity.dll Hotfix.dll Fantasy-Net.dll Fantasy.config) do (
    if not exist "%revgame_output%\%%F" goto build
)
goto run

:build
rem 仅缺少产物时才需要 SDK。
dotnet --version >nul 2>&1
if errorlevel 1 goto sdk_error
if not exist "Server\Main\Main.csproj" goto project_error
echo 缺少服务端编译产物，正在构建。
dotnet build "Server\Main\Main.csproj" --configuration Debug
if errorlevel 1 goto build_error

:run
echo.
echo 正在启动 RevGame 服务端，请等待启动完成日志。
echo PvP 外网：Fantasy KCP / UDP 20000
echo 内部通信：127.0.0.1 / TCP 11002
echo 停止服务：在此窗口按 Ctrl+C。
echo.

dotnet "%revgame_output%\Main.dll" --m Develop
set "revgame_exit_code=%errorlevel%"
echo.
if not "%revgame_exit_code%"=="0" echo 服务端异常退出，退出码：%revgame_exit_code%。请检查上方错误日志。
if "%revgame_exit_code%"=="0" echo 服务端已退出。
goto finish

:build_error
set "revgame_exit_code=%errorlevel%"
echo 服务端构建失败，退出码：%revgame_exit_code%。请检查上方错误日志。
goto finish

:dotnet_error
echo 未检测到 dotnet。请先安装 .NET 8 运行时，再重新打开此脚本。
set "revgame_exit_code=1"
goto finish

:sdk_error
echo 未检测到可用的 .NET SDK。请先安装 .NET 8 SDK，再重新打开此脚本。
set "revgame_exit_code=1"
goto finish

:project_error
echo 找不到 Server\Main\Main.csproj，请保留脚本与工程的目录结构。
set "revgame_exit_code=1"
goto finish

:directory_error
echo 无法进入脚本所在目录，请检查目录是否可访问。
pause
exit /b 1

:finish
popd
echo.
pause
exit /b %revgame_exit_code%

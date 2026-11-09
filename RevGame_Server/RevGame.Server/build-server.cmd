@echo off
setlocal

set "revgame_pause=1"
if /i "%~1"=="--no-pause" set "revgame_pause=0"
if not "%~1"=="" if /i not "%~1"=="--no-pause" goto usage
if not "%~2"=="" goto usage

rem Anchor SDK resolution and project paths to this script's directory.
pushd "%~dp0"
if errorlevel 1 goto directory_error

where.exe dotnet >nul 2>&1
if errorlevel 1 goto sdk_error

rem Building Main also builds Entity and Hotfix through project references.
dotnet build "Server\Main\Main.csproj" --configuration Debug --nologo
set "revgame_exit_code=%errorlevel%"
if not "%revgame_exit_code%"=="0" goto build_error

echo [RevGame] Build succeeded. Output: "%CD%\Server\Main\bin\Debug\net8.0"
goto finish

:build_error
echo [RevGame] Build failed with exit code %revgame_exit_code%. 1>&2
goto finish

:sdk_error
echo [RevGame] dotnet was not found. Install the .NET 8 SDK and reopen the terminal. 1>&2
set "revgame_exit_code=1"
goto finish

:directory_error
echo [RevGame] Cannot open the script directory. 1>&2
set "revgame_exit_code=1"
goto exit_script

:usage
echo Usage: build-server.cmd [--no-pause] 1>&2
exit /b 2

:finish
popd

:exit_script
if "%revgame_pause%"=="1" pause
exit /b %revgame_exit_code%

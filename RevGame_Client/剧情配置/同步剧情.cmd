@echo off
setlocal
set "PYTHONUTF8=1"
set "STORY_PROJECT=%~dp0..\UnityProject\RevGame.Unity"
set "STORY_CLI=%~dp0..\UnityProject\Revolution.RevCLI\rev.cmd"

if "%~1"=="" goto sync
if /I "%~1"=="--check" goto check
echo Usage: "%~nx0" [--check]
exit /b 2

:check
call "%STORY_CLI%" --project "%STORY_PROJECT%" --mode auto --timeout 300 call game.story.markdown-sync --set checkOnly=true
exit /b %errorlevel%

:sync
call "%STORY_CLI%" --project "%STORY_PROJECT%" --mode auto --timeout 300 call game.story.markdown-sync
exit /b %errorlevel%

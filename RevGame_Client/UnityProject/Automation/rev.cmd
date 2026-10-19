@echo off
setlocal
if defined REV_PYTHON (
  "%REV_PYTHON%" "%~dp0rev.py" %*
) else (
  py -3 "%~dp0rev.py" %*
)
exit /b %errorlevel%

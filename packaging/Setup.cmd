@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Arkade.ps1"
if errorlevel 1 (
  echo.
  echo Setup did not finish. See the error above.
)
echo.
pause



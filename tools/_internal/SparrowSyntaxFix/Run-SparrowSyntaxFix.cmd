@echo off
setlocal
chcp 65001 >nul
pushd "%~dp0"
set "SCRIPT=%~dp0Run-SparrowSyntaxFix.ps1"
if not exist "%SCRIPT%" (
  echo [FATAL] Cannot find "%SCRIPT%".
  pause
  exit /b 1
)
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -NoExit -File "%SCRIPT%"
set "EXITCODE=%ERRORLEVEL%"
popd
exit /b %EXITCODE%

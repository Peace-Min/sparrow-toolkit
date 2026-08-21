@echo off
setlocal
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\verify-offline-portable.ps1" -BundleRoot "%~dp0"
set "EXITCODE=%ERRORLEVEL%"
echo.
if "%EXITCODE%"=="0" echo Verification completed successfully.
if not "%EXITCODE%"=="0" echo Verification failed with exit code %EXITCODE%.
pause
exit /b %EXITCODE%

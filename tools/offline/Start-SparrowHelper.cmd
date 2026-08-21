@echo off
setlocal
chcp 65001 >nul

rem Keep user-created checker guides outside the portable folder so that replacing the
rem portable bundle during an upgrade does not erase them.  This path is also writable
rem when the bundle is copied from read-only media to a normal user account.
set "GUIDES=%LOCALAPPDATA%\SparrowRunner\checkers"
if not exist "%GUIDES%" mkdir "%GUIDES%"
if errorlevel 1 (
  echo [FATAL] Cannot create the checker guide folder:
  echo         %GUIDES%
  pause
  exit /b 1
)

call "%~dp0tools\Run-SparrowRunnerGui.cmd" --guides-dir "%GUIDES%" %*
set "EXITCODE=%ERRORLEVEL%"
if not "%EXITCODE%"=="0" pause
exit /b %EXITCODE%

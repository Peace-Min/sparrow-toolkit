@echo off
setlocal
chcp 65001 >nul
rem Explorer double-click entry point for the GUI.
rem   * Arguments are forwarded verbatim (%*) so all documented switches work here too.
rem   * Every failure branch PAUSEs so the error remains visible.
rem Kept ASCII-only so the console codepage cannot mojibake it.
set "PUBEXE=%~dp0SparrowRunner.Gui\publish\SparrowRunner.Gui.exe"
set "PROJECT=%~dp0SparrowRunner.Gui\SparrowRunner.Gui.csproj"
set "DEVEXE=%~dp0SparrowRunner.Gui\bin\Release\net8.0-windows\SparrowRunner.Gui.exe"
set "PUBDLL=%~dp0SparrowRunner.Gui\publish\SparrowRunner.Gui.dll"
set "DEVDLL=%~dp0SparrowRunner.Gui\bin\Release\net8.0-windows\SparrowRunner.Gui.dll"

rem [1] Prefer the pre-published bundle when it exists.
rem Repeated top-level checks preserve the real exit code under setlocal.
if exist "%PUBEXE%" echo [INFO] Launching published bundle: %PUBEXE%
if exist "%PUBEXE%" if exist "%PUBDLL%" if exist "%DEVDLL%" call :warn_if_stale
if exist "%PUBEXE%" "%PUBEXE%" %*
if exist "%PUBEXE%" set "EXITCODE=%ERRORLEVEL%"
if exist "%PUBEXE%" if not "%EXITCODE%"=="0" echo.
if exist "%PUBEXE%" if not "%EXITCODE%"=="0" echo [FATAL] The published GUI exe exited with code %EXITCODE%.
if exist "%PUBEXE%" if not "%EXITCODE%"=="0" pause
if exist "%PUBEXE%" exit /b %EXITCODE%

rem [2] Without a published bundle, build and run from source.
if not exist "%PROJECT%" (
  echo [FATAL] Cannot find "%PROJECT%".
  echo         On an air-gapped PC, run tools\publish-airgap.ps1 on an internet PC and copy the
  echo         SparrowRunner.Gui\publish\ bundle next to this script.
  pause
  exit /b 1
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [FATAL] .NET SDK "dotnet" not found, and there is no published GUI exe to fall back to.
  echo         Run tools\publish-airgap.ps1 on an internet PC, then copy the produced
  echo         SparrowRunner.Gui\publish\ bundle next to this script.
  pause
  exit /b 1
)

echo [INFO] No published GUI exe found; running via "dotnet run" (needs internet + .NET SDK).
echo [INFO] For an offline/air-gapped PC, first run tools\publish-airgap.ps1 on an internet PC to build the publish bundle.
dotnet run --project "%PROJECT%" -c Release -- %*
set "EXITCODE=%ERRORLEVEL%"
if not "%EXITCODE%"=="0" echo.
if not "%EXITCODE%"=="0" echo [FATAL] The GUI could not be built or started - see the messages above.
if not "%EXITCODE%"=="0" pause
exit /b %EXITCODE%

rem Compare the managed DLL, which contains the application code. The native apphost EXE can
rem remain byte-identical (and keep an old timestamp) after an incremental publish.
:warn_if_stale
set "STALE="
for /f "usebackq delims=" %%R in (`powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "if ((Get-Item -LiteralPath '%DEVDLL%').LastWriteTime -gt (Get-Item -LiteralPath '%PUBDLL%').LastWriteTime) { 'STALE' }"`) do set "STALE=%%R"
if not "%STALE%"=="STALE" goto :eof
for %%F in ("%PUBDLL%") do set "PUBSTAMP=%%~tF"
for %%F in ("%DEVDLL%") do set "DEVSTAMP=%%~tF"
echo [WARN] The published bundle is OLDER than your local Release build:
echo [WARN]   published : %PUBSTAMP%  %PUBDLL%
echo [WARN]   built     : %DEVSTAMP%  %DEVDLL%
echo [WARN] The published bundle wins, so your latest changes will NOT be running. Re-run
echo [WARN] tools\publish-airgap.ps1 (or delete SparrowRunner.Gui\publish\ to use the build).
goto :eof

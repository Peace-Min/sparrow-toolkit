@echo off
setlocal
chcp 65001 >nul
rem Explorer double-click entry point for the GUI.
rem   * Arguments are forwarded verbatim (%*) so the documented switches
rem     (--xls / --xls-out / --xls-autorun / --log-dir / --screenshot-dir / --guides-dir) work here too.
rem   * Every failure branch PAUSEs: without it the error text flashes and the window closes, leaving no diagnosis.
rem Kept ASCII-only on purpose (the other .cmd entry points are too) so the console codepage cannot mojibake it.
set "PUBEXE=%~dp0SparrowRunner.Gui\publish\SparrowRunner.Gui.exe"
set "PROJECT=%~dp0SparrowRunner.Gui\SparrowRunner.Gui.csproj"

set "DEVEXE=%~dp0SparrowRunner.Gui\bin\Release\net8.0-windows\SparrowRunner.Gui.exe"

rem [1] Air-gapped: if a pre-published self-contained GUI exe exists, run it (no .NET SDK needed).
rem     Written as repeated top-level "if exist" lines on purpose: inside a parenthesized block
rem     %ERRORLEVEL% expands at PARSE time (before the exe ran), and a bare "exit /b" loses the code
rem     under setlocal (measured) -- both would report a wrong exit code to the caller.
rem
rem     ALWAYS announce which binary is launched, and warn when publish\ is OLDER than a local
rem     Release build. A stale publish\ is silently preferred over a fresh build, which looks
rem     exactly like a product bug (observed: an 11-day-old bundle showed the old UI and a
rem     collapsed scope tree while the current build was fine). Naming the path makes that
rem     diagnosable in one glance instead of an afternoon.
if exist "%PUBEXE%" echo [INFO] Launching published bundle: %PUBEXE%
if exist "%PUBEXE%" if exist "%DEVEXE%" call :warn_if_stale
if exist "%PUBEXE%" "%PUBEXE%" %*
if exist "%PUBEXE%" set "EXITCODE=%ERRORLEVEL%"
if exist "%PUBEXE%" if not "%EXITCODE%"=="0" echo.
if exist "%PUBEXE%" if not "%EXITCODE%"=="0" echo [FATAL] The published GUI exe exited with code %EXITCODE%.
if exist "%PUBEXE%" if not "%EXITCODE%"=="0" pause
if exist "%PUBEXE%" exit /b %EXITCODE%

rem [2] No published exe: fall back to building/running from source (needs internet + .NET SDK).
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

rem Warn only when the local Release build is STRICTLY NEWER than the published bundle
rem (that is the case where publish\ silently wins and hides your latest changes).
rem
rem Timestamps must be ORDERED, not merely compared for equality: "%%~tF" is a locale-formatted
rem string ("2026-08-07 오후 02:21"), so a string compare cannot tell newer from older -- it only
rem tells "different", which fires a false warning right after a fresh publish (measured).
rem PowerShell does the ordering. This costs one process start, but only on a machine that has
rem BOTH a publish bundle and a local build -- i.e. a dev box. An air-gapped target has no
rem bin\Release, so this branch never runs there.
:warn_if_stale
set "STALE="
for /f "usebackq delims=" %%R in (`powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "if ((Get-Item -LiteralPath '%DEVEXE%').LastWriteTime -gt (Get-Item -LiteralPath '%PUBEXE%').LastWriteTime) { 'STALE' }"`) do set "STALE=%%R"
if not "%STALE%"=="STALE" goto :eof
for %%F in ("%PUBEXE%") do set "PUBSTAMP=%%~tF"
for %%F in ("%DEVEXE%") do set "DEVSTAMP=%%~tF"
echo [WARN] The published bundle is OLDER than your local Release build:
echo [WARN]   published : %PUBSTAMP%  %PUBEXE%
echo [WARN]   built     : %DEVSTAMP%  %DEVEXE%
echo [WARN] The published bundle wins, so your latest changes will NOT be running. Re-run
echo [WARN] tools\publish-airgap.ps1 (or delete SparrowRunner.Gui\publish\ to use the build).
goto :eof

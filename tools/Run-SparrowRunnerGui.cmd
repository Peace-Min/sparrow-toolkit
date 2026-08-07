@echo off
setlocal
chcp 65001 >nul
rem Explorer double-click entry point for the GUI.
rem   * Arguments are forwarded verbatim (%*) so the documented switches
rem     (--trackc-xls / --trackc-out / --trackc-autorun / --log-dir / --screenshot-dir / --guides-dir) work here too.
rem   * Every failure branch PAUSEs: without it the error text flashes and the window closes, leaving no diagnosis.
rem Kept ASCII-only on purpose (the other .cmd entry points are too) so the console codepage cannot mojibake it.
set "PUBEXE=%~dp0SparrowRunner.Gui\publish\SparrowRunner.Gui.exe"
set "PROJECT=%~dp0SparrowRunner.Gui\SparrowRunner.Gui.csproj"

rem [1] Air-gapped: if a pre-published self-contained GUI exe exists, run it (no .NET SDK needed).
rem     Written as repeated top-level "if exist" lines on purpose: inside a parenthesized block
rem     %ERRORLEVEL% expands at PARSE time (before the exe ran), and a bare "exit /b" loses the code
rem     under setlocal (measured) -- both would report a wrong exit code to the caller.
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

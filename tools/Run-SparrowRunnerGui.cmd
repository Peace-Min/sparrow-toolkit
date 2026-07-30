@echo off
setlocal
chcp 65001 >nul
rem [1] Air-gapped: if a pre-published self-contained GUI exe exists, run it (no .NET SDK needed).
set "PUBEXE=%~dp0SparrowRunner.Gui\publish\SparrowRunner.Gui.exe"
if exist "%PUBEXE%" (
  "%PUBEXE%"
  exit /b %ERRORLEVEL%
)
rem [2] No published exe: fall back to building/running from source (needs internet + .NET SDK).
set "PROJECT=%~dp0SparrowRunner.Gui\SparrowRunner.Gui.csproj"
if not exist "%PROJECT%" (
  echo [FATAL] Cannot find "%PROJECT%".
  pause
  exit /b 1
)
echo [INFO] No published GUI exe found; running via "dotnet run" (needs internet + .NET SDK).
echo [INFO] For an offline/air-gapped PC, first run tools\publish-airgap.ps1 on an internet PC to build the publish bundle.
dotnet run --project "%PROJECT%" -c Release
exit /b %ERRORLEVEL%

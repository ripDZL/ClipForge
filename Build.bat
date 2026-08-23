@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-ClipForge.ps1"
if errorlevel 1 (
  echo.
  echo BUILD FAILED
  pause
  exit /b 1
)
echo.
echo Build complete. Opening publish folder...
explorer.exe "%~dp0publish\ClipForge-win-x64"
pause

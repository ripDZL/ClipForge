@echo off
setlocal
cd /d "%~dp0"

where dotnet.exe >nul 2>&1
if errorlevel 1 (
  echo .NET 8 SDK not found.
  echo Install .NET 8 SDK 8.0.423 or newer from Microsoft, then try again.
  pause
  exit /b 1
)

if not exist "%~dp0src\ClipForge\Tools\ffmpeg\bin\ffmpeg.exe" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Setup-FFmpeg.ps1"
  if errorlevel 1 exit /b 1
)

set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
dotnet.exe run --project "%~dp0src\ClipForge\ClipForge.csproj" -c Debug -- %*
pause

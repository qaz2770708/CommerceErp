@echo off
if not exist "%~dp0app\CommerceErp.Desktop.exe" (
  echo Please run build.ps1 with the .NET 10 SDK first.
  pause
  exit /b 1
)
start "" "%~dp0app\CommerceErp.Desktop.exe" %*

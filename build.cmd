@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
 echo .NET 8 SDK is required: https://dotnet.microsoft.com/download/dotnet/8.0
 pause
 exit /b 1
)
rem scripts\package.ps1 publishes the single EXE with vda\, plugins\, integrations\ and notices into dist\ (same as the release workflow).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\package.ps1" -Out "%~dp0dist"
if errorlevel 1 (echo Build failed & pause & exit /b 1)
echo Output: %~dp0dist\DesktopSessionManager.exe
pause
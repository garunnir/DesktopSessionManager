@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
 echo .NET 8 SDK is required: https://dotnet.microsoft.com/download/dotnet/8.0
 pause
 exit /b 1
)
rem The csproj downloads VirtualDesktopAccessor.dll from the GitHub release and verifies its SHA256.
dotnet publish DesktopSessionManager.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o "%~dp0dist"
if errorlevel 1 (echo Build failed & pause & exit /b 1)
copy /Y "%~dp0VirtualDesktopAccessor.dll" "%~dp0dist\" >nul
copy /Y "%~dp0THIRD_PARTY_NOTICES.md" "%~dp0dist\" >nul
if not exist "%~dp0dist\plugins" mkdir "%~dp0dist\plugins"
xcopy /E /I /Y "%~dp0plugins\*" "%~dp0dist\plugins\" >nul
xcopy /E /I /Y "%~dp0integrations\*" "%~dp0dist\integrations\" >nul
echo Output: %~dp0dist\DesktopSessionManager.exe
pause

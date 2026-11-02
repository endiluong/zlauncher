@echo off
rem Builds Z Launcher + ZMonitor and installs them to %LOCALAPPDATA%\Programs\{ZLauncher,ZMonitor}.
rem Quit the widget first (Z Launcher > Widgets > Quit widget) or run this from an admin prompt,
rem otherwise the running ZMonitor.exe (admin) can't be replaced.
setlocal
set DOTNET=dotnet
if exist "%LOCALAPPDATA%\Microsoft\dotnet-sdk\dotnet.exe" set DOTNET=%LOCALAPPDATA%\Microsoft\dotnet-sdk\dotnet.exe
set OUT=%LOCALAPPDATA%\Programs

taskkill /im ZLauncher.exe /f >nul 2>&1
taskkill /im ZMonitor.exe /f >nul 2>&1

"%DOTNET%" publish "%~dp0src\ZMonitor\ZMonitor.csproj" -c Release -o "%OUT%\ZMonitor" -nologo
if errorlevel 1 (echo ZMonitor build failed & pause & exit /b 1)
"%DOTNET%" publish "%~dp0src\ZLauncher\ZLauncher.csproj" -c Release -o "%OUT%\ZLauncher" -nologo
if errorlevel 1 (echo ZLauncher build failed & pause & exit /b 1)

rem Start menu shortcut, so Z Launcher shows up in Windows search.
powershell -NoProfile -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Programs')+'\Z Launcher.lnk'); $s.TargetPath='%OUT%\ZLauncher\ZLauncher.exe'; $s.WorkingDirectory='%OUT%\ZLauncher'; $s.Save()"

echo Installed to %OUT%\ZLauncher and %OUT%\ZMonitor

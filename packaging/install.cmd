@echo off
rem Z Launcher installer (release zip). Copies both apps to %LOCALAPPDATA%\Programs and adds a Start menu shortcut.
rem Needs the .NET 10 Desktop Runtime: https://dotnet.microsoft.com/download/dotnet/10.0
setlocal
set OUT=%LOCALAPPDATA%\Programs

rem Ask a running widget / launcher to close so their files can be replaced.
taskkill /im ZLauncher.exe >nul 2>&1
taskkill /im ZMonitor.exe >nul 2>&1
timeout /t 2 /nobreak >nul

robocopy "%~dp0ZLauncher" "%OUT%\ZLauncher" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (echo Could not copy Z Launcher. & pause & exit /b 1)
robocopy "%~dp0ZMonitor" "%OUT%\ZMonitor" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (echo Could not copy ZMonitor. Quit the widget first ^(it runs as admin^), then run this again. & pause & exit /b 1)

powershell -NoProfile -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Programs')+'\Z Launcher.lnk'); $s.TargetPath='%OUT%\ZLauncher\ZLauncher.exe'; $s.WorkingDirectory='%OUT%\ZLauncher'; $s.Save()"

echo Installed Z Launcher and ZMonitor to %OUT%
echo Open "Z Launcher" from the Start menu (or press Ctrl+Alt+Z once it is running).
start "" "%OUT%\ZLauncher\ZLauncher.exe"

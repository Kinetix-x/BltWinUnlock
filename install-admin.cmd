@echo off
rem ===========================================================================
rem  Btwu - install launcher.
rem
rem  Asks for elevation (UAC), then runs scripts\install.ps1 in the elevated
rem  window.  The elevated window is started with -NoExit, so its output stays
rem  on screen after the install finishes or fails.
rem
rem  ASCII ONLY.  Never put non-ASCII characters in this file (cmd.exe would
rem  mangle them under a non-UTF8 code page).
rem
rem  Usage:
rem      install-admin.cmd                 install with the tray agent
rem      install-admin.cmd headless        install without the tray/UI stack
rem
rem  Why this file exists: an elevated console opens in C:\WINDOWS\system32, so
rem  a relative path such as scripts\install.ps1 does not resolve there.  %~dp0
rem  pins the path to the folder that holds this file.
rem ===========================================================================
setlocal
set "BTWU_SCRIPT=%~dp0scripts\install.ps1"
set "BTWU_MODE=%~1"

if not exist "%BTWU_SCRIPT%" ( echo [X] not found: "%BTWU_SCRIPT%" & pause & exit /b 1 )

echo Requesting administrator rights - confirm the UAC prompt...
rem  [char]34 builds a double quote without putting a literal one in this -Command
rem  payload, so the line survives cmd.exe quoting intact.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$s=$env:BTWU_SCRIPT; $q=[char]34; $a=@('-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File',($q+$s+$q)); if ($env:BTWU_MODE -eq 'headless') { $a+='-Headless' }; try { Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $a -ErrorAction Stop } catch { Write-Host '[X] Elevation was refused or failed.' -ForegroundColor Red; exit 1 }"
if errorlevel 1 ( echo. & echo [X] Elevation was refused or failed. & pause & exit /b 1 )

echo.
echo An elevated window was opened.  It stays open when the install finishes so
echo you can read the result, and it will ask for your account password once.
exit /b 0

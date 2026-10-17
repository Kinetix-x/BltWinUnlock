@echo off
rem ===========================================================================
rem  Btwu - uninstall / rollback launcher.
rem
rem  Asks for elevation (UAC), then runs scripts\uninstall.ps1 in the elevated
rem  window.  The elevated window is started with -NoExit, so its output stays
rem  on screen afterwards.
rem
rem  This is the fallback for when the management window cannot be started at
rem  all.  It works from the green package as well as from a checkout - both
rem  keep the script one level down in a folder called scripts\.
rem
rem  ASCII ONLY.  Never put non-ASCII characters in this file (cmd.exe would
rem  mangle them under a non-UTF8 code page).
rem
rem  Usage:
rem      uninstall-admin.cmd               roll back, keep the data directory
rem      uninstall-admin.cmd removedata    roll back and delete it too
rem
rem  Why this file exists: an elevated console opens in C:\WINDOWS\system32, so
rem  a relative path such as scripts\uninstall.ps1 does not resolve there.
rem  %~dp0 pins the path to the folder that holds this file.
rem ===========================================================================
setlocal
set "BTWU_SCRIPT=%~dp0scripts\uninstall.ps1"
set "BTWU_MODE=%~1"

if not exist "%BTWU_SCRIPT%" ( echo [X] not found: "%BTWU_SCRIPT%" & pause & exit /b 1 )

echo Requesting administrator rights - confirm the UAC prompt...
rem  [char]34 builds a double quote without putting a literal one in this -Command
rem  payload, so the line survives cmd.exe quoting intact.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$s=$env:BTWU_SCRIPT; $q=[char]34; $a=@('-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File',($q+$s+$q)); if ($env:BTWU_MODE -eq 'removedata') { $a+='-RemoveData' }; try { Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $a -ErrorAction Stop } catch { Write-Host '[X] Elevation was refused or failed.' -ForegroundColor Red; exit 1 }"
if errorlevel 1 ( echo. & echo [X] Elevation was refused or failed. & pause & exit /b 1 )

echo.
echo An elevated window was opened.  It stays open when the uninstall finishes
echo so you can read the result.
echo.
echo Note: the encrypted account password lives in %ProgramData%\BtwuUnlock.
echo       Keeping it means a copy of a credential that can stand in for your
echo       password is left on the disk.  Pass "removedata" to delete it too.
exit /b 0

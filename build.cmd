@echo off
rem Single build entry point for the whole project. Delegates to scripts\build.ps1.
rem ASCII only: cmd.exe parses this file with the OEM code page.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1" %*
exit /b %errorlevel%

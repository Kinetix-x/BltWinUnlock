@echo off
rem ===========================================================================
rem  Btwu - green package launcher.
rem
rem  Double-click this to open the management panel.
rem
rem  Why a .cmd and not a .lnk: a shortcut can only store an ABSOLUTE target
rem  (that is how Windows shortcuts work), so it breaks the moment the folder is
rem  renamed, moved, or copied to another machine - measured: double-clicking the
rem  shortcut in this package gave "the path does not exist" while btwu.exe was
rem  sitting right next to it. The whole point of a green package is that it runs
rem  from wherever it lands, so the entry point has to resolve its own folder.
rem
rem  ASCII ONLY.  cmd.exe reads this file with the console's code page, and any
rem  non-ASCII byte is mangled under a non-UTF8 code page.
rem
rem  Three deliberate details:
rem    1) %~dp0 - the path is relative to THIS file, so the folder can be moved.
rem    2) cd /d "%SystemRoot%" FIRST - otherwise the starting process inherits a
rem       working directory inside this folder, and a process's current directory
rem       pins that folder: it can then no longer be renamed or deleted (measured).
rem    3) /min - btwu.exe is a console-subsystem program (the CLI needs a console),
rem       so it is born with one; starting it minimised keeps that console from
rem       flashing in the middle of the screen before btwu hides it itself.
rem ===========================================================================
setlocal
cd /d "%SystemRoot%" 2>nul
start "" /min "%~dp0app\btwu.exe" gui
endlocal

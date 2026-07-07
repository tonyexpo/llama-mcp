@echo off
REM Thin wrapper so double-clicking works: Windows opens .ps1 files in an
REM editor by default instead of running them.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1"

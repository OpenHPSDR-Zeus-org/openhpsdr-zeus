@echo off
REM Thin wrapper so the develop-vs-working-tree classifier is double-clickable
REM and runnable from cmd. All arguments pass through to the PowerShell script:
REM   check-against-develop.cmd              report only
REM   check-against-develop.cmd -Clean       dry-run the cleanup
REM   check-against-develop.cmd -Clean -Force discard already-merged noise
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0check-against-develop.ps1" %*
if "%~1"=="" pause

@echo off
setlocal
title Rebuild Zeus Desktop
REM Run from the repo root (this script lives in <repo>\scripts).
cd /d "%~dp0.."

echo ============================================================
echo  Rebuilding Zeus desktop app
echo  Repo: %CD%
echo ============================================================
echo.

echo [1/3] Stopping any running Zeus instance (frees locked DLLs)...
taskkill /IM OpenhpsdrZeus.exe /F >nul 2>&1
REM Give the OS a moment to release the file handles before we rebuild.
ping -n 2 127.0.0.1 >nul

echo.
echo [2/3] Building frontend (zeus-web -^> wwwroot)...
echo.
call npm --prefix zeus-web run build
if errorlevel 1 (
  echo.
  echo *** Frontend build FAILED. Fix the errors above, then run this again. ***
  echo.
  pause
  exit /b 1
)

echo.
echo [3/3] Building backend (dotnet build OpenhpsdrZeus)...
echo.
dotnet build OpenhpsdrZeus -c Debug
if errorlevel 1 (
  echo.
  echo *** Backend build FAILED. Fix the errors above, then run this again. ***
  echo.
  pause
  exit /b 1
)

echo.
echo ============================================================
echo  Rebuild complete.
echo  Use the "Zeus" desktop shortcut to launch the new build.
echo ============================================================
echo.
pause

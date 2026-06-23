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

echo [1/4] Syncing latest from org develop (origin/develop)...
git fetch origin develop
if errorlevel 1 (
  echo  *** Could not reach origin to fetch develop. ***
  echo  *** Building the current tree as-is. ***
) else (
  REM --autostash tucks any uncommitted edits aside for the merge and restores
  REM them after, so a dirty tree does not block the sync.
  git merge --no-edit --autostash FETCH_HEAD
  if errorlevel 1 (
    echo.
    echo  *** Merge conflict pulling develop — aborting the merge and building ***
    echo  *** the current tree as-is. Resolve the develop sync by hand later:  ***
    echo  ***    git merge origin/develop   ^(then fix the conflicts^)          ***
    echo.
    git merge --abort
  )
)

echo.
echo [2/4] Stopping any running Zeus instance (frees locked DLLs)...
taskkill /IM OpenhpsdrZeus.exe /F >nul 2>&1
REM Give the OS a moment to release the file handles before we rebuild.
ping -n 2 127.0.0.1 >nul

echo.
echo [3/4] Building frontend (zeus-web -^> wwwroot)...
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
echo [4/4] Building backend (dotnet build OpenhpsdrZeus)...
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

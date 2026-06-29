@echo off
setlocal EnableDelayedExpansion
title Rebuild Zeus Desktop
REM Run from the repo root (this script lives in <repo>\scripts).
cd /d "%~dp0.."
set "ZEUS_ORG_REMOTE=OpenHPSDR-Zeus-org"
set "ZEUS_ORG_URL=https://github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus.git"
set "ZEUS_ORG_BRANCH=develop"

echo ============================================================
echo  Rebuilding Zeus desktop app
echo  Repo: %CD%
echo ============================================================
echo.

echo [1/6] Checking build prerequisites...
where npm >nul 2>&1
if errorlevel 1 (
  echo.
  echo *** Missing prerequisite: Node.js/npm is not on PATH. ***
  echo Install Node.js LTS, then close and reopen this window so PATH is refreshed.
  echo.
  echo Recommended Windows command:
  echo   winget install OpenJS.NodeJS.LTS
  echo.
  echo Or install it from:
  echo   https://nodejs.org/
  echo.
  pause
  exit /b 1
)

where dotnet >nul 2>&1
if errorlevel 1 (
  echo.
  echo *** Missing prerequisite: dotnet is not on PATH. ***
  echo Install the .NET 10 SDK, then close and reopen this window so PATH is refreshed.
  echo.
  echo Download:
  echo   https://dotnet.microsoft.com/download
  echo.
  pause
  exit /b 1
)

where git >nul 2>&1
if errorlevel 1 (
  echo.
  echo *** Missing prerequisite: git is not on PATH. ***
  echo Install Git for Windows, then close and reopen this window so PATH is refreshed.
  echo.
  echo Recommended Windows command:
  echo   winget install Git.Git
  echo.
  echo Or install it from:
  echo   https://git-scm.com/download/win
  echo.
  pause
  exit /b 1
)

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
  echo.
  echo *** This folder is not a Zeus Git checkout. ***
  echo Current folder:
  echo   %CD%
  echo.
  echo Run this script from the full openhpsdr-zeus source checkout, not
  echo from a copied scripts folder or your Windows user profile folder.
  echo.
  echo Fresh setup:
  echo   cd /d "%%USERPROFILE%%\Desktop"
  echo   git clone --recurse-submodules %ZEUS_ORG_URL%
  echo   cd openhpsdr-zeus
  echo   scripts\rebuild-zeus-desktop.cmd
  echo.
  pause
  exit /b 1
)

echo.
echo [2/6] Updating checkout from %ZEUS_ORG_REMOTE%/%ZEUS_ORG_BRANCH%...
set "DIRTY="
for /f "delims=" %%i in ('git status --porcelain') do set "DIRTY=1"
if defined DIRTY (
  echo.
  echo *** Working tree has local changes. ***
  echo Commit, stash, or discard local edits before updating from %ZEUS_ORG_REMOTE%/%ZEUS_ORG_BRANCH%.
  echo.
  git status --short
  echo.
  pause
  exit /b 1
)

git remote get-url "%ZEUS_ORG_REMOTE%" >nul 2>&1
if errorlevel 1 (
  git remote add "%ZEUS_ORG_REMOTE%" "%ZEUS_ORG_URL%"
  if errorlevel 1 (
    echo.
    echo *** Could not add %ZEUS_ORG_REMOTE% remote. ***
    echo.
    pause
    exit /b 1
  )
)

git fetch "%ZEUS_ORG_REMOTE%" "%ZEUS_ORG_BRANCH%"
if errorlevel 1 (
  echo.
  echo *** Could not fetch %ZEUS_ORG_REMOTE%/%ZEUS_ORG_BRANCH%. ***
  echo Check the network connection and Git remote access, then run this again.
  echo.
  pause
  exit /b 1
)

git show-ref --verify --quiet "refs/heads/%ZEUS_ORG_BRANCH%"
if errorlevel 1 (
  git switch --create "%ZEUS_ORG_BRANCH%" --track "%ZEUS_ORG_REMOTE%/%ZEUS_ORG_BRANCH%"
  if errorlevel 1 (
    echo.
    echo *** Could not create local %ZEUS_ORG_BRANCH% branch from %ZEUS_ORG_REMOTE%/%ZEUS_ORG_BRANCH%. ***
    echo.
    pause
    exit /b 1
  )
) else (
  git branch --set-upstream-to="%ZEUS_ORG_REMOTE%/%ZEUS_ORG_BRANCH%" "%ZEUS_ORG_BRANCH%"
  if errorlevel 1 (
    echo.
    echo *** Could not set local %ZEUS_ORG_BRANCH% to track %ZEUS_ORG_REMOTE%/%ZEUS_ORG_BRANCH%. ***
    echo.
    pause
    exit /b 1
  )

  git switch "%ZEUS_ORG_BRANCH%"
  if errorlevel 1 (
    echo.
    echo *** Could not switch to local %ZEUS_ORG_BRANCH% branch. ***
    echo.
    pause
    exit /b 1
  )

  git pull --ff-only "%ZEUS_ORG_REMOTE%" "%ZEUS_ORG_BRANCH%"
  if errorlevel 1 (
    echo.
    echo *** Local %ZEUS_ORG_BRANCH% is not a clean fast-forward from %ZEUS_ORG_REMOTE%/%ZEUS_ORG_BRANCH%. ***
    echo Resolve the branch state manually, then run this again.
    echo.
    pause
    exit /b 1
  )
)

echo.
echo [3/6] Syncing frontend model submodule...
if not exist "zeus-web\external\deepcw-engine\model.onnx" (
  git submodule update --init --recursive zeus-web/external/deepcw-engine
  if errorlevel 1 (
    echo.
    echo *** Submodule sync FAILED. Fix the errors above, then run this again. ***
    echo.
    pause
    exit /b 1
  )
)
if not exist "zeus-web\external\deepcw-engine\model.onnx" (
  echo.
  echo *** Missing frontend model: zeus-web\external\deepcw-engine\model.onnx ***
  echo Run git submodule update --init --recursive, then run this script again.
  echo.
  pause
  exit /b 1
)

echo.
echo [4/6] Stopping any running Zeus instance (frees locked DLLs)...
taskkill /IM OpenhpsdrZeus.exe /F >nul 2>&1
REM Give the OS a moment to release the file handles before we rebuild.
ping -n 2 127.0.0.1 >nul

echo.
echo [5/6] Restoring and building frontend (zeus-web -^> wwwroot)...
echo.
call npm --prefix zeus-web ci
if errorlevel 1 (
  echo.
  echo *** Frontend dependency restore FAILED. Fix the errors above, then run this again. ***
  echo.
  pause
  exit /b 1
)

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
echo [6/6] Building backend (dotnet build OpenhpsdrZeus)...
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

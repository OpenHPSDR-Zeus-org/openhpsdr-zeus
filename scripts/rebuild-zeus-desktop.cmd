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

echo [1/5] Checking build prerequisites...
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

echo.
echo [2/5] Syncing frontend model submodule...
if not exist "zeus-web\external\deepcw-engine\model.onnx" (
  where git >nul 2>&1
  if errorlevel 1 (
    echo.
    echo *** Missing prerequisite: git is not on PATH. ***
    echo Zeus needs Git to fetch the DeepCW model submodule for this checkout.
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
    echo   git clone --recurse-submodules https://github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus.git
    echo   cd openhpsdr-zeus
    echo   scripts\rebuild-zeus-desktop.cmd
    echo.
    pause
    exit /b 1
  )

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
echo [3/5] Stopping any running Zeus instance (frees locked DLLs)...
taskkill /IM OpenhpsdrZeus.exe /F >nul 2>&1
REM Give the OS a moment to release the file handles before we rebuild.
ping -n 2 127.0.0.1 >nul

echo.
echo [4/5] Restoring and building frontend (zeus-web -^> wwwroot)...
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
echo [5/5] Building backend (dotnet build OpenhpsdrZeus)...
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

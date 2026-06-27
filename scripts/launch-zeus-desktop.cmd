@echo off
setlocal EnableDelayedExpansion
REM Launch the freshly built Zeus desktop app.
REM This script lives in <repo>\scripts; the built exe is under
REM <repo>\OpenhpsdrZeus\bin\Debug\net10.0\OpenhpsdrZeus.exe.
set "REPO=%~dp0.."
set "EXE=%REPO%\OpenhpsdrZeus\bin\Debug\net10.0\OpenhpsdrZeus.exe"

if not exist "%REPO%\OpenhpsdrZeus\OpenhpsdrZeus.csproj" (
  echo This launcher is not inside a Zeus source checkout.
  echo Current launcher folder:
  echo   %~dp0
  echo.
  echo Run the launcher from:
  echo   ^<checkout^>\scripts\launch-zeus-desktop.cmd
  echo.
  echo Example:
  echo   %%USERPROFILE%%\Desktop\openhpsdr-zeus\scripts\launch-zeus-desktop.cmd
  echo.
  pause
  exit /b 1
)

if not exist "%EXE%" (
  echo Zeus has not been built yet.
  echo Run the "Rebuild Zeus" shortcut first, then try again.
  echo.
  pause
  exit /b 1
)

REM Replace any running instance so the new build takes over the ports.
taskkill /IM OpenhpsdrZeus.exe /F >nul 2>&1

REM Wait until the old process is fully gone before relaunching. This matters:
REM the desktop webview keeps UI settings in localStorage keyed to the loopback
REM origin http://127.0.0.1:6061. If 6061 is still held by the dying instance,
REM the new one falls back to a RANDOM port -> new origin -> UI looks reset.
set /a tries=0
:waitgone
tasklist /FI "IMAGENAME eq OpenhpsdrZeus.exe" 2>nul | find /i "OpenhpsdrZeus.exe" >nul
if not errorlevel 1 (
  set /a tries+=1
  if !tries! lss 15 (
    ping -n 2 127.0.0.1 >nul
    goto waitgone
  )
)

REM Pin the loopback port so the web origin (and thus localStorage / UI
REM settings) stays stable across every launch. Server-side settings live in
REM zeus-prefs.db and persist regardless; this keeps the UI side stable too.
set "ZEUS_DESKTOP_PORT=6061"

REM Launch detached. The app is a GUI-subsystem binary (WinExe), so Windows
REM never allocates a console for it — no console window behind the UI.
start "Zeus" "%EXE%" --desktop
exit /b 0

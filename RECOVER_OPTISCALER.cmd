@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Run after BUILD_WINDOWS.cmd deployed v1.0.1 Does not download or reinstall anything.
set "GAME=C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord"
set "GAMEBIN=%GAME%\bin\Win64_Shipping_Client"
set "HELPER=%GAME%\Modules\BannerlordFG\bin\Win64_Shipping_Client\BannerlordFG.Helper.cmd"
set "STATE=%LOCALAPPDATA%\BannerlordFG"
set "BACKUP=%STATE%\recovery\preflight-%RANDOM%-%RANDOM%"

if not exist "%HELPER%" (
  echo [ERROR] Updated BannerlordFG.Helper.cmd not found. Run BUILD_WINDOWS.cmd first.
  exit /b 1
)
if not exist "%GAMEBIN%\Bannerlord.exe" if not exist "%GAMEBIN%\Bannerlord.Native.exe" (
  echo [ERROR] Bannerlord executable not found.
  exit /b 1
)
tasklist /FI "IMAGENAME eq Bannerlord.exe" | find /I "Bannerlord.exe" >nul
if not errorlevel 1 (echo [ERROR] Close Bannerlord first. & exit /b 1)
tasklist /FI "IMAGENAME eq Bannerlord.Native.exe" | find /I "Bannerlord.Native.exe" >nul
if not errorlevel 1 (echo [ERROR] Close Bannerlord.Native first. & exit /b 1)
echo [1/3] Preserving prior manifests and logs...
if exist "%BACKUP%\" (echo [ERROR] Backup path collision. Retry. & exit /b 1)
mkdir "%BACKUP%" >nul 2>nul
if not exist "%BACKUP%\" (echo [ERROR] Could not create preflight backup. & exit /b 1)
if exist "%STATE%\install-*.json" (
  copy /Y "%STATE%\install-*.json" "%BACKUP%\" >nul
  if errorlevel 1 (echo [ERROR] Cannot back up manifests. Nothing removed. & exit /b 1)
)
if exist "%STATE%\first-run.log" (
  copy /Y "%STATE%\first-run.log" "%BACKUP%\" >nul
  if errorlevel 1 (echo [ERROR] Cannot back up old log. Nothing removed. & exit /b 1)
)
echo Backup snapshot: "%BACKUP%"
echo [2/3] Safely removing BannerlordFG-owned OptiScaler files...
call "%HELPER%" --uninstall-closed-game --gamebin "%GAMEBIN%"
if errorlevel 1 (
  echo [ERROR] Recovery did not complete. Some owned files may already have been safely removed.
  echo See "%STATE%\first-run.log"
  exit /b 2
)
echo [3/3] Checking for remaining graphics components...
if exist "%GAMEBIN%\OptiScaler.ini" (
  echo [WARNING] OptiScaler.ini still exists; investigate ownership before installing again.
  echo Log: "%STATE%\first-run.log"
  exit /b 3
)
if exist "%GAMEBIN%\dxgi.dll" (
  echo [WARNING] dxgi.dll still exists. It may be third-party or modified. No overwrite will be attempted.
  echo Log: "%STATE%\first-run.log"
  exit /b 3
)
if exist "%GAMEBIN%\OptiScaler\" (
  echo [WARNING] OptiScaler directory still exists. Check for files before reinstalling.
  echo Log: "%STATE%\first-run.log"
  exit /b 3
)
echo SUCCESS: recovery finished. Open Bannerlord and choose OptiScaler - Install.
echo Changed INI backups, if any: "%STATE%\recovery"
echo Full log: "%STATE%\first-run.log"
exit /b 0

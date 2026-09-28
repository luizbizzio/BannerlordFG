@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem GPL-3.0-only. Background helper for the BannerlordFG maintenance DLL.
rem CMD only launches Microsoft's .NET Framework host. It never downloads or
rem executes scripts; archive download and SHA256 validation remain in C#.
set "BFG_MAINTENANCE=%~dp0BannerlordFG.Maintenance.dll"
set "BFG_LOGDIR=%LOCALAPPDATA%\BannerlordFG"
if not exist "%BFG_LOGDIR%" mkdir "%BFG_LOGDIR%" 2>nul
echo [%DATE% %TIME%] CMD helper invoked: %~1 >> "%BFG_LOGDIR%\maintenance-host.log"
if not exist "%BFG_MAINTENANCE%" (
  echo [ERROR] Maintenance DLL is missing: "%BFG_MAINTENANCE%" >> "%BFG_LOGDIR%\maintenance-host.log"
  exit /b 2
)
set "BFG_OP=%~1"
set "BFG_GAMEBIN="
set "BFG_PID=0"
if /I "%BFG_OP%"=="--restore-legacy-fps" goto :invoke
if /I "%BFG_OP%"=="--auto-first-run" goto :gamebin
if /I "%BFG_OP%"=="--post-exit-uninstall" goto :gamebin
if /I "%BFG_OP%"=="--post-exit-restart" goto :gamebin
if /I "%BFG_OP%"=="--uninstall-closed-game" goto :gamebin
if /I "%BFG_OP%"=="--configure-live-controls-closed-game" goto :gamebin
echo [ERROR] Unsupported maintenance operation. >> "%BFG_LOGDIR%\maintenance-host.log"
exit /b 2
:gamebin
if /I not "%~2"=="--gamebin" goto :arguments
set "BFG_GAMEBIN=%~3"
if not defined BFG_GAMEBIN goto :arguments
if /I "%BFG_OP%"=="--uninstall-closed-game" goto :invoke
if /I "%BFG_OP%"=="--configure-live-controls-closed-game" goto :invoke
if /I not "%~4"=="--wait-pid" goto :arguments
set "BFG_PID=%~5"
if /I "%~6"=="--state-base" set "BFG_STATE_BASE=%~7"
if /I "%~8"=="--user-documents" set "BFG_USER_DOCUMENTS=%~9"
:invoke
rem Invoke a public static .NET entry point without generating a .ps1 or EXE.
rem All operation arguments are passed in environment variables, NOT interpolated
rem into PowerShell source; a game folder containing & is handled safely.
rem Use the game's state directory even when the UAC administrator is a
rem different account. The requested state path originates in our own DLL.
if defined BFG_STATE_BASE for %%I in ("%BFG_STATE_BASE%") do set "BFG_LOGDIR=%%~dpI"
if not exist "%BFG_LOGDIR%" mkdir "%BFG_LOGDIR%" 2>nul
echo [%DATE% %TIME%] CMD arguments validated, game pid=%BFG_PID%, operation=%BFG_OP% >> "%BFG_LOGDIR%\maintenance-host.log"
if defined BFG_STATE_BASE echo [%DATE% %TIME%] helper started > "%BFG_STATE_BASE%.helper-started"
powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$ErrorActionPreference='Stop'; try { [Console]::Out.WriteLine('PowerShell maintenance host started'); [void][System.Reflection.Assembly]::LoadFrom($env:BFG_MAINTENANCE); [Console]::Out.WriteLine('Maintenance DLL loaded'); $result=[BannerlordFG.Setup.MaintenanceEntry]::Run($env:BFG_OP,$env:BFG_GAMEBIN,[int]$env:BFG_PID); [Console]::Out.WriteLine('Maintenance exit code: '+$result); exit [int]$result } catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 2 }" >> "%BFG_LOGDIR%\maintenance-host.log" 2>&1
set "BFG_RC=%ERRORLEVEL%"
if not "%BFG_RC%"=="0" (
  echo [%DATE% %TIME%] Maintenance host failed, exit %BFG_RC%. >> "%BFG_LOGDIR%\maintenance-host.log"
  if defined BFG_STATE_BASE (
    echo The maintenance host failed with exit code %BFG_RC%. See maintenance-host.log. > "%BFG_STATE_BASE%.blocked"
    if exist "%BFG_STATE_BASE%.pending" del /q "%BFG_STATE_BASE%.pending" 2>nul
  )
)
exit /b %BFG_RC%
:arguments
echo [ERROR] Missing --gamebin or --wait-pid argument. >> "%BFG_LOGDIR%\maintenance-host.log"
exit /b 2

@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem GPL-3.0-only. Background helper for the BannerlordFG maintenance DLL.
rem CMD only launches Microsoft's .NET Framework host. It never downloads or
rem executes scripts; archive download and SHA256 validation remain in C#.
set "BFG_MAINTENANCE=%~dp0BannerlordFG.Maintenance.dll"
set "BFG_OP=%~1"
set "BFG_GAMEBIN="
set "BFG_PID=0"
set "BFG_STATE_BASE="
set "BFG_LOGDIR=%LOCALAPPDATA%\BannerlordFG"
set "BFG_LOGFILE=%BFG_LOGDIR%\BannerlordFG.log"
if not exist "%BFG_LOGDIR%" mkdir "%BFG_LOGDIR%" 2>nul
rem Route early failures to the game user's state directory whenever the caller
rem provided it. UAC may be running under a different administrator account.
rem The first log entry here also covers malformed/unsupported operations.
echo [%DATE% %TIME%] CMD helper invoked: %~1 >> "%BFG_LOGFILE%"
if /I "%BFG_OP%"=="--verify-runtime" goto :invoke
if /I "%BFG_OP%"=="--restore-legacy-fps" goto :invoke
if /I "%BFG_OP%"=="--auto-first-run" goto :gamebin
if /I "%BFG_OP%"=="--post-exit-uninstall" goto :gamebin
if /I "%BFG_OP%"=="--post-exit-settings" goto :gamebin
if /I "%BFG_OP%"=="--uninstall-closed-game" goto :gamebin
if /I "%BFG_OP%"=="--configure-live-controls-closed-game" goto :gamebin
set "BFG_FAILURE=Unsupported maintenance operation."
goto :early_failure
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
rem All BannerlordFG components write to one log. Do not redirect an
rem entire long-running PowerShell host into it: that would hold an exclusive
rem log handle while the game and elevated installer need to append events.
set "BFG_LOGFILE=%BFG_LOGDIR%\BannerlordFG.log"
echo [%DATE% %TIME%] CMD helper invoked and arguments validated, game pid=%BFG_PID%, operation=%BFG_OP% >> "%BFG_LOGFILE%"
rem This marker means CMD reached its argument validation stage, not that
rem the .NET maintenance worker successfully started.
if defined BFG_STATE_BASE echo [%DATE% %TIME%] helper started > "%BFG_STATE_BASE%.helper-started"
rem A missing DLL used to terminate before writing a marker or .blocked file.
if not exist "%BFG_MAINTENANCE%" (
  set "BFG_FAILURE=BannerlordFG.Maintenance.dll is missing from the mod folder. Reinstall the complete mod package."
  goto :early_failure
)
set "BFG_HOST_OUTPUT=%TEMP%\BannerlordFG-host-%RANDOM%-%RANDOM%-%RANDOM%.tmp"
powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$ErrorActionPreference='Stop'; try { [Console]::Out.WriteLine('PowerShell maintenance host started'); $folder=[IO.Path]::GetDirectoryName($env:BFG_MAINTENANCE); $known=@('System.Runtime.CompilerServices.Unsafe','System.Buffers','System.Memory','System.Numerics.Vectors','System.Threading.Tasks.Extensions','Microsoft.Bcl.AsyncInterfaces','System.Text.Encoding.CodePages','SharpCompress'); foreach($name in $known){ $path=[IO.Path]::Combine($folder,($name+'.dll')); if(-not [IO.File]::Exists($path)){throw ('Missing local runtime DLL: '+$path)}; $identity=[Reflection.AssemblyName]::GetAssemblyName($path); if($identity.Name -ne $name){throw ('Unexpected assembly name in '+$path)}; if($name -eq 'System.Threading.Tasks.Extensions' -and $identity.Version.ToString() -ne '4.2.0.1'){throw ('Expected System.Threading.Tasks.Extensions assembly 4.2.0.1; found '+$identity.FullName+'. Check NuGet restore/package pin.')} ; $asm=[Reflection.Assembly]::LoadFrom($path); if($asm.FullName -ne $identity.FullName){throw ('CLR loaded different local assembly: expected '+$identity.FullName+'; got '+$asm.FullName)}; if($env:BFG_OP -eq '--verify-runtime'){[Console]::Out.WriteLine('Runtime DLL identity OK: '+$asm.FullName)} }; [void][System.Reflection.Assembly]::LoadFrom($env:BFG_MAINTENANCE); [BannerlordFG.Setup.MaintenanceEntry]::RegisterLocalAssemblyBindings(); if($env:BFG_OP -eq '--verify-runtime'){ [void][BannerlordFG.Setup.MaintenanceEntry].GetMethods(); [BannerlordFG.Setup.MaintenanceEntry]::VerifyRuntimeAssemblies(); [BannerlordFG.Setup.MaintenanceEntry]::VerifyRuntimeExtraction(); [Console]::Out.WriteLine('Runtime LZMA2 7z extraction verified.'); [Console]::Out.WriteLine('Maintenance dependencies verified.'); exit 0 }; [Console]::Out.WriteLine('Maintenance DLL loaded'); $result=[BannerlordFG.Setup.MaintenanceEntry]::Run($env:BFG_OP,$env:BFG_GAMEBIN,[int]$env:BFG_PID); [Console]::Out.WriteLine('Maintenance exit code: '+$result); exit [int]$result } catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 2 }" > "%BFG_HOST_OUTPUT%" 2>&1
set "BFG_RC=%ERRORLEVEL%"
rem Import the helper's console output only AFTER PowerShell exits.
rem This does not interfere with the live GAME and SETUP log writers. The
rem importer uses the same single-writer file sharing policy as UnifiedLog.cs.
if exist "%BFG_HOST_OUTPUT%" (
  powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$p=$env:BFG_LOGFILE; $t=$env:BFG_HOST_OUTPUT; $s=$null; try { $content=[IO.File]::ReadAllText($t,[Console]::OutputEncoding); if($content.Length -gt 0){ $bytes=([Text.UTF8Encoding]::new($false)).GetBytes($content); for($i=0;$i -lt 100;$i++){ try { $s=[IO.FileStream]::new($p,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::Write,[IO.FileShare]::Read); [void]$s.Seek(0,[IO.SeekOrigin]::End); $s.Write($bytes,0,$bytes.Length); $s.Dispose(); $s=$null; break } catch [IO.IOException] { Start-Sleep -Milliseconds 25 } finally { if($s){$s.Dispose();$s=$null} } } } } catch { [Console]::Error.WriteLine('BannerlordFG log import failed: '+$_.Exception.Message) }"
  del /q "%BFG_HOST_OUTPUT%" 2>nul
)
if not "%BFG_RC%"=="0" (
  echo [%DATE% %TIME%] Maintenance host failed, exit %BFG_RC%. >> "%BFG_LOGFILE%"
  if defined BFG_STATE_BASE (
    if /I "%BFG_OP%"=="--post-exit-uninstall" (
      if not exist "%BFG_STATE_BASE%.uninstall-failed" echo The uninstall maintenance host failed with exit code %BFG_RC%. See BannerlordFG.log. > "%BFG_STATE_BASE%.uninstall-failed"
    ) else (
      if not exist "%BFG_STATE_BASE%.blocked" echo The maintenance host failed with exit code %BFG_RC%. See BannerlordFG.log. > "%BFG_STATE_BASE%.blocked"
      if exist "%BFG_STATE_BASE%.pending" del /q "%BFG_STATE_BASE%.pending" 2>nul
    )
  )
)
exit /b %BFG_RC%
:arguments
set "BFG_FAILURE=Maintenance CMD received invalid --gamebin or --wait-pid arguments."
goto :early_failure
:early_failure
rem Failed preflight must signal the game, not leave a two-minute blind wait.
rem For malformed arguments without --state-base, the game also detects the
rem CMD process exit using its retained process handle.
echo [%DATE% %TIME%] [ERROR] %BFG_FAILURE% >> "%BFG_LOGFILE%"
if defined BFG_STATE_BASE (
  if /I "%BFG_OP%"=="--post-exit-uninstall" (
    > "%BFG_STATE_BASE%.uninstall-failed" echo %BFG_FAILURE% See BannerlordFG.log.
  ) else (
    > "%BFG_STATE_BASE%.blocked" echo %BFG_FAILURE% See BannerlordFG.log.
    if exist "%BFG_STATE_BASE%.pending" del /q "%BFG_STATE_BASE%.pending" 2>nul
  )
)
exit /b 2

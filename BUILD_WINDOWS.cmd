@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Pure CMD build. Windows 10/11 provides curl.exe and tar.exe.
rem Requires VS 2022 Build Tools + MSBuild + .NET Framework 4.7.2 targeting pack.
set "ROOT=%~dp0"
set "GAME=C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord"
set "GAMEBIN=%GAME%\bin\Win64_Shipping_Client"
set "MODULES=%GAME%\Modules"
set "SOURCE=%ROOT%Module\BannerlordFG"
set "OUT=%SOURCE%\bin\Win64_Shipping_Client"
set "TARGET=%MODULES%\BannerlordFG"
set "HARMONY=%ROOT%deps\Harmony\0Harmony.dll"
set "ARCHIVE=%ROOT%BannerlordFG-v1.0.0.zip"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "TMPBUILD=%TEMP%\BannerlordFG-build-%RANDOM%-%RANDOM%"
set "OLD_ROTATED="
set "IN_PLACE="
set "PREVIOUS_PATH="
set "BACKUP="
set "ERROR_MSG=Unknown failure"

call :check_closed
if errorlevel 1 goto :fail
if not exist "%GAMEBIN%\TaleWorlds.MountAndBlade.dll" (
  set "ERROR_MSG=Bannerlord not found at configured Steam path"
  goto :fail
)
if not exist "%MODULES%\" (
  set "ERROR_MSG=Game Modules directory not found"
  goto :fail
)
if not exist "%SOURCE%\SubModule.xml" (
  set "ERROR_MSG=Source module missing"
  goto :fail
)
rem Reuse a successfully compiled standalone ZIP without downloading or rebuilding.
if /I "%~1"=="--deploy-only" goto :deploy_existing_archive
if not exist "%VSWHERE%" (
  set "ERROR_MSG=Missing vswhere. Install VS 2022 Build Tools, MSBuild, and .NET Framework 4.7.2 targeting pack"
  goto :fail
)
where curl.exe >nul 2>nul
if errorlevel 1 (
  set "ERROR_MSG=curl.exe not found"
  goto :fail
)
where tar.exe >nul 2>nul
if errorlevel 1 (
  set "ERROR_MSG=tar.exe not found"
  goto :fail
)
mkdir "%TMPBUILD%" 2>nul
if not exist "%TMPBUILD%\" (
  set "ERROR_MSG=Could not create temporary build directory"
  goto :fail
)

"%VSWHERE%" -latest -prerelease -products * -find "MSBuild\**\Bin\MSBuild.exe" > "%TMPBUILD%\msbuild_paths.txt"
set "MSBUILD="
for /f "usebackq delims=" %%M in ("%TMPBUILD%\msbuild_paths.txt") do if not defined MSBUILD set "MSBUILD=%%M"
if not defined MSBUILD (
  set "ERROR_MSG=MSBuild not found by vswhere"
  goto :fail
)
if not exist "%MSBUILD%" (
  set "ERROR_MSG=MSBuild executable not found"
  goto :fail
)

rem Keep source selection and copy in a separate CALL to prevent early %%VAR%% expansion.
if not exist "%HARMONY%" (
  call :install_harmony
  if errorlevel 1 goto :fail
)

 echo [2/5] Compiling BannerlordFG...
"%MSBUILD%" "%ROOT%src\BannerlordFG\BannerlordFG.csproj" /t:Rebuild /p:Configuration=Release "/p:BannerlordDir=%GAME%" /v:minimal
if errorlevel 1 (
  set "ERROR_MSG=Module compilation failed; current installation untouched"
  goto :fail
)
echo [3/5] Compiling maintenance DLL (no setup EXE)...
"%MSBUILD%" "%ROOT%src\BannerlordFG.Setup\BannerlordFG.Setup.csproj" /t:Rebuild /p:Configuration=Release /v:minimal
if errorlevel 1 (
  set "ERROR_MSG=Maintenance DLL compilation failed; current installation untouched"
  goto :fail
)
copy /Y "%HARMONY%" "%OUT%\0Harmony.dll" >nul
if errorlevel 1 (
  set "ERROR_MSG=Failed to copy Harmony to compiled module"
  goto :fail
)
for %%F in (BannerlordFG.dll BannerlordFG.Maintenance.dll 0Harmony.dll) do if not exist "%OUT%\%%F" (
  set "ERROR_MSG=Missing compiled module file: %%F"
  goto :fail
)
if not exist "%OUT%\BannerlordFG.Helper.cmd" (
  set "ERROR_MSG=Missing CMD maintenance helper"
  goto :fail
)
if exist "%OUT%\BUILD_REQUIRED.txt" del /Q "%OUT%\BUILD_REQUIRED.txt"

echo [4/5] Creating and validating standalone ZIP...
if exist "%ARCHIVE%" del /Q "%ARCHIVE%"
tar.exe -a -cf "%ARCHIVE%" -C "%ROOT%Module" BannerlordFG
if errorlevel 1 (
  set "ERROR_MSG=standalone ZIP creation failed"
  goto :fail
)
if not exist "%ARCHIVE%" (
  set "ERROR_MSG=Generated standalone ZIP is missing"
  goto :fail
)
mkdir "%TMPBUILD%\stage" >nul 2>nul
tar.exe -xf "%ARCHIVE%" -C "%TMPBUILD%\stage"
if errorlevel 1 (
  set "ERROR_MSG=Generated standalone ZIP cannot be extracted"
  goto :fail
)
:validate_staged_module
for %%F in (BannerlordFG.dll BannerlordFG.Maintenance.dll 0Harmony.dll) do if not exist "%TMPBUILD%\stage\BannerlordFG\bin\Win64_Shipping_Client\%%F" (
  set "ERROR_MSG=Generated standalone ZIP is missing %%F"
  goto :fail
)
if not exist "%TMPBUILD%\stage\BannerlordFG\bin\Win64_Shipping_Client\BannerlordFG.Helper.cmd" (
  set "ERROR_MSG=Generated ZIP is missing CMD helper"
  goto :fail
)
if not exist "%TMPBUILD%\stage\BannerlordFG\SubModule.xml" (
  set "ERROR_MSG=Generated standalone ZIP is missing SubModule.xml"
  goto :fail
)

call :check_closed
if errorlevel 1 (
  set "ERROR_MSG=Bannerlord is running. Close it and retry"
  goto :fail
)
echo [5/5] Checking permissions and safely deploying new module...
rem Program Files normally needs an elevated CMD. Fail before touching old module.
fltmc >nul 2>&1
if errorlevel 1 (
  set "ERROR_MSG=Deployment requires Administrator. Close mod managers and reopen CMD with Run as administrator, then run BUILD_WINDOWS.cmd again"
  goto :fail
)
if exist "%TARGET%\" (
  call :check_target_reparse
  if errorlevel 1 goto :fail
  call :backup_existing
  if errorlevel 1 goto :fail
  call :rotate_existing
  if errorlevel 1 (
    if not exist "%TARGET%\" (
      set "ERROR_MSG=Rename failed and target is missing. Inspect Modules and the saved backup before proceeding"
      goto :fail
    )
    echo [WARNING] Directory rename denied. Checking whether in-place deployment is safe...
    call :check_hardlinks
    if errorlevel 1 goto :fail
    call :probe_inplace
    if errorlevel 1 goto :fail
    set "IN_PLACE=1"
  )
)
echo Copying validated module to game directory...
robocopy "%TMPBUILD%\stage\BannerlordFG" "%TARGET%" /E /COPY:DAT /DCOPY:DAT /XJ /R:2 /W:1 /NFL /NDL /NJH /NJS /NP > "%TMPBUILD%\deploy.log" 2>&1
if errorlevel 8 (
  set "ERROR_MSG=New module copy failed. See deploy.log in temporary build directory"
  goto :rollback
)
for %%F in (BannerlordFG.dll BannerlordFG.Maintenance.dll 0Harmony.dll) do if not exist "%TARGET%\bin\Win64_Shipping_Client\%%F" (
  set "ERROR_MSG=Deployed module is missing %%F"
  goto :rollback
)
if not exist "%TARGET%\bin\Win64_Shipping_Client\BannerlordFG.Helper.cmd" (
  set "ERROR_MSG=Deployed module is missing CMD helper"
  goto :rollback
)
if not exist "%TARGET%\SubModule.xml" (
  set "ERROR_MSG=Deployed module is missing SubModule.xml"
  goto :rollback
)
rem Verify copied binaries byte-for-byte, even when ROBOCOPY returns success.
for %%F in (BannerlordFG.dll BannerlordFG.Maintenance.dll 0Harmony.dll) do (
  fc /B "%TMPBUILD%\stage\BannerlordFG\bin\Win64_Shipping_Client\%%F" "%TARGET%\bin\Win64_Shipping_Client\%%F" > "%TMPBUILD%\verify-%%F.log" 2>&1
  if errorlevel 1 (
    set "ERROR_MSG=Deployed %%F differs from compiled artifact"
    goto :rollback
  )
)
fc /B "%TMPBUILD%\stage\BannerlordFG\bin\Win64_Shipping_Client\BannerlordFG.Helper.cmd" "%TARGET%\bin\Win64_Shipping_Client\BannerlordFG.Helper.cmd" > "%TMPBUILD%\verify-helper.log" 2>&1
if errorlevel 1 (
  set "ERROR_MSG=Deployed CMD helper differs from package"
  goto :rollback
)
fc /B "%TMPBUILD%\stage\BannerlordFG\SubModule.xml" "%TARGET%\SubModule.xml" > "%TMPBUILD%\verify-xml.log" 2>&1
if errorlevel 1 (
  set "ERROR_MSG=Deployed SubModule.xml differs from packaged version"
  goto :rollback
)
rem An in-place update does not remove old binaries automatically.
rem Delete only our deprecated standalone EXE, never any third-party DLL.
if exist "%TARGET%\bin\Win64_Shipping_Client\BannerlordFG.Setup.exe" (
  del /Q "%TARGET%\bin\Win64_Shipping_Client\BannerlordFG.Setup.exe" > "%TMPBUILD%\legacy-exe-cleanup.log" 2>&1
  if exist "%TARGET%\bin\Win64_Shipping_Client\BannerlordFG.Setup.exe" (
    echo [WARNING] Old setup EXE remains. Close all Bannerlord tools and remove it manually.
  )
)
rem Old files remain in a separate AppData backup regardless of cleanup success.
if defined OLD_ROTATED (
  rmdir /S /Q "%PREVIOUS_PATH%" > "%TMPBUILD%\cleanup.log" 2>&1
  if exist "%PREVIOUS_PATH%\" (
    echo [WARNING] New version installed, but old module remains at "%PREVIOUS_PATH%"
    echo [WARNING] Remove the old folder before launching Bannerlord to avoid duplicate module IDs.
    echo [WARNING] Permanent backup: "%BACKUP%"
    echo [WARNING] Temporary logs: "%TMPBUILD%"
    exit /b 2
  )
)
if exist "%LOCALAPPDATA%\BannerlordFG\native-ui-off.flag" del /Q "%LOCALAPPDATA%\BannerlordFG\native-ui-off.flag"
if defined IN_PLACE echo [INFO] Updated the existing directory in place; no rename was required.
echo [POST-DEPLOY] Configuring two internal FG/FPS runtime keys; disabling user shortcuts...
call "%TARGET%\bin\Win64_Shipping_Client\BannerlordFG.Helper.cmd" --configure-live-controls-closed-game --gamebin "%GAMEBIN%"
if errorlevel 1 echo [WARNING] Existing OptiScaler hotkeys could not be migrated. Check first-run.log and the INI before launching.
if /I not "%~1"=="--deploy-only" (
  echo [POST-DEPLOY] Restoring any previously mod-owned native FPS counter setting...
  call "%TARGET%\bin\Win64_Shipping_Client\BannerlordFG.Helper.cmd" --restore-legacy-fps
  if errorlevel 1 echo [WARNING] Legacy FPS migration could not run. Check first-run.log.
)
echo.
if /I "%~1"=="--deploy-only" (
  echo SUCCESS: Previously compiled BannerlordFG archive installed.
) else (
  echo SUCCESS: BannerlordFG v1.0.0 compiled and installed.
)
echo Installed: "%TARGET%"
echo standalone ZIP: "%ARCHIVE%"
if defined BACKUP echo Previous module backup: "%BACKUP%"
if exist "%TMPBUILD%\" rmdir /S /Q "%TMPBUILD%"
exit /b 0

:backup_existing
set "BACKUP_ROOT=%LOCALAPPDATA%\BannerlordFG\build-backups"
if not exist "%BACKUP_ROOT%\" mkdir "%BACKUP_ROOT%" 2>nul
if not exist "%BACKUP_ROOT%\" (
  set "ERROR_MSG=Cannot create backup directory; existing module untouched"
  exit /b 1
)
set "BACKUP=%BACKUP_ROOT%\BannerlordFG-%RANDOM%-%RANDOM%"
if exist "%BACKUP%\" (
  set "ERROR_MSG=Backup path already exists. Retry build"
  exit /b 1
)
echo Backing up existing module to "%BACKUP%"...
robocopy "%TARGET%" "%BACKUP%" /E /COPY:DAT /DCOPY:DAT /XJ /R:2 /W:1 /NFL /NDL /NJH /NJS /NP > "%TMPBUILD%\backup.log" 2>&1
if errorlevel 8 (
  type "%TMPBUILD%\backup.log"
  set "ERROR_MSG=Cannot copy existing module for backup. Original installation has NOT been moved"
  exit /b 1
)
if exist "%TARGET%\SubModule.xml" if not exist "%BACKUP%\SubModule.xml" (
  set "ERROR_MSG=Backup verification failed; existing module has NOT been moved"
  exit /b 1
)
exit /b 0

:rotate_existing
rem Rename within the same Modules directory instead of MOVE to AppData.
set "OLD_NAME=BannerlordFG.predeploy-%RANDOM%-%RANDOM%"
set "PREVIOUS_PATH=%MODULES%\%OLD_NAME%"
if exist "%PREVIOUS_PATH%\" (
  set "ERROR_MSG=Temporary old-module path already exists. Retry build"
  exit /b 1
)
echo Renaming old module within Modules for safe rollback...
ren "%TARGET%" "%OLD_NAME%" > "%TMPBUILD%\rename.log" 2>&1
if errorlevel 1 (
  type "%TMPBUILD%\rename.log"
  set "ERROR_MSG=Directory rename blocked by Windows"
  exit /b 1
)
if not exist "%PREVIOUS_PATH%\" (
  set "ERROR_MSG=Renamed module cannot be found. Check Modules before launching"
  exit /b 1
)
set "OLD_ROTATED=1"
exit /b 0

:rollback
if defined IN_PLACE goto :rollback_inplace
echo [ERROR] %ERROR_MSG%
echo [ROLLBACK] Removing only the newly created module directory...
if exist "%TARGET%\" rmdir /S /Q "%TARGET%" > "%TMPBUILD%\rollback.log" 2>&1
if exist "%TARGET%\" (
  echo [CRITICAL] Partial new installation could not be removed: "%TARGET%"
  echo [CRITICAL] Previous module preserved in "%PREVIOUS_PATH%" and "%BACKUP%"
  goto :fail
)
if defined OLD_ROTATED (
  ren "%PREVIOUS_PATH%" "BannerlordFG" > "%TMPBUILD%\restore.log" 2>&1
  if errorlevel 1 (
    type "%TMPBUILD%\restore.log"
    echo [CRITICAL] Failed to restore old module. Original preserved in "%PREVIOUS_PATH%"
    echo [CRITICAL] Additional backup in "%BACKUP%"
    goto :fail
  )
  echo [ROLLBACK] Previous module restored successfully.
)
goto :fail

:rollback_inplace
echo [ERROR] %ERROR_MSG%
echo [ROLLBACK] Attempting to restore all original files from verified backup...
robocopy "%BACKUP%" "%TARGET%" /E /COPY:DAT /DCOPY:DAT /XJ /IS /IT /R:2 /W:1 /NFL /NDL /NJH /NJS /NP > "%TMPBUILD%\restore.log" 2>&1
if errorlevel 8 (
  echo [CRITICAL] Restore incomplete. Backup: "%BACKUP%"
  echo [CRITICAL] Restore log: "%TMPBUILD%\restore.log"
  goto :fail
)
if exist "%BACKUP%\bin\Win64_Shipping_Client\BannerlordFG.dll" (
  fc /B "%BACKUP%\bin\Win64_Shipping_Client\BannerlordFG.dll" "%TARGET%\bin\Win64_Shipping_Client\BannerlordFG.dll" > "%TMPBUILD%\verify-restore.log" 2>&1
  if errorlevel 1 (
    echo [CRITICAL] DLL was not restored byte-for-byte; inspect "%BACKUP%"
    goto :fail
  )
)
echo [ROLLBACK] Original backed-up files restored. Newly introduced files, if any, may remain.
goto :fail

:check_target_reparse
rem A junction or symlink could point into mod manager staging. Never copy through it.
fsutil reparsepoint query "%TARGET%" > "%TMPBUILD%\reparse.log" 2>&1
if not errorlevel 1 (
  type "%TMPBUILD%\reparse.log"
  set "ERROR_MSG=Target is a junction or symlink. Install the generated standalone ZIP from a clean non-linked folder instead"
  exit /b 1
)
exit /b 0

:check_hardlinks
rem An external mod manager may manage the target with hardlinks. Replacing those in place
rem could modify the staging copy, so only permit in-place with single links.
for %%F in ("%TARGET%\SubModule.xml" "%TARGET%\bin\Win64_Shipping_Client\BannerlordFG.dll" "%TARGET%\bin\Win64_Shipping_Client\BannerlordFG.Maintenance.dll" "%TARGET%\bin\Win64_Shipping_Client\BannerlordFG.Helper.cmd" "%TARGET%\bin\Win64_Shipping_Client\BannerlordFG.Setup.exe" "%TARGET%\bin\Win64_Shipping_Client\0Harmony.dll") do (
  if exist "%%~fF" (
    call :check_one_hardlink "%%~fF"
    if errorlevel 1 exit /b 1
  )
)
exit /b 0

:check_one_hardlink
set "HLCOUNT=0"
fsutil hardlink list "%~1" > "%TMPBUILD%\hardlinks.log" 2>&1
if errorlevel 1 (
  set "ERROR_MSG=Cannot inspect file hardlinks. Refusing in-place deployment; use standalone ZIP"
  exit /b 1
)
for /f "usebackq delims=" %%H in ("%TMPBUILD%\hardlinks.log") do set /a HLCOUNT+=1 >nul
if %HLCOUNT% NEQ 1 (
  echo [WARNING] Multiple or unknown hardlinks for "%~1". Details:
  type "%TMPBUILD%\hardlinks.log"
  set "ERROR_MSG=File may be managed by another installer. Refusing in-place overwrite; use standalone ZIP"
  exit /b 1
)
exit /b 0

:probe_inplace
set "PROBE=%TARGET%\.bannerlordfg-write-probe-%RANDOM%-%RANDOM%.tmp"
echo BannerlordFG deploy probe> "%PROBE%" 2> "%TMPBUILD%\probe.log"
if not exist "%PROBE%" (
  set "ERROR_MSG=Rename denied and target is not writable. Check ACLs/antivirus/handles; no deployment attempted"
  icacls "%MODULES%" > "%TMPBUILD%\modules-acl.log" 2>&1
  icacls "%TARGET%" > "%TMPBUILD%\target-acl.log" 2>&1
  exit /b 1
)
del /Q "%PROBE%" > "%TMPBUILD%\probe-delete.log" 2>&1
if exist "%PROBE%" (
  set "ERROR_MSG=Probe file could not be removed. In-place install canceled"
  exit /b 1
)
echo [INFO] Write probe succeeded. Deploying over existing files; full backup preserved.
exit /b 0

:deploy_existing_archive
if "%~2"=="" (
  set "ERROR_MSG=Missing ZIP path. Usage: BUILD_WINDOWS.cmd --deploy-only ""C:\path\BannerlordFG-v1.0.0.zip"""
  goto :fail
)
set "ARCHIVE=%~2"
if not exist "%ARCHIVE%" (
  set "ERROR_MSG=Already-built standalone ZIP not found"
  goto :fail
)
mkdir "%TMPBUILD%" 2>nul
mkdir "%TMPBUILD%\stage" 2>nul
if not exist "%TMPBUILD%\stage\" (
  set "ERROR_MSG=Unable to create temporary deploy folder"
  goto :fail
)
echo [DEPLOY ONLY] Extracting and verifying the previously compiled standalone ZIP...
tar.exe -xf "%ARCHIVE%" -C "%TMPBUILD%\stage"
if errorlevel 1 (
  set "ERROR_MSG=Cannot extract previously compiled standalone ZIP"
  goto :fail
)
goto :validate_staged_module

:fail
echo [ERROR] %ERROR_MSG%
if exist "%TMPBUILD%\" echo Temporary build files and logs: "%TMPBUILD%"
if defined BACKUP echo Existing module backup, if completed: "%BACKUP%"
exit /b 1

:check_closed
for %%P in (Bannerlord.exe Bannerlord.Native.exe Bannerlord.Launcher.exe TaleWorlds.MountAndBlade.Launcher.exe) do (
  tasklist /FI "IMAGENAME eq %%P" /FO CSV /NH 2>nul | find /I "%%P" >nul
  if not errorlevel 1 (
    echo [ERROR] %%P is running. Close the game and launcher normally.
    exit /b 1
  )
)
exit /b 0

:install_harmony
echo [1/5] Downloading pinned Harmony 2.3.3 from official NuGet...
mkdir "%TMPBUILD%\harmony" >nul 2>nul
curl.exe --fail --location --retry 3 --silent --show-error --output "%TMPBUILD%\harmony.nupkg" "https://api.nuget.org/v3-flatcontainer/lib.harmony/2.3.3/lib.harmony.2.3.3.nupkg"
if errorlevel 1 (
  set "ERROR_MSG=Harmony download failed"
  exit /b 1
)
tar.exe -xf "%TMPBUILD%\harmony.nupkg" -C "%TMPBUILD%\harmony"
if errorlevel 1 (
  set "ERROR_MSG=Harmony package extraction failed"
  exit /b 1
)
set "HARMONY_SRC="
for %%F in (net472 net48 net462 net452 net45) do if not defined HARMONY_SRC if exist "%TMPBUILD%\harmony\lib\%%F\0Harmony.dll" set "HARMONY_SRC=%TMPBUILD%\harmony\lib\%%F\0Harmony.dll"
if not defined HARMONY_SRC (
  set "ERROR_MSG=Harmony has no compatible .NET Framework DLL"
  exit /b 1
)
if not exist "%ROOT%deps\Harmony\" mkdir "%ROOT%deps\Harmony"
rem HARMONY_SRC is expanded AFTER the FOR above has executed.
copy /Y "%HARMONY_SRC%" "%HARMONY%" >nul
if errorlevel 1 (
  set "ERROR_MSG=Failed to copy Harmony DLL from: %HARMONY_SRC%"
  exit /b 1
)
if not exist "%HARMONY%" (
  set "ERROR_MSG=Harmony DLL missing after copy"
  exit /b 1
)
exit /b 0

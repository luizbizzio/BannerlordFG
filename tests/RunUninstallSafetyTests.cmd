@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "ROOT=%~dp0.."
set "OUT=%ROOT%\work\uninstall-test-%RANDOM%-%RANDOM%"
set "TEMP=%OUT%\temp"
set "TMP=%TEMP%"
set "NUGET_PACKAGES=%ROOT%\work\nuget-packages"
set "NUGET_HTTP_CACHE_PATH=%ROOT%\work\nuget-http"
set "MSBUILD=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
set "CSC=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
mkdir "%TEMP%" || exit /b 2
if not exist "%MSBUILD%" goto :failed
if not exist "%CSC%" goto :failed
"%MSBUILD%" "%ROOT%\src\BannerlordFG.Setup\BannerlordFG.Setup.csproj" /restore /t:Build /p:Configuration=Release /p:OutputPath="%OUT%" /v:minimal /nologo
if errorlevel 1 goto :failed
"%CSC%" /nologo /langversion:latest /target:exe /out:"%OUT%\UninstallSafetyHarness.exe" /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll "%ROOT%\tests\UninstallSafetyHarness.cs"
if errorlevel 1 goto :failed
"%OUT%\UninstallSafetyHarness.exe" "%OUT%\fixtures"
set "RESULT=%ERRORLEVEL%"
rmdir /s /q "%OUT%"
exit /b %RESULT%
:failed
rmdir /s /q "%OUT%"
exit /b 1

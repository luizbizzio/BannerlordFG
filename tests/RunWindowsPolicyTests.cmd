@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "ROOT=%~dp0.."
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
  set "CSC_LIST=%TEMP%\BannerlordFG-csc-%RANDOM%-%RANDOM%.txt"
  call :find_csc
)
if not exist "%CSC%" (
  echo [ERROR] C# compiler not available. Install VS 2022 Build Tools.
  exit /b 2
)
set "OUT=%TEMP%\BannerlordFG-Policy-%RANDOM%-%RANDOM%.exe"
"%CSC%" /nologo /target:exe /out:"%OUT%" /r:System.dll /r:System.Core.dll /r:System.Management.dll "%ROOT%\src\Common\RendererDecision.cs" "%ROOT%\src\Common\GameHostMarker.cs" "%ROOT%\src\BannerlordFG.Setup\GpuDetection.cs" "%ROOT%\tests\WindowsPolicyHarness.cs"
if errorlevel 1 exit /b 1
"%OUT%"
set "RESULT=%ERRORLEVEL%"
del /q "%OUT%" 2>nul
exit /b %RESULT%

:find_csc
"%VSWHERE%" -latest -products * -find "MSBuild\**\Bin\Roslyn\csc.exe" > "%CSC_LIST%"
for /f "usebackq delims=" %%I in ("%CSC_LIST%") do set "CSC=%%I"
del /q "%CSC_LIST%" 2>nul
exit /b 0

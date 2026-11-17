@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "ROOT=%~dp0.."
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "OUT=%ROOT%\work\diagnostics-tests-%RANDOM%-%RANDOM%"
mkdir "%OUT%" || exit /b 2
set "TEMP=%OUT%\temp"
set "TMP=%TEMP%"
mkdir "%TEMP%" || exit /b 2
if exist "%VSWHERE%" (
  "%VSWHERE%" -latest -products * -find "MSBuild\**\Bin\Roslyn\csc.exe" > "%OUT%\csc.txt"
  for /f "usebackq delims=" %%I in ("%OUT%\csc.txt") do set "CSC=%%I"
)
if not exist "%CSC%" goto :failed
"%CSC%" /nologo /target:library /out:"%OUT%\ReShadeFixture.dll" /r:System.dll "%ROOT%\tests\fixtures\ReShadeIdentity.cs"
if errorlevel 1 goto :failed
"%CSC%" /nologo /langversion:latest /target:exe /out:"%OUT%\DiagnosticsHarness.exe" /r:System.dll /r:System.Core.dll /r:System.Management.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "%ROOT%\src\Common\UnifiedLog.cs" "%ROOT%\src\Common\RendererDecision.cs" "%ROOT%\src\Common\GameHostMarker.cs" "%ROOT%\src\BannerlordFG.Setup\GpuDetection.cs" "%ROOT%\src\BannerlordFG\Core\NativeMenuLayout.cs" "%ROOT%\src\BannerlordFG\Diagnostics\DiagnosticPolicy.cs" "%ROOT%\src\BannerlordFG\Diagnostics\VendorIdentity.cs" "%ROOT%\src\BannerlordFG\Diagnostics\GameVersionDetector.cs" "%ROOT%\src\BannerlordFG\Diagnostics\LauncherDetector.cs" "%ROOT%\src\BannerlordFG\Diagnostics\DiagnosticsRuntimeContext.cs" "%ROOT%\src\BannerlordFG\Diagnostics\ModuleInventory.cs" "%ROOT%\src\BannerlordFG\Diagnostics\GraphicsInventory.cs" "%ROOT%\src\BannerlordFG\Diagnostics\WindowsProbe.cs" "%ROOT%\src\BannerlordFG\Diagnostics\DiagnosticsBundle.cs" "%ROOT%\src\BannerlordFG\Diagnostics\PrivacyRedactor.cs" "%ROOT%\src\BannerlordFG\Diagnostics\DiagnosticsExportService.cs" "%ROOT%\tests\DiagnosticsHarness.cs" "%ROOT%\tests\DiagnosticsExtendedTests.cs"
if errorlevel 1 goto :failed
"%OUT%\DiagnosticsHarness.exe" "%OUT%\ReShadeFixture.dll"
set "RESULT=%ERRORLEVEL%"
rmdir /s /q "%OUT%"
exit /b %RESULT%
:failed
rmdir /s /q "%OUT%"
exit /b 1

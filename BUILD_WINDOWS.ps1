# GPL-3.0-only. Optional compatibility wrapper; the supported build is CMD.
$ErrorActionPreference = 'Stop'
$cmd = Join-Path $PSScriptRoot 'BUILD_WINDOWS.cmd'
& cmd.exe /d /c ('"' + $cmd + '"')
exit $LASTEXITCODE

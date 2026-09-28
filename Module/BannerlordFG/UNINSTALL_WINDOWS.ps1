# Recovery only: preferred path is Options > Performance > Frame Generation -> Uninstall on the initial game menu.
# Run this script BEFORE removing the module folder, after closing the game normally.
$ErrorActionPreference = 'Stop'
$gameRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$bin = Join-Path $gameRoot 'bin\Win64_Shipping_Client'
$helper = Join-Path $PSScriptRoot 'bin\Win64_Shipping_Client\BannerlordFG.Helper.cmd'
if (-not (Test-Path $helper)) { throw 'BannerlordFG maintenance helper is missing. Reinstall the complete mod ZIP before using this recovery script.' }
if (-not (Test-Path (Join-Path $bin 'Bannerlord.exe')) -and -not (Test-Path (Join-Path $bin 'Bannerlord.Native.exe'))) {
    throw "Could not find Bannerlord executable in $bin"
}
Write-Host 'Close Bannerlord and its launcher NORMALLY before continuing.' -ForegroundColor Yellow
Read-Host 'Press Enter once closed'
if (Get-Process -Name Bannerlord,Bannerlord.Native -ErrorAction SilentlyContinue) {
    throw 'Bannerlord is still running. Nothing was removed.'
}
& $helper --uninstall-closed-game --gamebin $bin
Write-Host "Check $env:LOCALAPPDATA\BannerlordFG\first-run.log for the result." -ForegroundColor Cyan

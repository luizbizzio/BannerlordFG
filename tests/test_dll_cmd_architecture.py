"""Portable checks for DLL/CMD architecture and no custom setup executable.
Does not prove Windows PowerShell, UAC, Defender, or Bannerlord runtime behavior.
"""
from pathlib import Path
from xml.etree import ElementTree as ET
R=Path(__file__).resolve().parents[1]
MOD=R/'Module/BannerlordFG'
BUILD=(R/'BUILD_WINDOWS.cmd').read_text()
SUB=(R/'src/BannerlordFG/SubModule.cs').read_text()
CMD=(MOD/'bin/Win64_Shipping_Client/BannerlordFG.Helper.cmd').read_text()
ENTRY=(R/'src/BannerlordFG.Setup/MaintenanceEntry.cs').read_text()
PROJ=(R/'src/BannerlordFG.Setup/BannerlordFG.Setup.csproj').read_text()

def test_maintenance_build_is_library_not_executable():
    assert '<OutputType>Library</OutputType>' in PROJ
    assert '<AssemblyName>BannerlordFG.Maintenance</AssemblyName>' in PROJ
    assert 'Program.cs' not in PROJ and 'SetupForm.cs' not in PROJ
    assert not (R/'src/BannerlordFG.Setup/Program.cs').exists()
    assert not (R/'src/BannerlordFG.Setup/SetupForm.cs').exists()

def test_cmd_uses_windows_host_with_no_policy_bypass_or_remote_script():
    assert 'powershell.exe -NoLogo -NoProfile -NonInteractive' in CMD
    assert '-ExecutionPolicy Bypass' not in CMD
    assert 'Invoke-Expression' not in CMD and 'DownloadString' not in CMD
    assert '[System.Reflection.Assembly]::LoadFrom($env:BFG_MAINTENANCE)' in CMD
    assert '$env:BFG_GAMEBIN' in CMD and '$env:BFG_OP' in CMD
    assert 'maintenance-host.log' in CMD

def test_in_game_worker_launches_cmd_with_uac_for_graphics_changes():
    assert '_helper = Path.Combine(' in SUB
    assert '"BannerlordFG.Helper.cmd"' in SUB
    assert 'UseShellExecute = true' in SUB
    assert 'psi.Verb = "runas"' in SUB
    assert 'TaleWorlds.Engine.Utilities.QuitGame()' in SUB

def test_install_safety_reused_without_shell_reimplementation():
    auto=(R/'src/BannerlordFG.Setup/AutoFirstRun.cs').read_text()
    inst=(R/'src/BannerlordFG.Setup/OptiInstaller.cs').read_text()
    assert 'OptiInstaller.PrepareOfficialPackageAsync(Log)' in auto
    assert 'package.Verify();' in auto
    assert 'while ((ProcessAlive(waitPid) || OptiInstaller.BannerlordIsRunning())' in auto
    assert 'PinnedSha256' in inst and 'HashFile' in inst
    assert 'OptiInstaller.Uninstall(gameBin, Log)' in auto
    assert '--post-exit-uninstall' in ENTRY and '--auto-first-run' in ENTRY

def test_package_requires_cmd_and_dll_without_shipping_setup_exe():
    assert 'BannerlordFG.Maintenance.dll' in BUILD
    assert 'BannerlordFG.Helper.cmd' in BUILD
    assert 'BannerlordFG.Setup.exe' not in (MOD/'bin/Win64_Shipping_Client').as_posix() # template has none
    assert list(MOD.rglob('*.exe')) == []
    assert 'BannerlordFG-v1.0.0.zip' in BUILD
    assert ET.parse(MOD/'SubModule.xml').getroot().find('Version').get('value') == 'v1.0.0'

def test_launcher_does_not_block_graphics_maintenance():
    root = Path(__file__).resolve().parents[1]
    installer = (root / 'src' / 'BannerlordFG.Setup' / 'OptiInstaller.cs').read_text(encoding='utf-8')
    assert 'name.Equals("Bannerlord", StringComparison.OrdinalIgnoreCase)' in installer
    assert 'name.Equals("Bannerlord.Native", StringComparison.OrdinalIgnoreCase)' in installer
    assert 'name.StartsWith("TaleWorlds.MountAndBlade", StringComparison.OrdinalIgnoreCase)' not in installer

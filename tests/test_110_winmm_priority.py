"""v1.1.1 contracts for the WinMM-first, DXGI-fallback safe installer.
Not a substitute for compiling or testing OptiScaler in Bannerlord.
"""
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
INSTALLER = (ROOT/'src/BannerlordFG.Setup/OptiInstaller.cs').read_text()
RUNTIME = (ROOT/'src/BannerlordFG/SubModule.cs').read_text()
README = (ROOT/'README.md').read_text()
RECOVER = (ROOT/'RECOVER_OPTISCALER.cmd').read_text()


def function(start,end):
    return INSTALLER.split(start,1)[1].split(end,1)[0]


def test_winmm_first_with_dxgi_fallback_and_two_occupied_stop():
    code = function('private static string SelectProxyName(', 'internal static string ExistingInstallStatus(')
    assert code.index('if (!winmmOccupied) return "winmm.dll";') < code.index('if (!dxgiOccupied) return "dxgi.dll";')
    assert code.index('if (!dxgiOccupied) return "dxgi.dll";') < code.index('throw new IOException("Both winmm.dll and dxgi.dll are occupied')


def test_existing_injector_status_allows_either_single_foreign_proxy():
    code = function('internal static string ExistingInstallStatus(', 'internal static string ValidateGameBin(')
    assert 'LooksLikeOptiScaler(path)' in code
    assert 'proxy.Equals("winmm.dll"' in code and 'proxy.Equals("dxgi.dll"' in code
    assert 'continue;' in code
    assert 'File.Exists(Path.Combine(gameBin, "winmm.dll")) &&' in code
    assert 'File.Exists(Path.Combine(gameBin, "dxgi.dll"))' in code


def test_uninstall_only_deletes_manifest_owned_proxy():
    code = function('internal static void Uninstall(', 'internal static async Task InstallAsync(')
    assert 'ManifestProxyName(m)' in code
    assert 'checksums[managedProxy]' in code
    assert 'HashFile(target).Equals(kv.Value' in code
    assert 'skipped.Add(kv.Key' in code
    assert 'File.Delete(Path.Combine(gameBin, "dxgi.dll"))' not in code


def test_runtime_installer_gate_allows_foreign_winmm_and_foreign_dxgi():
    code = RUNTIME.split('bool dxgiExists = File.Exists(',1)[1].split('private static string ProxyFromManifestText(',1)[0]
    assert 'if (dxgiExists && winmmExists)' in code
    assert 'if (winmmExists)\n' not in code
    assert 'if (dxgiExists)\n' not in code


def test_metadata_and_recovery_agree_with_110():
    assert ET.parse(ROOT/'Module/BannerlordFG/SubModule.xml').getroot().find('Version').get('value') == 'v1.1.1'
    assert 'BannerlordFG-v1.1.1.zip' in (ROOT/'BUILD_WINDOWS.cmd').read_text()
    assert 'version-1.1.1-blue' in README
    assert 'prefers `winmm.dll` by default' in README
    assert 'Existing dxgi.dll preserved' in RECOVER
    assert 'Existing winmm.dll preserved' in RECOVER

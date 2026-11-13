"""Contract checks for generic, non-owning d3d11.dll coexistence in v1.1.2.

Static checks only: validate Windows compilation and behavior inside Bannerlord.
"""
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
INSTALLER = (ROOT / 'src/BannerlordFG.Setup/OptiInstaller.cs').read_text()
UI = (ROOT / 'src/BannerlordFG/SubModule.cs').read_text()
README = (ROOT / 'README.md').read_text()


def test_installer_allows_existing_d3d11_without_requiring_reshade_metadata():
    preflight = INSTALLER.split('internal static string ExistingInstallStatus(',1)[1].split('internal static string ValidateGameBin(',1)[0]
    assert 'proxy.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase)' in preflight
    assert preflight.index('LooksLikeOptiScaler(path)') < preflight.index('proxy.Equals("d3d11.dll"')
    assert 'ReShadeCompatibility' not in preflight
    assert 'winmm.dll' in preflight and 'dxgi.dll' in preflight


def test_menu_gate_allows_generic_d3d11():
    gate = UI.split('bool dxgiExists = File.Exists(',1)[1].split('private static string ProxyFromManifestText(',1)[0]
    assert '"d3d11.dll"' not in gate
    assert '"d3d12.dll"' in gate
    assert 'if (dxgiExists && winmmExists)' in gate


def test_install_never_overwrites_foreign_d3d11():
    install = INSTALLER.split('internal static async Task InstallAsync(',1)[1]
    assert 'File.Exists(Path.Combine(gameBin, f))' in install
    assert 'File.Copy(files[i], target, false)' in install
    assert 'checksums' in INSTALLER
    assert 'No external file will be overwritten.' in UI


def test_docs_do_not_promise_universal_reshade_success():
    assert 'This is a tested workaround, not a guarantee' in README
    assert 'does **not** guarantee compatibility' in README
    assert 'does not perform this workaround automatically' in README
    assert 'ReShadeCompatibility' not in UI
    assert not (ROOT / 'src/Shared/ReShadeCompatibility.cs').exists()

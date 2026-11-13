"""Dual-proxy regression contracts. Windows MSBuild and live-game tests remain required."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
I = (ROOT / 'src/BannerlordFG.Setup/OptiInstaller.cs').read_text()
W = (ROOT / 'src/BannerlordFG.Setup/AutoFirstRun.cs').read_text()
S = (ROOT / 'src/BannerlordFG/SubModule.cs').read_text()
U = (ROOT / 'Module/BannerlordFG/UNINSTALL_WINDOWS.ps1').read_text()

def body(text, start, end):
    return text.split(start, 1)[1].split(end, 1)[0]

def test_official_nightly_20261112_pinned_to_verified_sha256():
    assert 'internal const string Tag = "nightly-20261112"' in I
    assert 'OptiScaler_v10.0.0-pre1_20261112.7z' in I
    assert 'c32932ca340ffd605325101077b072209514ed0f110dc8b7ab92486eec1a5d93' in I
    assert 'pkg.Sha256.Equals(PinnedSha256' in I

def test_winmm_is_default_and_dxgi_is_safe_fallback():
    select = body(I, 'private static string SelectProxyName(', 'internal static string ExistingInstallStatus(')
    assert 'bool winmmOccupied = File.Exists(Path.Combine(gameBin, "winmm.dll"))' in select
    assert 'bool dxgiOccupied = File.Exists(Path.Combine(gameBin, "dxgi.dll"))' in select
    assert 'if (!winmmOccupied) return "winmm.dll";' in select
    assert 'if (!dxgiOccupied) return "dxgi.dll";' in select
    assert 'Both winmm.dll and dxgi.dll are occupied' in select
    status = body(I, 'internal static string ExistingInstallStatus(', 'internal static string ValidateGameBin(')
    assert 'LooksLikeOptiScaler(path)' in status
    assert 'proxy.Equals("winmm.dll"' in status
    assert 'proxy.Equals("dxgi.dll"' in status
    assert 'File.Exists(Path.Combine(gameBin, "winmm.dll")) &&' in status
    assert 'File.Exists(Path.Combine(gameBin, "dxgi.dll"))' in status
    assert 'No free supported proxy name' in status


def test_installer_never_overwrites_third_party_dxgi_or_winmm():
    install = body(I, 'internal static async Task InstallAsync(', 'private static string HashShort(')
    assert 'string proxyName = SelectProxyName(gameBin);' in install
    assert 'File.Copy(files[i], target, false)' in install
    assert 'if (!File.Exists(originalProxy) || File.Exists(renamedProxy))' in install
    assert 'File.Move(originalProxy, renamedProxy)' in install
    assert 'var collisions = relative.Where(f => File.Exists(Path.Combine(gameBin, f)))' in install
    assert 'proxyFileName", proxyName' in install
    assert 'movedProxySha256' in install
    assert 'finalFiles[proxyName] = HashFile(proxyPath)' in install
    assert 'File.Delete(Path.Combine(gameBin, "dxgi.dll"))' not in install

def test_uninstall_uses_manifest_selection_not_arbitrary_dxgi():
    uninstall = body(I, 'internal static void Uninstall(', 'internal static async Task InstallAsync(')
    assert 'string managedProxy = ManifestProxyName(m)' in uninstall
    assert 'checksums[managedProxy]' in uninstall
    assert 'HashFile(target).Equals(kv.Value' in uninstall
    assert 'skipped.Add(kv.Key' in uninstall
    assert 'movedDxgiSha256' in uninstall  # backward compatibility
    assert 'movedProxySha256' in uninstall
    assert 'foreach (var proxy in Proxies)' not in uninstall

def test_incomplete_recovery_tracks_selected_proxy_and_legacy_tags():
    recover = body(I, 'internal static bool CanRecoverIncompleteInstall(', 'internal static void RecoverIncompleteInstall(')
    assert 'managedProxy = ManifestProxyName(manifest)' in recover
    assert 'owned[managedProxy]' in recover
    assert 'candidate in new[] { managedProxy, "OptiScaler.dll"' in recover
    assert 'File.Delete(' not in recover
    for tag in ('nightly-20260926', 'nightly-20261016', 'nightly-20261106', 'nightly-20261109', 'nightly-20261112'):
        assert tag in I

def test_runtime_state_and_uninstall_ack_use_selected_proxy():
    assert 'ProxyFromManifestText(previous)' in S
    assert 'ProxyFromManifestText(text)' in S
    assert 'HasCompleteManagedInstall(gameBin)' in W
    assert 'GetManagedProxyName(gameBin)' in W
    assert '!File.Exists(StateManifest())' in S

def test_actual_launcher_process_checked_before_removal():
    assert 'name.Equals("TaleWorlds.MountAndBlade.Launcher",' in I
    assert 'p.ProcessName.Equals("TaleWorlds.MountAndBlade.Launcher",' in W
    assert 'TaleWorlds.MountAndBlade.Launcher -ErrorAction SilentlyContinue' in U

def test_no_native_binaries_bundled_and_version_metadata_retained():
    folder = ROOT / 'Module/BannerlordFG'
    assert not [file for file in folder.rglob('*') if file.suffix.lower() in {'.dll', '.exe'}]
    assert 'v1.1.2' in (folder / 'SubModule.xml').read_text()

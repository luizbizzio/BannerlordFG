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

def test_official_nightly_20261116_pinned_to_verified_sha256():
    assert 'internal const string Tag = "nightly-20261116"' in I
    assert 'OptiScaler_v10.0.0-pre1_20261116.7z' in I
    assert '6cd5704b47ff883666df7fec80ce55d113ecc1097a1db5caa3a1058bc007c2d0' in I
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
    assert 'CheckRuntimeCollisions(gameBin, pkg)' in install
    assert 'File.Exists(Path.Combine(gameBin, rel))' in I
    assert 'proxyFileName", proxyName' in install
    assert 'movedProxySha256' in install
    assert 'finalFiles[proxyName] = HashFile(proxyPath)' in install
    assert 'File.Delete(Path.Combine(gameBin, "dxgi.dll"))' not in install

def test_uninstall_uses_manifest_selection_not_arbitrary_dxgi():
    uninstall = body(I, 'internal static void Uninstall(', 'internal static async Task InstallAsync(')
    assert 'VerifiedRemovalPlan(gameBin, manifestPath)' in uninstall
    assert 'SafeManagedPath(gameBin, item.Relative, managedProxy)' in uninstall
    assert 'DeleteVerifiedFile(item.Path, item.Sha256)' in uninstall
    assert 'ManifestProxyName(manifest)' in I
    assert 'movedDxgiSha256' in I  # backward compatibility
    assert 'movedProxySha256' in I
    assert 'foreach (var proxy in Proxies)' not in uninstall

def test_incomplete_recovery_tracks_selected_proxy_and_legacy_tags():
    recover = body(I, 'internal static bool CanRecoverIncompleteInstall(', 'internal static void RecoverIncompleteInstall(')
    assert 'managedProxy = ManifestProxyName(manifest)' in recover
    assert 'owned[managedProxy]' in recover
    assert 'candidate in new[] { managedProxy, "OptiScaler.dll"' in recover
    assert 'File.Delete(' not in recover
    for tag in ('nightly-20260926', 'nightly-20261016', 'nightly-20261106', 'nightly-20261109', 'nightly-20261112', 'nightly-20261116'):
        assert tag in I

def test_runtime_state_and_uninstall_ack_use_selected_proxy():
    assert 'ProxyFromManifestText(text)' in S
    assert 'tracked != null && tracked.Count > 0' in S
    assert 'HasCompleteManagedInstall(gameBin)' in W
    assert 'OptiInstaller.IsGameProcessBusy(waitPid, gameBin)' in W
    assert '!File.Exists(StateManifest())' in S

def test_actual_launcher_process_checked_before_removal():
    assert 'foreach (string name in GpuDetection.GameProcessNames)' in I
    assert 'GpuDetection.IsExpectedGameProcess(process, gameBin' in I
    assert 'GameHostMarker.IsActive(gameBin, process.Id)' in I
    assert 'TaleWorlds.MountAndBlade.Launcher -ErrorAction SilentlyContinue' in U

def test_no_native_binaries_bundled_and_version_metadata_retained():
    folder = ROOT / 'Module/BannerlordFG'
    expected = {'0Harmony.dll', 'BannerlordFG.dll', 'BannerlordFG.Maintenance.dll',
                'SharpCompress.dll', 'Microsoft.Bcl.AsyncInterfaces.dll',
                'System.Buffers.dll', 'System.Memory.dll', 'System.Numerics.Vectors.dll',
                'System.Runtime.CompilerServices.Unsafe.dll',
                'System.Text.Encoding.CodePages.dll', 'System.Threading.Tasks.Extensions.dll'}
    assert {file.name for file in folder.rglob('*.dll')} == expected
    assert not list(folder.rglob('*.exe'))
    assert 'v1.1.3' in (folder / 'SubModule.xml').read_text()

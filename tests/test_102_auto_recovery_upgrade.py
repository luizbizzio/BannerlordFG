"""Static contracts for known-release incomplete-install recovery.

They do not run Bannerlord or execute Windows maintenance binaries.
"""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
SETUP = (ROOT / 'src/BannerlordFG.Setup/OptiInstaller.cs').read_text()
WORKER = (ROOT / 'src/BannerlordFG.Setup/AutoFirstRun.cs').read_text()


def section(start, end):
    return SETUP.split(start, 1)[1].split(end, 1)[0]


def test_known_archive_pairs_are_pinned_to_actual_published_builds():
    constants = dict(re.findall(r'(?:internal|private) const string (\w+) = "([^"]+)";', SETUP))
    assert constants['Tag'] == 'nightly-20261016'
    assert constants['PinnedSha256'] == '09149a5b17514a0594faf612467267c62ed8851dbc1dde0ade6763f1152e600f'
    assert constants['PreviousTag'] == 'nightly-20260926'
    assert constants['PreviousPinnedSha256'] == '464e2ecc5dab477e940186959a7fdee64880f61c63f52ff0f516e5502bc74e94'
    matcher = section('private static bool IsKnownManagedArchive(', 'internal const string ReleasePage')
    assert 'string.Equals(tag, Tag, StringComparison.Ordinal)' in matcher
    assert 'string.Equals(digest, PinnedSha256, StringComparison.OrdinalIgnoreCase)' in matcher
    assert 'string.Equals(tag, PreviousTag, StringComparison.Ordinal)' in matcher
    assert 'string.Equals(digest, PreviousPinnedSha256, StringComparison.OrdinalIgnoreCase)' in matcher
    assert '||' in matcher
    assert '|| true' not in matcher.lower()


def test_recovery_rejects_unknown_build_or_different_game():
    preflight = section('internal static bool CanRecoverIncompleteInstall(', 'internal static void RecoverIncompleteInstall(')
    assert 'Str(manifest, "gameBin").Equals(gameBin, StringComparison.OrdinalIgnoreCase)' in preflight
    assert 'IsKnownManagedArchive(Str(manifest, "tag"), Str(manifest, "archiveSha256"))' in preflight
    assert 'Manifest release tag or archive SHA256 is not a recognized BannerlordFG build.' in preflight
    assert 'Manifest has an unrecognized installation stage.' in preflight
    assert 'File.Exists(Path.Combine(gameBin, "dxgi.dll"))' in preflight
    assert 'File.Exists(Path.Combine(gameBin, "OptiScaler.ini"))' in preflight
    assert 'A previously installed binary was modified' in preflight
    assert 'An untracked file exists in the OptiScaler directory' in preflight
    assert 'ReparsePoint' in preflight
    assert 'File.Delete(' not in preflight


def test_cross_version_recovery_runs_after_validated_download_and_game_exit():
    worker = WORKER.split('internal static void Run(string requestedGameBin, int waitPid)', 1)[1]
    assert 'CanRecoverIncompleteInstall(gameBin, out recoveryReason)' in worker
    assert worker.index('package.Verify();') < worker.index('WriteAtomic(ready, waitPid.ToString());')
    assert worker.index('if (ProcessAlive(waitPid) || OptiInstaller.BannerlordIsRunning())') < worker.index('OptiInstaller.RecoverIncompleteInstall(gameBin, Log);')
    assert worker.index('OptiInstaller.RecoverIncompleteInstall(gameBin, Log);') < worker.index('OptiInstaller.InstallAsync(gameBin, gpu')
    recover = section('internal static void RecoverIncompleteInstall(', 'private static string GetManifestPath(')
    assert recover.index('HashFile(manifest).Equals(HashFile(snapshot)') < recover.index('Uninstall(gameBin, log);')
    assert 'File.Exists(manifest)' in recover
    assert 'ExistingInstallStatus(gameBin)' in recover


def test_manual_recovery_kept_for_unsafe_or_complete_installs():
    preflight = section('internal static bool CanRecoverIncompleteInstall(', 'internal static void RecoverIncompleteInstall(')
    assert 'The installed graphics files are already present.' in preflight
    assert 'return false;' in preflight
    assert 'Existing manifest is not safe to repair automatically:' in WORKER
    assert 'WriteAtomic(StatePath(gameBin, ".blocked"), blocked)' in WORKER
    assert (ROOT / 'Module/BannerlordFG/UNINSTALL_WINDOWS.ps1').is_file()

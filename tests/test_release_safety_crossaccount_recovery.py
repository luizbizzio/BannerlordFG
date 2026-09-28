"""Static release checks. These do not execute Windows UAC or the game."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
mod = (ROOT / 'src/BannerlordFG/SubModule.cs').read_text()
setup = (ROOT / 'src/BannerlordFG.Setup/OptiInstaller.cs').read_text()
auto = (ROOT / 'src/BannerlordFG.Setup/AutoFirstRun.cs').read_text()
entry = (ROOT / 'src/BannerlordFG.Setup/MaintenanceEntry.cs').read_text()
helper = (ROOT / 'Module/BannerlordFG/bin/Win64_Shipping_Client/BannerlordFG.Helper.cmd').read_text()
ignore = (ROOT / '.gitignore').read_text()


def test_helper_transports_original_user_state_and_documents():
    assert '--state-base' in mod and '--user-documents' in mod
    assert 'BFG_STATE_BASE=%~7' in helper
    assert 'BFG_USER_DOCUMENTS=%~9' in helper
    assert 'ConfigureCallerContext(gameBin)' in entry
    assert 'BFG_VERIFIED_STATE_DIR' in entry
    assert 'first-run-' in entry and 'expectedId' in entry


def test_elevated_worker_uses_caller_user_state_and_documents():
    fps = (ROOT / 'src/BannerlordFG.Setup/FpsCounterCleanup.cs').read_text()
    limiter = (ROOT / 'src/BannerlordFG.Setup/NativeLimiterCleanup.cs').read_text()
    assert 'AutoFirstRun.Folder' in setup
    assert 'AutoFirstRun.Folder' in fps and 'AutoFirstRun.Folder' in limiter
    assert 'BFG_VERIFIED_USER_DOCUMENTS' in fps
    assert 'BFG_VERIFIED_USER_DOCUMENTS' in limiter
    assert 'BFG_VERIFIED_STATE_DIR' in auto


def test_no_cross_account_elevated_relaunch():
    assert 'File.GetAccessControl(launch)' in auto
    assert 'WindowsIdentity.GetCurrent().User.Value' in auto
    assert 'Cross-account UAC detected' in auto
    assert auto.index('Cross-account UAC detected') < auto.index('using (var p = Process.Start(startInfo))')


def test_recovery_only_after_download_and_game_exit():
    worker = auto.split('internal static void Run(string requestedGameBin, int waitPid)', 1)[1]
    assert 'CanRecoverIncompleteInstall(gameBin' in worker
    assert worker.index('package.Verify();') < worker.index('WriteAtomic(ready, waitPid.ToString());')
    assert worker.index('BannerlordIsRunning())') < worker.index('RecoverIncompleteInstall(gameBin, Log)')
    assert worker.index('RecoverIncompleteInstall(gameBin, Log)') < worker.index('InstallAsync(gameBin, gpu')


def test_recovery_preserves_manifests_and_unknown_files():
    assert 'Verified interrupted-install manifest snapshot' in setup
    assert 'HashFile(manifest).Equals(HashFile(snapshot)' in setup
    assert 'An untracked file exists in the OptiScaler directory' in setup
    assert 'An untracked graphics/configuration file already exists' in setup
    assert 'A previously installed binary was modified' in setup
    assert 'Uninstall(gameBin, log)' in setup
    assert 'Manifest retained: unrecognized or modified tracked files' in setup
    assert 'FileAttributes.ReparsePoint' in setup


def test_game_allows_helper_owned_partial_recovery_without_force_overwrite():
    section = mod.split('private string ExistingInstallBlocker()', 1)[1].split('private void CloseNotice()', 1)[0]
    assert 'Delegating read-only ownership preflight' in section
    assert 'return null;' in section
    assert 'No graphics files will be overwritten' in section


def test_required_cmd_is_staged_in_public_repository():
    assert '!Module/BannerlordFG/bin/Win64_Shipping_Client/BannerlordFG.Helper.cmd' in ignore
    assert 'Module/BannerlordFG/bin/Win64_Shipping_Client/*' in ignore
    assert (ROOT / 'Module/BannerlordFG/bin/Win64_Shipping_Client/BannerlordFG.Helper.cmd').exists()

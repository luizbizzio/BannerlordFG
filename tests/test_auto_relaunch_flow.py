"""Regression checks for automatic download -> normal exit -> install -> relaunch.

Tests validate source-level invariants, not actual Windows game behavior.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MOD = (ROOT / 'src/BannerlordFG/SubModule.cs').read_text()
AUTO = (ROOT / 'src/BannerlordFG.Setup/AutoFirstRun.cs').read_text()


def section(start, end):
    return MOD.split(start, 1)[1].split(end, 1)[0]


def test_main_menu_install_prepares_before_normal_close():
    begin = section('private void BeginInstall(bool explicitlyRequested', 'private void BeginUninstall()')
    ready = section('private void CheckWorker()', 'private void CancelAutomaticSetup()')
    assert 'StartWorker("--auto-first-run")' in begin
    assert 'ShowSetupNotice();' in begin
    assert 'CloseAfterVerifiedDownload();' in ready
    assert 'File.Exists(ready)' in ready


def test_campaign_close_requires_consent_and_blocks_gameplay_during_download():
    begin = section('private void BeginInstallFromOptions()', 'private void PromptRestartForSetting()')
    notice = section('private void ShowSetupNotice()', 'private void ShowInstallFeedback(')
    ready = section('private void CheckWorker()', 'private void CancelAutomaticSetup()')
    assert 'Save your campaign before continuing' in begin
    assert '_autoRestartInstallApproved = true' in begin
    assert 'Installing OptiScaler' in notice
    assert '_noticeVisible = true;' in notice
    assert 'if (_enteredGameplay && (!_autoRestartInstallApproved || !_noticeVisible))' in ready
    assert 'ConfirmCloseAfterDownload();' in ready


def test_verified_download_required_before_quit_without_launch_capture():
    close = section('private void CloseAfterVerifiedDownload()', 'private void ConfirmCloseAfterDownload()')
    assert 'File.Exists(ready)' in close
    assert 'File.ReadAllText(ready).Trim() != _pid.ToString()' in close
    assert 'TaleWorlds.Engine.Utilities.QuitGame()' in close
    assert 'CaptureCurrentLaunch();' not in close
    assert 'StartWorker(' not in close
    assert 'State(".restart")' not in close


def test_background_worker_cleans_state_without_relaunch():
    run = AUTO.split('internal static void Run(string requestedGameBin, int waitPid)', 1)[1]
    assert run.index('package.Verify();') < run.index('WriteAtomic(ready, waitPid.ToString());')
    assert run.index('InstallAsync(gameBin, gpu') < run.index('installCompleted = true;')
    assert run.index('if (pending != null) try { File.Delete(pending); }') < run.index('OptiScaler installation complete')
    assert run.index('mutex.ReleaseMutex()') < run.index('OptiScaler installation complete')
    assert 'RelaunchDirect' not in AUTO


def test_confirmation_cleared_if_campaign_changes_or_install_fails():
    assert 'Campaign changed during OptiScaler download' in MOD
    assert 'if (_enteredGameplay) _autoRestartInstallApproved = false;' in MOD
    assert 'Player cancelled first-run setup' in MOD
    assert 'Player postponed OptiScaler installation' in MOD

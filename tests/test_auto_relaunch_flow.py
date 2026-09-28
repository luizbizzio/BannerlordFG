"""Regression checks for automatic download -> normal exit -> install -> relaunch.

Tests validate source-level invariants, not actual Windows game behavior.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MOD = (ROOT / 'src/BannerlordFG/SubModule.cs').read_text()
AUTO = (ROOT / 'src/BannerlordFG.Setup/AutoFirstRun.cs').read_text()


def section(start, end):
    return MOD.split(start, 1)[1].split(end, 1)[0]


def test_main_menu_install_downloads_before_auto_close():
    begin = section('private void BeginInstall(bool explicitlyRequested', 'private void BeginUninstall()')
    ready = section('private void CheckWorker()', 'private void CancelAutomaticSetup()')
    assert 'StartWorker("--auto-first-run")' in begin
    assert 'ShowSetupNotice();' in begin
    assert 'RestartAfterVerifiedDownload();' in ready
    assert 'File.Exists(ready)' in ready


def test_campaign_restart_requires_consent_and_blocks_gameplay_during_download():
    begin = section('private void BeginInstallFromOptions()', 'private void PromptRestartForSetting()')
    notice = section('private void ShowSetupNotice()', 'private void ShowInstallFeedback(')
    ready = section('private void CheckWorker()', 'private void CancelAutomaticSetup()')
    assert 'Save your campaign before continuing' in begin
    assert '_autoRestartInstallApproved = true' in begin
    assert 'Installing OptiScaler' in notice
    assert '_noticeVisible = true;' in notice
    assert 'if (_enteredGameplay && (!_autoRestartInstallApproved || !_noticeVisible))' in ready
    assert 'ConfirmRestartAfterDownload();' in ready


def test_verified_download_and_launch_capture_required_before_exit():
    restart = section('private void RestartAfterVerifiedDownload()', 'private void ConfirmRestartAfterDownload()')
    assert 'File.Exists(ready)' in restart
    assert 'File.ReadAllText(ready).Trim() != _pid.ToString()' in restart
    assert 'CaptureCurrentLaunch();' in restart
    assert 'if (!File.Exists(State(".launch")))' in restart
    assert restart.index('File.WriteAllText(State(".restart"), _pid.ToString())') < restart.index('TaleWorlds.Engine.Utilities.QuitGame()')
    assert 'File.Delete(State(".restart"))' in restart


def test_background_worker_cleans_state_and_then_relaunches():
    run = AUTO.split('internal static void Run(string requestedGameBin, int waitPid)', 1)[1]
    assert run.index('package.Verify();') < run.index('WriteAtomic(ready, waitPid.ToString());')
    assert run.index('InstallAsync(gameBin, gpu') < run.index('installCompleted = true;')
    assert run.index('if (pending != null) try { File.Delete(pending); }') < run.index('RelaunchDirect(gameBin, waitPid);')
    assert run.index('mutex.ReleaseMutex()') < run.index('RelaunchDirect(gameBin, waitPid);')


def test_confirmation_cleared_if_campaign_changes_or_install_fails():
    assert 'Campaign changed during OptiScaler download' in MOD
    assert 'if (_enteredGameplay) _autoRestartInstallApproved = false;' in MOD
    assert 'Player cancelled first-run setup' in MOD
    assert 'Player postponed OptiScaler restart' in MOD

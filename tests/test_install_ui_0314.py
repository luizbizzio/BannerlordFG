"""Portable regression tests for the v0.3.14 install/UI state contract.
These do not substitute for compiling against the player's TaleWorlds DLLs.
"""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
UI = (ROOT / 'src/BannerlordFG/Core/NativePerformancePatch.cs').read_text()
MOD = (ROOT / 'src/BannerlordFG/SubModule.cs').read_text()
HELPER = (ROOT / 'src/BannerlordFG.Setup/MaintenanceEntry.cs').read_text()
CMD = (ROOT / 'BUILD_WINDOWS.cmd').read_text()
CAP = (ROOT / 'src/BannerlordFG/Core/NativeMenuContract.cs').read_text()


def test_post_fg_counter_is_not_constructed_without_managed_install():
    assert re.search(r'if \(installed\)\s*\{\s*try\s*\{\s*rows\.Add\(CreateChoice\(vm, new FgShowFpsData', UI)
    assert UI.index('if (installed)', UI.index('private void PopulateVisualRows')) < UI.index('new FgShowFpsData')
    assert UI.index('new FgShowFpsData') < UI.index('if (!installed)')
    assert '{=BFGOptiScaler}OptiScaler' in UI
    assert 'Graphics Component' not in UI


def test_opt_in_install_has_callback_diagnostics_and_no_silent_gameplay_guard():
    fn = MOD.split('private void BeginInstallFromOptions()', 1)[1].split('private void PromptRestartForSetting()', 1)[0]
    assert 'OptiScaler Install clicked' in fn
    assert 'BeginInstall(true)' in fn
    assert '_enteredGameplay || _requestedExit' not in fn
    assert '_setupPending' not in MOD
    assert 'ShowInstallFeedback("OptiScaler installation blocked", blocker)' in MOD
    assert 'Recovered stale pending marker' in MOD


def test_installer_requests_approval_without_escalating_restart():
    worker = MOD.split('private bool StartWorker(string flags)', 1)[1].split('private void BeginInstall(', 1)[0]
    assert 'flags == "--auto-first-run"' in worker
    assert 'flags == "--post-exit-uninstall"' in worker
    assert 'psi.Verb = "runas"' in worker
    assert 'UseShellExecute = true' in worker
    assert 'approve the Windows administrator prompt' in MOD


def test_in_game_install_requires_restart_consent_before_automatic_exit():
    begin = MOD.split('private void BeginInstallFromOptions()', 1)[1].split('private void PromptRestartForSetting()', 1)[0]
    ready = MOD.split('private void CheckWorker()', 1)[1].split('private void CancelAutomaticSetup()', 1)[0]
    assert 'Save your campaign before continuing' in begin
    assert '"Install and restart"' in begin
    assert '_autoRestartInstallApproved = true; _beginApprovedInstallOnNextTick = true;' in begin
    assert 'if (_beginApprovedInstallOnNextTick)' in MOD
    assert 'BeginInstall(true);' in MOD.split('if (_beginApprovedInstallOnNextTick)', 1)[1].split('}', 1)[0]
    assert 'if (_enteredGameplay && (!_autoRestartInstallApproved || !_noticeVisible))' in ready
    assert 'ConfirmRestartAfterDownload();' in ready
    assert 'RestartAfterVerifiedDownload();' in ready
    assert 'if ((_noticeVisible || _manualInstallRequested)' in ready


def test_unlimited_sentinel_cannot_be_reintroduced():
    assert 'fps == 0' not in CAP
    assert 'return 30 + 5 * position;' in CAP
    assert 'position > 66' in CAP
    assert 'new FgUnlimitedData' not in UI


def test_previous_native_fps_setting_restored_only_if_mod_owned():
    assert '--restore-legacy-fps' in HELPER
    assert 'FpsCounterCleanup.RestoreAfterUninstall' in HELPER
    assert '--restore-legacy-fps' in CMD
    assert 'if (current == applied && current != original)' in (
        ROOT / 'src/BannerlordFG.Setup/FpsCounterCleanup.cs').read_text()

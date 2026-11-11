"""Source-level regression checks for diagnostics before PowerShell startup.

These tests do not execute Windows CMD/UAC or the Bannerlord game.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CMD = (ROOT / "Module/BannerlordFG/bin/Win64_Shipping_Client/BannerlordFG.Helper.cmd").read_text()
SUB = (ROOT / "src/BannerlordFG/SubModule.cs").read_text()


def test_missing_dll_is_checked_after_initial_status_marker():
    marker = 'helper started > "%BFG_STATE_BASE%.helper-started"'
    dll_check = 'if not exist "%BFG_MAINTENANCE%"'
    assert CMD.index(marker) < CMD.index(dll_check)
    assert 'BannerlordFG.Maintenance.dll is missing from the mod folder.' in CMD
    assert 'goto :early_failure' in CMD


def test_early_failures_produce_logs_and_user_visible_blocked_state():
    early = CMD.split(':early_failure\n', 1)[1]
    assert '[ERROR] %BFG_FAILURE%' in early
    assert '%BFG_STATE_BASE%.blocked' in early
    assert '%BFG_STATE_BASE%.pending' in early
    assert 'exit /b 2' in early
    assert 'if defined BFG_STATE_BASE for %%I in' in CMD


def test_install_process_handle_is_retained_and_exit_code_reported():
    assert 'private Process _installCmdProcess;' in SUB
    launcher = SUB.split('private bool StartWorker(string flags)', 1)[1].split(
        'private bool MaintenanceWorkerIsActive()', 1)[0]
    assert 'if (flags == "--auto-first-run")' in launcher
    assert '_installCmdProcess = process;' in launcher
    assert 'process = null;' in launcher
    assert 'if (process != null) process.Dispose();' in launcher
    watchdog = SUB.split('private void CheckInstallCmdExit(', 1)[1].split(
        'private void CancelAutomaticSetup()', 1)[0]
    assert 'process.HasExited' in watchdog
    assert 'int exitCode = process.ExitCode;' in watchdog
    assert 'File.Exists(blocked) || File.Exists(ready)' in watchdog
    assert 'File.WriteAllText(blocked, reason);' in watchdog
    assert 'File.WriteAllText(State(".disabled")' in watchdog


def test_exit_detection_runs_before_generic_timeout_and_not_on_uac_wait():
    watcher = SUB.split('private void CheckWorker()', 1)[1].split(
        'private void CheckInstallCmdExit(', 1)[0]
    assert watcher.index('CheckInstallCmdExit(ready, blocked);') < watcher.index('TotalSeconds > 120')
    assert 'if (!process.HasExited) return;' in SUB
    assert 'psi.Verb = "runas"' in SUB
    assert 'approve the Windows administrator prompt if one appears' in SUB

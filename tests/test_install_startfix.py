"""Regression checks for Windows maintenance startup diagnostics.

These validate source contracts only; Windows CMD/UAC and real game execution
must still be tested on a Windows computer.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SUB = (ROOT / "src/BannerlordFG/SubModule.cs").read_text()
AUTO = (ROOT / "src/BannerlordFG.Setup/AutoFirstRun.cs").read_text()
INSTALL = (ROOT / "src/BannerlordFG.Setup/OptiInstaller.cs").read_text()
HELPER = (ROOT / "Module/BannerlordFG/bin/Win64_Shipping_Client/BannerlordFG.Helper.cmd").read_text()


def test_elevated_launcher_is_actual_cmd_executable():
    assert 'Path.Combine(Environment.SystemDirectory, "cmd.exe")' in SUB
    assert 'psi.Verb = "runas"' in SUB
    assert '/D /S /C' in SUB and '--state-base' in SUB
    assert 'Maintenance cmd.exe launched' in SUB


def test_cmd_helper_reports_early_failure_and_host_entry():
    assert HELPER.index('CMD helper invoked:') < HELPER.index('if not exist "%BFG_MAINTENANCE%"')
    assert 'helper started > "%BFG_STATE_BASE%.helper-started"' in HELPER
    assert "PowerShell maintenance host started" in HELPER
    assert "Maintenance DLL loaded" in HELPER
    assert 'maintenance-host.log' in HELPER
    assert '%BFG_STATE_BASE%.blocked' in HELPER


def test_ui_does_not_wait_forever_if_cmd_exits_early():
    assert 'TotalSeconds > 120' in SUB
    assert '!MaintenanceWorkerIsActive()' in SUB
    assert 'State(".helper-started")' in SUB
    assert 'State(".disabled")' in SUB
    assert 'ShowInstallFeedback("OptiScaler installation failed"' in SUB


def test_worker_identifies_stages_and_network_timeouts():
    assert 'Maintenance DLL entered automatic install' in AUTO
    assert 'Detecting display GPU through Windows WMI' in AUTO
    assert 'searcher.Options.Timeout = TimeSpan.FromSeconds(20)' in AUTO
    assert 'Requesting pinned OptiScaler release metadata from GitHub' in INSTALL
    assert 'WithDownloadDeadline(client.DownloadStringTaskAsync' in INSTALL
    assert 'TimeSpan.FromMinutes(4)' in INSTALL
    assert 'client.CancelAsync()' in INSTALL

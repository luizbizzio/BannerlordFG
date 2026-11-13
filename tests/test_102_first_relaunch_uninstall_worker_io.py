"""Prevent an elevated worker's redirected logging handles from reaching the first game relaunch.

These tests verify code/package contracts only, not actual Windows process behavior.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
AUTO = (ROOT / "src/BannerlordFG.Setup/AutoFirstRun.cs").read_text()
SUB = (ROOT / "src/BannerlordFG/SubModule.cs").read_text()
CMD_PATH = ROOT / "Module/BannerlordFG/bin/Win64_Shipping_Client/BannerlordFG.Helper.cmd"
CMD = CMD_PATH.read_text()


def test_verified_download_closes_without_an_automatic_relaunch():
    section = SUB.split('private void CloseAfterVerifiedDownload()', 1)[1].split('private void ConfirmCloseAfterDownload()', 1)[0]
    assert 'File.ReadAllText(ready).Trim() != _pid.ToString()' in section
    assert 'TaleWorlds.Engine.Utilities.QuitGame();' in section
    assert 'StartWorker(' not in section
    assert '.restart' not in section
    assert 'RelaunchDirect' not in AUTO
    assert 'Process.Start(' not in AUTO


def test_cmd_buffers_powershell_output_then_imports_into_one_log():
    assert 'BFG_HOST_OUTPUT=' in CMD
    assert '> "%BFG_HOST_OUTPUT%" 2>&1' in CMD
    assert 'BannerlordFG.log' in CMD
    assert 'FileStream' in CMD
    assert 'FileShare]::Read' in CMD
    assert '>> "%BFG_LOGFILE%" 2>&1' not in CMD
    assert 'WriteAtomic(ready, waitPid.ToString())' in AUTO


def test_helper_is_valid_windows_cmd_line_endings():
    b = CMD_PATH.read_bytes()
    assert b.count(b"\r\n") > 10
    assert b.count(b"\n") == b.count(b"\r\n")
    for label in [b":gamebin\r\n", b":invoke\r\n", b":arguments\r\n"]:
        assert label in b

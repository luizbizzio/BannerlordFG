"""Prevent an elevated worker's redirected logging handles from reaching the first game relaunch.

These tests verify code/package contracts only, not actual Windows process behavior.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
AUTO = (ROOT / "src/BannerlordFG.Setup/AutoFirstRun.cs").read_text()
SUB = (ROOT / "src/BannerlordFG/SubModule.cs").read_text()
CMD_PATH = ROOT / "Module/BannerlordFG/bin/Win64_Shipping_Client/BannerlordFG.Helper.cmd"
CMD = CMD_PATH.read_text()


def test_game_relaunch_uses_shell_not_inherited_worker_stdout():
    section = AUTO.split("private static void RelaunchDirect(", 1)[1].split("internal static void RunRestartOnly(", 1)[0]
    assert "UseShellExecute = true" in section
    assert "UseShellExecute = false" not in section.split("var startInfo =", 1)[1].split("using (var p", 1)[0]
    assert "FileName = exe" in section
    assert "Arguments = string.Join(" in section
    assert "args.Select(QuoteWindowsArgument)" in section
    assert "WorkingDirectory = gameBin" in section
    assert "GetOwner(typeof(SecurityIdentifier))" in section
    assert "WindowsIdentity.GetCurrent().User.Value" in section


def test_uninstaller_has_independent_log_from_installer_and_game():
    invoke = CMD.split(":invoke", 1)[1]  # labels may be mentioned in goto references
    assert 'if /I "%BFG_OP%"=="--post-exit-uninstall" set "BFG_LOGFILE=%BFG_LOGDIR%\\maintenance-uninstall.log"' in CMD
    assert '>> "%BFG_LOGFILE%" 2>&1' in CMD
    assert 'Maintenance host failed, exit %BFG_RC%. >> "%BFG_LOGFILE%"' in CMD
    assert 'maintenance-uninstall.log' in SUB
    assert 'WriteAtomic(ready, waitPid.ToString())' in AUTO


def test_helper_is_valid_windows_cmd_line_endings():
    b = CMD_PATH.read_bytes()
    assert b.count(b"\r\n") > 10
    assert b.count(b"\n") == b.count(b"\r\n")
    for label in [b":gamebin\r\n", b":invoke\r\n", b":arguments\r\n"]:
        assert label in b

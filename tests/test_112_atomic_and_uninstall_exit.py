"""Regression checks for the failed 2026-11-09 installer and missing uninstall ACK.

Static tests only; Windows cmd.exe, .NET and the game are not run here.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WORKER = (ROOT / "src/BannerlordFG.Setup/AutoFirstRun.cs").read_text()
MODULE = (ROOT / "src/BannerlordFG/SubModule.cs").read_text()
CMD = (ROOT / "Module/BannerlordFG/bin/Win64_Shipping_Client/BannerlordFG.Helper.cmd").read_text()


def test_write_atomic_replaces_existing_markers_and_handles_create_race():
    block = WORKER.split("private static void WriteAtomic(", 1)[1].split(
        "// Some graphics settings are saved on game exit.", 1)[0]
    assert "File.Replace(temporary, path, null);" in block
    assert "File.Move(temporary, path);" in block
    assert "catch (IOException)" in block
    assert "if (!File.Exists(path)) throw;" in block
    assert "File.Delete(temporary)" in block
    assert 'WriteAtomic(StatePath(gameBin, ".enable-next-launch"), "true");' in WORKER


def test_uninstall_has_its_own_retained_process_handle_and_exits_safely():
    launch = MODULE.split("private bool StartWorker(string flags)", 1)[1].split(
        "private bool MaintenanceWorkerIsActive()", 1)[0]
    assert 'flags == "--post-exit-uninstall"' in launch
    assert '_uninstallCmdProcess = process;' in launch
    poll = MODULE.split("private void PollUninstallAcknowledgement()", 1)[1].split(
        "private bool IsManagedGraphicsInstalled()", 1)[0]
    assert "uninstaller.HasExited" in poll
    assert "uninstaller.ExitCode" in poll
    assert poll.index("uninstaller.HasExited") < poll.index('string ready = State(".uninstall-ready");')
    assert 'ShowInstallFeedback("OptiScaler uninstall failed", detail)' in poll
    assert 'File.Delete(State(".uninstall-pending"))' in poll


def test_uninstall_cmd_errors_do_not_mark_install_as_blocked():
    assert '"%BFG_STATE_BASE%.uninstall-failed"' in CMD
    assert '"--post-exit-uninstall"' in CMD
    assert '"%BFG_STATE_BASE%.blocked"' in CMD
    assert 'See BannerlordFG.log' in CMD

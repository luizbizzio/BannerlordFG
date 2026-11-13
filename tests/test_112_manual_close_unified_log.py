"""Static contract checks for manual launch and shared log, not a live Windows test."""
from pathlib import Path
R=Path(__file__).resolve().parents[1]
SUB=(R/'src/BannerlordFG/SubModule.cs').read_text()
AUTO=(R/'src/BannerlordFG.Setup/AutoFirstRun.cs').read_text()
ENTRY=(R/'src/BannerlordFG.Setup/MaintenanceEntry.cs').read_text()
CMD=(R/'Module/BannerlordFG/bin/Win64_Shipping_Client/BannerlordFG.Helper.cmd').read_text()
LOGGER=(R/'src/Common/UnifiedLog.cs').read_text()


def test_installer_only_closes_after_verified_download():
    close=SUB.split('private void CloseAfterVerifiedDownload()',1)[1].split('private void ConfirmCloseAfterDownload()',1)[0]
    assert 'File.ReadAllText(ready).Trim() != _pid.ToString()' in close
    assert 'TaleWorlds.Engine.Utilities.QuitGame();' in close
    assert 'StartWorker(' not in close
    assert 'RelaunchDirect' not in AUTO
    assert 'Process.Start(' not in AUTO


def test_settings_cleanup_does_not_launch():
    assert 'StartWorker("--post-exit-settings")' in SUB
    assert 'case "--post-exit-settings":' in ENTRY
    assert 'AutoFirstRun.RunSettingsAfterExit(gameBin, waitPid);' in ENTRY
    assert 'RunRestartOnly' not in ENTRY
    assert '--post-exit-install-restart' not in CMD
    assert '--post-exit-restart' not in CMD


def test_one_persistent_mod_log_and_short_lived_host_buffer():
    assert 'internal const string FileName = "BannerlordFG.log"' in LOGGER
    assert 'FileMode.OpenOrCreate' in LOGGER
    assert 'FileShare.Read' in LOGGER
    assert 'Thread.Sleep(25)' in LOGGER
    assert 'UnifiedLog.Write(directory, "GAME", message)' in SUB
    assert 'UnifiedLog.Write(Folder, "SETUP", message)' in AUTO
    assert '> "%BFG_HOST_OUTPUT%" 2>&1' in CMD
    assert 'del /q "%BFG_HOST_OUTPUT%"' in CMD
    assert '"%BFG_LOGFILE%" 2>&1' not in CMD
    for obsolete in ('runtime.log','first-run.log','maintenance-host.log','maintenance-uninstall.log','maintenance-restart.log'):
        assert obsolete not in SUB and obsolete not in AUTO and obsolete not in CMD


def test_version_and_nightly_not_changed():
    opt=(R/'src/BannerlordFG.Setup/OptiInstaller.cs').read_text()
    assert 'internal const string Tag = "nightly-20261112"' in opt
    assert 'c32932ca340ffd605325101077b072209514ed0f110dc8b7ab92486eec1a5d93' in opt
    assert 'v1.1.2' in (R/'Module/BannerlordFG/SubModule.xml').read_text()

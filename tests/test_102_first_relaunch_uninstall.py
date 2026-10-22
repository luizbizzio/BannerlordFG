"""Source-level checks: uninstall must work on the first automatic relaunch.

Cannot replace an actual Windows/Bannerlord UI test.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MOD = (ROOT / "src/BannerlordFG/SubModule.cs").read_text()
UI = (ROOT / "src/BannerlordFG/Core/NativePerformancePatch.cs").read_text()
WORKER = (ROOT / "src/BannerlordFG.Setup/AutoFirstRun.cs").read_text()


def test_startup_negative_cache_is_bounded_and_retried_on_tick():
    method = MOD.split("private bool IsManagedGraphicsInstalled()", 1)[1].split("private string StateManifest()", 1)[0]
    tick = MOD.split("protected override void OnApplicationTick", 1)[1].split("protected override void OnGameStart", 1)[0]
    assert "_cachedManagedInstall ||" in method
    assert "DateTime.UtcNow < _nextManagedInstallProbeUtc" in method
    assert "DateTime.UtcNow.AddSeconds(2)" in method
    assert "IsManagedGraphicsInstalled();" in tick
    assert "_managedInstallProbeElapsed >= 2f" in tick
    assert "ScheduleManagedInstallRefresh();" in method
    assert "File.ReadAllText(manifest)" in method


def test_install_refresh_changes_only_owned_visual_group():
    refresh = UI.split("private void RefreshManagedInstallRows()", 1)[1].split("private void RefreshSelectionRows()", 1)[0]
    assert "LiveGroups()" in refresh
    assert "TryGetOwner(group, out OptionsVM vm)" in refresh
    assert "id == IdInstall" in refresh
    assert "group.Options.Clear();" in refresh
    assert "PopulateVisualRows(group, vm" in refresh
    assert "Uninstall" in refresh
    assert "RefreshManagedInstallRows();" in UI


def test_uninstall_click_not_silent_and_rechecks_initial_negative():
    block = MOD.split("private void BeginUninstall()", 1)[1].split("private void StartUninstallAfterConfirmation()", 1)[0]
    assert "OptiScaler Uninstall clicked" in block
    assert "_nextManagedInstallProbeUtc = DateTime.MinValue;" in block
    assert "if (!IsManagedGraphicsInstalled())" in block
    assert "MaintenanceWorkerIsActive()" in block
    assert "StartUninstallAfterConfirmation();" in block


def test_worker_does_not_relaunch_before_manifest_commit_or_marker_cleanup():
    run = WORKER.split("internal static void Run(string requestedGameBin, int waitPid)", 1)[1]
    assert run.index("OptiInstaller.InstallAsync(gameBin, gpu") < run.index("installCompleted = true;")
    assert run.index("mutex.ReleaseMutex()") < run.index("RelaunchDirect(gameBin, waitPid);")
    assert run.index("File.Delete(pending)") < run.index("RelaunchDirect(gameBin, waitPid);")

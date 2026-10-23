"""Structural regression checks for 1.0.0 memory, I/O and first-relaunch fix.

These are source-level tests; a Windows MSBuild and live Bannerlord smoke test
remain required before releasing a compiled mod.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
UI = (ROOT / "src/BannerlordFG/Core/NativePerformancePatch.cs").read_text()
MOD = (ROOT / "src/BannerlordFG/SubModule.cs").read_text()
AUTO = (ROOT / "src/BannerlordFG.Setup/AutoFirstRun.cs").read_text()


def test_owned_menu_metadata_does_not_strongly_pin_viewmodels():
    assert "ConditionalWeakTable<OptionGroupVM, GroupOwner>" in UI
    assert "ConditionalWeakTable<OptionGroupVM, Marker>" in UI
    assert "ConditionalWeakTable<TextObject, Marker>" in UI
    assert "ConditionalWeakTable<OptionGroup, Marker>" in UI
    assert "WeakReference<OptionGroupVM>" in UI
    assert "RegisterOurGroup(__instance, __1)" in UI
    assert "RegisterOurGroup(extra, vm)" in UI
    assert "RegisterOurGroup(group, vm)" in UI
    assert "Dictionary<OptionGroupVM, OptionsVM>" not in UI
    assert "HashSet<OptionGroupVM>" not in UI


def test_dead_weak_refs_are_compacted_and_never_pinned_by_refresh():
    assert "CompactDeadGroups()" in UI
    assert "_liveGroupRefs.RemoveAll" in UI
    assert "foreach (var group in LiveGroups())" in UI
    assert "_ownGroups.ToArray()" not in UI
    assert "_groupOwners.GetValue(group, _ => new GroupOwner()).Vm = vm;" in UI


def test_negative_manifest_snapshot_is_rechecked_at_bounded_rate():
    method = MOD.split("private bool IsManagedGraphicsInstalled()", 1)[1].split("private string StateManifest()", 1)[0]
    assert "DateTime.UtcNow < _nextManagedInstallProbeUtc" in method
    assert method.index("if (_managedStateInitialized &&") < method.index("File.ReadAllText(manifest)")
    assert "_managedStateInitialized = true;" in method
    assert "DateTime.UtcNow.AddSeconds(2)" in method
    assert "NativePerformancePatch.ScheduleManagedInstallRefresh();" in method


def test_background_poll_only_runs_for_active_or_recovered_install():
    tick = MOD.split("protected override void OnApplicationTick", 1)[1].split("protected override void OnGameStart", 1)[0]
    assert "(_manualInstallRequested || _noticeVisible || _monitorInstallAtStartup)" in tick
    assert "if (_awaitingUninstallAck)" in tick
    assert "if (_uninstallAckElapsed >= 0.25f)" in tick
    assert "PollUninstallAcknowledgement();" in tick
    assert "_monitorInstallAtStartup = !_cachedManagedInstall && File.Exists(State(\".pending\"));" in MOD


def test_poll_is_disarmed_on_success_failure_and_user_cancel():
    check = MOD.split("private void CheckWorker()", 1)[1].split("private void BeginInstallFromOptions()", 1)[0]
    assert check.count("_manualInstallRequested = false;") >= 3
    assert check.count("_monitorInstallAtStartup = false;") >= 3
    assert "Player cancelled first-run setup" in check


def test_worker_cleans_up_before_relaunch():
    install = AUTO.split("internal static void Run(string requestedGameBin, int waitPid)", 1)[1]
    assert "bool installCompleted = false;" in install
    assert "installCompleted = true;" in install
    assert install.index("if (pending != null) try { File.Delete(pending); }") < install.index("RelaunchDirect(gameBin, waitPid);")
    assert install.index("mutex.ReleaseMutex()") < install.index("RelaunchDirect(gameBin, waitPid);")
    assert install.index("if (package != null) package.Dispose();") < install.index("RelaunchDirect(gameBin, waitPid);")
    assert install.count("RelaunchDirect(gameBin, waitPid);") == 1
    assert "if (gameBin != null && !installCompleted)" in install
    assert "if (markersCleared)" in install


def test_first_relaunch_uninstall_ignores_only_completed_stale_pending_marker():
    uninstall = MOD.split("private void BeginUninstall()", 1)[1].split("private void StartUninstallAfterConfirmation()", 1)[0]
    assert "if (!IsManagedGraphicsInstalled() || MaintenanceWorkerIsActive())" in uninstall
    assert "File.Delete(State(\".pending\"))" in uninstall
    assert "StartUninstallAfterConfirmation();" in uninstall
    assert "_controller.TryDisable" not in uninstall


def test_pinned_release_and_native_limit_remain_unchanged():
    assert "nightly-20261016" in (ROOT / "src/BannerlordFG.Setup/OptiInstaller.cs").read_text()
    assert "v1.0.3" in (ROOT / "Module/BannerlordFG/SubModule.xml").read_text()
    assert "if (_guardFailed" in MOD
    assert "_controller.MaintainVanillaSettings();" in MOD

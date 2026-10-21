"""v1.0.1 no-EXE regression checks. Integration on Windows remains mandatory."""
from pathlib import Path
p=Path(__file__).resolve().parents[1]
def read(path): return (p/path).read_text(encoding='utf-8')


def test_first_install_remembers_none_not_stale_performance():
    sub=read('src/BannerlordFG/SubModule.cs')
    ctrl=read('src/BannerlordFG/Core/FgController.cs')
    graphics=read('src/BannerlordFG/Core/GameGraphicsBridge.cs')
    assert 'CurrentNativeDlss > 0f ? "active" : "none"' in sub
    assert 'State(".native-upscaler-before-install")' in sub
    assert 'preferQualityOnFirstInstall: useQuality' in sub
    assert 'if (!preferQualityOnFirstInstall && dlss > 0)' in graphics
    assert 'else if (!preferQualityOnFirstInstall && s.LastCompatibleDlss.HasValue)' in graphics
    assert 'TryFindQualityPreset(out dlss)' in graphics
    assert 'CurrentNativeDlss => _graphics.CurrentDlss' in ctrl


def test_quality_is_queried_by_name_with_safe_fallback():
    graphics=read('src/BannerlordFG/Core/GameGraphicsBridge.cs')
    assert 'var labels = selection.GetSelectableOptionNames().ToList()' in graphics
    assert 'for (int i = labels.Count - 1; i >= 1; --i)' in graphics
    assert 'label.IndexOf("Quality"' in graphics
    assert 'label.IndexOf("Ultra"' in graphics
    assert 'int normalQuality = -1;' in graphics
    assert 'last.IndexOf("Performance"' in graphics
    assert 'last.IndexOf("DLAA"' in graphics


def test_uninstall_never_disables_fg_before_helper_ack():
    sub=read('src/BannerlordFG/SubModule.cs')
    begin=sub.split('private void StartUninstallAfterConfirmation()',1)[1].split('private void PollUninstallAcknowledgement()',1)[0]
    assert '_controller.TryDisable' not in begin
    assert 'State(".uninstall-pending")' in begin
    assert '--post-exit-uninstall' in begin
    assert '_awaitingUninstallAck = true' in begin
    poll=sub.split('private void PollUninstallAcknowledgement()',1)[1].split('private bool IsManagedGraphicsInstalled()',1)[0]
    assert 'State(".uninstall-ready")' in poll
    assert 'State(".uninstall-failed")' in poll
    assert poll.index('State(".uninstall-ready")') < poll.index('TaleWorlds.Engine.Utilities.QuitGame()')


def test_worker_checks_ownership_then_signals_then_uninstalls_after_exit():
    auto=read('src/BannerlordFG.Setup/AutoFirstRun.cs')
    uninstall=auto.split('internal static void RunUninstall(',1)[1].split('internal static void Run(',1)[0]
    assert uninstall.index('HasManagedInstall(gameBin)') < uninstall.index('WriteAtomic(ready, waitPid.ToString())')
    assert uninstall.index('WriteAtomic(ready, waitPid.ToString())') < uninstall.index('OptiInstaller.Uninstall(gameBin, Log)')
    assert uninstall.index('OptiInstaller.Uninstall(gameBin, Log)') < uninstall.index('StatePath(gameBin, ".uninstalled")')
    assert 'throw; // Propagate failure to maintenance-host.log' in uninstall
    assert 'StatePath(gameBin, ".uninstall-failed")' in uninstall


def test_original_native_settings_restored_only_after_verified_removal():
    sub=read('src/BannerlordFG/SubModule.cs')
    ctrl=read('src/BannerlordFG/Core/FgController.cs')
    assert 'File.Exists(State(".uninstalled"))' in sub
    assert 'TryCompleteUninstall(out cleanup)' in sub
    clean=ctrl.split('public bool TryCompleteUninstall(',1)[1].split('// The separate Base FPS limiter',1)[0]
    assert '_graphics.RestoreOriginal(Settings)' in clean
    assert 'Settings.PendingRestart = false;' in clean
    assert 'Settings.LastCompatibleDlss = null;' in clean


def test_launcher_process_is_only_ignored_after_dxgi_unloads():
    auto=read('src/BannerlordFG.Setup/AutoFirstRun.cs')
    check=auto.split('private static bool OriginalPidStillUsingGraphics(',1)[1].split('internal static bool InstallIfNeeded(',1)[0]
    assert 'TaleWorlds.MountAndBlade.Launcher' in check
    assert 'Path.Combine(gameBin, "dxgi.dll")' in check
    assert 'foreach (ProcessModule module in p.Modules)' in check
    assert 'catch { return true; }' in check # Fail closed if module scan denied.
    uninstall=auto.split('internal static void RunUninstall(',1)[1].split('internal static void Run(',1)[0]
    assert 'OriginalPidStillUsingGraphics(waitPid, gameBin)' in uninstall
    assert 'OptiInstaller.BannerlordIsRunning()' in uninstall

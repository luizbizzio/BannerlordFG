"""Live toggle, FPS overlay and legacy cleanup source-level regressions."""
import unittest
from pathlib import Path
from xml.etree import ElementTree as ET
R=Path(__file__).resolve().parents[1]
N=(R/'src/BannerlordFG/Core/NativePerformancePatch.cs').read_text()
D=(R/'src/BannerlordFG/Core/FgMenuOptions.cs').read_text()
F=(R/'src/BannerlordFG/Core/NativeFpsCounter.cs').read_text()
S=(R/'src/BannerlordFG/SubModule.cs').read_text()
C=(R/'src/BannerlordFG.Setup/AutoFirstRun.cs').read_text()
U=(R/'src/BannerlordFG.Setup/FpsCounterCleanup.cs').read_text()
L=(R/'src/BannerlordFG.Setup/NativeLimiterCleanup.cs').read_text()

class Changes0312(unittest.TestCase):
    def test_multiplier_fixed_and_custom_slider_absent(self):
        self.assertNotIn('FgBaseLimitData',N+D)
        self.assertIn('GetSelectableOptionsLimit() => 1',D)
        self.assertIn('new SelectionData(false, "2x")',D)
    def test_show_fps_uses_optiscaler_and_legacy_cleanup_remains(self):
        self.assertIn('Controller.TrySetFpsOverlay(desired, out message)',D)
        self.assertNotIn('if (ok) _requestRestart?.Invoke();',D)
        self.assertIn('show_framerate',F)  # Old users are restored only on uninstall.
        self.assertIn('PromptRestartForSetting',S)
        self.assertIn('TaleWorlds.Engine.Utilities.QuitGame();',S)
        self.assertIn('RunRestartOnly(',C)
        self.assertIn('FpsCounterCleanup.ApplyRequestedAfterExit(Log);',C)
        self.assertIn('desired + m.Groups["after"].Value',U)
    def test_no_restart_in_live_toggle_path(self):
        live=D.split('bool canToggleLive = Controller.CanToggleLive;',1)[1].split('NativePerformancePatch.ScheduleSelectionRefresh();',1)[0]
        self.assertIn('Controller.TryToggleLive(out message)',live)
        self.assertIn('if (!canToggleLive) _requestRestart?.Invoke();',live)
        self.assertNotIn('if (canToggleLive) _requestRestart?.Invoke();',live)
    def test_no_crash_spam_or_unwanted_other_mod_dependencies(self):
        self.assertIn('if (!success)',D)
        self.assertIn('BFGOptiScaler}OptiScaler',N)
        x=ET.parse(R/'Module/BannerlordFG/SubModule.xml').getroot()
        self.assertEqual(x.find('Version').attrib['value'],'v1.0.1')
        self.assertEqual(x.findall('./DependedModules/DependedModule'),[])
        self.assertIn('0Harmony.dll',(R/'BUILD_WINDOWS.cmd').read_text())
    def test_install_and_uninstall_own_original_values_only(self):
        self.assertIn('fps-counter-owned.txt',F)
        self.assertIn('if (current == applied && current != original)',U)
        self.assertEqual(C.count('FpsCounterCleanup.RestoreAfterUninstall(Log);'),2)
        self.assertEqual(C.count('NativeLimiterCleanup.RestoreAfterUninstall(Log);'),2)
        self.assertIn('native-unlimited.marker',L)
        self.assertIn('current < 30f || current > 360f',L)
    def test_restart_without_killing_game_process(self):
        self.assertIn('StartWorker("--post-exit-restart")',S)
        self.assertIn('RelaunchDirect(gameBin, waitPid)',C)
        self.assertNotIn('taskkill',S.lower())
        self.assertNotIn('.Kill(',S)

if __name__=='__main__': unittest.main(verbosity=2)

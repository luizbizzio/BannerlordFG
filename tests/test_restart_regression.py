"""Source-level regression checks only; not a simulation of Steam/game runtime."""
import unittest
from pathlib import Path
R=Path(__file__).resolve().parents[1]
S=(R/'src/BannerlordFG/SubModule.cs').read_text()
A=(R/'src/BannerlordFG.Setup/AutoFirstRun.cs').read_text()
N=(R/'src/BannerlordFG/Core/NativePerformancePatch.cs').read_text()

class RestartCrashRegressions(unittest.TestCase):
    def test_game_does_not_launch_an_exe_after_install(self):
        self.assertIn('private void CloseAfterVerifiedDownload()', S)
        self.assertIn('TaleWorlds.Engine.Utilities.QuitGame()', S)
        self.assertNotIn('RelaunchDirect', A)
        self.assertNotIn('Process.Start(', A)
        self.assertNotIn('RunRestartOnly', A)
        self.assertNotIn('"--post-exit-install-restart"', S)

    def test_provider_placeholder_carries_no_custom_data(self):
        self.assertIn('new OptionGroup(title, Enumerable.Empty<IOptionData>())',N)
        self.assertIn('GroupConstructedPostfix',N)
        self.assertIn('PopulateVisualRows',N)
        self.assertNotIn('new OptionGroup(title, actions)',N)
    def test_crash_diagnostics_and_isolation(self):
        self.assertIn('RegisterExceptionDiagnostics();',S)
        self.assertIn('FirstChanceException',S)
        self.assertIn('native-ui-off.flag',S)
    def test_never_force_exit_or_set_sixty(self):
        self.assertIn('TaleWorlds.Engine.Utilities.QuitGame()',S)
        self.assertNotIn('Process.GetCurrentProcess().Kill(',S)
        self.assertNotIn('taskkill',S.lower()+A.lower())
        self.assertNotIn('NativeOptions.SetConfig(Limit, 60',S)
if __name__=='__main__': unittest.main(verbosity=2)

"""Source-level regression checks only; not a simulation of Steam/game runtime."""
import unittest
from pathlib import Path
R=Path(__file__).resolve().parents[1]
S=(R/'src/BannerlordFG/SubModule.cs').read_text()
A=(R/'src/BannerlordFG.Setup/AutoFirstRun.cs').read_text()
N=(R/'src/BannerlordFG/Core/NativePerformancePatch.cs').read_text()

class RestartCrashRegressions(unittest.TestCase):
    def test_launcher_named_host_reuses_explicit_module_argument(self):
        for needle in ['TaleWorlds.MountAndBlade.Launcher.exe','Environment.GetCommandLineArgs()',
                       'explicitModules.Length == 1','"_MODULES_*" + string.Join("*", moduleIds)',
                       'ReadActiveModuleIds()', 'GetModulesNames', 'GetActiveModules',
                       'ReadSelectedLauncherModules()', 'LauncherData.xml',
                       'Bannerlord.exe', 'refusing direct restart without the player']:
            self.assertIn(needle,S)
        self.assertIn('Path.GetFullPath(lines[0])',A)
        self.assertIn('FileName = exe',A)
        self.assertIn('_lateCaptureRetries < 4',S)
        self.assertIn('CaptureCurrentLaunch();',S)
        self.assertNotIn('steam://rungameid',A)
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

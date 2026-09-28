"""Portable code-contract tests ONLY, not compiled/running-game tests."""
import unittest
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SUB = (ROOT / 'src/BannerlordFG/SubModule.cs').read_text()
AUTO = (ROOT / 'src/BannerlordFG.Setup/AutoFirstRun.cs').read_text()
PROGRAM = (ROOT / 'src/BannerlordFG.Setup/MaintenanceEntry.cs').read_text()
INSTALL = (ROOT / 'src/BannerlordFG.Setup/OptiInstaller.cs').read_text()

class FirstRunChecks(unittest.TestCase):
    def test_launcher_uses_bannerlordfg_name(self):
        xml = ET.parse(ROOT / 'Module/BannerlordFG/SubModule.xml').getroot()
        self.assertEqual(xml.find('Name').attrib['value'], 'BannerlordFG')
        self.assertEqual(xml.find('Version').attrib['value'], 'v1.0.0')
        self.assertTrue(xml.find('SubModules/SubModule/DLLName').attrib['value'] == 'BannerlordFG.dll')

    def test_first_run_only_on_clean_install(self):
        for s in ['.disabled', '.blocked', '.pending', 'OptiScaler.ini', '--auto-first-run']:
            self.assertIn(s, SUB)
        self.assertIn('installation is initiated only by the user', SUB)
        self.assertIn('BeginInstall(true)', SUB)
        self.assertNotIn('_setupPending', SUB)

    def test_setting_up_dialog_visible_and_error_feedback(self):
        for s in ['Installing OptiScaler', 'ShowSetupNotice()', 'InformationManager.HideInquiry()',
                  'OptiScaler installation failed', 'InformationManager.ShowInquiry']:
            self.assertIn(s, SUB)

    def test_no_forced_game_termination(self):
        self.assertIn('TaleWorlds.Engine.Utilities.QuitGame()', SUB)
        self.assertNotIn('taskkill', SUB.lower())
        self.assertNotIn('Process.GetCurrentProcess().Kill(', SUB)
        self.assertNotIn('Environment.Exit(', SUB)
        self.assertIn('while ((ProcessAlive(waitPid) || OptiInstaller.BannerlordIsRunning())', AUTO)
        self.assertIn('throw new TimeoutException', AUTO)

    def test_download_verification_precedes_exit(self):
        run = AUTO.split('internal static void Run(string requestedGameBin, int waitPid)', 1)[1]
        self.assertLess(run.index('PrepareOfficialPackageAsync(Log)'), run.index('WriteAtomic(ready, waitPid.ToString())'))
        self.assertIn('package.Verify();', run)
        self.assertIn('File.ReadAllText(ready).Trim() == _pid.ToString()', SUB)

    def test_uninstall_is_post_exit_and_does_not_restart(self):
        self.assertIn('BeginInstallFromOptions', SUB)
        self.assertIn('BeginUninstall', SUB)
        self.assertIn('--post-exit-uninstall', SUB)
        uninst = AUTO.split('internal static void RunUninstall(', 1)[1].split('internal static void Run(', 1)[0]
        self.assertIn('OptiInstaller.Uninstall(gameBin, Log)', uninst)
        self.assertIn('while ((OriginalPidStillUsingGraphics(waitPid, gameBin)', uninst)
        self.assertIn('if (!ProcessAlive(waitPid)) return false;', AUTO)
        self.assertNotIn('RelaunchDirect(', uninst)
        self.assertIn('StatePath(gameBin, ".disabled")', uninst)
        self.assertIn('if (explicitlyRequested) try { File.Delete(State(".disabled")); }', SUB)

    def test_direct_exe_relaunch_on_success_only(self):
        self.assertIn('CaptureCurrentLaunch()', SUB)
        self.assertIn('Environment.GetCommandLineArgs()', SUB)
        self.assertIn('ReadActiveModuleIds()', SUB)
        self.assertIn('ReadSelectedLauncherModules()', SUB)
        self.assertIn('Bannerlord.exe', SUB)
        self.assertIn('File.Delete(marker); // one-shot authorization', AUTO)
        self.assertIn('RelaunchDirect(gameBin, waitPid);', AUTO)
        self.assertIn('Arguments = string.Join(" ", args.Select(QuoteWindowsArgument))', AUTO)
        self.assertIn('FileName = exe', AUTO)
        self.assertNotIn('steam://rungameid/261550', AUTO)

    def test_gpu_detection_and_foreign_file_safety(self):
        self.assertIn('Win32_VideoController', AUTO)
        self.assertIn('nvidia && amd', AUTO)
        self.assertIn('ExistingInstallStatus(gameBin)', AUTO)
        self.assertIn('HashFile(target)', INSTALL)
        self.assertIn('File.Copy(files[i], target, false)', INSTALL)
        self.assertIn('manifest["postinstallFiles"]', INSTALL)

    def test_offline_uninstall_recovery_in_module(self):
        self.assertIn('--uninstall-closed-game', PROGRAM)
        self.assertTrue((ROOT/'Module/BannerlordFG/UNINSTALL_WINDOWS.ps1').exists())

    def test_native_options_integration_without_obsolete_shortcut(self):
        native_ui = (ROOT/'src/BannerlordFG/Core/NativePerformancePatch.cs').read_text(encoding='utf-8')
        submodule = (ROOT/'src/BannerlordFG/SubModule.cs').read_text(encoding='utf-8')
        self.assertIn('Add visual-only action/selection rows', native_ui)
        self.assertNotIn('Ctrl+F10', submodule)

if __name__ == '__main__': unittest.main(verbosity=2)

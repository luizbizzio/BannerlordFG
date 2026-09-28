"""Static regression assertions for the safe CMD deployment flow (not a Windows execution test)."""
from pathlib import Path
import unittest
S = (Path(__file__).resolve().parents[1] / 'BUILD_WINDOWS.cmd').read_text(encoding='utf-8')
class TestSafeDeployment(unittest.TestCase):
    def test_checks_elevation_before_touching_old_module(self):
        self.assertLess(S.index('fltmc >nul 2>&1'), S.index('call :backup_existing'))
        self.assertLess(S.index('call :backup_existing'), S.index('call :rotate_existing'))
    def test_copies_backup_before_rename(self):
        self.assertIn('robocopy "%TARGET%" "%BACKUP%" /E /COPY:DAT', S)
        self.assertIn('ren "%TARGET%" "%OLD_NAME%"', S)
    def test_handles_robocopy_error_codes(self):
        self.assertGreaterEqual(S.count('if errorlevel 8 ('), 3)
    def test_rollback_preserves_old_source_on_copy_failure(self):
        self.assertIn('if defined OLD_ROTATED (', S)
        self.assertIn('ren "%PREVIOUS_PATH%" "BannerlordFG"', S)
        self.assertLess(S.index('if not exist "%TARGET%\\SubModule.xml"'), S.index('if defined OLD_ROTATED ('))
    def test_no_destructive_action_before_backup(self):
        stage = S[S.index('echo [5/5]'):S.index(':backup_existing')]
        self.assertNotIn('move "%TARGET%"', stage.lower())
        self.assertNotIn('rmdir /S /Q "%TARGET%"', stage)
    def test_no_forced_process_kill_or_powershell(self):
        self.assertNotIn('taskkill', S.lower())
        self.assertNotIn('powershell', S.lower())
if __name__ == '__main__': unittest.main()

"""Static checks of the rename-denied fallback and deploy-only routing.

These do not substitute for Windows runtime validation.
"""
from pathlib import Path
import unittest
S = (Path(__file__).resolve().parents[1] / 'BUILD_WINDOWS.cmd').read_text(encoding='utf-8')

class LockfixTests(unittest.TestCase):
    def test_deploy_only_does_not_need_msbuild(self):
        self.assertLess(S.index('if /I "%~1"=="--deploy-only" goto :deploy_existing_archive'), S.index('if not exist "%VSWHERE%"'))
        block = S.split('\n:deploy_existing_archive\n',1)[1].split('\n:fail\n',1)[0]
        self.assertIn('tar.exe -xf "%ARCHIVE%"', block)
        self.assertIn('goto :validate_staged_module', block)
    def test_never_install_through_reparse_target(self):
        check = S.index('call :check_target_reparse')
        back = S.index('call :backup_existing', check)
        self.assertLess(check, back)
        self.assertIn('fsutil reparsepoint query "%TARGET%"', S)
        self.assertIn('fsutil hardlink list "%~1"', S)
        self.assertLess(S.index('call :check_hardlinks'), S.index('call :probe_inplace'))
    def test_backup_then_rename_then_probe_then_in_place(self):
        segment = S[S.index('call :check_target_reparse',S.index('echo [5/5]')):S.index('echo Copying validated module')]
        for part in ('call :backup_existing', 'call :rotate_existing', 'call :probe_inplace', 'set "IN_PLACE=1"'):
            self.assertIn(part,segment)
        self.assertLess(segment.index('call :backup_existing'),segment.index('call :rotate_existing'))
    def test_verify_compiled_artifacts_and_rollback(self):
        self.assertIn(r'fc /B "%TMPBUILD%\stage\BannerlordFG\bin\Win64_Shipping_Client\%%F"',S)
        self.assertIn('if defined IN_PLACE goto :rollback_inplace',S)
        self.assertIn(r'robocopy "%BACKUP%" "%TARGET%" /E',S)
        self.assertNotIn('/MIR',S)
    def test_actual_new_module_never_created_before_backup(self):
        part=S[S.index('echo [5/5]'):S.index('echo Copying validated module')]
        self.assertLess(part.index('call :backup_existing'),part.index('call :rotate_existing'))
        self.assertLess(part.index('call :backup_existing'),part.index('call :probe_inplace'))

if __name__=='__main__': unittest.main()

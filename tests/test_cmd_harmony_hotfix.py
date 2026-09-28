"""Checks the CMD-specific premature variable expansion regression."""
from pathlib import Path
import unittest
SCRIPT = (Path(__file__).resolve().parents[1] / 'BUILD_WINDOWS.cmd').read_text(encoding='utf-8')
class HarmonyCmdHotfix(unittest.TestCase):
    def test_calls_isolated_subroutine(self):
        self.assertIn('call :install_harmony', SCRIPT)
        self.assertIn('\n:install_harmony\n', SCRIPT)
    def test_expands_source_only_after_for_line(self):
        block = SCRIPT.split('\n:install_harmony\n', 1)[1]
        self.assertLess(block.index('set "HARMONY_SRC="'), block.index('for %%F in (net472'))
        self.assertLess(block.index('for %%F in (net472'), block.index('copy /Y "%HARMONY_SRC%"'))
        self.assertIn('if not exist "%HARMONY%"', block)
if __name__ == '__main__': unittest.main()

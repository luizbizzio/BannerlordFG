import unittest
from pathlib import Path
R=Path(__file__).resolve().parents[1]
S=(R/'src/BannerlordFG/SubModule.cs').read_text()
N=(R/'src/BannerlordFG/Core/NativePerformancePatch.cs').read_text()

class InstallState0313(unittest.TestCase):
    def test_uninstall_requires_complete_managed_install(self):
        self.assertIn('IsManagedGraphicsInstalled', S)
        self.assertIn('File.Exists(dxgi)', S)
        self.assertIn('stage', S)
        self.assertIn('complete', S)
        self.assertIn('postinstallFiles', S)
        self.assertIn('if (!installed)', N)
        self.assertIn('{=BFGInstall}Install', N)
        self.assertIn('{=BFGUninstall}Uninstall', N)
    def test_unlimited_ui_removed(self):
        self.assertNotIn('FgUnlimitedData', N)
        self.assertNotIn('Unlimited Frame Limiter', N)

if __name__=='__main__': unittest.main(verbosity=2)

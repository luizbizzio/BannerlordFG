"""Static contract tests only, not a substitute for actual C# compilation or Bannerlord runtime."""
import re
import unittest
from pathlib import Path
from xml.etree import ElementTree as ET
ROOT=Path(__file__).resolve().parents[1]
SRC=ROOT/'src'/'BannerlordFG'/'Core'
class ContractTests(unittest.TestCase):
    def test_13_languages_and_managed_label(self):
        s=(SRC/'Locale.cs').read_text()
        for lang in ['English','Português (BR)','简体中文','繁體中文','Deutsch','Français','Italiano','日本語','한국어','Polski','Русский','Español (LA)','Türkçe']:
            self.assertIn('["'+lang+'"]', s)
        self.assertIn('Managed by BannerlordFG',s)
    def test_native_slider_preserved_and_separate_unlimited_with_rollback(self):
        native=(SRC/'NativeUnlimitedLimiter.cs').read_text()
        ui=(SRC/'NativePerformancePatch.cs').read_text()
        self.assertNotIn('if (__1 >= 359.5f', ui)
        self.assertNotIn('new FgUnlimitedData',ui)
        self.assertNotIn('NativeUnlimitedLimiter.StoredFiniteLimit ?? 360f',ui)
        self.assertNotIn('NativeOptions.SetConfig(type, 0f)', native)
        self.assertIn('NativeOptions.SetConfig(type, saved)',native)
        self.assertIn('RestoreFiniteAndRetire',native)
        self.assertNotIn('FgBaseLimitData',ui)
    def test_no_mcm_dependency(self):
        xml=ET.parse(ROOT/'Module'/'BannerlordFG'/'SubModule.xml').getroot()
        deps=[x.attrib['Id'] for x in xml.findall('./DependedModules/DependedModule')]
        self.assertNotIn('Bannerlord.MBOptionScreen',deps)
        self.assertNotIn('Bannerlord.ButterLib',deps)
    def test_off_restores_original_and_guard(self):
        s=(SRC/'FgController.cs').read_text()+(SRC/'GameGraphicsBridge.cs').read_text()
        self.assertIn('_graphics.RestoreOriginal(Settings)',s)
        self.assertIn('NativeOptions.SetConfig(Dlss, s.SavedGameDlss.Value)',s)
        self.assertIn('RepairOldFrameCap', s)
        self.assertIn('NativeOptions.SetConfig(Limit, s.SavedGameFps.Value)', s)
        self.assertNotIn('NativeOptions.SetConfig(Limit, s.BaseFps)', s)
        self.assertIn('TryApplySavedAtStartup',s)
    def test_native_frame_limiter_not_managed_and_defaults_unlimited(self):
        s=(SRC/'FgController.cs').read_text()
        settings=(SRC/'FgSettings.cs').read_text()
        graphics=(SRC/'GameGraphicsBridge.cs').read_text()
        self.assertIn('public bool NativeLimiterManaged => false;', s)
        self.assertIn('public int BaseFps { get; set; } = 0;', settings)
        self.assertNotIn('NativeOptions.SetConfig(Limit, s.BaseFps)', graphics)
        self.assertNotIn('CurrentFrameLimiter - s.BaseFps', graphics)
    def test_no_parallel_optiscaler_fps_limiter(self):
        s=(SRC/'OptiProfile.cs').read_text()
        self.assertIn('Put(t, "Framerate", "FramerateLimit", "0")',s)
        self.assertIn('Put(t, "Menu", "OverlayMenu", "true")',s)
if __name__=='__main__': unittest.main(verbosity=2)

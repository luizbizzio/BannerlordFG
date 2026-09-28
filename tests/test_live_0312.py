"""v0.3.12 invariants only; NO Windows/DXGI/OptiScaler runtime coverage."""
import unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT/'src/BannerlordFG/Core'
PROFILE = (CORE/'OptiProfile.cs').read_text()
INST = (ROOT/'src/BannerlordFG.Setup/OptiInstaller.cs').read_text()
KEYS = (CORE/'OptiLiveHotkeys.cs').read_text()
C = (CORE/'FgController.cs').read_text()
D = (CORE/'FgMenuOptions.cs').read_text()
N = (CORE/'NativePerformancePatch.cs').read_text()
LIMIT = (CORE/'NativeUnlimitedLimiter.cs').read_text()
CLEAN = (ROOT/'src/BannerlordFG.Setup/NativeLimiterCleanup.cs').read_text()
SETTINGS = (CORE/'FgSettings.cs').read_text()

class Live0312(unittest.TestCase):
    def test_native_fps_limiter_never_repurposes_last_notch(self):
        self.assertNotIn('__1 >= 359.5f',N)
        self.assertNotIn('NumericLabelPrefix',N)
        self.assertNotIn('NativeUnlimitedLimiter.StoredFiniteLimit ?? 360f',N)
        self.assertNotIn('new FgUnlimitedData',N)
        self.assertNotIn('NativeUnlimitedLimiter.TryDisable(out message)',D)
        self.assertNotIn('StoredFiniteLimit',LIMIT)
        self.assertIn('current < 30f || current > 360f',CLEAN)
        self.assertIn('NativeOptions.SetConfig(type, saved)',LIMIT)

    def test_live_internal_keys_are_consistent_in_installer_profile_and_injection(self):
        self.assertIn('ConfiguredAtLaunch(iniPath)', C)
        self.assertIn('CanToggleLive => _liveBackendReadyAtLaunch && _liveKeysReadyAtLaunch', C)
        self.assertIn('OptiLiveHotkeys.RequestFgToggle', C)
        self.assertIn('OptiLiveHotkeys.RequestOverlayToggle', C)
        self.assertIn('ConfigureLiveShortcutsInText(t)', PROFILE)
        self.assertIn('ConfigureLiveControlAssignments(text)', INST)
        self.assertIn('"FGShortcutKey", "0x86"', PROFILE)
        self.assertIn('"FpsShortcutKey", "0x87"', PROFILE)
        self.assertIn('"Menu", "FGShortcutKey", "0x86"', INST)
        self.assertIn('"Menu", "FpsShortcutKey", "0x87"', INST)
        self.assertIn('VK_F23 = 0x86', KEYS)
        self.assertIn('VK_F24 = 0x87', KEYS)

    def test_fps_overlay_defaults_on_but_is_live_controllable_when_initialized(self):
        self.assertIn('public bool ShowFpsOverlay { get; set; } = true;', SETTINGS)
        self.assertIn('"ShowFps", "true"', INST)
        self.assertIn('"FpsOverlayType", "0"', INST)
        self.assertIn('Controller.TrySetFpsOverlay(desired, out message)', D)
        self.assertIn('OptiLiveHotkeys.RequestOverlayToggle(out message)', C)
        self.assertIn('OptiLiveHotkeys.cs', (ROOT/'src/BannerlordFG/BannerlordFG.csproj').read_text())

    def test_multiplier_display_readonly_and_graphics_component_last(self):
        self.assertIn('GetSelectableOptionsLimit() => 1',D)
        self.assertIn('new ValueTuple<string, bool>("Only validated 2x is available.", true)',D)
        self.assertNotIn('new FgUnlimitedData', N)
        self.assertLess(N.index('new FgShowFpsData'),N.index('rows.Add(CreateAction(vm, IdUninstall'))
        self.assertNotIn('MultiplierFallback',N)

if __name__=='__main__': unittest.main(verbosity=2)

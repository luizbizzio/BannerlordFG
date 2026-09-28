"""v0.3.12 source contracts. Runtime validation still requires Windows/Bannerlord."""
import unittest
from pathlib import Path
R=Path(__file__).resolve().parents[1]
N=(R/'src/BannerlordFG/Core/NativePerformancePatch.cs').read_text()
D=(R/'src/BannerlordFG/Core/FgMenuOptions.cs').read_text()
C=(R/'src/BannerlordFG/Core/FgController.cs').read_text()
G=(R/'src/BannerlordFG/Core/GameGraphicsBridge.cs').read_text()
L=(R/'src/BannerlordFG/Core/NativeUpscalingLock.cs').read_text()
U=(R/'src/BannerlordFG/Core/NativeUnlimitedLimiter.cs').read_text()

class Controls(unittest.TestCase):
    def test_no_custom_base_limit_and_five_installed_rows(self):
        self.assertIn('new FgEnabledData(_controller, _isInstalled',N)
        self.assertNotIn('rows.Add(CreateChoice(vm, new FgMultiplierData',N)
        self.assertIn('rows.Add(CreateAction(vm, IdMultiplier, ExplainMultiplier',N)
        self.assertIn('rows.Add(CreateChoice(vm, new FgShowFpsData',N)
        self.assertNotIn('rows.Add(CreateChoice(vm, new FgUnlimitedData',N)
        self.assertIn('rows.Add(CreateAction(vm, IdUninstall',N)
        self.assertNotIn('Base FPS Limit', N)
        self.assertNotIn('FgBaseLimitData', N+D)
        self.assertNotIn('TrySetBaseFps', C)
        self.assertLess(N.index('new FgShowFpsData'),N.index('rows.Add(CreateAction(vm, IdUninstall'))
        self.assertEqual(N.count('rows.Add(CreateAction(vm, IdInstall'),1)
    def test_2x_only_noninteractive(self):
        self.assertIn('GetSelectableOptionsLimit() => 1',D)
        self.assertIn('new SelectionData(false, "2x")',D)
        self.assertIn('"Only validated 2x is available.", true',D)
        self.assertIn('Multiplier = 2', (R/'src/BannerlordFG/Core/FgSettings.cs').read_text())
    def test_native_upscaling_is_locked_while_fg(self):
        self.assertIn('nameof(NativeDisabledPostfix)',N)
        self.assertIn('nameof(NativeRowEnabledPostfix)',N)
        self.assertIn('NativeUpscalingLock.IsScalingOption(__0)',N)
        self.assertIn('"Managed by BannerlordFG", true',N)
        for key in ['ResolutionScale', 'DynamicResolution']:
            self.assertIn('NativeOptions.NativeOptionsType.'+key,L)
        self.assertIn('TryFindQualityPreset',G)
    def test_native_limiter_360_retained_with_separate_unlimited(self):
        self.assertNotIn('__1 >= 359.5f',N)
        self.assertNotIn('NativeUnlimitedLimiter.TryEnable',D)
        self.assertNotIn('NativeUnlimitedLimiter.TryDisable',D)
        self.assertNotIn('StoredFiniteLimit',U)
        self.assertNotIn('NativeOptions.SetConfig(type, 0f)',U)
        self.assertIn('RestoreFiniteAndRetire',U)
        self.assertNotIn('NativeUnlimitedLimiter.IsActive',N)
        self.assertNotIn('NativeUnlimitedLimiter.ReapplyAfterNativeSave()',N)
    def test_only_owned_vms_custom_ids(self):
        self.assertIn('new OptionGroup(title, Enumerable.Empty<IOptionData>())',N)
        self.assertIn('GroupManagedPrefix',N)
        self.assertIn('CategoryManagedPrefix',N)
        self.assertIn('SetConfigPrefix(IOptionData __0, float __1)',N)

if __name__=='__main__': unittest.main(verbosity=2)

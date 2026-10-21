"""Static regressions for the early native-provider / visual-only UI integration.
Cannot prove the game actually renders without the user's Windows runtime.
"""
import unittest
from pathlib import Path
from xml.etree import ElementTree as ET
R = Path(__file__).resolve().parents[1]
N = (R/'src/BannerlordFG/Core/NativePerformancePatch.cs').read_text()
S = (R/'src/BannerlordFG/SubModule.cs').read_text()
B = (R/'BUILD_WINDOWS.cmd').read_text()

class NativePerformanceChecks(unittest.TestCase):
    def test_provider_patch_precedes_group_ctor_and_viewmodel(self):
        self.assertIn('new Harmony(HarmonyId)', N)
        self.assertIn('nameof(ProviderPostfix)',N)
        self.assertIn('nameof(GroupConstructedPostfix)',N)
        self.assertLess(N.index('nameof(ProviderPostfix)'),N.index('nameof(GroupConstructedPostfix)'))
        self.assertIn('new OptionGroup(title, Enumerable.Empty<IOptionData>())',N)
        self.assertIn('groups.Insert(insert, blankGroup)',N)
        self.assertIn('NativeOptions.NativeOptionsType.DLSS',N)
        self.assertIn('new OptionGroupVM(title, vm, Enumerable.Empty<IOptionData>())',N)
    def test_empty_provider_and_manual_rows_keep_custom_ids_out_of_vanilla(self):
        self.assertIn('GroupConstructedPostfix',N)
        self.assertIn('new ActionOptionDataVM(callback, vm, option,',N)
        self.assertIn('rows.Add(CreateAction(vm, IdInstall',N)
        self.assertIn('new FgEnabledData(_controller, _isInstalled',N)
        self.assertIn('rows.Add(CreateAction(vm, IdUninstall',N)
        self.assertIn('if (!installed)',N)
        self.assertNotIn('new OptionGroup(title, actions)',N)
        self.assertNotIn('CreateNumeric(vm, new FgBaseLimitData',N)
        self.assertIn('!x.IsNative() && !x.IsAction()',N)
        for hook in ['GroupManagedPrefix','GroupChangedPrefix','GroupNoopPrefix',
                     'CategoryManagedPrefix','GetConfigPrefix','SetConfigPrefix']:
            self.assertIn(hook,N)
    def test_existing_cached_ui_fallback(self):
        for method in ['PerformanceGetterPostfix','OptionsConstructedPostfix',
                       'OptionsRefreshPostfix','CategoryInitializePostfix',
                       'GroupRefreshPostfix','TryAttachExisting']:
            self.assertIn(method,N)
        self.assertIn('_performanceOptionCategory',N)
        self.assertIn('[ThreadStatic] private static bool _attaching;',N)
        self.assertIn('catch (Exception ex) { Error(',N)
    def test_no_launcher_dependencies_and_bundled_harmony(self):
        xml=ET.parse(R/'Module/BannerlordFG/SubModule.xml').getroot()
        self.assertEqual(xml.findall('./DependedModules/DependedModule'),[])
        self.assertIn('NativePerformancePatch.Start',S)
        self.assertNotIn('UIExtenderEx',S)
        self.assertNotIn('Ctrl+F10',S)
        self.assertIn('0Harmony.dll',B)
        self.assertIn('BannerlordFG.Maintenance.dll',B)
        self.assertIn('native-ui-off.flag',B)
    def test_root_cause_missing_group_reset_fixed(self):
        # The actual uploaded ViewModelCollection.dll has ResetData only on
        # GroupedOptionCategoryVM; this bogus v0.3.7 preflight killed all hooks.
        self.assertNotIn('AccessTools.Method(group, "ResetData"', N)
        self.assertNotIn('instance._harmony.Patch(groupReset', N)
        self.assertIn('Missing required Options API:', N)
        self.assertIn('Harmony native Performance v1.0.1 installed', N)
    def test_native_fps_not_modified(self):
        self.assertNotIn('Base FPS Limit',N)
        self.assertNotIn('CreateChoice(vm, new FgMultiplierData', N)
        self.assertNotIn('SetConfig(Limit',N)

if __name__ == '__main__': unittest.main(verbosity=2)

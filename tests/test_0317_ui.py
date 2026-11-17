from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def get(p): return (ROOT/p).read_text()
def test_sharpness_removed():
 s=get('src/BannerlordFG/Core/OptiProfile.cs'); assert '"Sharpness", "Sharpness"' not in s; assert '"OverrideSharpness", "false"' in s
def test_multiplier_is_fixed_in_description_without_extra_row():
 s=get('src/BannerlordFG/Core/NativePerformancePatch.cs'); assert 'new TextObject("{=BFGFrameGeneration}Frame Generation")' in s; assert 'Fixed at 2x.' in s; assert 'IdMultiplier' not in s; assert 'rows.Add(CreateChoice(vm, new FgMultiplierData' not in s
def test_fps_description_is_not_hotkey():
 s=get('src/BannerlordFG/Core/NativePerformancePatch.cs'); assert 'Toggle live with Page Up' not in s
def test_uninstall_has_feedback():
 s=get('src/BannerlordFG/SubModule.cs'); x=s.split('private void BeginUninstall()',1)[1].split('private bool IsManagedGraphicsInstalled()',1)[0]; assert x.count('ShowInstallFeedback(')>=4; assert 'QuitGame()' in x
def test_upscaling_quality_available():
 s=get('src/BannerlordFG/Core/NativePerformancePatch.cs'); assert 'if (__1 <= 0f)' in s; assert 'LastCompatibleDlss = __1' in s

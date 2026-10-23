"""v1.0.1 source-contract checks. They are not a Windows/game integration test."""
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
def source(rel): return (ROOT / rel).read_text(encoding='utf-8')


def test_version_100_everywhere_that_is_a_release_identifier():
    assert ET.parse(ROOT / 'Module/BannerlordFG/SubModule.xml').getroot().find('Version').get('value') == 'v1.0.3'
    assert 'BannerlordFG-v1.0.3.zip' in source('BUILD_WINDOWS.cmd')
    assert 'SUCCESS: BannerlordFG v1.0.3 compiled and installed.' in source('BUILD_WINDOWS.cmd')


def test_title_and_multiplier_button_with_actual_callback():
    ui = source('src/BannerlordFG/Core/NativePerformancePatch.cs')
    assert 'Frame Generation | Multiplier' not in ui
    assert ui.count('new TextObject("{=BFGFrameGeneration}Frame Generation")') == 2
    assert 'rows.Add(CreateAction(vm, IdMultiplier, ExplainMultiplier' in ui
    assert 'private void ExplainMultiplier()' in ui
    assert 'clicking deliberately has no effect' in ui
    assert 'new FgMultiplierData' not in ui


def test_requires_upscaler_and_none_hidden_from_visual_only_native_option():
    ui = source('src/BannerlordFG/Core/NativePerformancePatch.cs')
    choice = source('src/BannerlordFG/Core/FgMenuOptions.cs')
    assert 'IdUpscalingRequired' not in ui and 'ExplainUpscaling' not in ui
    assert 'Frame Generation requires DLSS or FSR' in ui
    assert 'p.TryHideNoneInUpscaling(__instance, __1)' in ui
    assert 'rows.Insert(i, row)' in ui and 'rows.RemoveAt(i)' in ui
    assert '!_controller.NativeDlssManaged' in ui
    assert 'new FgUpscalingQualityData(original' in ui
    assert '_native.GetSelectableOptionNames().Skip(1)' in choice
    assert 'float nativeIndex = index + 1f;' in choice
    assert 'NativeOptions.SetConfig(_type, nativeIndex)' in choice
    assert 'if (__1 <= 0f)' in ui # Backstop if other UI can still issue native ID 0.


def test_shortcuts_disabled_in_new_install_and_existing_managed_ini():
    profile = source('src/BannerlordFG/Core/OptiProfile.cs')
    installer = source('src/BannerlordFG.Setup/OptiInstaller.cs')
    setup = source('src/BannerlordFG.Setup/MaintenanceEntry.cs')
    build = source('BUILD_WINDOWS.cmd')
    for key in ('ShortcutKey', 'FGShortcutKey', 'FpsShortcutKey', 'FpsCycleShortcutKey'):
        assert '"' + key + '"' in profile
        assert '"' + key + '"' in installer
    assert 'ConfigureLiveShortcutsInText(t)' in profile
    assert 'ConfigureLiveControlAssignments(text)' in installer
    assert '--configure-live-controls-closed-game' in setup
    assert '--configure-live-controls-closed-game --gamebin "%GAMEBIN%"' in build
    assert 'HashFile(Path.Combine(gameBin, "dxgi.dll")).Equals(expectedProxy' in installer
    assert 'Hotkey' in installer and 'Hotkey' in profile
    assert (ROOT / 'src/BannerlordFG/Core/OptiLiveHotkeys.cs').exists()


def test_live_fg_requires_initialized_backend_and_two_internal_keys():
    c = source('src/BannerlordFG/Core/FgController.cs')
    ui = source('src/BannerlordFG/Core/FgMenuOptions.cs')
    assert 'public bool CanToggleLive => _liveBackendReadyAtLaunch && _liveKeysReadyAtLaunch;' in c
    assert 'OptiLiveHotkeys.RequestFgToggle(out message)' in c
    assert 'if (!canToggleLive) _requestRestart?.Invoke();' in ui
    assert 'next-launch state; restart' not in ui


def test_fsr31_default_and_no_rcas_sharpness_override():
    profile = source('src/BannerlordFG/Core/OptiProfile.cs')
    install = source('src/BannerlordFG.Setup/OptiInstaller.cs')
    assert '"Dx11Upscaler", "fsr31"' in profile
    assert '"CAS", "Enabled", "false"' in profile
    assert '"OverrideSharpness", "false"' in profile
    assert '"Dx11Upscaler", "fsr31"' in install
    assert '"CAS", "Enabled", "false"' in install


def test_disabling_fg_does_not_switch_upscaler_off_mid_session():
    controller = source('src/BannerlordFG/Core/FgController.cs')
    game = source('src/BannerlordFG/Core/GameGraphicsBridge.cs')
    submodule = source('src/BannerlordFG/SubModule.cs')
    disable = controller.split('public bool TryDisable(', 1)[1].split('TryRestoreVanillaAfterFgStopped', 1)[0]
    assert '_graphics.RestoreOriginal(Settings)' not in disable
    assert 'Settings.PendingRestart = true;' in disable
    assert 'public bool TryRestoreVanillaAfterFgStopped' in controller
    assert '_liveBackendReadyAtLaunch || Settings.Enabled' in controller
    assert '_controller.TryRestoreVanillaAfterFgStopped(out restored)' in submodule
    assert '!_controller.NativeDlssManaged' in submodule
    assert 'if (!s.Enabled) return;' not in game.split('public void Enforce(', 1)[1].split('public bool RepairOldFrameCap(', 1)[0]


def test_version_does_not_change_the_pinned_third_party_build():
    xml = ET.parse(ROOT/'Module/BannerlordFG/SubModule.xml').getroot()
    assert xml.find('Version').get('value') == 'v1.0.3'
    assert 'nightly-20261016' in source('src/BannerlordFG.Setup/OptiInstaller.cs')

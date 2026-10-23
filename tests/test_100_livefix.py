"""Static regression checks for v1.0.1 live UI; not a substitute for a Windows game run."""
from pathlib import Path
from xml.etree import ElementTree
ROOT=Path(__file__).resolve().parents[1]

def load(p):return (ROOT/p).read_text(encoding='utf-8')

UI=load('src/BannerlordFG/Core/NativePerformancePatch.cs')
CTL=load('src/BannerlordFG/Core/FgController.cs')
MENU=load('src/BannerlordFG/Core/FgMenuOptions.cs')
PROFILE=load('src/BannerlordFG/Core/OptiProfile.cs')
INSTALLER=load('src/BannerlordFG.Setup/OptiInstaller.cs')
HOTKEY=load('src/BannerlordFG/Core/OptiLiveHotkeys.cs')
SUB=load('src/BannerlordFG/SubModule.cs')

def test_only_frame_generation_title_and_no_upscaling_required_button():
    assert 'new TextObject("{=BFGFrameGeneration}Frame Generation")' in UI
    assert 'IdUpscalingRequired' not in UI
    assert 'ExplainUpscaling' not in UI
    assert 'Frame Generation requires DLSS or FSR upscaling' in UI

def test_multiplier_is_fixed_silent_action():
    body=UI.split('private void ExplainMultiplier()',1)[1].split('private void RefreshUpscalingLocks()',1)[0]
    assert 'InformationManager' not in body and 'DisplayMessage' not in body
    assert 'TrySetMultiplier' not in body
    assert '"{=BFGMultiplier}Multiplier", "2x"' in UI

def test_live_control_toggle_requires_initialized_backend():
    assert 'CanToggleLive => _liveBackendReadyAtLaunch && _liveKeysReadyAtLaunch' in CTL
    assert 'bool canToggleLive = Controller.CanToggleLive;' in MENU
    assert 'Controller.TryToggleLive(out message)' in MENU
    assert 'OptiLiveHotkeys.RequestFgToggle(out message)' in CTL
    assert 'if (!canToggleLive) _requestRestart?.Invoke();' in MENU
    assert 'new FgEnabledData(_controller, _isInstalled' in UI
def test_only_two_internal_keys_reserved_and_documented():
    assert '"FGShortcutKey", "0x86"' in PROFILE
    assert '"FpsShortcutKey", "0x87"' in PROFILE
    assert '"Menu", "FGShortcutKey", "0x86"' in INSTALLER
    assert '"Menu", "FpsShortcutKey", "0x87"' in INSTALLER
    assert 'VK_F23 = 0x86' in HOTKEY and 'VK_F24 = 0x87' in HOTKEY
    assert 'Thread.Sleep(100)' in HOTKEY
    assert '"ShortcutKey", "FGShortcutKey", "FpsShortcutKey", "FpsCycleShortcutKey"' in PROFILE

def test_fps_visible_state_tracks_live_assumed_not_saved_preference():
    assert 'Controller.OverlayAssumedEnabled ? 1 : 0' in MENU
    assert 'OptiLiveHotkeys.RequestOverlayToggle(out message)' in CTL
    assert 'if (sent) _overlayAssumedEnabled = enabled;' in CTL
    assert 'new NativeFpsCounter()' in SUB
    assert 'nativeCounter.TrySet(false' in SUB

def test_existing_installs_migrated_with_ownership_check():
    build=load('BUILD_WINDOWS.cmd')
    prog=load('src/BannerlordFG.Setup/MaintenanceEntry.cs')
    assert '--configure-live-controls-closed-game --gamebin "%GAMEBIN%"' in build
    assert '--configure-live-controls-closed-game' in prog
    assert 'HashFile(Path.Combine(gameBin, "dxgi.dll")).Equals(expectedProxy' in INSTALLER
    assert 'File.Copy(ini, backup, false);' in INSTALLER

def test_release_stays_100():
    assert ElementTree.parse(ROOT/'Module/BannerlordFG/SubModule.xml').getroot().find('Version').attrib['value']=='v1.0.3'
    assert 'BannerlordFG-v1.0.3.zip' in load('BUILD_WINDOWS.cmd')

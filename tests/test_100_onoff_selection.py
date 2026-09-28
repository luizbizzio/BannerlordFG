"""Checks the FG selector contract independently of the legacy action control."""
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
def read(path): return (ROOT / path).read_text(encoding="utf-8")
UI = read("src/BannerlordFG/Core/NativePerformancePatch.cs")
MENU = read("src/BannerlordFG/Core/FgMenuOptions.cs")
CTL = read("src/BannerlordFG/Core/FgController.cs")

def test_two_state_control_has_same_options_as_fps_and_is_not_action():
    fg = MENU.split("internal sealed class FgEnabledData", 1)[1].split("internal sealed class FgShowFpsData",1)[0]
    assert 'GetSelectableOptionsLimit() => 2' in fg
    assert 'new SelectionData(false, "Off"), new SelectionData(false, "On")' in fg
    assert 'new FgEnabledData(_controller, _isInstalled' in UI
    assert 'rows.Add(CreateAction(vm, IdToggle' not in UI
    assert 'rows.Add(CreateAction(vm, IdMultiplier, ExplainMultiplier' in UI
    assert 'rows.Add(CreateChoice(vm, new FgShowFpsData' in UI

def test_setting_does_not_send_duplicate_toggle_during_refresh():
    fg = MENU.split("internal sealed class FgEnabledData", 1)[1].split("internal sealed class FgShowFpsData",1)[0]
    assert 'if (desired == Controller.FgAssumedEnabled) return;' in fg
    assert 'if (!Installed()) return;' in fg
    assert 'Controller.TryToggleLive(out message)' in fg
    assert 'if (!canToggleLive) _requestRestart?.Invoke();' in fg
    assert 'if (id != IdFpsCounter && id != IdToggle) continue;' in UI
    assert 'FgAssumedEnabled => CanToggleLive ? _liveAssumedEnabled : Settings.Enabled' in CTL

def test_live_toggle_does_not_persist_disabled_backend_or_require_restart():
    fg = MENU.split("internal sealed class FgEnabledData", 1)[1].split("internal sealed class FgShowFpsData",1)[0]
    assert 'bool success = canToggleLive ? Controller.TryToggleLive(out message) :' in fg
    assert '_refreshUpscalingLocks?.Invoke();' in fg
    assert 'no in-process status API' in MENU
    assert 'last requested state, not direct backend readback' in UI

"""Regression guard for the orphaned catch introduced while removing Unlimited."""
from pathlib import Path

NATIVE = (Path(__file__).resolve().parents[1] / "src/BannerlordFG/Core/NativePerformancePatch.cs").read_text(encoding="utf-8-sig")

def test_orphaned_save_hook_catch_removed():
    assert 'native FPS config save failed' not in NATIVE
    assert 'NativeSavedPostfix' not in NATIVE
    before_option = NATIVE.split('private static bool IsOurOption', 1)[0]
    refresh = before_option.split('private void RefreshSelectionRows()', 1)[1]
    assert refresh.endswith('        }\n\n        ') or refresh.rstrip().endswith('}')
    assert 'catch (Exception ex) { _active?.Error(' not in refresh

def test_no_unsupported_unlimited_hooks():
    assert 'FgUnlimitedData' not in NATIVE
    assert 'IdUnlimited' not in NATIVE
    assert 'NativeSavedPostfix' not in NATIVE

"""v1.1.2 progress transport and manual ReShade workaround source contracts.

These are static/source tests, not an MSBuild or Bannerlord UI integration test.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
AUTO = (ROOT / 'src/BannerlordFG.Setup/AutoFirstRun.cs').read_text()
OPTI = (ROOT / 'src/BannerlordFG.Setup/OptiInstaller.cs').read_text()
UI = (ROOT / 'src/BannerlordFG/SubModule.cs').read_text()
README = (ROOT / 'README.md').read_text()


def test_real_progress_uses_received_bytes_not_timer():
    assert 'int percent = total > 0' in OPTI
    assert 'received * 100L / total' in OPTI
    assert 'percent >= 0 ? bucket * 10 : -1' in OPTI
    assert 'progress("downloading", 0, 0, 0)' in OPTI
    assert 'progress("verifying", -1' in OPTI
    assert 'progress("verified", 100' in OPTI
    assert 'TimeSpan.FromMinutes(4)' in OPTI
    assert 'PinnedSha256' in OPTI


def test_per_game_status_is_atomic_and_not_a_log_scrape():
    assert 'StatePath(gameBin, ".progress")' in AUTO
    assert 'File.Replace(temp, path, null)' in AUTO
    assert 'File.Move(temp, path)' in AUTO
    assert 'PublishProgress(gameBin, stage, percent, received, total)' in AUTO
    assert 'File.Delete(StatePath(gameBin, ".progress"))' in AUTO
    assert 'Progress status unavailable' in AUTO
    assert 'unified BannerlordFG.log as a progress transport' in AUTO


def test_progress_is_bounded_to_current_install_dialog():
    assert 'if (!_noticeVisible) return;' in UI
    assert 'if (raw == null || raw == _lastProgressNotice) return;' in UI
    assert 'if (info.Length > 128 || info.Length == 0) return;' in UI
    assert 'RefreshSetupProgress();' in UI
    assert 'new string(\'#\', filled)' in UI
    assert 'new string(\'-\', 20 - filled)' in UI
    assert 'Download complete. Verifying the official SHA-256 checksum...' in UI
    assert '(_manualInstallRequested || _noticeVisible || _monitorInstallAtStartup)' in UI
    assert '".helper-started", ".progress"' in UI
    assert 'CancelAutomaticSetup()' in UI


def test_reshade_remains_a_documented_manual_opt_in():
    assert '`d3d11.dll`' in README and '`winmm.dll`' in README
    assert '`LoadReshade=false`' in README
    assert '`KeyOverlay=120,0,0,0`' in README
    assert 'does not perform this workaround automatically' in README
    assert 'Do not overwrite any DLL' in README
    assert 'ReShade64.dll' not in UI
    assert 'LoadReshade' not in OPTI


def test_release_identity_111_is_consistent():
    assert 'v1.1.2' in (ROOT / 'Module/BannerlordFG/SubModule.xml').read_text()
    assert 'BannerlordFG-v1.1.2.zip' in (ROOT / 'BUILD_WINDOWS.cmd').read_text()
    assert 'version-1.1.2-blue' in README
    for p in ('src/BannerlordFG/Properties/AssemblyInfo.cs',
              'src/BannerlordFG.Setup/Properties/AssemblyInfo.cs'):
        assert 'AssemblyVersion("1.1.2.0")' in (ROOT / p).read_text()

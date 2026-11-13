"""Source-contract regressions for the v1.1.2 initial-install relaunch race.

These tests cannot emulate Windows Steam process lifetime or build a DLL.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OPTI = (ROOT / "src/BannerlordFG.Setup/OptiInstaller.cs").read_text()
AUTO = (ROOT / "src/BannerlordFG.Setup/AutoFirstRun.cs").read_text()
SUB = (ROOT / "src/BannerlordFG/SubModule.cs").read_text()


def test_unpack_before_worker_signals_game_to_exit():
    prepare = OPTI.split("internal static async Task<PreparedPackage> PrepareOfficialPackageAsync", 1)[1].split(
        "private static async Task<T> WithDownloadDeadline", 1)[0]
    worker = AUTO.split("internal static void Run(string requestedGameBin, int waitPid)", 1)[1]
    assert prepare.index('progress("extracting", -1') < prepare.index('progress("verified", 100')
    assert prepare.index("ExtractSafe(pkg.Archive, unpacked)") < prepare.index("pkg.Verify();")
    assert worker.index("PrepareOfficialPackageAsync(") < worker.index('WriteAtomic(ready, waitPid.ToString());')
    assert "Preparing OptiScaler files before closing the game" in SUB


def test_only_game_closed_phase_writes_to_game_directory():
    install = OPTI.split("internal static async Task InstallAsync(", 1)[1]
    assert "string root = pkg.ExtractedRoot;" in install
    assert "ExtractSafe(pkg.Archive, extracted)" not in install
    assert install.index('if (BannerlordIsRunning())\n                    throw new IOException("Bannerlord reopened') < install.index("File.Copy(files[i], target, false);")
    assert install.index("pkg.Verify();") < install.index("File.Copy(files[i], target, false);")
    assert 'File.Move(originalProxy, renamedProxy);' in install


def test_prepared_files_are_hash_checked_before_install():
    prepared = OPTI.split("internal sealed class PreparedPackage", 1)[1].split("internal static async Task<PreparedPackage> PrepareOfficialPackageAsync", 1)[0]
    assert "ExtractedHashes.Count" in prepared
    assert "HashFile(file).Equals(expected" in prepared
    assert "Prepared OptiScaler file changed" in prepared
    assert "Extracted OptiScaler package escaped its temporary directory" in prepared


def test_background_install_never_launches_game():
    assert 'RelaunchDirect' not in AUTO
    assert 'Process.Start(' not in AUTO
    assert 'RunRestartOnly' not in AUTO
    assert 'RunSettingsAfterExit(' in AUTO
    assert 'TaleWorlds.Engine.Utilities.QuitGame();' in SUB
    assert '"--post-exit-settings"' in SUB
    assert '"--post-exit-install-restart"' not in SUB


def test_no_change_to_reshade_winmm_policy_or_download_percent():
    assert 'private static string SelectProxyName(string gameBin)' in OPTI
    assert 'if (!winmmOccupied) return "winmm.dll";' in OPTI
    assert 'proxy.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase)' in OPTI
    assert "progress(\"downloading\", percent >= 0 ? bucket * 10" in OPTI
    assert 'LoadReshade' not in OPTI

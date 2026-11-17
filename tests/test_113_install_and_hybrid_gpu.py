"""v1.1.3 regression contracts for runtime collisions, vanilla/BLSE PIDs,
and hybrid GPU safety. These inspect source policy and are NOT Windows GPU tests.
"""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
OPTI = (ROOT / 'src/BannerlordFG.Setup/OptiInstaller.cs').read_text()
GPU = (ROOT / 'src/BannerlordFG.Setup/GpuDetection.cs').read_text()
AUTO = (ROOT / 'src/BannerlordFG.Setup/AutoFirstRun.cs').read_text()
BUILD = (ROOT / 'BUILD_WINDOWS.cmd').read_text()
UI = (ROOT / 'src/BannerlordFG/Core/NativePerformancePatch.cs').read_text()


def block(code, first, last):
    return code.split(first, 1)[1].split(last, 1)[0]


def test_krowwsss_license_and_setup_files_are_skipped():
    policy = block(OPTI, 'internal static bool IsRuntimePayloadFile(',
                   'private static string[] RuntimePayloadFiles(')
    for name in ('setup_linux.sh', 'setup_windows.bat', 'Licenses/',
                 'README.md', 'README.txt'):
        assert name in policy
    # Does not discard all non-DLL resources: other runtime assets are retained.
    assert 'return !file.Equals' in policy
    assert '.EndsWith(".dll")' not in policy


def test_unchanged_runtime_files_still_block_overwrite():
    validation = block(OPTI, 'internal static void CheckRuntimeCollisions(',
                       'internal static async Task InstallAsync(')
    assert 'File.Exists(Path.Combine(gameBin, rel))' in validation
    assert 'throw new IOException("Installation stopped: these runtime files already exist:' in validation
    assert 'OptiScaler.dll' in validation and 'OptiScaler.ini' in validation
    installer = block(OPTI, 'internal static async Task InstallAsync(',
                      'private static string HashShort(')
    assert 'var files = unattended ? RuntimePayloadFiles(root)' in installer
    assert 'CheckRuntimeCollisions(gameBin, pkg);' in installer
    assert 'File.Copy(files[i], target, false);' in installer
    assert '"installedFiles", relative.Select' in installer
    assert 'File.Move(originalProxy, renamedProxy);' in installer


def test_collision_preflight_happens_before_game_shutdown():
    worker = block(AUTO, 'internal static void Run(string requestedGameBin, int waitPid)',
                   'private static string') if 'private static string' in AUTO.split('internal static void Run(string requestedGameBin, int waitPid)', 1)[1] else AUTO.split('internal static void Run(string requestedGameBin, int waitPid)', 1)[1]
    assert worker.index('package.Verify();') < worker.index('CheckRuntimeCollisions(gameBin, package);')
    assert worker.index('CheckRuntimeCollisions(gameBin, package);') < worker.index('WriteAtomic(ready, waitPid.ToString());')
    assert worker.index('WriteAtomic(ready, waitPid.ToString());') < worker.index('OptiInstaller.InstallAsync(gameBin, gpu')
    assert 'if (!recoverInterrupted)' in worker


def test_process_names_cover_vanilla_and_all_three_blse_launch_modes():
    names = block(GPU, 'internal static readonly string[] GameProcessNames = {', '};')
    for process in ('Bannerlord', 'Bannerlord.Native', 'TaleWorlds.MountAndBlade.Launcher',
                    'Bannerlord.BLSE.Launcher', 'Bannerlord.BLSE.LauncherEx',
                    'Bannerlord.BLSE.Standalone'):
        assert f'"{process}"' in names
    assert 'return name + ".exe";' in GPU


def test_pid_name_alone_cannot_authenticate_process():
    finder = block(GPU, 'private static HashSet<int> FindGameProcessIds(',
                   '// A telemetry name encodes')
    verifier = block(GPU, 'internal static bool IsExpectedGameProcess(',
                     'private static bool SameExecutablePath(')
    assert 'IsExpectedGameProcess(p, gameBin, log)' in finder
    assert 'string expected = Path.GetFullPath(Path.Combine(gameBin, image));' in verifier
    assert 'SameExecutablePath(mainPath, expected)' in verifier
    assert 'SameExecutablePath(wmiPath, expected)' in verifier
    assert 'WHERE ProcessId = ' in verifier
    assert 'return true;' not in finder.split('IsExpectedGameProcess(p, gameBin, log)', 1)[0]


def test_blse_counts_as_running_for_dll_safety_in_every_install_path():
    guard = block(OPTI, 'internal static bool BannerlordIsRunning(string gameBin)',
                  'private static Dictionary<string, object> ParseJsonObject(')
    assert 'foreach (string name in GpuDetection.GameProcessNames)' in guard
    assert 'IsGameProcessBusy(process, gameBin)' in guard
    assert 'GpuDetection.IsExpectedGameProcess(process, gameBin' in OPTI
    assert 'return true;' in guard
    for operation in ('Close the game before installing.',
                      'Close Bannerlord before uninstalling.',
                      'Close Bannerlord before recovering graphics files.'):
        assert operation in OPTI
    assert 'OptiInstaller.BannerlordIsRunning(gameBin)' in AUTO
    assert 'Bannerlord.BLSE.LauncherEx.exe' in BUILD


def test_measured_renderer_pid_dxgi_luid_no_fastest_gpu_guess():
    assert 'SampleGameRenderUsage(pids, log)' in GPU
    assert 'TryParseEngine(name, gamePids, out pid, out luid)' in GPU
    assert 'adapters.ContainsKey(x.Luid)' in GPU
    assert 'AdapterLuid' in GPU
    assert 'GameProcessNames' in GPU
    assert 'IsDirectGameProcess(process.ProcessName)' in GPU
    assert 'bool marked = GameHostMarker.IsActive(gameBin, pid)' in GPU
    assert 'InGameCaller = pid == originalPid && marked' in GPU
    assert 'new[] { "NVIDIA", "AMD", "Intel" }' not in GPU


def test_hybrid_launcher_intel_sample_not_claimed_as_game_adapter():
    check = block(GPU, 'internal static string TryDetectActiveGpu(',
                  'internal static string DetectOnlyUnambiguousVendor(')
    assert 'RendererDecision.Select(hosts,' in check
    assert 'GPU render probe had no uniquely identified game-renderer PID/LUID' in check
    assert 'return null;' in check
    fallback = GPU.split('internal static string DetectOnlyUnambiguousVendor(', 1)[1]
    assert 'if (vendors.Count == 1)' in fallback
    assert 'if (vendors.Count > 1)' in fallback
    assert 'No GPU was selected.' in fallback


def test_no_unrequested_harmony_or_blse_winmm_hook_changes():
    assert 'private static bool SetConfigPrefix(IOptionData __0, float __1)' in UI
    assert 'Harmony native Performance installed' in UI
    assert 'LoadLibrary("winmm.dll")' not in AUTO
    assert 'RelaunchDirect' not in AUTO
    assert 'nightly-20261116' in OPTI

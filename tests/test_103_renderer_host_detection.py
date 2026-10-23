"""Source-level safeguards for native renderer-host name handling.

The game's injected DLL may run in TaleWorlds.MountAndBlade.Launcher.exe,
which was not accepted by the original Bannerlord.exe-only process probe.
Real PDH/DXGI performance counters still need a Windows integration test.
"""
from pathlib import Path

BASE = Path(__file__).resolve().parents[1]
GPU = (BASE / 'src/BannerlordFG.Setup/GpuDetection.cs').read_text()
SUB = (BASE / 'src/BannerlordFG/SubModule.cs').read_text()
AUTO = (BASE / 'src/BannerlordFG.Setup/AutoFirstRun.cs').read_text()


def test_game_module_can_run_with_launcher_process_image():
    assert 'TaleWorlds.MountAndBlade.Launcher.exe' in SUB
    assert 'Process.GetCurrentProcess().Id' in SUB
    assert 'DetectGpu(gameBin, waitPid)' in AUTO


def test_prober_accepts_exact_known_renderer_host_names_not_foreign_binaries():
    identity = GPU.split('private static string ExpectedExecutableName(', 1)[1].split(
        'private static bool IsExpectedGameProcess(', 1)[0]
    assert '"Bannerlord.exe"' in identity
    assert '"Bannerlord.Native.exe"' in identity
    assert '"TaleWorlds.MountAndBlade.Launcher.exe"' in identity
    assert 'return null;' in identity

    verify = GPU.split('private static bool IsExpectedGameProcess(', 1)[1].split(
        'private static bool SameExecutablePath(', 1)[0]
    assert 'ExpectedExecutableName(process.ProcessName)' in verify
    assert 'string expected = Path.GetFullPath(Path.Combine(gameBin, image));' in verify
    assert 'SameExecutablePath(mainPath, expected)' in verify
    assert 'SameExecutablePath(wmiPath, expected)' in verify
    assert 'processName, image' in verify
    assert 'Process.GetProcessesByName' not in verify


def test_caller_is_inspected_before_process_discovery_but_no_early_return():
    discovery = GPU.split('private static HashSet<int> FindGameProcessIds(', 1)[1].split(
        '// A telemetry name encodes', 1)[0]
    assert 'Process.GetProcessById(originalPid)' in discovery
    assert 'IsExpectedGameProcess(p, gameBin, log)' in discovery
    assert '"Bannerlord.Native", "TaleWorlds.MountAndBlade.Launcher"' in discovery
    assert 'return ids;' in discovery
    assert 'return ids;' == discovery.strip().splitlines()[-2].strip()
    assert 'return ids;' not in discovery.split('foreach (string name', 1)[0]


def test_gpu_inference_requires_measured_3d_activity():
    assert 'SampleGameRenderUsage(pids, log)' in GPU
    assert 'scored[0].Value < 0.5' in GPU
    assert 'Active Bannerlord 3D renderer detected via PDH GPU Engine PID and DXGI adapter LUID' in GPU
    assert 'return active ?? GpuDetection.DetectByVendorPriority(Log);' in AUTO

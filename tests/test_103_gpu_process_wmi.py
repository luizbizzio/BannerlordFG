"""Guards the process-identity fallback against inaccessible CLR MainModule.

These are source-contract tests. Actual WMI/PDH/DXGI behavior requires Windows.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CODE = (ROOT / "src/BannerlordFG.Setup/GpuDetection.cs").read_text()


def test_process_path_has_exact_match_only():
    assert 'Path.Combine(gameBin, image)' in CODE
    assert 'ExpectedExecutableName(process.ProcessName)' in CODE
    assert 'Path.GetFullPath(actual)' in CODE
    assert 'StringComparison.OrdinalIgnoreCase' in CODE
    assert 'if (SameExecutablePath(mainPath, expected)) return true;' in CODE
    assert 'if (SameExecutablePath(wmiPath, expected))' in CODE
    assert 'processName, image' in CODE


def test_process_wmi_fallback_uses_specific_pid_and_executable_path():
    assert 'SELECT Name, ExecutablePath FROM Win32_Process WHERE ProcessId = ' in CODE
    assert 'Win32_Process.ExecutablePath' in CODE
    assert 'searcher.Options.Timeout = TimeSpan.FromSeconds(3)' in CODE
    assert 'catch (System.ComponentModel.Win32Exception ex)' in CODE
    assert 'rejected " + image + " PID' in CODE
    assert 'verified game host PID(s) (caller PID ' in CODE


def test_does_not_bypass_executable_validation():
    block = CODE.split('private static bool IsExpectedGameProcess(', 1)[1].split(
        'private static HashSet<int> FindGameProcessIds(', 1)[0]
    assert 'SameExecutablePath(wmiPath, expected)' in block
    assert 'return true;' in block
    assert "return !process.HasExited" not in block
    assert 'return false;' in block


def test_pid_discovery_logs_and_passes_logger():
    assert 'FindGameProcessIds(gameBin, originalPid, log)' in CODE
    assert 'IsExpectedGameProcess(p, gameBin, log)' in CODE
    assert 'GPU render probe unavailable: no verified Bannerlord renderer host is running' in CODE

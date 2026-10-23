"""Source-contract checks for automatic no-prompt multi-GPU detection.

Windows GPU Engine + DXGI COM behavior still requires an integration test on
real hardware; these tests do not claim to exercise it.
"""
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
GPU = (ROOT / 'src/BannerlordFG.Setup/GpuDetection.cs').read_text()
AUTO = (ROOT / 'src/BannerlordFG.Setup/AutoFirstRun.cs').read_text()
PROJECT = ROOT / 'src/BannerlordFG.Setup/BannerlordFG.Setup.csproj'


def test_new_source_included_in_maintenance_csproj():
    tree = ET.parse(PROJECT)
    compile_names = [elem.attrib['Include'] for elem in tree.iter() if elem.tag.endswith('Compile')]
    assert 'GpuDetection.cs' in compile_names


def test_runtime_renderer_detection_precedes_fallback():
    block = AUTO.split('private static string DetectGpu(', 1)[1].split('private static bool ProcessAlive', 1)[0]
    assert block.index('TryDetectActiveGpu') < block.index('DetectByVendorPriority')
    assert 'return active ?? GpuDetection.DetectByVendorPriority(Log);' in block
    assert 'Mixed NVIDIA/AMD adapters' not in AUTO


def test_renderer_probe_uses_game_exe_pid_and_dxgi_luid():
    for snippet in ('Process.GetProcessesByName(name)',
                    'Path.Combine(gameBin, image)',
                    'Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine',
                    'CreateDXGIFactory1', 'EnumAdapters1', 'GetDesc1',
                    'AdapterLuid', 'TryParseEngine', 'Utilization Percentage'):
        assert snippet in GPU
    assert 'gamePids.Contains(pid)' in GPU
    assert r'engtype_(3D|Graphics|Render)(?:_\d+)?$' in GPU
    assert 'return null;' in GPU


def test_fallback_prioritizes_gpu_vendors_without_selection_ui():
    assert 'new[] { "NVIDIA", "AMD", "Intel" }' in GPU
    assert 'VEN_10DE' in GPU and 'VEN_1002' in GPU and 'VEN_8086' in GPU
    assert 'No supported NVIDIA, AMD or Intel GPU' in GPU
    assert 'mixed NVIDIA/AMD adapters' not in GPU
    assert 'MessageBox' not in GPU and 'ShowInquiry' not in GPU


def test_probe_cannot_block_install_if_telemetry_fails():
    assert 'catch (Exception ex)' in GPU
    assert 'applying vendor fallback' in GPU
    assert 'scored[0].Value < 0.5' in GPU
    assert 'scored[0].Value < scored[1].Value * 2.0' in GPU


def test_gpu_probe_is_before_optiscaler_download():
    run = AUTO.split('internal static void Run(string requestedGameBin, int waitPid)', 1)[1]
    assert run.index('DetectGpu(gameBin, waitPid)') < run.index('PrepareOfficialPackageAsync(Log)')


def test_windows_gpu_engine_uses_pdh_not_broken_wmi_formatted_utilization():
    for expected in ('PdhOpenQueryW', 'PdhAddEnglishCounterW',
                     'PdhCollectQueryData', 'PdhGetFormattedCounterValue',
                     'PdhCloseQuery', 'PdhFmtDouble', 'Thread.Sleep(1000)',
                     'SELECT Name FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine',
                     'GPU render probe via PDH:'):
        assert expected in GPU
    assert 'SELECT Name, UtilizationPercentage' not in GPU
    assert 'PdhCloseQuery(query)' in GPU
    assert 'PdhAddEnglishCounterW(query, path' in GPU


def test_observed_hybrid_gpu_renderer_name_and_luid_are_supported():
    import re
    # Observed from an actual Bannerlord battle on Win11: GPU Engine's
    # graphics_1 engine reached 78.309%, while WMI still returned zero.
    name = 'pid_8780_luid_0x00000000_0x01c5724d_phys_0_eng_9_engtype_graphics_1'
    pattern = (r'^pid_(\d+)_luid_0x([0-9a-f]+)_0x([0-9a-f]+)_phys_\d+'
               r'_eng_\d+_engtype_(3D|Graphics|Render)(?:_\d+)?$')
    match = re.fullmatch(pattern, name, flags=re.IGNORECASE)
    assert match and int(match.group(1)) == 8780
    assert ((int(match.group(2), 16) << 32) | int(match.group(3), 16)) == 0x01c5724d
    assert r'(?:_\d+)?$' in GPU


def test_fallback_still_works_if_pdh_unavailable():
    assert 'catch (Exception ex)' in GPU
    assert 'return active ?? GpuDetection.DetectByVendorPriority(Log);' in AUTO
    assert 'PDH had no readable GPU Engine counters; applying vendor fallback' in GPU

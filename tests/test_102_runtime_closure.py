"""Prevent publishing 1.0.3 without SharpCompress runtime assemblies."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CS = (ROOT / 'src/BannerlordFG.Setup/BannerlordFG.Setup.csproj').read_text()
CMD = (ROOT / 'BUILD_WINDOWS.cmd').read_text()

RUNTIME = {
    'System.Memory': '4.6.0',
    'System.Buffers': '4.6.0',
    'System.Numerics.Vectors': '4.6.0',
    'System.Runtime.CompilerServices.Unsafe': '6.1.0',
    'System.Threading.Tasks.Extensions': '4.5.4',
}


def test_packages_pinned_to_known_netfx_runtime_versions():
    for name, version in RUNTIME.items():
        assert f'PackageReference Include="{name}" Version="{version}"' in CS


def test_copy_runtime_assemblies_and_validate_every_release_stage():
    assert 'NUGET_PACKAGES' in CMD
    assert ':copy_runtime_dep' in CMD
    for name in RUNTIME:
        dll = name + '.dll'
        assert f'"{dll}"' in CMD
        # Build, ZIP stage, deployed module, and postcopy hash verification.
        assert CMD.count(dll) >= 5
    assert 'for %%F in (BannerlordFG.dll' in CMD

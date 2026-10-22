"""Preflight must exercise real LZMA2 7z extraction before deploying."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CMD = (ROOT / 'BUILD_WINDOWS.cmd').read_text()
HELPER = (ROOT / 'Module/BannerlordFG/bin/Win64_Shipping_Client/BannerlordFG.Helper.cmd').read_text()
MAINT = (ROOT / 'src/BannerlordFG.Setup/MaintenanceEntry.cs').read_text()

def test_preflight_before_zip_creation_and_deployment():
    assert CMD.index('call "%OUT%\\BannerlordFG.Helper.cmd" --verify-runtime') < CMD.index('echo [4/5] Creating and validating standalone ZIP')
    assert 'Maintenance runtime preflight failed' in CMD

def test_preflight_exercises_real_7zip_lzma2_decode():
    assert 'if /I "%BFG_OP%"=="--verify-runtime" goto :invoke' in HELPER
    assert "$env:BFG_OP -eq '--verify-runtime'" in HELPER
    assert 'VerifyRuntimeExtraction()' in HELPER
    assert 'SafeSevenZipExtractor.Extract(archive, destination)' in MAINT
    assert '7z runtime smoke-test extracted unexpected content' in MAINT

def test_local_runtime_libraries_are_preloaded_without_resolver_callbacks():
    for name in ('System.Memory','SharpCompress','System.Buffers','System.Threading.Tasks.Extensions'):
        assert name in HELPER
    assert '[Reflection.Assembly]::LoadFrom($path)' in HELPER
    assert 'add_AssemblyResolve' not in HELPER
    assert 'GetNewClosure' not in HELPER

def test_no_new_external_executables():
    assert 'BannerlordFG.Maintenance.dll' in HELPER
    assert 'powershell.exe -NoLogo -NoProfile -NonInteractive' in HELPER
    assert 'tar.exe' not in HELPER.lower()

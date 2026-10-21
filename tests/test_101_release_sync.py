"""Source contracts matching the Nexus Mods v1.0.1 published binaries."""
from pathlib import Path
import xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[1]

def test_101_package_version():
    xml=ET.parse(ROOT/'Module/BannerlordFG/SubModule.xml').getroot()
    assert xml.find('Version').get('value')=='v1.0.1'
    assert 'BannerlordFG-v1.0.1.zip' in (ROOT/'BUILD_WINDOWS.cmd').read_text()

def test_101_pinned_optiscaler():
    code=(ROOT/'src/BannerlordFG.Setup/OptiInstaller.cs').read_text()
    for expected in ('nightly-20261016',
                     'OptiScaler_v10.0.0-pre1_20261016.7z',
                     '09149a5b17514a0594faf612467267c62ed8851dbc1dde0ade6763f1152e600f'):
        assert expected in code

def test_101_recovery_message():
    code=(ROOT/'src/BannerlordFG/SubModule.cs').read_text()
    assert 'Close Bannerlord; run UNINSTALL_WINDOWS.ps1. Follow RECOVERY_GUIDE.txt.' in code
    assert (ROOT/'Module/BannerlordFG/RECOVERY_GUIDE.txt').is_file()
    script=(ROOT/'Module/BannerlordFG/UNINSTALL_WINDOWS.ps1').read_text()
    assert 'if ($LASTEXITCODE -ne 0)' in script

def test_101_runtime_message():
    code=(ROOT/'src/BannerlordFG/Core/NativePerformancePatch.cs').read_text()
    assert 'Harmony native Performance v1.0.1 installed' in code

def test_101_assembly_metadata():
    for project in ('BannerlordFG','BannerlordFG.Setup'):
        subdir=ROOT/'src'/project
        content=(subdir/'Properties/AssemblyInfo.cs').read_text()
        for attr in ('AssemblyVersion("1.0.1.0")','AssemblyFileVersion("1.0.1.0")'):
            assert attr in content
        assert 'Properties\\AssemblyInfo.cs' in (subdir/(project+'.csproj')).read_text()

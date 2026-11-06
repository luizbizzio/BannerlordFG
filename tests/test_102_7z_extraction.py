"""Static release checks for the managed OptiScaler LZMA extraction (v1.1.0)."""
from pathlib import Path
import xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parents[1]


def test_no_windows_tar_for_optiscaler_archive():
    source = (ROOT/'src/BannerlordFG.Setup/OptiInstaller.cs').read_text()
    assert 'SafeSevenZipExtractor.Extract(archive, output)' in source
    assert 'RunTar(' not in source
    assert 'Environment.SystemDirectory, "tar.exe"' not in source
    assert 'HashFile(archive).Equals(PinnedSha256' in source
    assert 'nightly-20261106' in source


def test_bounded_managed_extraction():
    source = (ROOT/'src/BannerlordFG.Setup/SafeSevenZipExtractor.cs').read_text()
    for term in ('SevenZipArchive.OpenArchive', 'MaxUnpackedBytes', 'MaxEntries',
                 'FileMode.CreateNew', 'ReparsePoint', 'LinkTarget', 'IsEncrypted',
                 'IsComplete', 'Path.GetFullPath', 'actualTotal', 'expectedTotal'):
        assert term in source


def test_bundled_runtime_dependencies():
    project = (ROOT/'src/BannerlordFG.Setup/BannerlordFG.Setup.csproj').read_text()
    script = (ROOT/'BUILD_WINDOWS.cmd').read_text()
    assert 'PackageReference Include="SharpCompress" Version="0.50.5"' in project
    assert 'SafeSevenZipExtractor.cs' in project
    assert ' /restore /t:Rebuild' in script
    for name in ('SharpCompress.dll', 'Microsoft.Bcl.AsyncInterfaces.dll',
                 'System.Text.Encoding.CodePages.dll'):
        assert name in script
    assert 'BannerlordFG-v1.1.0.zip' in script
    assert 'v1.1.0' == ET.parse(ROOT/'Module/BannerlordFG/SubModule.xml').getroot().find('Version').get('value')

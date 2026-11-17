"""Pure source/package validation. Does not run Windows, tar.exe or the game."""
import re
import unittest
import zipfile
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
MODULE = ROOT/'Module/BannerlordFG'
CODE = (ROOT/'src/BannerlordFG.Setup/OptiInstaller.cs').read_text()
INI = ROOT/'tests/fixtures/optiscaler_nightly_ini_excerpt.ini'

class PackageChecks(unittest.TestCase):
    def test_submodule_declares_correct_dll_and_version(self):
        r = ET.parse(MODULE/'SubModule.xml').getroot()
        self.assertEqual(r.find('Id').attrib['value'], 'BannerlordFG')
        self.assertEqual(r.find('Version').attrib['value'], 'v1.1.3')
        self.assertEqual(r.find('SubModules/SubModule/DLLName').attrib['value'], 'BannerlordFG.dll')
        self.assertEqual(r.find('MultiplayerModule').attrib['value'], 'false')

    def test_projects_output_to_actual_mod_directory(self):
        for project, name in [('BannerlordFG', 'BannerlordFG'), ('BannerlordFG.Setup', 'BannerlordFG.Maintenance')]:
            file = ROOT/f'src/{project}/{project}.csproj'
            r = ET.parse(file).getroot()
            ns={'x':'http://schemas.microsoft.com/developer/msbuild/2003'}
            self.assertIn(name, [e.text for e in r.findall('.//x:AssemblyName', ns)])
            self.assertTrue(any('Module\\BannerlordFG\\bin\\Win64_Shipping_Client' in e.text
                                for e in r.findall('.//x:OutputPath', ns)))

    def test_standalone_zip_has_only_audited_runtime_and_documents(self):
        runtime = {'0Harmony.dll', 'BannerlordFG.dll', 'BannerlordFG.Maintenance.dll',
                   'BannerlordFG.Helper.cmd', 'SharpCompress.dll',
                   'Microsoft.Bcl.AsyncInterfaces.dll', 'System.Buffers.dll',
                   'System.Memory.dll', 'System.Numerics.Vectors.dll',
                   'System.Runtime.CompilerServices.Unsafe.dll',
                   'System.Text.Encoding.CodePages.dll',
                   'System.Threading.Tasks.Extensions.dll'}
        documents = {'HARMONY_LICENSE.txt', 'LICENSE', 'README_FIRST_RUN.txt',
                     'RECOVERY_GUIDE.txt', 'SHARPCOMPRESS_LICENSE.txt',
                     'SubModule.xml', 'THIRD_PARTY_NOTICES.txt', 'UNINSTALL_WINDOWS.ps1'}
        expected = ({f'BannerlordFG/{name}' for name in documents} |
                    {f'BannerlordFG/bin/Win64_Shipping_Client/{name}' for name in runtime})
        local = {p.relative_to(ROOT/'Module').as_posix() for p in MODULE.rglob('*') if p.is_file()}
        self.assertEqual(local, expected)
        with zipfile.ZipFile(ROOT/'BannerlordFG-v1.1.3.zip') as archive:
            actual = {name for name in archive.namelist() if not name.endswith('/')}
            self.assertEqual(actual, expected)
        build = (ROOT/'BUILD_WINDOWS.cmd').read_text()
        self.assertIn('tar.exe -a -cf "%ARCHIVE%" -C "%TMPBUILD%\\package" BannerlordFG', build)
        self.assertNotIn('tar.exe -a -cf "%ARCHIVE%" -C "%ROOT%Module" BannerlordFG', build)

    def test_release_asset_is_pinned_7z_and_not_zip(self):
        self.assertIn('OptiScaler_v10.0.0-pre1_20261116.7z',CODE)
        self.assertIn('6cd5704b47ff883666df7fec80ce55d113ecc1097a1db5caa3a1058bc007c2d0',CODE)
        self.assertIn('pkg.Sha256.Equals(PinnedSha256',CODE)
        self.assertIn('SafeSevenZipExtractor.Extract(archive, output)',CODE)
        self.assertNotIn('RunTar(',CODE)
        self.assertNotIn('ZipFile.OpenRead(archive)',CODE)

    def test_installer_prevents_foreign_dll_overwrite(self):
        self.assertIn('File.Copy(files[i], target, false)',CODE)
        self.assertIn('internal static string ExistingInstallStatus(string gameBin)', CODE)
        self.assertIn('internal static void Uninstall(', CODE)
        self.assertIn('DeleteVerifiedFile(item.Path, item.Sha256)',CODE)

    def test_manifest_never_adopts_arbitrary_existing_proxies(self):
        self.assertNotIn('foreach (var proxy in Proxies)\n                {\n                    string file = Path.Combine(gameBin, proxy)', CODE)
        self.assertIn('ONLY the selected proxy created by our own File.Move', CODE)

    def test_pinned_ini_keys_present_in_fixture(self):
        ini = INI.read_text(encoding='utf-8-sig')
        for section, keys in {'Upscalers':['Dx11Upscaler'], 'FrameGen':['Enabled','FGInput','FGOutput','DebugView'],
                              'UpscaleRatio':['UpscaleRatioOverrideEnabled'], 'QualityOverrides':['QualityRatioOverrideEnabled']}.items():
            match = re.search(r'^\['+section+r'\]\s*\n(.*?)(?=^\[|\Z)',ini,re.M|re.S|re.I)
            self.assertIsNotNone(match, section)
            for key in keys: self.assertRegex(match.group(1),r'(?m)^\s*'+key+r'\s*=')

    def test_build_script_has_correct_msbuild_discovery(self):
        script=(ROOT/'BUILD_WINDOWS.cmd').read_text()
        self.assertIn('-prerelease -products * -find',script)
        self.assertIn('BannerlordFG-v1.1.3.zip',script)
        self.assertIn('BannerlordFG.Maintenance.dll', script)
        self.assertIn('BannerlordFG.Helper.cmd', script)

if __name__=='__main__': unittest.main(verbosity=2)

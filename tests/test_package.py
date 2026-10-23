"""Pure source/package validation. Does not run Windows, tar.exe or the game."""
import re
import unittest
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
        self.assertEqual(r.find('Version').attrib['value'], 'v1.0.3')
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

    def test_no_redistributed_third_party_dlls(self):
        self.assertEqual([str(p) for p in MODULE.rglob('*') if p.suffix.lower() in {'.dll','.exe','.addon64'}], [])

    def test_release_asset_is_pinned_7z_and_not_zip(self):
        self.assertIn('OptiScaler_v10.0.0-pre1_20261016.7z',CODE)
        self.assertIn('09149a5b17514a0594faf612467267c62ed8851dbc1dde0ade6763f1152e600f',CODE)
        self.assertIn('pkg.Sha256.Equals(PinnedSha256',CODE)
        self.assertIn('SafeSevenZipExtractor.Extract(archive, output)',CODE)
        self.assertNotIn('RunTar(',CODE)
        self.assertNotIn('ZipFile.OpenRead(archive)',CODE)

    def test_installer_prevents_foreign_dll_overwrite(self):
        self.assertIn('File.Copy(files[i], target, false)',CODE)
        self.assertIn('internal static string ExistingInstallStatus(string gameBin)', CODE)
        self.assertIn('internal static void Uninstall(', CODE)
        self.assertIn('HashFile(target).Equals(kv.Value',CODE)

    def test_manifest_never_adopts_arbitrary_existing_proxies(self):
        self.assertNotIn('foreach (var proxy in Proxies)\n                {\n                    string file = Path.Combine(gameBin, proxy)', CODE)
        self.assertIn('ONLY the proxy created by our own File.Move', CODE)

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
        self.assertIn('BannerlordFG-v1.0.3.zip',script)
        self.assertIn('BannerlordFG.Maintenance.dll', script)
        self.assertIn('BannerlordFG.Helper.cmd', script)

if __name__=='__main__': unittest.main(verbosity=2)

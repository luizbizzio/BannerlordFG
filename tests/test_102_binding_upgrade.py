"""No permissive runtime assembly upgrades: pin exact CLR assembly identity instead."""
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
HELPER = (ROOT / 'Module/BannerlordFG/bin/Win64_Shipping_Client/BannerlordFG.Helper.cmd').read_text()
PROJ = (ROOT / 'src/BannerlordFG.Setup/BannerlordFG.Setup.csproj').read_text()

def test_pin_tasks_extensions_to_matching_assembly():
    assert '<PackageReference Include="System.Threading.Tasks.Extensions" Version="4.5.4" />' in PROJ
    assert "-ne '4.2.0.1'" in HELPER

def test_drop_power_shell_dynamic_resolver_to_avoid_stack_overflow():
    assert 'add_AssemblyResolve' not in HELPER
    assert 'GetNewClosure' not in HELPER
    assert '$allowUpgrade' not in HELPER
    assert '$asm.FullName -ne $identity.FullName' in HELPER


def test_no_duplicate_packagereferences_and_matching_copy_source():
    import xml.etree.ElementTree as ET
    root = ET.fromstring(PROJ)
    package_refs = root.findall('.//{http://schemas.microsoft.com/developer/msbuild/2003}PackageReference')
    packages = [p.attrib['Include'].lower() for p in package_refs]
    assert len(packages) == len(set(packages)), 'Duplicate NuGet PackageReference (NU1504)'
    assert packages.count('system.threading.tasks.extensions') == 1
    cmd = (ROOT / 'BUILD_WINDOWS.cmd').read_text()
    assert 'call :copy_runtime_dep "system.threading.tasks.extensions" "4.5.4" "net461" "System.Threading.Tasks.Extensions.dll"' in cmd
    assert '"system.threading.tasks.extensions" "4.6.0"' not in cmd

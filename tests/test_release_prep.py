"""Release packaging and low-overhead runtime checks (source-level only)."""
from pathlib import Path
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parents[1]

def read(name):
    return (ROOT / name).read_text(encoding="utf-8")

def test_gpl_license_present_in_repository_and_mod_package():
    license_text = read("LICENSE")
    assert "GNU GENERAL PUBLIC LICENSE" in license_text
    assert "Version 3, 29 June 2007" in license_text
    assert license_text == read("Module/BannerlordFG/LICENSE")

def test_license_notice_keeps_third_party_attribution():
    assert "Harmony" in read("THIRD_PARTY_NOTICES.txt")
    assert "OptiScaler" in read("THIRD_PARTY_NOTICES.txt")
    assert "MIT" in read("Module/BannerlordFG/HARMONY_LICENSE.txt")
    assert "SPDX-License-Identifier: GPL-3.0-only" in read("src/BannerlordFG/SubModule.cs")

def test_repository_contains_no_distributed_third_party_or_game_binaries():
    for folder in ("deps", "Module/BannerlordFG/bin", "src"):
        assert not list((ROOT / folder).rglob("*.dll"))
        assert not list((ROOT / folder).rglob("*.exe"))

def test_version_and_module_identity():
    xml = ElementTree.parse(ROOT / "Module/BannerlordFG/SubModule.xml")
    module = xml.getroot()
    assert module.find("Version").attrib["value"] == "v1.0.1"
    assert module.find("Id").attrib["value"] == "BannerlordFG"
    assert module.find("SubModules/SubModule/DLLName").attrib["value"] == "BannerlordFG.dll"

def test_manifest_status_is_snapshotted_until_next_game_launch():
    code = read("src/BannerlordFG/SubModule.cs")
    assert "if (_managedStateInitialized) return _cachedManagedInstall;" in code
    assert "_managedStateInitialized = true;" in code
    assert 'File.Exists(manifest) && File.Exists(ini) && File.Exists(dxgi)' in code

def test_source_uses_compiled_shortcut_regex_and_single_final_pass():
    code = read("src/BannerlordFG/Core/OptiProfile.cs")
    assert "ShortcutAssignment = new Regex" in code
    assert "RegexOptions.Compiled" in code
    assert code.count("t = ConfigureLiveShortcutsInText(t);") == 0
    assert code.count("return ConfigureLiveShortcutsInText(t);") == 1

def test_windows_build_script_uses_msbuild_and_generates_release_archive():
    build = read("BUILD_WINDOWS.cmd")
    assert "Microsoft Visual Studio" in build
    assert "MSBuild.exe" in build
    assert "src\\BannerlordFG\\BannerlordFG.csproj" in build
    assert "BannerlordFG-v1.0.1.zip" in build

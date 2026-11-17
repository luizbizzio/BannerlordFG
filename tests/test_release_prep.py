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

def test_local_build_dependencies_are_known_and_not_game_binaries():
    dependency = ROOT / "deps/Harmony/0Harmony.dll"
    module_harmony = ROOT / "Module/BannerlordFG/bin/Win64_Shipping_Client/0Harmony.dll"
    assert {p.relative_to(ROOT).as_posix() for p in (ROOT / "deps").rglob("*.dll")} == {
        "deps/Harmony/0Harmony.dll"}
    assert dependency.read_bytes() == module_harmony.read_bytes()
    assert not list((ROOT / "src").rglob("*.exe"))
    assert not list((ROOT / "Module/BannerlordFG").rglob("*.exe"))

def test_version_and_module_identity():
    xml = ElementTree.parse(ROOT / "Module/BannerlordFG/SubModule.xml")
    module = xml.getroot()
    assert module.find("Version").attrib["value"] == "v1.1.3"
    assert module.find("Id").attrib["value"] == "BannerlordFG"
    assert module.find("SubModules/SubModule/DLLName").attrib["value"] == "BannerlordFG.dll"

def test_manifest_status_is_cached_and_negative_recheck_throttled():
    code = read("src/BannerlordFG/SubModule.cs")
    assert "DateTime.UtcNow < _nextManagedInstallProbeUtc" in code
    assert "DateTime.UtcNow.AddSeconds(2)" in code
    assert "_managedStateInitialized = true;" in code
    assert '_cachedManifestPresent && File.Exists(ini)' in code
    assert 'tracked != null && tracked.Count > 0' in code

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
    assert "BannerlordFG-v1.1.3.zip" in build

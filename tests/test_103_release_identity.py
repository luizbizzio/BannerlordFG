"""Guard against shipping preview source with an outdated v1.0.2 identity."""
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def test_release_identity_is_103_everywhere():
    assert ET.parse(ROOT / "Module/BannerlordFG/SubModule.xml").getroot().find("Version").get("value") == "v1.0.3"
    for name in ("BannerlordFG", "BannerlordFG.Setup"):
        info = (ROOT / "src" / name / "Properties/AssemblyInfo.cs").read_text()
        assert 'AssemblyVersion("1.0.3.0")' in info
        assert 'AssemblyFileVersion("1.0.3.0")' in info
    cmd = (ROOT / "BUILD_WINDOWS.cmd").read_text()
    assert 'BannerlordFG-v1.0.3.zip' in cmd
    assert 'SUCCESS: BannerlordFG v1.0.3 compiled and installed.' in cmd
    assert 'version-1.0.3-blue' in (ROOT / "README.md").read_text()
    assert 'BannerlordFG-setup/1.0.3' in (ROOT / "src/BannerlordFG.Setup/OptiInstaller.cs").read_text()


def test_windows_batch_remains_crlf():
    for path in ROOT.rglob("*.cmd"):
        b = path.read_bytes()
        assert b"\r\n" in b, path
        assert b"\n" not in b.replace(b"\r\n", b""), path

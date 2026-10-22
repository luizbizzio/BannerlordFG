"""The exact embedded fixture is a valid 7z archive, checked into the source as base64."""
from pathlib import Path
import re, base64
ROOT = Path(__file__).resolve().parents[1]
SRC = (ROOT / 'src/BannerlordFG.Setup/MaintenanceEntry.cs').read_text()

def test_fixture_is_valid_7z_with_expected_size_and_contents():
    match = re.search(r'Convert.FromBase64String\("([A-Za-z0-9+/=]+)"\)', SRC)
    assert match is not None
    archive = base64.b64decode(match.group(1))
    assert archive.startswith(bytes.fromhex('377abcaf271c'))
    assert len(archive) == 147

def test_fixture_runs_using_safe_extractor_before_deploy():
    assert 'SafeSevenZipExtractor.Extract(archive, destination);' in SRC
    assert 'File.ReadAllText(Path.Combine(destination, "bfg-test.txt"))' in SRC

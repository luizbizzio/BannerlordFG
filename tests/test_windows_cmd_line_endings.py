"""Regression: CMD scripts with LF-only line endings may lose CALL :label targets.

Run against the actual files shipped in the GitHub repository and release archive.
"""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def test_all_cmd_scripts_use_windows_crlf():
    scripts = list(ROOT.rglob("*.cmd")) + list(ROOT.rglob("*.bat"))
    assert scripts, "No Windows batch files found"
    for script in scripts:
        data = script.read_bytes()
        assert b"\n" in data, str(script)
        assert data.count(b"\r\n") == data.count(b"\n"), f"Non-CRLF line endings: {script}"
        assert data.count(b"\r") == data.count(b"\n"), f"Stray CR: {script}"


def test_windows_batch_subroutine_targets_are_declared():
    for script in list(ROOT.rglob("*.cmd")) + list(ROOT.rglob("*.bat")):
        text = script.read_bytes().decode("utf-8", "replace")
        labels = set(re.findall(r"(?im)^:([a-z_][a-z_0-9]*)\s*$", text))
        calls = re.findall(r"(?im)\bcall\s+:([a-z_][a-z_0-9]*)\b", text)
        missing = set(calls) - {label.lower() for label in labels}
        assert not missing, f"Missing CALL labels {missing} in {script}"


def test_repo_checkout_preserves_crlf():
    attributes = (ROOT / ".gitattributes").read_text(encoding="utf-8")
    assert "*.cmd text eol=crlf" in attributes
    assert "*.bat text eol=crlf" in attributes

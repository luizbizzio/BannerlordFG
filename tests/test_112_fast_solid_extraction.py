"""Regressions: safe sequential LZMA2 7z extraction in BannerlordFG v1.1.2."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EXTRACT = (ROOT / 'src/BannerlordFG.Setup/SafeSevenZipExtractor.cs').read_text()
INSTALL = (ROOT / 'src/BannerlordFG.Setup/OptiInstaller.cs').read_text()


def test_sequential_reader_reuses_7z_decoder():
    assert 'archive.ExtractAllEntries()' in EXTRACT
    assert 'while (reader.MoveToNextEntry())' in EXTRACT
    assert 'reader.OpenEntryStream()' in EXTRACT
    assert 'entry.OpenEntryStream()' not in EXTRACT


def test_sequential_reader_keeps_archive_safety_checks():
    for text in ('MaxUnpackedBytes', 'MaxEntries', 'IsEncrypted', 'LinkTarget',
                 'IsComplete', 'ReservedNames', 'ReparsePoint', 'FileMode.CreateNew',
                 'entryIndexes.TryGetValue', '!extracted.Add(i)',
                 'entry.IsDirectory != reader.Entry.IsDirectory',
                 'entry.Size != reader.Entry.Size',
                 'actualTotal > MaxUnpackedBytes', 'actualTotal != expectedTotal',
                 'extracted.Count != entries.Length', 'CheckPath(root, target)'):
        assert text in EXTRACT
    assert EXTRACT.index('file/directory conflicts before writing') < EXTRACT.index('archive.ExtractAllEntries()')


def test_retain_verified_package_and_measure_extract_and_hashing():
    assert 'nightly-20261112' in INSTALL
    assert 'PinnedSha256' in INSTALL
    assert 'Sequential 7z extraction finished in ' in INSTALL
    assert 'Extracted file hashing and verification finished in ' in INSTALL
    assert 'pkg.Verify();' in INSTALL

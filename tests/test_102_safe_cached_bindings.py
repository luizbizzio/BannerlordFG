"""Regression: PS 5.1 must bind legacy SharpCompress assembly IDs without recursive loads."""
from pathlib import Path
import re
ROOT = Path(__file__).resolve().parents[1]
CODE = (ROOT / 'src/BannerlordFG.Setup/MaintenanceEntry.cs').read_text()
HELPER = (ROOT / 'Module/BannerlordFG/bin/Win64_Shipping_Client/BannerlordFG.Helper.cmd').read_text()


def test_registers_cache_before_smoke_and_install():
    assert '[BannerlordFG.Setup.MaintenanceEntry]::RegisterLocalAssemblyBindings(); if($env:BFG_OP' in HELPER
    assert 'AppDomain.CurrentDomain.AssemblyResolve += ResolveKnownLocalVersion;' in CODE
    assert CODE.index('knownRuntimeAssemblies = validated;') < CODE.index('AssemblyResolve += ResolveKnownLocalVersion;')


def test_resolver_never_loads_or_reads_files():
    resolver = CODE.split('private static Assembly ResolveKnownLocalVersion(', 1)[1].split('private static void ConfigureCallerContext(', 1)[0]
    assert 'Assembly.Load' not in resolver
    assert 'LoadFrom' not in resolver
    assert 'LoadFile' not in resolver
    assert 'File.' not in resolver
    assert 'return match;' in resolver
    assert 'requested.GetPublicKeyToken()' in resolver
    assert 'args.RequestingAssembly' in resolver
    assert 'requested.Version > actual.Version' in resolver


def test_preflight_forces_exact_previously_failed_version():
    assert 'System.Buffers, Version=4.0.2.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51' in CODE
    assert 'object.ReferenceEquals(oldBuffers, cachedBuffers)' in CODE
    assert 'new Version(4, 0, 2, 0)' in CODE
    assert 'SafeSevenZipExtractor.Extract(archive, destination)' in CODE
    assert 'VerifyRuntimeAssemblies();' in HELPER


def test_scripts_remain_windows_compatible():
    for filename in ('BUILD_WINDOWS.cmd', 'Module/BannerlordFG/bin/Win64_Shipping_Client/BannerlordFG.Helper.cmd'):
        raw = (ROOT/filename).read_bytes()
        assert raw.count(b'\n') == raw.count(b'\r\n')

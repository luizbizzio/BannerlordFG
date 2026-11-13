"""v1.1.2 candidate contracts for OptiScaler nightly-20261112 and safe fallback.

Static checks only. The Windows helper, DLL injection, and game startup require
a real Windows/Bannerlord validation pass.
"""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
OPTI = (ROOT/"src/BannerlordFG.Setup/OptiInstaller.cs").read_text()
WORKER = (ROOT/"src/BannerlordFG.Setup/AutoFirstRun.cs").read_text()
HELPER = (ROOT/"Module/BannerlordFG/bin/Win64_Shipping_Client/BannerlordFG.Helper.cmd").read_text()
README = (ROOT/"README.md").read_text()

def constants():
    return dict(re.findall(r'(?:internal|private) const string (\w+) = "([^"]+)";', OPTI))

def test_20261112_pin_is_explicit_and_sha_verified():
    c = constants()
    assert c["Tag"] == "nightly-20261112"
    assert c["AssetName"] == "OptiScaler_v10.0.0-pre1_20261112.7z"
    assert c["PinnedSha256"] == "c32932ca340ffd605325101077b072209514ed0f110dc8b7ab92486eec1a5d93"
    assert "pkg.Sha256.Equals(PinnedSha256" in OPTI

def test_prior_installations_retained_without_reusing_new_digest():
    c = constants()
    known = [("Prior","nightly-20261109","e65cfc7092840cde79b24467bf0b3404bc358e56866478f46811eed06ffaa150"),
             ("Older","nightly-20261106","5b92e637cdead99c8d994360850407e93876f00b5f0776f2be39a1ee2caa5726"),
             ("Previous","nightly-20261016","09149a5b17514a0594faf612467267c62ed8851dbc1dde0ade6763f1152e600f"),
             ("Legacy","nightly-20260926","464e2ecc5dab477e940186959a7fdee64880f61c63f52ff0f516e5502bc74e94")]
    matcher = OPTI.split("private static bool IsKnownManagedArchive(",1)[1].split("internal const string ReleasePage",1)[0]
    for prefix,tag,sha in known:
        assert c[f"{prefix}Tag"] == tag
        assert c[f"{prefix}PinnedSha256"] == sha
        assert f"string.Equals(tag, {prefix}Tag" in matcher
        assert f"string.Equals(digest, {prefix}PinnedSha256" in matcher

def test_install_worker_does_not_report_success_after_managed_failure():
    run = WORKER.split("internal static void Run(string requestedGameBin, int waitPid)",1)[1]
    catch = run.split('Log("Automatic installation did not complete: " + ex);',1)[1].split("finally",1)[0]
    assert 'WriteAtomic(StatePath(gameBin, ".blocked"), ex.Message)' in catch
    assert "throw;" in catch

def test_cmd_preserves_detailed_worker_errors_and_separates_uninstall():
    assert 'if not exist "%BFG_STATE_BASE%.blocked"' in HELPER
    assert 'if not exist "%BFG_STATE_BASE%.uninstall-failed"' in HELPER
    assert 'if /I "%BFG_OP%"=="--post-exit-uninstall"' in HELPER

def test_no_untested_automatic_reshade_integration():
    assert 'LoadReshade' not in OPTI
    assert 'ReShade64.dll' not in OPTI
    assert 'does not automatically enable it' in README
    assert 'nightly-20261112' in README

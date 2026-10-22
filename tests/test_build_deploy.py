"""Static checks for safe CMD build/deployment. This is not an MSBuild test."""
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
S=(ROOT/'BUILD_WINDOWS.cmd').read_text(encoding='utf-8')

def test_fixed_game_path_and_no_vortex_dependency():
    assert 'Mount & Blade II Bannerlord' in S
    assert 'BUILD_WINDOWS.cmd' in S
    assert 'BannerlordFG-v1.0.2.zip' in S # standalone ZIP, no mod manager needed

def test_compile_before_package():
    assert S.index(r'src\BannerlordFG\BannerlordFG.csproj') < S.index(r'src\BannerlordFG.Setup\BannerlordFG.Setup.csproj')
    assert S.index(r'src\BannerlordFG.Setup\BannerlordFG.Setup.csproj') < S.index('tar.exe -a -cf')
    assert 'BannerlordFG.Maintenance.dll' in S and 'BannerlordFG.Helper.cmd' in S
    assert 'BannerlordFG.Setup.exe' not in S.split('for %%F in (BannerlordFG.dll',1)[1].split(r'if exist "%OUT%\BUILD_REQUIRED',1)[0]

def test_safe_deploy_and_backup():
    assert 'robocopy "%TARGET%" "%BACKUP%"' in S
    assert 'goto :rollback' in S
    assert 'call :check_hardlinks' in S
    assert 'taskkill' not in S.lower()

def test_package_root_and_version():
    assert 'BannerlordFG-v1.0.2.zip' in S
    assert 'tar.exe -a -cf "%ARCHIVE%" -C "%ROOT%Module" BannerlordFG' in S
    assert 'BannerlordFG.Helper.cmd' in S

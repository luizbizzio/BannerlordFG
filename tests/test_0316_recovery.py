"""Regression contracts for safe cleanup of manifests left by live INI edits.
These do not substitute for compiling/running the actual .NET helper on Windows.
"""
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
INSTALL=(ROOT/'src/BannerlordFG.Setup/OptiInstaller.cs').read_text()
AUTO=(ROOT/'src/BannerlordFG.Setup/AutoFirstRun.cs').read_text()
PROGRAM=(ROOT/'src/BannerlordFG.Setup/MaintenanceEntry.cs').read_text()
CMD=(ROOT/'RECOVER_OPTISCALER.cmd').read_text()

def test_changed_managed_config_is_preserved_instead_of_reblocking():
    assert 'IsOwnedConfig(item.Relative)' in INSTALL
    assert 'BackupChangedConfig(item.Path, item.Relative, recovery, log);' in INSTALL
    assert INSTALL.index('BackupChangedConfig(item.Path, item.Relative, recovery, log);') < INSTALL.index('File.Delete(manifestPath);')

def test_backup_copy_verified_before_original_is_deleted():
    backup=INSTALL.split('private static string BackupChangedConfig(',1)[1].split('private static void ReportPossibleHolders(',1)[0]
    for op in ('File.Copy(source, destination, false)', 'HashFile(destination)', 'DeleteVerifiedFile(source, originalHash)'):
        assert op in backup
    assert backup.index('File.Copy(source, destination, false)') < backup.index('HashFile(destination)') < backup.index('DeleteVerifiedFile(source, originalHash)')
    assert 'DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")' in INSTALL
    assert 'Guid.NewGuid().ToString("N")' in INSTALL

def test_only_explicitly_owned_configs_can_auto_backup():
    section=INSTALL.split('private static bool IsOwnedConfig(',1)[1].split('private sealed class ManagedRemovalFile',1)[0]
    assert 'OptiScaler.ini' in section
    assert 'OptiScaler.ini.bannerlordfg-original.bak' in section
    assert 'dxgi.dll' not in section
    assert 'throw new InvalidDataException("Managed file was modified; nothing removed:' in INSTALL

def test_partial_manifest_proxy_is_only_adopted_if_hash_matches():
    assert 'installed.ContainsKey("OptiScaler.dll")' in INSTALL
    assert 'HashFile(renamed).Equals(installed["OptiScaler.dll"]' in INSTALL
    assert 'final[proxy] = installed["OptiScaler.dll"]' in INSTALL

def test_cli_returns_error_for_retained_manifest():
    assert 'if (OptiInstaller.HasManagedInstall(gameBin))' in AUTO
    assert 'Recovery is incomplete: manifest retained' in AUTO
    assert 'return 2;' in PROGRAM
    assert 'if errorlevel 1 (' in CMD

def test_recovery_does_not_force_delete_unknown_dlls():
    assert 'Safe' not in CMD or 'del /' not in CMD.lower()
    assert 'Existing dxgi.dll preserved' in CMD
    assert 'Existing winmm.dll preserved' in CMD
    assert 'No overwrite' not in CMD or 'del /' not in CMD.lower()
    assert 'OptiScaler directory still exists' in CMD
    assert 'preflight-' in CMD

def test_fsr31_selected_for_nvidia_in_unattended_installer():
    assert 'gpu != "NVIDIA"' not in AUTO
    assert AUTO.count('true /* FSR 3.1 for every GPU including NVIDIA */') == 2

def test_manifest_removal_requires_all_owned_files_gone():
    assert 'plan.Any(item => TrackedFileExists(item.Path))' in INSTALL
    assert 'Untracked files remain in OptiScaler; manifest retained for review.' in INSTALL
    assert 'Original manifest and recovery snapshot retained:' in INSTALL

def test_archive_version_bumped():
    assert 'v1.1.3' in (ROOT/'Module/BannerlordFG/SubModule.xml').read_text()
    assert 'v1.1.3' in (ROOT/'BUILD_WINDOWS.cmd').read_text()

// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BannerlordFG.Setup
{
    // Executed by Windows PowerShell's .NET Framework host from the CMD helper.
    // A class-library entry point keeps all install/manifest/rollback logic in C#
    // without shipping an unsigned standalone setup executable.
    public static class MaintenanceEntry
    {
        // A separate administrator account has a different LOCALAPPDATA and
        // Documents directory. Trust only the caller's expected per-game state
        // filename; reject an arbitrary path before touching any user files.
        private static void ConfigureCallerContext(string gameBin)
        {
            string stateBase = Environment.GetEnvironmentVariable("BFG_STATE_BASE");
            if (string.IsNullOrWhiteSpace(stateBase)) return; // legacy CLI maintenance
            string bin = OptiInstaller.ValidateGameBin(gameBin);
            string expectedId;
            using (var sha = SHA256.Create())
                expectedId = BitConverter.ToString(sha.ComputeHash(
                    Encoding.UTF8.GetBytes(bin.ToLowerInvariant()))).Replace("-", "").Substring(0, 12);
            string fullBase = Path.GetFullPath(stateBase);
            string parent = Path.GetDirectoryName(fullBase);
            if (!Path.GetFileName(fullBase).Equals("first-run-" + expectedId,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(parent).Equals("BannerlordFG", StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(parent) ||
                (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The game user's installer state directory failed validation.");
            Environment.SetEnvironmentVariable("BFG_VERIFIED_STATE_DIR", parent);
            string documents = Environment.GetEnvironmentVariable("BFG_USER_DOCUMENTS");
            if (!string.IsNullOrWhiteSpace(documents))
            {
                string fullDocuments = Path.GetFullPath(documents);
                // OneDrive documents can be redirected through a junction. A
                // missing/unavailable folder must defer optional FPS cleanup,
                // not block the verified OptiScaler installation or switch to
                // the administrator's unrelated Documents directory.
                Environment.SetEnvironmentVariable("BFG_VERIFIED_USER_DOCUMENTS", fullDocuments);
            }
        }

        public static int Run(string operation, string gameBin, int waitPid)
        {
            try
            {
                ConfigureCallerContext(gameBin);
                switch ((operation ?? string.Empty).ToLowerInvariant())
                {
                    case "--auto-first-run":
                        AutoFirstRun.Run(gameBin, waitPid);
                        return 0;
                    case "--post-exit-uninstall":
                        AutoFirstRun.RunUninstall(gameBin, waitPid);
                        return 0;
                    case "--post-exit-restart":
                        AutoFirstRun.RunRestartOnly(gameBin, waitPid);
                        return 0;
                    case "--uninstall-closed-game":
                        AutoFirstRun.RunManualUninstall(gameBin);
                        return 0;
                    case "--configure-live-controls-closed-game":
                        OptiInstaller.ConfigureExistingManagedLiveControls(gameBin, AutoFirstRun.Log);
                        return 0;
                    case "--restore-legacy-fps":
                        if (OptiInstaller.BannerlordIsRunning())
                            throw new InvalidOperationException("Close Bannerlord before legacy FPS restoration.");
                        FpsCounterCleanup.RestoreAfterUninstall(AutoFirstRun.Log);
                        return 0;
                    default:
                        throw new ArgumentException("Unknown maintenance operation: " + operation);
                }
            }
            catch (Exception ex)
            {
                AutoFirstRun.Log("Maintenance command failed: " + ex);
                Console.Error.WriteLine("BannerlordFG maintenance failed: " + ex.Message);
                return 2;
            }
        }
    }
}

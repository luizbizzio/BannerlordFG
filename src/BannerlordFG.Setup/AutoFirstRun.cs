// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BannerlordFG.Setup
{
    /// <summary>
    /// First-run worker started by the Bannerlord C# module. Downloading the pinned
    /// official package is allowed while the game is open. Its graphics DLLs are
    /// installed ONLY after every Bannerlord process exits. The game is never
    /// restarted by the installer: the player launches it manually afterwards.
    /// No dialogs, no Steam launch-option changes, and no silent DLL overwrites.
    /// </summary>
    internal static class AutoFirstRun
    {
        // The in-game caller supplies its own state directory to the elevated CMD.
        // MaintenanceEntry validates the explicit path before this class uses it;
        // otherwise a different UAC administrator would read the wrong AppData.
        internal static string Folder => Environment.GetEnvironmentVariable("BFG_VERIFIED_STATE_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BannerlordFG");

        internal static string StatePath(string gameBin, string suffix)
        {
            using (var hash = SHA256.Create())
            {
                byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(gameBin.ToLowerInvariant()));
                string id = BitConverter.ToString(bytes).Replace("-", "").Substring(0, 12);
                return Path.Combine(Folder, "first-run-" + id + suffix);
            }
        }

        internal static void Log(string message)
        {
            BannerlordFG.Diagnostics.UnifiedLog.Write(Folder, "SETUP", message);
        }

        // One small per-game status marker shared with the game process. The
        // helper may run elevated or under a different UAC account; its state
        // directory was already validated by MaintenanceEntry. Never use the
        // unified BannerlordFG.log as a progress transport.
        private static void PublishProgress(string gameBin, string stage,
            int percent, long received, long total)
        {
            try
            {
                if (string.IsNullOrEmpty(gameBin)) return;
                string path = StatePath(gameBin, ".progress");
                string value = stage + "|" + percent + "|" +
                    Math.Max(0L, received) + "|" + Math.Max(0L, total);
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, value, new UTF8Encoding(false));
                try
                {
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            catch (Exception ex)
            {
                // Status display is optional: it must never interrupt the
                // verified download or installation.
                Log("Progress status unavailable: " + ex.Message);
            }
        }

        // First identify Bannerlord.exe's active 3D rendering adapter by PID +
        // DXGI LUID. If Windows does not expose reliable GPU Engine activity,
        // use a deterministic, no-prompt NVIDIA > AMD > Intel hardware fallback.
        private static string DetectGpu(string gameBin, int waitPid)
        {
            string active = GpuDetection.TryDetectActiveGpu(gameBin, waitPid, Log);
            return active ?? GpuDetection.DetectByVendorPriority(Log);
        }

        private static bool ProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            try { using (var p = Process.GetProcessById(pid)) return !p.HasExited; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        // The original game-host PID can belong to TaleWorlds' launcher. A
        // launcher that survives QuitGame is not a blocker once it no longer
        // has our OWN managed proxy loaded. An external DXGI (such as ReShade)
        // must never be mistaken for BannerlordFG's winmm.dll proxy.
        private static bool OriginalPidStillUsingGraphics(int waitPid, string gameBin)
        {
            if (!ProcessAlive(waitPid)) return false;
            try
            {
                using (var p = Process.GetProcessById(waitPid))
                {
                    if (!p.ProcessName.Equals("TaleWorlds.MountAndBlade.Launcher",
                        StringComparison.OrdinalIgnoreCase)) return true;
                    string managedName = OptiInstaller.GetManagedProxyName(gameBin);
                    if (managedName == null) return true; // Cannot prove no owned DLL is loaded.
                    string proxy = Path.GetFullPath(Path.Combine(gameBin, managedName));
                    foreach (ProcessModule module in p.Modules)
                    {
                        if (string.Equals(Path.GetFullPath(module.FileName), proxy,
                            StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    // Still-running Bannerlord.exe is checked separately.
                    return false;
                }
            }
            catch (ArgumentException) { return false; } // PID already exited.
            catch (InvalidOperationException) { return false; } // PID already exited.
            catch { return true; } // Cannot prove proxy unloaded; do not delete.
        }

        internal static bool InstallIfNeeded(string gameBin)
        {
            string previous = OptiInstaller.ExistingInstallStatus(gameBin);
            if (previous != null)
            {
                Log(previous);
                // We cannot infer a complete, owned install just from an INI file.
                // Never silently take control of someone else's installation.
                return File.Exists(Path.Combine(gameBin, "OptiScaler.ini"));
            }
            string gpu = DetectGpu(gameBin, 0);
            Task.Run(() => OptiInstaller.InstallAsync(gameBin, gpu, true /* FSR 3.1 for every GPU including NVIDIA */, Log, true))
                .GetAwaiter().GetResult();
            File.WriteAllText(StatePath(gameBin, ".enable-next-launch"), "true");
            return true;
        }

        private static void WriteAtomic(string path, string text)
        {
            // One-shot state files can survive an interrupted earlier attempt.
            // File.Move refuses to overwrite such a marker, even after all
            // OptiScaler graphics files were installed successfully.
            // Keep the temporary file beside the destination to update safely.
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path))
                    File.Replace(temporary, path, null);
                else
                {
                    try { File.Move(temporary, path); }
                    catch (IOException)
                    {
                        // The destination may have appeared after the Exists
                        // check. Never delete an existing marker before replace.
                        if (!File.Exists(path)) throw;
                        File.Replace(temporary, path, null);
                    }
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        // Some graphics settings are saved on game exit. Apply their deferred
        // cleanup after shutdown, but never start a new Bannerlord process.
        internal static void RunSettingsAfterExit(string requestedGameBin, int waitPid)
        {
            try
            {
                if (waitPid <= 0) throw new InvalidOperationException("Missing original game PID.");
                OptiInstaller.ValidateGameBin(requestedGameBin);
                DateTime deadline = DateTime.UtcNow.AddMinutes(15);
                while ((ProcessAlive(waitPid) || OptiInstaller.BannerlordIsRunning()) &&
                    DateTime.UtcNow < deadline) Thread.Sleep(1000);
                if (ProcessAlive(waitPid) || OptiInstaller.BannerlordIsRunning())
                { Log("Settings cleanup deferred: Bannerlord did not shut down normally."); return; }
                FpsCounterCleanup.ApplyRequestedAfterExit(Log);
                NativeLimiterCleanup.ApplyRequestedAfterExit(Log);
                Log("Settings cleanup complete. Start Bannerlord manually when ready.");
            }
            catch (Exception ex) { Log("Settings cleanup failed: " + ex); }
        }

        internal static void RunManualUninstall(string requestedGameBin)
        {
            try
            {
                string gameBin = OptiInstaller.ValidateGameBin(requestedGameBin);
                if (OptiInstaller.BannerlordIsRunning())
                    throw new InvalidOperationException("Close Bannerlord normally before uninstalling.");
                File.WriteAllText(StatePath(gameBin, ".disabled"), DateTime.UtcNow.ToString("o"));
                try { File.Delete(StatePath(gameBin, ".enable-next-launch")); } catch { }
                OptiInstaller.Uninstall(gameBin, Log);
                FpsCounterCleanup.RestoreAfterUninstall(Log);
                NativeLimiterCleanup.RestoreAfterUninstall(Log);
                if (OptiInstaller.HasManagedInstall(gameBin))
                    throw new IOException("Recovery is incomplete: manifest retained for modified/foreign assets. See BannerlordFG.log.");
                Log("Manual recovery uninstall completed without starting Bannerlord.");
            }
            catch (Exception ex) { Log("Manual uninstall failed: " + ex); throw; }
        }

        // User-initiated uninstall: game calls native QuitGame; helper only waits.
        // Never terminate or relaunch the game on uninstall.
        internal static void RunUninstall(string requestedGameBin, int waitPid)
        {
            string gameBin = null;
            string ready = null;
            string pending = null;
            try
            {
                if (waitPid <= 0) throw new InvalidOperationException("Invalid game process id.");
                gameBin = OptiInstaller.ValidateGameBin(requestedGameBin);
                ready = StatePath(gameBin, ".uninstall-ready");
                pending = StatePath(gameBin, ".uninstall-pending");
                if (!File.Exists(pending) || File.ReadAllText(pending).Trim() != waitPid.ToString())
                    throw new InvalidOperationException("No matching in-game uninstall request. Nothing removed.");
                // Refuse foreign/incomplete installs before telling Bannerlord
                // to close; the menu must not appear to have uninstalled FG.
                if (!OptiInstaller.HasCompleteManagedInstall(gameBin))
                    throw new InvalidOperationException(
                        "No complete BannerlordFG-managed OptiScaler install was found. Use manual recovery for an incomplete manifest.");
                Log("Uninstall ownership preflight passed; acknowledging helper to game PID " + waitPid + ".");
                WriteAtomic(ready, waitPid.ToString());
                DateTime deadline = DateTime.UtcNow.AddMinutes(3);
                DateTime nextProgressLog = DateTime.UtcNow.AddSeconds(10);
                while ((OriginalPidStillUsingGraphics(waitPid, gameBin) ||
                    OptiInstaller.BannerlordIsRunning()) && DateTime.UtcNow < deadline)
                {
                    if (DateTime.UtcNow >= nextProgressLog)
                    {
                        Log("Uninstall waiting for Bannerlord shutdown: original PID " + waitPid +
                            " still using game graphics=" + OriginalPidStillUsingGraphics(waitPid, gameBin) +
                            ", other game process active=" + OptiInstaller.BannerlordIsRunning() + ".");
                        nextProgressLog = DateTime.UtcNow.AddSeconds(10);
                    }
                    Thread.Sleep(1000);
                }
                if (OriginalPidStillUsingGraphics(waitPid, gameBin) || OptiInstaller.BannerlordIsRunning())
                    throw new TimeoutException("Game did not fully exit within three minutes (original PID " +
                        waitPid + "). If the launcher still hosts the game, close it too. " +
                        "No loaded graphics DLLs have been modified.");
                Log("Game exited. Removing only manifest-owned OptiScaler files...");
                OptiInstaller.Uninstall(gameBin, Log);
                if (OptiInstaller.HasManagedInstall(gameBin))
                    throw new IOException("Uninstall incomplete: modified or foreign assets remain. Manifest retained; inspect BannerlordFG.log.");
                // Optional vanilla-setting cleanup must not undo a verified
                // graphics removal if the user's old config is read-only.
                try { FpsCounterCleanup.RestoreAfterUninstall(Log); }
                catch (Exception ex) { Log("Native FPS cleanup deferred: " + ex.Message); }
                try { NativeLimiterCleanup.RestoreAfterUninstall(Log); }
                catch (Exception ex) { Log("Legacy limiter cleanup deferred: " + ex.Message); }
                // Commit the disabled state only after successful removal. The
                // next game launch reads .uninstalled to restore vanilla input.
                File.WriteAllText(StatePath(gameBin, ".disabled"), DateTime.UtcNow.ToString("o"));
                try { File.Delete(StatePath(gameBin, ".enable-next-launch")); } catch { }
                WriteAtomic(StatePath(gameBin, ".uninstalled"), DateTime.UtcNow.ToString("o"));
                Log("Safe uninstall complete. Vanilla graphics will be restored on next launch.");
            }
            catch (Exception ex)
            {
                Log("Safe uninstall failed: " + ex);
                if (gameBin != null)
                    try { WriteAtomic(StatePath(gameBin, ".uninstall-failed"), ex.Message); } catch { }
                throw; // Propagate failure to BannerlordFG.log and CMD exit code.
            }
            finally
            {
                if (ready != null) try { File.Delete(ready); } catch { }
                if (pending != null) try { File.Delete(pending); } catch { }
            }
        }

        internal static void Run(string requestedGameBin, int waitPid)
        {
            Log("Maintenance DLL entered automatic install (game PID=" + waitPid + ").");
            string gameBin = null;
            string pending = null;
            string ready = null;
            Mutex mutex = null;
            bool lockTaken = false;
            OptiInstaller.PreparedPackage package = null;
            bool installCompleted = false;
            bool markersCleared = false;
            try
            {
                if (waitPid <= 0) throw new InvalidOperationException("Missing Bannerlord process PID.");
                gameBin = OptiInstaller.ValidateGameBin(requestedGameBin);
                Directory.CreateDirectory(Folder);
                pending = StatePath(gameBin, ".pending");
                ready = StatePath(gameBin, ".ready");
                string mutexName = "Local\\BannerlordFG-auto-" + Path.GetFileNameWithoutExtension(pending);
                mutex = new Mutex(false, mutexName);
                try { lockTaken = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { lockTaken = true; }
                if (!lockTaken) { Log("Another helper is already working on this installation."); return; }

                // Preserve all existing installations. No replacing another mod's
                // dbghelp.dll, dxgi.dll, or other graphics hooks on first run.
                string existing = OptiInstaller.ExistingInstallStatus(gameBin);
                bool recoverInterrupted = false;
                if (OptiInstaller.HasManagedInstall(gameBin))
                {
                    string recoveryReason;
                    recoverInterrupted = OptiInstaller.CanRecoverIncompleteInstall(gameBin, out recoveryReason);
                    if (!recoverInterrupted)
                    {
                        if (existing != null && existing.Contains("already installed") &&
                            OptiInstaller.HasCompleteManagedInstall(gameBin))
                            Log("Complete BannerlordFG installation detected; no replacement attempted.");
                        else
                        {
                            string blocked = "Existing manifest is not safe to repair automatically: " + recoveryReason;
                            Log(blocked);
                            WriteAtomic(StatePath(gameBin, ".blocked"), blocked);
                        }
                        return;
                    }
                    Log("Verified incomplete manifest from a supported OptiScaler build; downloading current pinned release before safe post-exit recovery.");
                }
                if (File.Exists(StatePath(gameBin, ".disabled")))
                {
                    Log("Player previously uninstalled FG; automatic setup disabled.");
                    return;
                }
                if (existing != null && !recoverInterrupted)
                {
                    Log("First run skipped: " + existing);
                    // A skipped install is not a valid reason to force-close a game.
                    if (!File.Exists(Path.Combine(gameBin, "OptiScaler.ini")))
                        WriteAtomic(StatePath(gameBin, ".blocked"), existing);
                    return;
                }
                Log("Detecting Bannerlord renderer first; falling back to NVIDIA > AMD > Intel when necessary...");
                string gpu = DetectGpu(gameBin, waitPid);
                Log("GPU automatically detected: " + gpu);
                PublishProgress(gameBin, "preparing", -1, 0, 0);
                package = Task.Run(() => OptiInstaller.PrepareOfficialPackageAsync(
                    Log, (stage, percent, received, total) =>
                        PublishProgress(gameBin, stage, percent, received, total)))
                    .GetAwaiter().GetResult();
                package.Verify();
                if (File.Exists(StatePath(gameBin, ".disabled")))
                {
                    Log("Setup cancelled during download. No graphics files changed.");
                    return;
                }
                // Signal the module only AFTER download and hash verification.
                WriteAtomic(ready, waitPid.ToString());
                Log("Pinned official 7z ready. Awaiting safe initial-menu shutdown, or normal game exit.");

                DateTime deadline = DateTime.UtcNow.AddHours(4);
                while ((ProcessAlive(waitPid) || OptiInstaller.BannerlordIsRunning()) && DateTime.UtcNow < deadline)
                    Thread.Sleep(1500);
                if (ProcessAlive(waitPid) || OptiInstaller.BannerlordIsRunning())
                    throw new TimeoutException("Game still running; installation postponed rather than modifying loaded DLLs.");

                // If the user disabled FG while the helper was downloading, never
                // install despite the previously verified package.
                if (File.Exists(StatePath(gameBin, ".disabled")))
                    throw new InvalidOperationException("Automatic installation cancelled by player.");
                // Repeat collision check: another installer might have changed the
                // target directory during the download or while the game was running.
                if (recoverInterrupted)
                {
                    // A second ownership/hash check prevents a changed or foreign
                    // file from being deleted after the player closed the game.
                    OptiInstaller.RecoverIncompleteInstall(gameBin, Log);
                }
                existing = OptiInstaller.ExistingInstallStatus(gameBin);
                if (existing != null) throw new IOException("Installation cancelled after download: " + existing);
                Task.Run(() => OptiInstaller.InstallAsync(gameBin, gpu, true /* FSR 3.1 for every GPU including NVIDIA */, Log,
                    true, package)).GetAwaiter().GetResult();
                WriteAtomic(StatePath(gameBin, ".enable-next-launch"), "true");
                Log("First-run installation complete. Clearing worker state before the player launches again.");
                installCompleted = true;
            }
            catch (Exception ex)
            {
                Log("Automatic installation did not complete: " + ex);
                if (gameBin != null)
                    try { WriteAtomic(StatePath(gameBin, ".blocked"), ex.Message); } catch { }
                // No restart on failure. Report a nonzero exit to the CMD host
                // as well: a logged managed failure is not a successful install.
                throw;
            }
            finally
            {
                if (package != null) package.Dispose();
                if (gameBin != null)
                    try { File.Delete(StatePath(gameBin, ".progress")); }
                    catch (Exception ex) { Log("Progress marker cleanup: " + ex.Message); }
                if (ready != null) try { File.Delete(ready); } catch (Exception ex) { Log("Ready marker cleanup: " + ex.Message); }
                if (pending != null) try { File.Delete(pending); } catch (Exception ex) { Log("Pending marker cleanup: " + ex.Message); }
                markersCleared = (ready == null || !File.Exists(ready)) &&
                    (pending == null || !File.Exists(pending));
                if (lockTaken && mutex != null) mutex.ReleaseMutex();
                if (mutex != null) mutex.Dispose();
            }
            // No auto relaunch: require a complete installation, marker
            // cleanup, and a separate normal launch initiated by the player.
            if (installCompleted && gameBin != null)
            {
                if (markersCleared)
                    Log("OptiScaler installation complete. Start Bannerlord manually using your usual launcher.");
                else
                    Log("Install completed, but cleanup markers remain. Check BannerlordFG.log before starting the game.");
            }
            if (gameBin != null)
                try { File.Delete(StatePath(gameBin, ".restart")); } catch { }

        }
    }
}

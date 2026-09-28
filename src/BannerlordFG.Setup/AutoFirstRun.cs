// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BannerlordFG.Setup
{
    /// <summary>
    /// First-run worker started by the Bannerlord C# module. Downloading the pinned
    /// official package is allowed while the game is open. Its graphics DLLs are
    /// installed ONLY after every Bannerlord process exits. A successful setup is
    /// relaunched directly from its original executable/argument vector only
    /// when the mod requests a safe initial-menu restart.
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
            try
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, "first-run.log"),
                    DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine);
            }
            catch { /* A log failure must not affect the running game. */ }
        }

        // The Windows WMI list can include an iGPU and a discrete GPU. Prefer a
        // single discrete NVIDIA/AMD adapter over Intel integrated graphics.
        // Do NOT guess when two different discrete vendor families are present.
        private static string DetectGpu()
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
            {
                // WMI can hang on broken display drivers. Report the failure
                // instead of leaving the in-game install notice indefinitely.
                searcher.Options.Timeout = TimeSpan.FromSeconds(20);
                string[] names = searcher.Get().Cast<ManagementBaseObject>()
                    .Select(x => Convert.ToString(x["Name"]) ?? "").ToArray();
                bool nvidia = names.Any(x => x.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0);
                bool amd = names.Any(x => x.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                          x.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0);
                if (nvidia && amd) throw new InvalidOperationException(
                    "Mixed NVIDIA/AMD adapters: cannot safely infer the GPU selected inside Bannerlord.");
                if (nvidia) return "NVIDIA";
                if (amd) return "AMD";
                if (names.Any(x => x.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0)) return "Intel";
                throw new InvalidOperationException("No supported GPU found through Win32_VideoController.");
            }
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
        // has our injected DXGI proxy loaded. Never assume this if module
        // enumeration is denied or an actual Bannerlord game PID still exists.
        private static bool OriginalPidStillUsingGraphics(int waitPid, string gameBin)
        {
            if (!ProcessAlive(waitPid)) return false;
            try
            {
                using (var p = Process.GetProcessById(waitPid))
                {
                    if (!p.ProcessName.Equals("TaleWorlds.MountAndBlade.Launcher",
                        StringComparison.OrdinalIgnoreCase)) return true;
                    string proxy = Path.GetFullPath(Path.Combine(gameBin, "dxgi.dll"));
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
            string gpu = DetectGpu();
            Task.Run(() => OptiInstaller.InstallAsync(gameBin, gpu, true /* FSR 3.1 for every GPU including NVIDIA */, Log, true))
                .GetAwaiter().GetResult();
            File.WriteAllText(StatePath(gameBin, ".enable-next-launch"), "true");
            return true;
        }

        private static void WriteAtomic(string path, string text)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            try { File.Move(temporary, path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        // Quote according to the Windows CommandLineToArgvW escaping convention.
        // Never send original arguments through cmd.exe or PowerShell.
        private static string QuoteWindowsArgument(string argument)
        {
            if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
                return argument;
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char ch in argument)
            {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"')
                {
                    result.Append('\\', slashes * 2 + 1);
                    result.Append('"');
                    slashes = 0;
                    continue;
                }
                result.Append('\\', slashes);
                slashes = 0;
                result.Append(ch);
            }
            result.Append('\\', slashes * 2);
            result.Append('"');
            return result.ToString();
        }

        private static void RelaunchDirect(string gameBin, int waitPid)
        {
            try
            {
                string marker = StatePath(gameBin, ".restart");
                if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != waitPid.ToString())
                {
                    Log("Setup complete. No auto-restart authorization (gameplay/manual exit).");
                    return;
                }
                File.Delete(marker); // one-shot authorization; no restart loops
                if (OptiInstaller.BannerlordIsRunning())
                {
                    Log("Direct relaunch skipped: another Bannerlord instance is active.");
                    return;
                }
                string launch = StatePath(gameBin, ".launch");
                if (!File.Exists(launch))
                {
                    Log("Direct relaunch unavailable: exact game command line was not captured. Start the game manually.");
                    return;
                }
                // Cross-account UAC lets another administrator maintain Program
                // Files, but must not start the player's game as that administrator.
                // Same-account UAC retains the existing automatic relaunch flow.
                string originalOwner = File.GetAccessControl(launch)
                    .GetOwner(typeof(SecurityIdentifier)).Value;
                string helperUser = WindowsIdentity.GetCurrent().User.Value;
                if (!string.Equals(originalOwner, helperUser, StringComparison.OrdinalIgnoreCase))
                {
                    Log("OptiScaler installed. Cross-account UAC detected: direct relaunch as a different administrator is unsafe. Start Bannerlord normally from Steam.");
                    return;
                }
                string[] lines = File.ReadAllLines(launch, Encoding.UTF8);
                if (lines.Length < 1)
                    throw new InvalidDataException("Incomplete original game command line.");
                string exe = Path.GetFullPath(lines[0]);
                string name = Path.GetFileName(exe);
                if (!string.Equals(Path.GetDirectoryName(exe).TrimEnd('\\'), gameBin.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                    (!name.Equals("Bannerlord.exe", StringComparison.OrdinalIgnoreCase) &&
                     !name.Equals("Bannerlord.Native.exe", StringComparison.OrdinalIgnoreCase)) ||
                    !File.Exists(exe))
                    throw new InvalidDataException("Refusing to launch an executable outside the verified game directory.");
                var args = lines.Skip(1).ToArray();
                if (args.Any(s => s.IndexOfAny(new[] { '\r', '\n' }) >= 0))
                    throw new InvalidDataException("Original game arguments contain invalid characters.");
                if (!string.Join(" ", args).Contains("_MODULES_"))
                    Log("Direct relaunch: no _MODULES_ CLI argument; game may use its saved module configuration.");
                // Allow Steam's bookkeeping to observe the normal game shutdown.
                Thread.Sleep(5000);
                if (OptiInstaller.BannerlordIsRunning()) return;
                var startInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = string.Join(" ", args.Select(QuoteWindowsArgument)),
                    WorkingDirectory = gameBin,
                    UseShellExecute = false
                };
                using (var p = Process.Start(startInfo))
                {
                    if (p == null) throw new InvalidOperationException("Process.Start did not return a game process.");
                    Log("Direct game EXE launched without native launcher: " + name);
                }
            }
            catch (Exception ex)
            {
                Log("Direct game relaunch failed; use the launcher manually: " + ex);
            }
        }

        // Settings changed from native Options. Wait for a normal engine exit,
        // then restore the FPS flag if Bannerlord re-saved its cached config.
        // The restart marker and original module vector are one-shot.
        internal static void RunRestartOnly(string requestedGameBin, int waitPid)
        {
            try
            {
                if (waitPid <= 0) throw new InvalidOperationException("Missing original game PID.");
                string gameBin = OptiInstaller.ValidateGameBin(requestedGameBin);
                DateTime deadline = DateTime.UtcNow.AddMinutes(15);
                while ((ProcessAlive(waitPid) || OptiInstaller.BannerlordIsRunning()) &&
                    DateTime.UtcNow < deadline) Thread.Sleep(1000);
                if (ProcessAlive(waitPid) || OptiInstaller.BannerlordIsRunning())
                { Log("Restart deferred: Bannerlord did not shut down normally."); return; }
                FpsCounterCleanup.ApplyRequestedAfterExit(Log);
                NativeLimiterCleanup.ApplyRequestedAfterExit(Log);
                RelaunchDirect(gameBin, waitPid);
            }
            catch (Exception ex) { Log("Safe settings restart failed: " + ex); }
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
                    throw new IOException("Recovery is incomplete: manifest retained for modified/foreign assets. See first-run.log.");
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
                if (!OptiInstaller.HasManagedInstall(gameBin) ||
                    !File.Exists(Path.Combine(gameBin, "OptiScaler.ini")) ||
                    !File.Exists(Path.Combine(gameBin, "dxgi.dll")))
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
                    throw new IOException("Uninstall incomplete: modified or foreign assets remain. Manifest retained; inspect first-run.log.");
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
                throw; // Propagate failure to maintenance-host.log and CMD exit code.
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
                            File.Exists(Path.Combine(gameBin, "dxgi.dll")))
                            Log("Complete BannerlordFG installation detected; no replacement attempted.");
                        else
                        {
                            string blocked = "Existing manifest is not safe to repair automatically: " + recoveryReason;
                            Log(blocked);
                            WriteAtomic(StatePath(gameBin, ".blocked"), blocked);
                        }
                        return;
                    }
                    Log("Verified incomplete manifest found; downloading before safe post-exit recovery.");
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
                Log("Detecting display GPU through Windows WMI...");
                string gpu = DetectGpu();
                Log("GPU automatically detected: " + gpu);
                package = Task.Run(() => OptiInstaller.PrepareOfficialPackageAsync(Log))
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
                // install or restart despite the previously verified package.
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
                Log("First-run installation complete. Clearing worker state before direct restart.");
                installCompleted = true;
            }
            catch (Exception ex)
            {
                Log("Automatic installation did not complete: " + ex);
                if (gameBin != null)
                    try { WriteAtomic(StatePath(gameBin, ".blocked"), ex.Message); } catch { }
                // No restart on failure, and no repeated popup or install loop.
            }
            finally
            {
                if (package != null) package.Dispose();
                if (ready != null) try { File.Delete(ready); } catch (Exception ex) { Log("Ready marker cleanup: " + ex.Message); }
                if (pending != null) try { File.Delete(pending); } catch (Exception ex) { Log("Pending marker cleanup: " + ex.Message); }
                markersCleared = (ready == null || !File.Exists(ready)) &&
                    (pending == null || !File.Exists(pending));
                // Earlier exits (e.g. ownership conflict, cancellation) also
                // pass through finally but skip the code after this try block.
                // Retire one-shot restart authorization in those cases here.
                if (gameBin != null && !installCompleted)
                    try { File.Delete(StatePath(gameBin, ".restart")); } catch { }
                if (lockTaken && mutex != null) mutex.ReleaseMutex();
                if (mutex != null) mutex.Dispose();
            }
            // Only relaunch after the helper has disposed its package, removed
            // .pending/.ready and released the install mutex. This prevents a
            // fresh game from treating the old successful install as pending.
            if (installCompleted && gameBin != null)
            {
                if (markersCleared)
                {
                    Log("Install markers removed and mutex released; relaunching Bannerlord.");
                    RelaunchDirect(gameBin, waitPid);
                }
                else Log("Install completed, but cleanup markers remain. Relaunch deferred; inspect first-run.log before starting the game manually.");
            }
            if (gameBin != null)
                try { File.Delete(StatePath(gameBin, ".restart")); } catch { }
        }
    }
}

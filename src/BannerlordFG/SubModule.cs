// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Threading;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using BannerlordFG.Core;

namespace BannerlordFG
{
    /// <summary>
    /// First-run bootstrap and native Performance interface. Game shutdown uses the engine quit API only.
    /// No manual shortcut, task termination, launcher arguments, or MCM dependency.
    /// </summary>
    public sealed class SubModule : MBSubModuleBase
    {
        private FgController _controller;
        private float _guardElapsed;
        private bool _guardFailed;
        private string _gameBin;
        private string _helper;
        private int _pid;
        // Keep the process handle until the first-run worker confirms startup
        // or exits. A PID alone can be reused before the watchdog polls.
        private Process _installCmdProcess;
        // Separate retained handle: uninstall must not close the game unless
        // its own maintenance CMD is still running and has acknowledged it.
        private Process _uninstallCmdProcess;
        private bool _enteredGameplay;
        private bool _requestedExit;
        private bool _awaitingUninstallAck;
        private float _uninstallAckElapsed;
        private DateTime _uninstallAckDeadline;
        private bool _noticeVisible;
        private string _lastProgressNotice;
        private bool _manualInstallRequested;
        // Auto-close from an active campaign only after explicit consent.
        // Installation never auto-restarts the game.
        private bool _autoRestartInstallApproved;
        private bool _beginApprovedInstallOnNextTick;
        private bool _monitorInstallAtStartup;
        private bool _readyNoticeGiven;
        private bool _failureNoticeGiven;
        private float _pollElapsed;
        // Avoid filesystem access in hot UI hooks. A negative startup snapshot is
        // retried at most once every two seconds, because the elevated helper may
        // finish committing its manifest during the next manual launch.
        private bool _managedStateInitialized;
        private bool _cachedManagedInstall;
        private DateTime _nextManagedInstallProbeUtc;
        private float _managedInstallProbeElapsed;
        private string _cachedBinHash;
        private string _cachedManifestPath;
        private static int _diagnosticInstalled;
        private static int _diagnosticLogs;

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
            try
            {
                RegisterExceptionDiagnostics();
                _gameBin = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                _pid = Process.GetCurrentProcess().Id;
                _helper = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "BannerlordFG.Helper.cmd");
                _controller = new FgController(_gameBin);
                // Do not reset the native graphics until the maintenance worker
                // has confirmed full removal of the owned OptiScaler files.
                if (File.Exists(State(".uninstalled")) &&
                    !File.Exists(Path.Combine(_gameBin, "OptiScaler.ini")) &&
                    // The player's ReShade dxgi.dll may legitimately remain.
                    // Successful uninstall is identified by the removed manifest.
                    !File.Exists(StateManifest()))
                {
                    string cleanup;
                    if (_controller.TryCompleteUninstall(out cleanup))
                    {
                        File.Delete(State(".uninstalled"));
                        Log(cleanup);
                    }
                    else Log(cleanup);
                }
                string restored;
                if (_controller.TryRestoreVanillaAfterFgStopped(out restored)) Log(restored);
                else if (!string.IsNullOrWhiteSpace(restored)) Log(restored);
                // v0.3.12 briefly exposed an unsupported uncapped sentinel. If its marker
                // exists after a crash, restore the previous finite 30..360 value once and
                // remove the marker. v0.3.14 never exposes Unlimited again.
                NativeUnlimitedLimiter.RestoreFiniteAndRetire();
                string next = State(".enable-next-launch");
                if (File.Exists(next))
                {
                    string result;
                    // The game's renderer may choose Performance automatically
                    // when OptiScaler first makes DLSS input available. If the
                    // player had None before clicking Install, override that
                    // automatic choice with the actual Quality preset once.
                    string previous = State(".native-upscaler-before-install");
                    bool useQuality = !File.Exists(previous) ||
                        File.ReadAllText(previous).Trim().Equals("none", StringComparison.OrdinalIgnoreCase);
                    if (_controller.TryEnable(out result, preferQualityOnFirstInstall: useQuality))
                    {
                        File.Delete(next);
                        try { File.Delete(previous); } catch { }
                    }
                    else Log("Post-install enable: " + result);
                }
                if (_controller.Settings.Enabled && IsManagedGraphicsInstalled())
                {
                    string status;
                    if (!_controller.TryApplySavedAtStartup(out status) && !string.IsNullOrEmpty(status)) Log(status);
                }
                else if (_controller.Settings.Enabled)
                    Log("Saved FG state ignored: a complete BannerlordFG-managed OptiScaler installation is missing.");
                // The option below controls OptiScaler's post-FG overlay, not the
                // vanilla Bannerlord counter. Prevent the confusing duplicate
                // (native base FPS) while retaining an uninstall-restorable backup.
                if (IsManagedGraphicsInstalled())
                {
                    try
                    {
                        var nativeCounter = new NativeFpsCounter();
                        if (nativeCounter.IsAvailable && nativeCounter.IsEnabled)
                        {
                            string result;
                            if (!nativeCounter.TrySet(false, out result)) Log(result);
                            else Log("Disabled duplicate native base-FPS counter (may require one launch). " + result);
                        }
                    }
                    catch (Exception ex) { Log("Native FPS duplicate suppression unavailable: " + ex.Message); }
                }
                // User-controlled diagnostic safe mode leaves the vanilla UI untouched.
                // Useful for distinguishing an Options UI crash from an OptiScaler/DXGI crash.
                if (File.Exists(Path.Combine(Path.GetDirectoryName(State(".disabled")), "native-ui-off.flag")))
                    Log("Native Performance UI disabled by diagnostic safe-mode flag.");
                else NativePerformancePatch.Start(_controller, IsManagedGraphicsInstalled,
                    BeginInstallFromOptions, BeginUninstall, PromptRestartForSetting, Log);
                // A deliberate uninstall is persistent. An existing third-party
                // OptiScaler installation is never silently taken over.
                // Upgrade recovery: v0.2.3 searched for ZIP although the release
                // only publishes a 7z. Re-attempt once using the now-pinned 7z.
                string oldBlock = State(".blocked");
                if (File.Exists(oldBlock) && File.ReadAllText(oldBlock).Contains("Cannot uniquely identify official ZIP"))
                {
                    File.Delete(oldBlock);
                    Log("Cleared v0.2.3 ZIP-only download failure; retrying with pinned 7z.");
                }
                // v0.3.14: installation is initiated only by the user. The old
                // automatic first-run worker competed with the Install button,
                // hid pending states and could trigger UAC without a user click.
                // Only resume polling an interrupted pre-install session. A
                // completed install is already snapshotted and requires no I/O.
                _monitorInstallAtStartup = !_cachedManagedInstall && File.Exists(State(".pending"));
                Log("Startup: OptiScaler installation is opt-in under Options > Performance.");
            }
            catch (Exception ex) { Log("Startup: " + ex); }
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            NativePerformancePatch.PumpPendingUi();
            // A quick manual relaunch can overlap the final manifest commit.
            // Never pin an initial "not installed" snapshot for the whole session;
            // poll cheaply on the normal game tick, not in Harmony's UI hooks.
            if (!_requestedExit && _managedStateInitialized && !_cachedManagedInstall)
            {
                _managedInstallProbeElapsed += dt;
                if (_managedInstallProbeElapsed >= 2f)
                {
                    _managedInstallProbeElapsed = 0f;
                    IsManagedGraphicsInstalled();
                }
            }
            // The confirmation dialog must finish closing before opening the
            // blocking download dialog; avoid nested TaleWorlds inquiries.
            if (_beginApprovedInstallOnNextTick)
            {
                _beginApprovedInstallOnNextTick = false;
                BeginInstall(true);
            }
            // Wait for an explicit acknowledgement from the elevated worker.
            // A UAC cancellation or failed helper must not switch FG off.
            if (_awaitingUninstallAck)
            {
                // UAC acknowledgement may take seconds. No need to read its
                // marker files 60-240 times per second while awaiting it.
                _uninstallAckElapsed += dt;
                if (_uninstallAckElapsed >= 0.25f)
                {
                    _uninstallAckElapsed = 0f;
                    PollUninstallAcknowledgement();
                }
            }
            // A user may click Install from within a campaign. The worker can
            // download then, but must wait until the player exits normally to
            // install graphics DLLs. Continue reporting its status in gameplay.
            if (!_requestedExit && !string.IsNullOrEmpty(_gameBin) &&
                (_manualInstallRequested || _noticeVisible || _monitorInstallAtStartup))
            {
                _pollElapsed += dt;
                if (_pollElapsed >= 0.5f)
                {
                    _pollElapsed = 0;
                    CheckWorker();
                    RefreshSetupProgress();
                }
            }
            if (_guardFailed || _controller == null || !_controller.NativeDlssManaged ||
                !IsManagedGraphicsInstalled()) return;
            _guardElapsed += dt;
            if (_guardElapsed < 2f) return;
            _guardElapsed = 0;
            try { _controller.MaintainVanillaSettings(); }
            catch (Exception ex) { _guardFailed = true; Log("Graphics guard disabled: " + ex.Message); }
        }

        protected override void OnGameStart(TaleWorlds.Core.Game game, TaleWorlds.Core.IGameStarter starter)
        {
            _enteredGameplay = true;
            if (_noticeVisible)
            {
                // Another campaign began while a download was in progress.
                // The original approval no longer protects unsaved progress.
                _autoRestartInstallApproved = false;
                try { InformationManager.HideInquiry(); _noticeVisible = false; } catch { }
                Log("Campaign changed during OptiScaler download; automatic exit requires renewed confirmation.");
            }
            base.OnGameStart(game, starter);
        }

        private static void RegisterExceptionDiagnostics()
        {
            if (Interlocked.Exchange(ref _diagnosticInstalled, 1) != 0) return;
            AppDomain.CurrentDomain.FirstChanceException += (sender, e) =>
            {
                try
                {
                    if (Volatile.Read(ref _diagnosticLogs) >= 12) return;
                    var ex = e.Exception;
                    string stack = ex.StackTrace ?? string.Empty;
                    if (stack.IndexOf("GameOptions", StringComparison.OrdinalIgnoreCase) < 0 &&
                        stack.IndexOf("NativePerformancePatch", StringComparison.OrdinalIgnoreCase) < 0 &&
                        stack.IndexOf("BannerlordFG", StringComparison.OrdinalIgnoreCase) < 0) return;
                    if (Interlocked.Increment(ref _diagnosticLogs) <= 12)
                        Log("OPTIONS FIRST-CHANCE exception: " + ex);
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                try { Log("UNHANDLED managed exception: " + e.ExceptionObject); } catch { }
            };
        }

        private void CaptureCurrentLaunch()
        {
            // Steam often starts TaleWorlds.MountAndBlade.Launcher.exe and this
            // Bannerlord build exposes THAT name from MainModule, not Bannerlord.exe.
            // Capture an explicit, validated direct-game command using the ordered
            // active module IDs. Never restart vanilla and silently drop other mods.
            try
            {
                string actualExe = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
                string bin = Path.GetDirectoryName(actualExe);
                string name = Path.GetFileName(actualExe);
                string directExe = Path.Combine(bin, "Bannerlord.exe");
                if (!File.Exists(directExe))
                    directExe = Path.Combine(bin, "Bannerlord.Native.exe");
                if (!File.Exists(directExe))
                    throw new FileNotFoundException("Direct Bannerlord game executable is missing in " + bin);

                string[] arguments = Environment.GetCommandLineArgs();
                var gameArgs = new List<string>();
                var explicitModules = arguments.Skip(1)
                    .Where(x => x.StartsWith("_MODULES_*", StringComparison.Ordinal) &&
                        x.EndsWith("*_MODULES_", StringComparison.Ordinal)).ToArray();
                if (explicitModules.Length == 1)
                {
                    // A Steam-launched game may still report the *launcher* name
                    // as MainModule. The _MODULES_ command line is authoritative
                    // regardless of that misleading process filename.
                    string[] ids = explicitModules[0].Substring(10,
                        explicitModules[0].Length - 20).Split(new[] { '*' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!ids.Contains("Native", StringComparer.OrdinalIgnoreCase) ||
                        !ids.Contains("BannerlordFG", StringComparer.OrdinalIgnoreCase) ||
                        ids.Any(id => !ValidModuleId(id)))
                        throw new InvalidDataException("Existing _MODULES_ argument does not include Native and BannerlordFG.");
                    if (name.Equals("Bannerlord.exe", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Bannerlord.Native.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        directExe = actualExe;
                        gameArgs.AddRange(arguments.Skip(1));
                    }
                    else
                    {
                        gameArgs.Add("/singleplayer");
                        gameArgs.Add(explicitModules[0]);
                        if (arguments.Skip(1).Any(x => x.Equals("/anticheat", StringComparison.OrdinalIgnoreCase)))
                            gameArgs.Add("/anticheat");
                    }
                    Log("Direct restart: reused exact original module argument from host " + name + ".");
                }
                else
                {
                    var moduleIds = ReadActiveModuleIds();
                    if (moduleIds.Count == 0)
                    {
                        Log("Engine module API unavailable; checking saved launcher mod order.");
                        moduleIds = ReadSelectedLauncherModules();
                    }
                    if (!moduleIds.Contains("Native", StringComparer.OrdinalIgnoreCase) ||
                        !moduleIds.Contains("BannerlordFG", StringComparer.OrdinalIgnoreCase))
                        throw new InvalidDataException("Active module list incomplete; refusing direct restart without the player's mods.");
                    gameArgs.Add("/singleplayer");
                    gameArgs.Add("_MODULES_*" + string.Join("*", moduleIds) + "*_MODULES_");
                    Log("Direct restart: using " + moduleIds.Count + " ordered modules, game EXE: " +
                        Path.GetFileName(directExe) + " (original process: " + name + ").");
                }
                string launch = State(".launch");
                string tmp = launch + ".tmp";
                var lines = new[] { directExe }.Concat(gameArgs).ToArray();
                if (lines.Any(x => x.IndexOfAny(new[] { '\r', '\n' }) >= 0))
                    throw new InvalidDataException("Unexpected newline in game launch argument.");
                File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
                if (File.Exists(launch)) File.Delete(launch);
                File.Move(tmp, launch);
                Log("Direct restart command captured successfully.");
            }
            catch (Exception ex)
            {
                // Do not reuse a stale command captured on an earlier session.
                try { File.Delete(State(".launch")); } catch { }
                Log("Direct restart unavailable: " + ex);
            }
        }

        private static bool ValidModuleId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128) return false;
            return id.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.');
        }

        private static List<string> ReadActiveModuleIds()
        {
            // The user's uploaded MountAndBlade DLL contains GetActiveModules;
            // the engine's GetModulesNames method is another possible entry.
            // Reflection avoids depending on their signatures at compile time.
            foreach (var candidate in new[] {
                new { Owner = typeof(TaleWorlds.Engine.Utilities), Method = "GetModulesNames" },
                new { Owner = typeof(MBSubModuleBase).Assembly.GetType("TaleWorlds.MountAndBlade.Module"),
                      Method = "GetActiveModules" }
            })
            {
                try
                {
                    if (candidate.Owner == null) continue;
                    var method = candidate.Owner.GetMethod(candidate.Method,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null, Type.EmptyTypes, null);
                    if (method == null) continue;
                    var values = method.Invoke(null, null) as IEnumerable;
                    if (values == null) continue;
                    var ids = new List<string>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (object item in values)
                    {
                        string id = GetModuleId(item);
                        if (!ValidModuleId(id) || !seen.Add(id))
                            throw new InvalidDataException("Invalid/duplicate ID from " + candidate.Method);
                        ids.Add(id);
                        if (ids.Count > 128) throw new InvalidDataException("Too many active module IDs.");
                    }
                    if (ids.Count > 0)
                    {
                        Log("Captured " + ids.Count + " modules using " + candidate.Method + ".");
                        if (ids.Contains("Native", StringComparer.OrdinalIgnoreCase) &&
                            ids.Contains("BannerlordFG", StringComparer.OrdinalIgnoreCase)) return ids;
                    }
                }
                catch (Exception ex) { Log("Module list " + candidate.Method + " failed: " + ex.Message); }
            }
            return new List<string>();
        }

        private static string GetModuleId(object item)
        {
            if (item is string literal) return literal;
            if (item == null) return null;
            object source = item;
            // Module.GetActiveModules may return Module objects rather than IDs.
            foreach (string name in new[] { "ModuleInfo", "Info" })
            {
                var prop = source.GetType().GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop == null) continue;
                var nested = prop.GetValue(source, null);
                if (nested != null) { source = nested; break; }
            }
            foreach (string name in new[] { "Id", "ModuleId", "IdString" })
            {
                var prop = source.GetType().GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null)
                {
                    var value = prop.GetValue(source, null) as string;
                    if (ValidModuleId(value)) return value;
                }
                var field = source.GetType().GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    var value = field.GetValue(source) as string;
                    if (ValidModuleId(value)) return value;
                }
            }
            return null;
        }

        private static List<string> ReadSelectedLauncherModules()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Mount and Blade II Bannerlord", "Configs", "LauncherData.xml");
            if (!File.Exists(path)) return new List<string>();
            var xml = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                xml.Load(reader);
            var nodes = xml.SelectNodes("/UserData/SingleplayerData/ModDatas/UserModData");
            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (nodes == null) return ids;
            foreach (XmlNode node in nodes)
            {
                if (!string.Equals(node.SelectSingleNode("IsSelected")?.InnerText.Trim(), "true",
                    StringComparison.OrdinalIgnoreCase)) continue;
                string id = node.SelectSingleNode("Id")?.InnerText.Trim();
                if (!ValidModuleId(id) || !seen.Add(id))
                    throw new InvalidDataException("Invalid/duplicate launcher module ID.");
                ids.Add(id);
            }
            return ids;
        }

        private static string FormatSetupProgress(string raw)
        {
            const string footer = "\n\nOnce verified, Bannerlord closes normally. Installation finishes after exit. " +
                "Open the game yourself afterwards. You can cancel the installation.";
            if (string.IsNullOrEmpty(raw))
                return "Starting the installer. Waiting for Windows administrator approval..." + footer;
            string[] parts = raw.Split('|');
            int percent;
            long received;
            long total;
            if (parts.Length != 4 || raw.Length > 128 ||
                !int.TryParse(parts[1], out percent) ||
                !long.TryParse(parts[2], out received) ||
                !long.TryParse(parts[3], out total) ||
                received < 0 || total < 0 || percent < -1 || percent > 100)
                return "Preparing OptiScaler. Check BannerlordFG.log if this takes too long." + footer;
            if (parts[0] == "preparing") return "Checking the game and GPU..." + footer;
            if (parts[0] == "connecting") return "Connecting to GitHub..." + footer;
            if (parts[0] == "verifying")
                return "Download complete. Verifying the official SHA-256 checksum..." + footer;
            if (parts[0] == "extracting")
                return "Download verified. Preparing OptiScaler files before closing the game..." + footer;
            if (parts[0] == "verified")
                return "OptiScaler downloaded and verified. Closing the game safely..." + footer;
            if (parts[0] != "downloading")
                return "Preparing OptiScaler..." + footer;
            if (percent >= 0)
            {
                int filled = Math.Min(20, percent / 5);
                string bar = "[" + new string('#', filled) +
                    new string('-', 20 - filled) + "]";
                return "Downloading OptiScaler: " + percent + "%\n" + bar + footer;
            }
            return "Downloading OptiScaler: " + (received / (1024L * 1024L)) +
                " MB received (total size not provided)." + footer;
        }

        private void ShowSetupNotice()
        {
            if (_noticeVisible) return;
            try
            {
                _lastProgressNotice = null;
                InformationManager.ShowInquiry(new InquiryData(
                    "Installing OptiScaler",
                    FormatSetupProgress(null),
                    false, true, "", "Cancel", null, () => CancelAutomaticSetup()), false, true);
                _noticeVisible = true;
            }
            catch (Exception ex)
            {
                if (_enteredGameplay) _autoRestartInstallApproved = false;
                Log("Could not display setup inquiry: " + ex.Message);
                try { InformationManager.DisplayMessage(new InformationMessage("BannerlordFG: downloading OptiScaler.")); } catch { }
            }
        }

        private void RefreshSetupProgress()
        {
            // Read at most once each 500 ms during a pending installation.
            // Reopen the standard inquiry only when a 10% download milestone
            // or phase changes, so no custom UI hooks or per-frame updates.
            if (!_noticeVisible) return;
            string raw = null;
            try
            {
                string path = State(".progress");
                if (!File.Exists(path)) return;
                var info = new FileInfo(path);
                if (info.Length > 128 || info.Length == 0) return;
                raw = File.ReadAllText(path);
            }
            catch (IOException) { return; } // Atomic replacement race.
            catch (UnauthorizedAccessException) { return; }
            if (raw == null || raw == _lastProgressNotice) return;
            string display = FormatSetupProgress(raw);
            try
            {
                // InquiryData isn't an observable Gauntlet ViewModel.
                // A new standard inquiry reliably renders the updated text.
                InformationManager.HideInquiry();
                _noticeVisible = false;
                InformationManager.ShowInquiry(new InquiryData(
                    "Installing OptiScaler", display,
                    false, true, "", "Cancel", null, () => CancelAutomaticSetup()), false, true);
                _noticeVisible = true;
                _lastProgressNotice = raw;
            }
            catch (Exception ex)
            {
                Log("OptiScaler progress UI unavailable: " + ex.Message);
                // Never block the maintenance worker because of a UI error.
                if (!_noticeVisible) ShowSetupNotice();
            }
        }

        private void ShowInstallFeedback(string title, string explanation)
        {
            Log(title + ": " + explanation);
            try
            {
                InformationManager.ShowInquiry(new InquiryData(title, explanation,
                    true, false, "OK", "", null, null), false, true);
            }
            catch (Exception ex)
            {
                Log("Install status dialog unavailable: " + ex.Message);
                try { InformationManager.DisplayMessage(new InformationMessage("BannerlordFG: " + title)); }
                catch { }
            }
        }

        private string ExistingInstallBlocker()
        {
            if (IsManagedGraphicsInstalled()) return "OptiScaler is already installed. Restart Bannerlord to refresh the Performance menu.";
            if (File.Exists(StateManifest()))
            {
                // The elevated helper verifies EVERY owned SHA256 before any
                // cleanup. An interrupted manifest may therefore be recovered
                // on an explicit Install click after the game exits. A complete
                // or malformed manifest must not be taken over automatically.
                try
                {
                    string previous = File.ReadAllText(StateManifest());
                    if (previous.Contains("\"installedFiles\"") &&
                        previous.Contains("\"archiveSha256\"") &&
                        (!previous.Contains("\"stage\":\"complete\"") ||
                         !previous.Contains("\"postinstallFiles\"") ||
                         !File.Exists(Path.Combine(_gameBin, ProxyFromManifestText(previous))) ||
                         !File.Exists(Path.Combine(_gameBin, "OptiScaler.ini"))))
                    {
                        Log("Incomplete manifest detected. Delegating read-only ownership preflight and post-exit recovery to the maintenance DLL.");
                        return null;
                    }
                }
                catch (Exception ex) { Log("Incomplete manifest preflight failed: " + ex.Message); }
                return "An existing BannerlordFG manifest cannot be safely classified for automatic recovery. No graphics files will be overwritten. Inspect BannerlordFG.log and the manifest.";
            }
            if (File.Exists(Path.Combine(_gameBin, "OptiScaler.ini")))
                return "OptiScaler.ini already exists, but there is no complete BannerlordFG-managed installation. This may be an external or incomplete installation. Back up and resolve it first; the installer will not overwrite its files.";
            if (Directory.Exists(Path.Combine(_gameBin, "OptiScaler")))
                return "An existing OptiScaler directory was detected. Resolve the previous installation before retrying; its contents have not been changed.";
            // Prefer winmm.dll; use dxgi.dll only when winmm.dll is already
            // occupied by another mod. ReShade's DXGI file is left untouched.
            bool dxgiExists = File.Exists(Path.Combine(_gameBin, "dxgi.dll"));
            bool winmmExists = File.Exists(Path.Combine(_gameBin, "winmm.dll"));
            if (dxgiExists && winmmExists)
                return "Both winmm.dll and dxgi.dll exist. The installer cannot safely select a proxy.";
            // Keep this lightweight UI preflight aligned with the helper:
            // d3d11.dll is external and never a BannerlordFG proxy target.
            // The helper separately checks for unmanaged OptiScaler and exact
            // file collisions after the official package is downloaded.
            foreach (string proxy in new[] { "version.dll", "d3d12.dll", "wininet.dll", "winhttp.dll" })
            {
                if (File.Exists(Path.Combine(_gameBin, proxy)))
                    return "Another graphics-hook DLL already exists: " + proxy + ". No external file will be overwritten.";
            }
            return null;
        }

        private static string ProxyFromManifestText(string text)
        {
            // New manifests record the proxy explicitly. Old v1.0.4 manifests
            // predate this field and always used dxgi.dll.
            const string key = "\"proxyFileName\":\"";
            int index = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return "dxgi.dll"; // Legacy manifest.
            int start = index + key.Length;
            int end = text.IndexOf('"', start);
            if (end < 0) return null;
            string value = text.Substring(start, end - start);
            if (value.Equals("winmm.dll", StringComparison.OrdinalIgnoreCase)) return "winmm.dll";
            if (value.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase)) return "dxgi.dll";
            return null; // Unknown ownership; stop safely.
        }

        private void CloseNotice()
        {
            _lastProgressNotice = null;
            if (!_noticeVisible) return;
            try { InformationManager.HideInquiry(); } catch (Exception ex) { Log("Hide inquiry: " + ex.Message); }
            _noticeVisible = false;
        }

        private void CloseAfterVerifiedDownload()
        {
            if (_requestedExit) return;
            try
            {
                // The elevated worker installs graphics files ONLY after the
                // verified package is ready and Bannerlord has shut down.
                string ready = State(".ready");
                if (!File.Exists(ready) || File.ReadAllText(ready).Trim() != _pid.ToString() ||
                    File.Exists(State(".blocked")) || File.Exists(State(".disabled")))
                {
                    ShowInstallFeedback("OptiScaler setup", "The verified download is no longer ready. Check BannerlordFG.log before retrying.");
                    return;
                }
                Log("Verified download ready. Closing Bannerlord normally. The player must launch it manually after installation.");
                _requestedExit = true;
                TaleWorlds.Engine.Utilities.QuitGame();
            }
            catch (Exception ex)
            {
                _requestedExit = false;
                Log("Verified-download shutdown failed: " + ex);
                ShowInstallFeedback("OptiScaler shutdown failed", ex.Message);
            }
        }

        private void ConfirmCloseAfterDownload()
        {
            try
            {
                InformationManager.ShowInquiry(new InquiryData(
                    "OptiScaler download complete",
                    "OptiScaler was downloaded and verified. Save your campaign before proceeding. Close Bannerlord now to finish installing? You must open it yourself afterwards.",
                    true, true, "Close game", "Later",
                    () => CloseAfterVerifiedDownload(),
                    () => Log("Player postponed OptiScaler installation; it will finish after a normal game exit.")),
                    false, true);
            }
            catch (Exception ex)
            {
                Log("Shutdown confirmation unavailable: " + ex);
                ShowInstallFeedback("OptiScaler ready",
                    "Download verified. Save and quit Bannerlord normally. The installer finishes after exit; open it again yourself.");
            }
        }

        private void CheckWorker()
        {
            try
            {
                if (File.Exists(State(".disabled"))) return;
                string ready = State(".ready");
                if (File.Exists(ready) && File.ReadAllText(ready).Trim() == _pid.ToString())
                {
                    if (_enteredGameplay && (!_autoRestartInstallApproved || !_noticeVisible))
                    {
                        // Gameplay may have started after the original click.
                        // Ask for a normal shutdown after the verified download.
                        // Never relaunch Bannerlord automatically.
                        if (!_readyNoticeGiven)
                        {
                            _readyNoticeGiven = true;
                            _manualInstallRequested = false;
                            _monitorInstallAtStartup = false;
                            CloseNotice();
                            ConfirmCloseAfterDownload();
                        }
                        return;
                    }
                    CloseNotice();
                    _manualInstallRequested = false;
                    _monitorInstallAtStartup = false;
                    _autoRestartInstallApproved = false;
                    CloseAfterVerifiedDownload();
                    return;
                }
                string blocked = State(".blocked");
                // Detect a CMD that exits before its PowerShell worker produces
                // a result. This is independent of UAC visibility and avoids the
                // generic two-minute timeout for missing files or bad arguments.
                CheckInstallCmdExit(ready, blocked);
                string pending = State(".pending");
                if ((_noticeVisible || _manualInstallRequested) && !File.Exists(blocked) &&
                    File.Exists(pending) && !File.Exists(ready) &&
                    (DateTime.UtcNow - File.GetLastWriteTimeUtc(pending)).TotalSeconds > 120 &&
                    !MaintenanceWorkerIsActive())
                {
                    // The UAC prompt may be slow, so allow two minutes. Never
                    // leave a progress dialog open forever after cmd.exe exits
                    // before starting the DLL. A late helper observes .disabled.
                    string stage = File.Exists(State(".helper-started"))
                        ? "The CMD helper started, but no maintenance worker is active."
                        : "The elevated CMD helper did not confirm startup.";
                    File.WriteAllText(State(".disabled"), "Maintenance startup timed out.");
                    File.WriteAllText(blocked, stage +
                        " Review BannerlordFG.log and Windows Defender Protection History.");
                    Log("Maintenance watchdog: " + stage);
                }
                if ((_noticeVisible || _manualInstallRequested || _monitorInstallAtStartup) &&
                    !_failureNoticeGiven && File.Exists(blocked))
                {
                    _failureNoticeGiven = true;
                    _manualInstallRequested = false;
                    _monitorInstallAtStartup = false;
                    _autoRestartInstallApproved = false;
                    CloseNotice();
                    string explanation = File.ReadAllText(blocked);
                    ShowInstallFeedback("OptiScaler installation failed",
                        explanation + "\n\nThe game remains open. See BannerlordFG.log for details.");
                }
                if (_monitorInstallAtStartup && !File.Exists(pending) && !File.Exists(ready))
                    _monitorInstallAtStartup = false;
            }
            catch (Exception ex) { Log("Worker status: " + ex.Message); }
        }

        private void CheckInstallCmdExit(string ready, string blocked)
        {
            var process = _installCmdProcess;
            if (process == null) return;
            try
            {
                if (!process.HasExited) return;
                int exitCode = process.ExitCode;
                process.Dispose();
                _installCmdProcess = null;
                Log("OptiScaler maintenance cmd.exe exited with code " + exitCode + ".");
                // CMD itself writes .blocked for known failures. Never overwrite
                // a worker's more specific explanation, nor a verified result.
                if (File.Exists(blocked) || File.Exists(ready) ||
                    File.Exists(State(".disabled")) || _requestedExit) return;
                string phase = File.Exists(State(".helper-started"))
                    ? "Maintenance CMD started, but setup ended without confirmation."
                    : "Maintenance CMD exited before confirming script startup.";
                string reason = phase + " CMD exit code: " + exitCode +
                    ". Check BannerlordFG.log in %LOCALAPPDATA%\\BannerlordFG.";
                File.WriteAllText(blocked, reason);
                File.WriteAllText(State(".disabled"), "Maintenance CMD exited without a worker result.");
                Log("Maintenance CMD early exit: " + reason);
            }
            catch (Exception ex) { Log("Cannot inspect maintenance CMD exit: " + ex.Message); }
        }

        private void CancelAutomaticSetup()
        {
            try
            {
                // Worker honours .disabled before touching graphics DLLs.
                File.WriteAllText(State(".disabled"), "First-run cancelled " + DateTime.UtcNow.ToString("o"));
                CloseNotice();
                _manualInstallRequested = false;
                _monitorInstallAtStartup = false;
                _autoRestartInstallApproved = false;
                Log("Player cancelled first-run setup; explicit Install remains available in Options > Performance.");
            }
            catch (Exception ex) { Log("Cancel setup: " + ex.Message); }
        }

        private void BeginInstallFromOptions()
        {
            Log("OptiScaler Install clicked from native Performance menu.");
            if (_requestedExit)
            {
                ShowInstallFeedback("OptiScaler setup", "Bannerlord is already closing. Start the game again if installation does not complete.");
                return;
            }
            try
            {
                if (_enteredGameplay && !Recent(State(".pending")) &&
                    !IsManagedGraphicsInstalled())
                {
                    // OnGameStart stays true after returning to the main menu.
                    // Once the player confirms, keep the progress popup open so
                    // there is no gameplay between consent and normal shutdown.
                    InformationManager.ShowInquiry(new InquiryData(
                        "Install OptiScaler",
                        "Save your campaign before continuing. Bannerlord will download OptiScaler, close after verification, and finish installing. You must open the game again yourself. Continue?",
                        true, true, "Install and close", "Cancel",
                        () => { _autoRestartInstallApproved = true; _beginApprovedInstallOnNextTick = true; },
                        () => { _autoRestartInstallApproved = false; Log("Player cancelled OptiScaler install and shutdown."); }),
                        false, true);
                    return;
                }
                _autoRestartInstallApproved = false;
                BeginInstall(true);
            }
            catch (Exception ex)
            {
                _autoRestartInstallApproved = false;
                _beginApprovedInstallOnNextTick = false;
                ShowInstallFeedback("OptiScaler installation failed", ex.Message);
                Log("Options > Install failed: " + ex);
            }
        }

        private void PromptRestartForSetting()
        {
            if (_requestedExit) return;
            if (_enteredGameplay)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "BannerlordFG: saved. Restart the game normally to apply."));
                Log("Settings changed during gameplay; normal manual restart required.");
                return;
            }
            try
            {
                InformationManager.ShowInquiry(new InquiryData(
                    "Apply BannerlordFG settings",
                    "This change requires reopening Bannerlord. Close the game now, then start it yourself?",
                    true, true, "Close game", "Later",
                    () => RestartNormallyForSettings(),
                    () => Log("Restart postponed by player; settings are saved.")), false, true);
            }
            catch (Exception ex) { Log("Restart prompt unavailable: " + ex.Message); }
        }

        private void RestartNormallyForSettings()
        {
            if (_requestedExit || _enteredGameplay) return;
            try
            {
                // Apply deferred native settings only after Bannerlord closes.
                // This helper does not attempt to launch the game afterwards.
                if (!StartWorker("--post-exit-settings"))
                    throw new IOException("Failed to start settings cleanup helper.");
                _requestedExit = true;
                Log("Settings updated: requesting native QuitGame. Reopen Bannerlord manually.");
                TaleWorlds.Engine.Utilities.QuitGame();
            }
            catch (Exception ex)
            {
                _requestedExit = false;
                Log("FG settings shutdown deferred: " + ex);
            }
        }

        private bool StartWorker(string flags)
        {
            if (string.IsNullOrEmpty(_helper) || !File.Exists(_helper))
            {
                Log("Missing BannerlordFG.Helper.cmd; rebuild the DLL and CMD package.");
                return false;
            }
            bool modifiesGraphicsFiles = flags == "--auto-first-run" ||
                flags == "--post-exit-uninstall";
            // Bannerlord is typically started unelevated by Steam, while its
            // bin directory is under Program Files. Request UAC for the separate
            // installer only. Merely compiling the module as Administrator does
            // NOT give the game process permission to write those files.
            // Elevate cmd.exe itself. ShellExecute/runas on a .cmd association
            // can return a process without ever entering our helper script.
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            string args = "/D /S /C \"\"" + _helper.Replace("\"", "") + "\" " + flags +
                " --gamebin \"" + _gameBin.Replace("\"", "") + "\" --wait-pid " + _pid +
                " --state-base \"" + State("").Replace("\"", "") + "\"" +
                " --user-documents \"" + Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments).Replace("\"", "") + "\"\"";
            var psi = new ProcessStartInfo {
                FileName = cmd, Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(_helper),
                UseShellExecute = true,
                CreateNoWindow = !modifiesGraphicsFiles,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (modifiesGraphicsFiles) psi.Verb = "runas";
            Log("Launching maintenance cmd.exe (operation=" + flags +
                ", elevated=" + modifiesGraphicsFiles + ").");
            var process = Process.Start(psi);
            if (process == null) return false;
            try
            {
                Log("Maintenance cmd.exe launched (PID=" + process.Id + ").");
                if (flags == "--auto-first-run")
                {
                    // Retaining the handle permits a reliable HasExited/ExitCode
                    // check without polling an unrelated process with a reused PID.
                    if (_installCmdProcess != null) _installCmdProcess.Dispose();
                    _installCmdProcess = process;
                    process = null;
                }
                else if (flags == "--post-exit-uninstall")
                {
                    if (_uninstallCmdProcess != null) _uninstallCmdProcess.Dispose();
                    _uninstallCmdProcess = process;
                    process = null;
                }
                return true;
            }
            finally
            {
                if (process != null) process.Dispose();
            }
        }

        private bool MaintenanceWorkerIsActive()
        {
            string pending = State(".pending");
            string mutexName = "Local\\BannerlordFG-auto-" + Path.GetFileNameWithoutExtension(pending);
            Mutex mutex = null;
            try
            {
                if (!Mutex.TryOpenExisting(mutexName, out mutex)) return false;
                bool acquired = false;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (acquired) mutex.ReleaseMutex();
                return !acquired;
            }
            catch (UnauthorizedAccessException) { return true; } // Elevated worker owns it.
            finally { if (mutex != null) mutex.Dispose(); }
        }

        private void BeginInstall(bool explicitlyRequested = false)
        {
            if (_requestedExit || string.IsNullOrEmpty(_gameBin)) return;
            try
            {
                if (Recent(State(".pending")))
                {
                    // Do not mistake an active Windows PowerShell-hosted worker
                    // for an orphaned marker. Inspect the install-specific mutex;
                    // a generic powershell.exe process is not proof of ownership.
                    if (File.Exists(State(".ready")) || MaintenanceWorkerIsActive())
                    {
                        if (explicitlyRequested) ShowSetupNotice();
                        Log("OptiScaler installer already running; no second download started.");
                        return;
                    }
                    if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(State(".pending"))).TotalMinutes < 2)
                    {
                        if (explicitlyRequested) ShowInstallFeedback("OptiScaler setup", "A previous installation may still be downloading. Wait two minutes before retrying.");
                        return;
                    }
                    File.Delete(State(".pending"));
                    Log("Recovered stale pending marker from a previous setup attempt.");
                }
                string blocker = ExistingInstallBlocker();
                if (blocker != null)
                {
                    if (explicitlyRequested) ShowInstallFeedback("OptiScaler installation blocked", blocker);
                    else Log("Automatic OptiScaler installation skipped: " + blocker);
                    return;
                }
                if (string.IsNullOrEmpty(_helper) || !File.Exists(_helper))
                    throw new FileNotFoundException("BannerlordFG.Helper.cmd is missing from the installed module. Rebuild and reinstall BannerlordFG first.");
                foreach (var key in new[] { ".ready", ".restart", ".blocked", ".helper-started", ".progress" })
                    try { File.Delete(State(key)); } catch { }
                if (explicitlyRequested) try { File.Delete(State(".disabled")); } catch { }
                _readyNoticeGiven = false;
                _failureNoticeGiven = false;
                // Snapshot BEFORE the official proxy is installed. The native
                // renderer may replace None with Performance on the next run.
                // Saving its post-install value would lose the user's intent.
                File.WriteAllText(State(".native-upscaler-before-install"),
                    _controller != null && _controller.CurrentNativeDlss > 0f ? "active" : "none");
                File.WriteAllText(State(".pending"), DateTime.UtcNow.ToString("o"));
                if (explicitlyRequested)
                    InformationManager.DisplayMessage(new InformationMessage(
                        "BannerlordFG: approve the Windows administrator prompt if one appears."));
                if (!StartWorker("--auto-first-run")) throw new IOException("Could not start helper.");
                _manualInstallRequested = true;
                ShowSetupNotice();
                Log("OptiScaler setup started. Download and verification will finish before normal game shutdown.");
            }
            catch (Exception ex)
            {
                try { File.Delete(State(".pending")); } catch { }
                _manualInstallRequested = false;
                _autoRestartInstallApproved = false;
                CloseNotice(); Log("Begin installation failed: " + ex);
                if (explicitlyRequested) ShowInstallFeedback("OptiScaler installation failed", ex.Message);
            }
        }

        private void BeginUninstall()
        {
            Log("OptiScaler Uninstall clicked from native Performance menu.");
            // A freshly launched game may have cached the state while the
            // helper was completing. Revalidate once before acting on a click.
            if (!_cachedManagedInstall)
            {
                _nextManagedInstallProbeUtc = DateTime.MinValue;
                if (!IsManagedGraphicsInstalled())
                {
                    ShowInstallFeedback("OptiScaler uninstall",
                        "A complete managed installation has not been detected yet. Wait for setup to finish or check BannerlordFG.log.");
                    return;
                }
            }
            // Never silently ignore a click. A running campaign must be saved and
            // exited before the graphics injector can be removed safely.
            if (_requestedExit)
            {
                ShowInstallFeedback("OptiScaler uninstall", "Bannerlord is already closing. Wait for the uninstall to finish.");
                return;
            }
            if (Recent(State(".pending")))
            {
                // A previous installer used to start the game BEFORE clearing its
                // pending marker. A complete verified install is safe to uninstall
                // as soon as no install worker still holds the mutex.
                if (!IsManagedGraphicsInstalled() || MaintenanceWorkerIsActive())
                {
                    ShowInstallFeedback("OptiScaler uninstall",
                        "Installation is still finalizing. Wait a moment and retry.");
                    return;
                }
                try { File.Delete(State(".pending")); }
                catch (Exception ex)
                {
                    Log("Cannot clear completed installation marker: " + ex.Message);
                    ShowInstallFeedback("OptiScaler uninstall",
                        "The previous installer has not finished cleaning up. Wait a moment and retry.");
                    return;
                }
                Log("Cleared completed install marker; proceeding with uninstall.");
            }
            if (_enteredGameplay)
            {
                // This flag remains true even after leaving a campaign. Do not
                // silently reject the Uninstall click or require an unreachable
                // state; explicitly ask permission before closing the game.
                try
                {
                    InformationManager.ShowInquiry(new InquiryData(
                        "Uninstall OptiScaler",
                        "Save your campaign before continuing. Uninstall requires Bannerlord to close. Close the game and uninstall now?",
                        true, true, "Uninstall", "Cancel",
                        () => StartUninstallAfterConfirmation(),
                        () => Log("Player cancelled OptiScaler uninstall.")), false, true);
                }
                catch (Exception ex)
                {
                    Log("Uninstall confirmation unavailable: " + ex);
                    ShowInstallFeedback("OptiScaler uninstall", "Close Bannerlord; run UNINSTALL_WINDOWS.ps1. Follow RECOVERY_GUIDE.txt.");
                }
                return;
            }
            StartUninstallAfterConfirmation();
        }

        private void StartUninstallAfterConfirmation()
        {
            if (_requestedExit || _awaitingUninstallAck) return;
            try
            {
                foreach (var suffix in new[] { ".uninstall-ready", ".uninstall-failed" })
                    try { File.Delete(State(suffix)); } catch { }
                File.WriteAllText(State(".uninstall-pending"), _pid.ToString());
                if (!StartWorker("--post-exit-uninstall"))
                    throw new IOException("Maintenance CMD did not start. Check Windows UAC and BannerlordFG.log.");
                _awaitingUninstallAck = true;
                _uninstallAckElapsed = 0f;
                _uninstallAckDeadline = DateTime.UtcNow.AddMinutes(2);
                Log("Uninstall CMD launched; waiting for maintenance DLL ownership check before closing game.");
                InformationManager.DisplayMessage(new InformationMessage(
                    "BannerlordFG: approve UAC. The game will close after the uninstaller is ready."));
            }
            catch (Exception ex)
            {
                _awaitingUninstallAck = false;
                try { File.Delete(State(".uninstall-pending")); } catch { }
                Log("Uninstall not started, FG unchanged: " + ex);
                ShowInstallFeedback("OptiScaler uninstall failed", ex.Message);
            }
        }

        private void PollUninstallAcknowledgement()
        {
            if (!_awaitingUninstallAck || _requestedExit) return;
            try
            {
                string error = State(".uninstall-failed");
                if (File.Exists(error))
                {
                    string detail = File.ReadAllText(error);
                    _awaitingUninstallAck = false;
                    if (_uninstallCmdProcess != null)
                    {
                        _uninstallCmdProcess.Dispose();
                        _uninstallCmdProcess = null;
                    }
                    try { File.Delete(State(".uninstall-pending")); } catch { }
                    Log("Uninstall worker rejected request; leaving FG unchanged: " + detail);
                    ShowInstallFeedback("OptiScaler uninstall failed", detail +
                        "\n\nCheck BannerlordFG.log for details.");
                    return;
                }
                // A CMD that has already exited cannot perform post-exit
                // removal, even if an acknowledgement marker was written
                // earlier. Do not close the game on a stale .uninstall-ready.
                var uninstaller = _uninstallCmdProcess;
                if (uninstaller != null && uninstaller.HasExited)
                {
                    int exitCode = uninstaller.ExitCode;
                    uninstaller.Dispose();
                    _uninstallCmdProcess = null;
                    _awaitingUninstallAck = false;
                    try { File.Delete(State(".uninstall-pending")); } catch { }
                    string detail = "The uninstall CMD exited before the helper was ready " +
                        "(exit code " + exitCode + "). No game files were removed by this request. " +
                        "Check BannerlordFG.log.";
                    Log("Uninstall helper early exit: " + detail);
                    ShowInstallFeedback("OptiScaler uninstall failed", detail);
                    return;
                }
                string ready = State(".uninstall-ready");
                if (File.Exists(ready) && File.ReadAllText(ready).Trim() == _pid.ToString())
                {
                    _awaitingUninstallAck = false;
                    if (_uninstallCmdProcess != null)
                    {
                        _uninstallCmdProcess.Dispose();
                        _uninstallCmdProcess = null;
                    }
                    _requestedExit = true;
                    Log("Uninstall helper acknowledged verified ownership; closing Bannerlord normally.");
                    InformationManager.DisplayMessage(new InformationMessage(
                        "BannerlordFG: uninstaller ready; closing game to remove OptiScaler."));
                    TaleWorlds.Engine.Utilities.QuitGame();
                    return;
                }
                if (DateTime.UtcNow > _uninstallAckDeadline)
                {
                    _awaitingUninstallAck = false;
                    if (_uninstallCmdProcess != null)
                    {
                        _uninstallCmdProcess.Dispose();
                        _uninstallCmdProcess = null;
                    }
                    try { File.Delete(State(".uninstall-pending")); } catch { }
                    string detail = "Maintenance worker did not acknowledge within two minutes. " +
                        "The game remains open. Check BannerlordFG.log.";
                    Log("Uninstall acknowledgement timeout: " + detail);
                    ShowInstallFeedback("OptiScaler uninstall failed", detail);
                }
            }
            catch (Exception ex)
            {
                _requestedExit = false;
                _awaitingUninstallAck = false;
                if (_uninstallCmdProcess != null)
                {
                    _uninstallCmdProcess.Dispose();
                    _uninstallCmdProcess = null;
                }
                Log("Uninstall acknowledgement failed: " + ex);
                ShowInstallFeedback("OptiScaler uninstall failed", ex.Message);
            }
        }

        private bool IsManagedGraphicsInstalled()
        {
            // Successful installs remain cached. Only retry an initial negative
            // result, at a bounded rate, so early manual launches self-correct.
            if (_managedStateInitialized && (_cachedManagedInstall ||
                DateTime.UtcNow < _nextManagedInstallProbeUtc)) return _cachedManagedInstall;
            _nextManagedInstallProbeUtc = DateTime.UtcNow.AddSeconds(2);
            bool wasInitialized = _managedStateInitialized;
            bool installed = false;
            try
            {
                if (!string.IsNullOrEmpty(_gameBin))
                {
                    string manifest = StateManifest();
                    string ini = Path.Combine(_gameBin, "OptiScaler.ini");
                    if (File.Exists(manifest) && File.Exists(ini))
                    {
                        string text = File.ReadAllText(manifest);
                        string proxyPath = Path.Combine(_gameBin, ProxyFromManifestText(text));
                        // An interrupted install is not a managed installation.
                        installed = File.Exists(proxyPath) &&
                            text.IndexOf("\"stage\":\"complete\"", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            text.IndexOf("\"postinstallFiles\"", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
            }
            catch { installed = false; }
            _cachedManagedInstall = installed;
            _managedStateInitialized = true;
            if (wasInitialized && installed)
            {
                Log("Completed OptiScaler manifest detected after startup; refreshing Frame Generation controls.");
                NativePerformancePatch.ScheduleManagedInstallRefresh();
            }
            return installed;
        }

        private string StateManifest()
        {
            if (_cachedManifestPath != null) return _cachedManifestPath;
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BannerlordFG");
            return _cachedManifestPath = Path.Combine(directory, "install-" + HashBin() + ".json");
        }
        private string State(string suffix)
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BannerlordFG");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "first-run-" + HashBin() + suffix);
        }
        private string HashBin()
        {
            if (_cachedBinHash != null) return _cachedBinHash;
            using (var h = SHA256.Create())
                return _cachedBinHash = BitConverter.ToString(h.ComputeHash(
                    Encoding.UTF8.GetBytes(_gameBin.ToLowerInvariant()))).Replace("-", "").Substring(0, 12);
        }
        private static bool Recent(string path)
        {
            if (!File.Exists(path)) return false;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(6)) return true;
            try { File.Delete(path); } catch { return true; }
            return false;
        }
        private static void Log(string message)
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BannerlordFG");
                BannerlordFG.Diagnostics.UnifiedLog.Write(directory, "GAME", message);
            }
            catch { }
        }
    }
}

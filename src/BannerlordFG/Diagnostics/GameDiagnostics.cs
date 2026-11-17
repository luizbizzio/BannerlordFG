// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BannerlordFG.Setup;

namespace BannerlordFG.Diagnostics
{
    // One bounded startup snapshot. It never changes game settings or injector
    // files, and no hardware, process-module or directory scan runs on a tick.
    internal static class GameDiagnostics
    {
        private static int _started;
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BannerlordFG");

        internal static void Start(string gameBin, int pid, IList<string> engineActiveIds)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            try
            {
                // Copy any engine-owned collection on its own thread. The
                // background worker touches only this copy and Win32 APIs.
                var active = engineActiveIds == null ? null : engineActiveIds.ToList();
                bool engineVerified = active != null && active.Count > 0;
                if (!engineVerified)
                    active = ModuleInventory.ParseModuleArgument(Environment.GetCommandLineArgs().Skip(1));
                string activeSource = engineVerified ? "Bannerlord module API" :
                    active != null ? "game _MODULES_ argument" : "unknown";
                DiagnosticsRuntimeContext.SetActiveModules(active, activeSource);
                Task.Run(() => Collect(gameBin, pid, active, activeSource));
            }
            catch (Exception ex) { Write("WARN", "Startup", "Diagnostic task unavailable: " + ex.GetType().Name); }
        }

        private static void Collect(string gameBin, int pid, IList<string> active, string activeSource)
        {
            try
            {
                Write("INFO", "Startup", "Game module initialized; startup diagnostics beginning.");
                string executable = "unknown";
                try { executable = Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName); }
                catch (Exception ex) { Warn("Host executable unavailable", ex); }
                Write("INFO", "Host", "Executable=" + executable + "; kind=" +
                    DiagnosticPolicy.HostKind(executable) + "; PID=" + pid + ".");
                string parent = LauncherDetector.VerifiedParent(gameBin, pid, Warn);
                string[] assemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(x => !x.IsDynamic).Select(x => x.GetName().Name).ToArray();
                LauncherEvidence launcher = DiagnosticPolicy.IdentifyLauncher(executable, parent, assemblies);
                Write("INFO", "Host", "Launcher=" + launcher.Launcher +
                    "; BLSE=" + launcher.Blse + "; evidence=" + launcher.Source + ".");
                GameVersionEvidence gameVersion = GameVersionDetector.Detect(gameBin);
                Write("INFO", "Versions", "BannerlordFG=" +
                    typeof(GameDiagnostics).Assembly.GetName().Version + "; Bannerlord=" +
                    gameVersion.Version + "; source=" + gameVersion.Source + ".");
                if (gameVersion.Warning != null) Write("WARN", "Versions", gameVersion.Warning);

                Probe("Windows", () => Write("INFO", "Windows", WindowsProbe.WindowsVersion(Warn)));
                Probe("CPU", () => Write("INFO", "CPU", WindowsProbe.Cpu(Warn)));
                Probe("Memory", () => Write("INFO", "Memory", "Installed RAM=" + WindowsProbe.InstalledRam(Warn) + "."));
                var gpus = new List<GpuDescriptor>();
                Probe("GPU inventory", () =>
                {
                    gpus = WindowsProbe.Gpus(Warn);
                    Write("INFO", "GPU", "Detected adapters=" + gpus.Count + ".");
                    foreach (GpuDescriptor gpu in gpus)
                        Write("INFO", "GPU", "Vendor=" + Safe(gpu.Vendor) + "; model=" + Safe(gpu.Model) +
                            "; dedicated VRAM=" + (gpu.DedicatedBytes.HasValue ?
                                (gpu.DedicatedBytes.Value / 1073741824.0).ToString("F2", CultureInfo.InvariantCulture) + " GiB" : "unknown") +
                            "; driver=" + Safe(gpu.Driver) +
                            "; manufacturer source=" + Safe(gpu.VendorSource) + ".");
                });
                Probe("Active GPU", () =>
                {
                    var adapter = GpuDetection.TryDetectActiveAdapter(gameBin, pid, _ => { });
                    if (adapter == null) Write("INFO", "GPU", "Game renderer adapter=unknown (no verified PID/LUID activity).");
                    else
                    {
                        GpuDescriptor physical = gpus.FirstOrDefault(x => x.Luid == adapter.Luid);
                        Write("INFO", "GPU", "Game renderer verified by PDH PID/LUID: model=" +
                            Safe(adapter.Description) + "; vendor=" +
                            (physical?.Vendor ?? "unknown") + "; vendor source=" +
                            (physical?.VendorSource ?? "unknown") + "; LUID=0x" +
                            adapter.Luid.ToString("X16") + ".");
                    }
                });

                Probe("Modules", () =>
                {
                    string root = Path.GetFullPath(Path.Combine(gameBin, "..", "..", "Modules"));
                    List<string> selected = null;
                    try { selected = SubModule.ReadSelectedLauncherModules(); }
                    catch (Exception ex) { Warn("Launcher selection unavailable", ex); }
                    List<ModuleRecord> modules = ModuleInventory.Scan(root, active,
                        active != null, message => Write("WARN", "Modules", message), selected);
                    Write("INFO", "Modules", "Visible installed modules=" + modules.Count +
                        "; active source=" + activeSource + "; active count=" +
                        (active == null ? "unknown" : active.Count.ToString()) +
                        "; launcher-selected count=" + (selected == null ? "unknown" : selected.Count.ToString()) +
                        ". MO2 virtual files are included only when visible to this process.");
                    foreach (ModuleRecord module in modules)
                        Write("INFO", "Modules", "ID=" + Safe(module.Id) + "; name=" + Safe(module.Name) +
                            "; version=" + Safe(module.Version) + "; state=" + module.State +
                            "; load order=" + (module.LoadOrder.HasValue ? module.LoadOrder.ToString() : "unknown") +
                            "; launcher-selected=" + (module.LauncherSelected.HasValue ?
                                (module.LauncherSelected.Value ? "yes" : "no") : "unknown") +
                            "; dependencies=" + (module.Dependencies.Length > 0 ?
                                string.Join(",", module.Dependencies.Select(Safe)) : "none declared") + ".");
                });
                Probe("Graphics", () =>
                {
                    List<string> loaded = LoadedModules(out bool inspected);
                    GraphicsSnapshot graphics = GraphicsInventory.Inspect(gameBin, ManifestPath(gameBin),
                        loaded, inspected, message => Write("WARN", "Graphics", message));
                    DiagnosticsRuntimeContext.SetGraphicsStatus(graphics);
                    Write("INFO", "Graphics", "DirectX rendering API=" + graphics.DirectX + ".");
                    Write("INFO", "OptiScaler", "Installed=" + DiagnosticPolicy.InstalledState(graphics.OptiScaler) +
                        "; proxy=" + (graphics.OptiScaler.Proxy ?? "unknown") +
                        "; loaded=" + DiagnosticPolicy.LoadedState(graphics.OptiScaler) +
                        "; version=" + (graphics.OptiScaler.Version ?? "unknown") +
                        "; release=" + graphics.OptiBuild + ".");
                    Write("INFO", "ReShade", "Installed=" + DiagnosticPolicy.InstalledState(graphics.ReShade) +
                        "; proxy=" + (graphics.ReShade.Proxy ?? "unknown") +
                        "; loaded=" + DiagnosticPolicy.LoadedState(graphics.ReShade) +
                        "; version=" + (graphics.ReShade.Version ?? "unknown") + ".");
                    Write("INFO", "FrameGen", "Backend configured=" + graphics.Backend +
                        "; enabled=" + DiagnosticPolicy.FrameGenerationState(graphics.Backend,
                            graphics.FgConfigured, null) + "; runtime confirmation=unavailable" +
                        "; DX11 upscaler=" + graphics.Upscaler + ".");
                    Write("INFO", "FPS", "OptiScaler counter configured=" +
                        (graphics.FpsConfigured.HasValue ? (graphics.FpsConfigured.Value ? "on" : "off") : "unknown") +
                        "; runtime state=unknown.");
                    foreach (string conflict in graphics.Conflicts.Distinct())
                        Write("WARN", "DLL", "Possible conflict: " + conflict + ".");
                });
                Probe("Saved graphics settings", SavedGraphicsSettings);
                Write("INFO", "Startup", "Startup diagnostics complete.");
            }
            catch (Exception ex) { Warn("Startup diagnostics stopped", ex); }
        }

        private static void SavedGraphicsSettings()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Mount and Blade II Bannerlord", "Configs", "engine_config.txt");
            if (!File.Exists(path)) { Write("INFO", "Graphics", "Saved engine settings=unknown (config missing)."); return; }
            if (new FileInfo(path).Length > 4 * 1024 * 1024)
                throw new InvalidDataException("Engine config too large");
            var config = GraphicsInventory.ReadIni(File.ReadAllLines(path));
            string width = GraphicsInventory.Get(config, "", "display_width");
            string height = GraphicsInventory.Get(config, "", "display_height");
            string resolution = int.TryParse(width, out int w) && int.TryParse(height, out int h) &&
                w > 0 && h > 0 ? w + "x" + h : "unknown";
            Write("INFO", "Graphics", "Saved resolution=" + resolution +
                "; display mode code=" + Value(config, "display_mode") +
                "; DLSS input code=" + Value(config, "dlss_technique") +
                "; resolution scale=" + Value(config, "resolution_scale") +
                "; dynamic resolution=" + Value(config, "enable_dynamic_resolution_scale") +
                "; shadow resolution code=" + Value(config, "shadowmap_resolution") + ".");
        }

        private static string Value(Dictionary<string, string> config, string key)
        {
            return GraphicsInventory.Get(config, "", key) ?? "unknown";
        }

        private static List<string> LoadedModules(out bool inspected)
        {
            var loaded = new List<string>();
            inspected = false;
            try
            {
                using (Process process = Process.GetCurrentProcess())
                    foreach (ProcessModule module in process.Modules)
                        loaded.Add(module.FileName);
                inspected = true;
            }
            catch (Exception ex) { Warn("Loaded DLL inspection unavailable", ex); }
            return loaded;
        }

        private static string ManifestPath(string gameBin)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(gameBin.ToLowerInvariant()));
                return Path.Combine(LogDirectory, "install-" +
                    BitConverter.ToString(hash).Replace("-", "").Substring(0, 12) + ".json");
            }
        }

        private static string FileVersion(string path)
        {
            try
            {
                if (!File.Exists(path)) return "unknown";
                var version = FileVersionInfo.GetVersionInfo(path);
                return Safe(string.IsNullOrWhiteSpace(version.ProductVersion) ? version.FileVersion : version.ProductVersion);
            }
            catch { return "unknown"; }
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown";
            return value.Trim().Replace("\r", " ").Replace("\n", " ");
        }

        private static void Probe(string name, Action action)
        {
            try { action(); }
            catch (Exception ex) { Warn(name + " unavailable", ex); }
        }

        private static void Warn(string context, Exception exception)
        {
            Write("WARN", "Diagnostics", context + ": " + exception.GetType().Name + ".");
        }

        private static void Warn(string message)
        {
            Write("WARN", "Diagnostics", message);
        }

        private static void Write(string level, string component, string message)
        {
            UnifiedLog.Write(LogDirectory, level, component, message);
        }
    }
}

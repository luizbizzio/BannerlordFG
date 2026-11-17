// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using BannerlordFG.Setup;

namespace BannerlordFG.Diagnostics
{
    internal sealed class DiagnosticsBundle
    {
        internal readonly SortedDictionary<string, object> SystemInfo = NewObject();
        internal readonly SortedDictionary<string, object> GameInfo = NewObject();
        internal readonly SortedDictionary<string, object> GraphicsStatus = NewObject();
        internal readonly List<SortedDictionary<string, object>> Modules =
            new List<SortedDictionary<string, object>>();
        internal readonly List<string> Warnings = new List<string>();
        internal readonly List<string> Errors = new List<string>();
        internal string ActiveSource = "unknown";

        internal static SortedDictionary<string, object> NewObject()
        {
            return new SortedDictionary<string, object>(StringComparer.Ordinal);
        }

        internal static DiagnosticsBundle Collect(string gameBin)
        {
            var result = new DiagnosticsBundle();
            Action<string> warn = message => result.Warnings.Add(message);
            string[] active = DiagnosticsRuntimeContext.ActiveModules(out result.ActiveSource);
            if (active == null)
            {
                active = ModuleInventory.ParseModuleArgument(Environment.GetCommandLineArgs().Skip(1))?.ToArray();
                if (active != null) result.ActiveSource = "game _MODULES_ argument";
            }

            Capture(result, "Windows", () => result.SystemInfo["windows"] = WindowsProbe.WindowsVersion(warn));
            Capture(result, "CPU", () => result.SystemInfo["cpu"] = WindowsProbe.Cpu(warn));
            Capture(result, "RAM", () => result.SystemInfo["installedRam"] = WindowsProbe.InstalledRam(warn));
            var gpus = new List<GpuDescriptor>();
            Capture(result, "GPU inventory", () =>
            {
                gpus = WindowsProbe.Gpus(warn);
                result.SystemInfo["gpus"] = gpus.OrderBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
                    .Select(x => (object)new SortedDictionary<string, object>(StringComparer.Ordinal) {
                        { "vendor", x.Vendor ?? "unknown" },
                        { "vendorSource", x.VendorSource ?? "unknown" },
                        { "model", x.Model ?? "unknown" },
                        { "dedicatedVramBytes", x.DedicatedBytes.HasValue ?
                            (object)x.DedicatedBytes.Value : "unknown" },
                        { "driver", x.Driver ?? "unknown" }
                    }).ToArray();
            });
            if (!result.SystemInfo.ContainsKey("gpus")) result.SystemInfo["gpus"] = new object[0];
            Capture(result, "Active GPU", () =>
            {
                int pid;
                using (Process process = Process.GetCurrentProcess()) pid = process.Id;
                var adapter = GpuDetection.TryDetectActiveAdapter(gameBin, pid, _ => { });
                var gpu = adapter == null ? null : gpus.FirstOrDefault(x => x.Luid == adapter.Luid);
                result.SystemInfo["activeGpu"] = adapter == null ? (object)"unknown" :
                    new SortedDictionary<string, object>(StringComparer.Ordinal) {
                        { "model", adapter.Description ?? "unknown" },
                        { "vendor", gpu?.Vendor ?? "unknown" },
                        { "vendorSource", gpu?.VendorSource ?? "unknown" },
                        { "evidence", "PDH game PID and DXGI LUID" }
                    };
            });
            if (!result.SystemInfo.ContainsKey("activeGpu")) result.SystemInfo["activeGpu"] = "unknown";

            Capture(result, "Game identity", () =>
            {
                GameVersionEvidence version = GameVersionDetector.Detect(gameBin);
                result.GameInfo["bannerlordVersion"] = version.Version;
                result.GameInfo["versionSource"] = version.Source;
                if (version.Warning != null) warn(version.Warning);
                result.GameInfo["bannerlordFgVersion"] =
                    typeof(DiagnosticsBundle).Assembly.GetName().Version.ToString();
                using (Process process = Process.GetCurrentProcess())
                {
                    string executable = Path.GetFileName(process.MainModule.FileName);
                    result.GameInfo["hostExecutable"] = executable;
                    result.GameInfo["hostKind"] = DiagnosticPolicy.HostKind(executable);
                    string parent = LauncherDetector.VerifiedParent(gameBin, process.Id, warn);
                    string[] assemblies = AppDomain.CurrentDomain.GetAssemblies()
                        .Where(x => !x.IsDynamic).Select(x => x.GetName().Name).ToArray();
                    LauncherEvidence launcher = DiagnosticPolicy.IdentifyLauncher(executable,
                        parent, assemblies);
                    result.GameInfo["launcher"] = launcher.Launcher;
                    result.GameInfo["blse"] = launcher.Blse;
                    result.GameInfo["launcherEvidence"] = launcher.Source;
                }
            });
            Capture(result, "Saved graphics settings", () =>
            {
                var config = SavedEngineConfig();
                foreach (string key in new[] { "display_width", "display_height", "display_mode",
                    "dlss_technique", "resolution_scale", "enable_dynamic_resolution_scale",
                    "shadowmap_resolution" })
                    result.GameInfo[key] = GraphicsInventory.Get(config, "", key) ?? "unknown";
            });

            Capture(result, "Module inventory", () =>
            {
                string root = Path.GetFullPath(Path.Combine(gameBin, "..", "..", "Modules"));
                foreach (ModuleRecord module in ModuleInventory.Scan(root, active, active != null, warn))
                    result.Modules.Add(new SortedDictionary<string, object>(StringComparer.Ordinal) {
                        { "id", module.Id }, { "name", module.Name },
                        { "version", module.Version }, { "state", module.State },
                        { "loadOrder", module.LoadOrder.HasValue ?
                            (object)module.LoadOrder.Value : "unknown" },
                        { "dependencies", module.Dependencies }
                    });
            });
            Capture(result, "Graphics components", () =>
            {
                bool inspected;
                List<string> loaded = LoadedModules(out inspected, warn);
                GraphicsSnapshot graphics = GraphicsInventory.Inspect(gameBin, ManifestPath(gameBin),
                    loaded, inspected, warn);
                result.GraphicsStatus["directX"] = graphics.DirectX;
                result.GraphicsStatus["optiScaler"] = Component(graphics.OptiScaler,
                    graphics.OptiBuild);
                result.GraphicsStatus["reShade"] = Component(graphics.ReShade, null);
                result.GraphicsStatus["frameGenerationBackendConfigured"] = graphics.Backend;
                result.GraphicsStatus["frameGenerationConfigured"] = State(graphics.FgConfigured);
                result.GraphicsStatus["frameGenerationRequested"] = RequestedFg();
                result.GraphicsStatus["frameGenerationActive"] = "unknown";
                result.GraphicsStatus["runtimeReadback"] = "unavailable";
                result.GraphicsStatus["dx11UpscalerConfigured"] = graphics.Upscaler;
                result.GraphicsStatus["fpsCounterConfigured"] = State(graphics.FpsConfigured);
                result.GraphicsStatus["fpsCounterRuntime"] = "unknown";
                result.GraphicsStatus["possibleDllConflicts"] = graphics.Conflicts.ToArray();
            });
            return result;
        }

        private static SortedDictionary<string, object> Component(ComponentEvidence evidence,
            string release)
        {
            var result = NewObject();
            result["installed"] = DiagnosticPolicy.InstalledState(evidence);
            result["loaded"] = DiagnosticPolicy.LoadedState(evidence);
            result["installedVersion"] = evidence.InstalledVersion ?? "unknown";
            result["loadedVersion"] = evidence.Loaded == true ?
                evidence.LoadedVersion ?? "unknown" : "unknown";
            result["proxy"] = evidence.Proxy ?? "unknown";
            if (release != null) result["release"] = release;
            return result;
        }

        private static string State(bool? value)
        {
            return !value.HasValue ? "unknown" : value.Value ? "on" : "off";
        }

        private static string RequestedFg()
        {
            string path = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "BannerlordFG", "preferences.xml");
            if (!File.Exists(path)) return "unknown";
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 };
                using (var reader = XmlReader.Create(path, settings))
                {
                    var document = new XmlDocument { XmlResolver = null };
                    document.Load(reader);
                    string value = document.DocumentElement?.GetAttribute("enabled");
                    return value == "true" ? "on" : value == "false" ? "off" : "unknown";
                }
            }
            catch { return "unknown"; }
        }

        private static Dictionary<string, string> SavedEngineConfig()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Mount and Blade II Bannerlord", "Configs", "engine_config.txt");
            if (!File.Exists(path)) return new Dictionary<string, string>();
            if (new FileInfo(path).Length > 4 * 1024 * 1024)
                throw new InvalidDataException("Engine config too large");
            return GraphicsInventory.ReadIni(File.ReadAllLines(path));
        }

        private static List<string> LoadedModules(out bool inspected, Action<string> warning)
        {
            var loaded = new List<string>();
            inspected = false;
            try
            {
                using (Process process = Process.GetCurrentProcess())
                    foreach (ProcessModule module in process.Modules) loaded.Add(module.FileName);
                inspected = true;
            }
            catch (Exception ex) { warning("Loaded DLL inspection unavailable: " + ex.GetType().Name); }
            return loaded;
        }

        private static string ManifestPath(string gameBin)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(gameBin.ToLowerInvariant()));
                string id = BitConverter.ToString(hash).Replace("-", "").Substring(0, 12);
                return Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), "BannerlordFG", "install-" + id + ".json");
            }
        }

        private static void Capture(DiagnosticsBundle bundle, string name, Action work)
        {
            try { work(); }
            catch (Exception ex) { bundle.Warnings.Add(name + " unavailable: " + ex.GetType().Name); }
        }
    }
}

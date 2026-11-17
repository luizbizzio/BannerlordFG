// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace BannerlordFG.Diagnostics
{
    internal sealed class GraphicsSnapshot
    {
        internal ComponentEvidence OptiScaler = new ComponentEvidence();
        internal ComponentEvidence ReShade = new ComponentEvidence();
        internal string OptiBuild = "unknown";
        internal string Backend = "unknown";
        internal bool? FgConfigured;
        internal bool? FpsConfigured;
        internal string Upscaler = "unknown";
        internal string DirectX = "unknown";
        internal readonly List<string> Conflicts = new List<string>();
    }

    internal static class GraphicsInventory
    {
        private static readonly string[] ProxyNames = {
            "winmm.dll", "dxgi.dll", "d3d11.dll", "d3d12.dll",
            "version.dll", "wininet.dll", "winhttp.dll" };

        internal static GraphicsSnapshot Inspect(string gameBin, string manifestPath,
            IEnumerable<string> loadedPaths, bool modulesInspected, Action<string> warning)
        {
            var snapshot = new GraphicsSnapshot();
            var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (modulesInspected && loadedPaths != null)
                foreach (string path in loadedPaths)
                    try { loaded.Add(Path.GetFullPath(path)); } catch { }
            string ini = Path.Combine(gameBin, "OptiScaler.ini");
            snapshot.OptiScaler.Present = File.Exists(ini) || File.Exists(manifestPath);
            string managedProxy = null;
            if (File.Exists(manifestPath))
            {
                try
                {
                    var manifest = Json(File.ReadAllText(manifestPath));
                    managedProxy = String(manifest, "proxyFileName") ?? "dxgi.dll";
                    snapshot.OptiBuild = String(manifest, "tag") ?? "unknown";
                    snapshot.OptiScaler.Proxy = managedProxy;
                    snapshot.OptiScaler.ValidInstall = VerifyManifest(gameBin, manifest, managedProxy);
                }
                catch (UnauthorizedAccessException)
                {
                    snapshot.OptiScaler.ValidInstall = null;
                    warning("Managed OptiScaler manifest access denied; integrity unknown.");
                }
                catch (IOException)
                {
                    snapshot.OptiScaler.ValidInstall = null;
                    warning("Managed OptiScaler manifest could not be read; integrity unknown.");
                }
                catch (Exception ex)
                {
                    snapshot.OptiScaler.ValidInstall = false;
                    warning("Managed OptiScaler manifest unreadable: " + ex.GetType().Name);
                }
            }
            if (File.Exists(ini))
            {
                try
                {
                    if (new FileInfo(ini).Length > 4 * 1024 * 1024)
                        throw new InvalidDataException("OptiScaler INI too large");
                    var values = ReadIni(File.ReadAllLines(ini));
                    snapshot.Backend = Get(values, "FrameGen", "FGOutput") ?? "unknown";
                    snapshot.FgConfigured = Boolean(Get(values, "FrameGen", "Enabled"));
                    snapshot.FpsConfigured = Boolean(Get(values, "Menu", "ShowFps"));
                    snapshot.Upscaler = Get(values, "Upscalers", "Dx11Upscaler") ?? "unknown";
                }
                catch (Exception ex) { warning("OptiScaler configuration unreadable: " + ex.GetType().Name); }
            }

            var recognized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string filename in ProxyNames.Concat(new[] { "ReShade64.dll", "OptiScaler.dll" }))
            {
                string path = Path.Combine(gameBin, filename);
                if (!File.Exists(path)) continue;
                string identity = FileIdentity(path);
                if (filename.Equals(managedProxy, StringComparison.OrdinalIgnoreCase))
                {
                    if (identity == "ReShade")
                        snapshot.Conflicts.Add("managed OptiScaler proxy filename contains a ReShade DLL");
                    else identity = "OptiScaler"; // Hash validation still decides integrity.
                }
                recognized[filename] = identity;
                if (identity == "OptiScaler")
                {
                    snapshot.OptiScaler.Present = true;
                    if (snapshot.OptiScaler.Proxy == null) snapshot.OptiScaler.Proxy = filename;
                    if (snapshot.OptiScaler.InstalledVersion == null)
                        snapshot.OptiScaler.InstalledVersion = Version(path);
                    if (snapshot.OptiScaler.Version == null)
                        snapshot.OptiScaler.Version = snapshot.OptiScaler.InstalledVersion;
                }
                if (identity == "ReShade")
                {
                    snapshot.ReShade.Present = true;
                    if (snapshot.ReShade.Proxy == null) snapshot.ReShade.Proxy = filename;
                    if (snapshot.ReShade.InstalledVersion == null)
                        snapshot.ReShade.InstalledVersion = Version(path);
                    if (snapshot.ReShade.Version == null)
                        snapshot.ReShade.Version = snapshot.ReShade.InstalledVersion;
                }
            }
            bool optiLoaded = recognized.Where(x => x.Value == "OptiScaler")
                .Any(x => loaded.Contains(Path.GetFullPath(Path.Combine(gameBin, x.Key))));
            bool reshadeLoaded = recognized.Where(x => x.Value == "ReShade")
                .Any(x => loaded.Contains(Path.GetFullPath(Path.Combine(gameBin, x.Key))));
            // A virtualized mod manager or external loader can map a DLL from
            // outside the game bin. Inspect only likely proxy names, never log
            // their paths, and require version metadata before attribution.
            if (modulesInspected)
                foreach (string path in loaded)
                {
                    string filename = Path.GetFileName(path);
                    if (!ProxyNames.Contains(filename, StringComparer.OrdinalIgnoreCase) &&
                        !filename.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase) &&
                        !filename.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase)) continue;
                    string identity = FileIdentity(path);
                    if (identity == "OptiScaler")
                    {
                        optiLoaded = true;
                        snapshot.OptiScaler.Present = true;
                        if (snapshot.OptiScaler.Proxy == null) snapshot.OptiScaler.Proxy = filename;
                        snapshot.OptiScaler.LoadedVersion = Version(path);
                        if (snapshot.OptiScaler.Version == null)
                            snapshot.OptiScaler.Version = snapshot.OptiScaler.LoadedVersion;
                    }
                    if (identity == "ReShade")
                    {
                        reshadeLoaded = true;
                        snapshot.ReShade.Present = true;
                        if (snapshot.ReShade.Proxy == null) snapshot.ReShade.Proxy = filename;
                        snapshot.ReShade.LoadedVersion = Version(path);
                        if (snapshot.ReShade.Version == null)
                            snapshot.ReShade.Version = snapshot.ReShade.LoadedVersion;
                    }
                }
            snapshot.OptiScaler.Loaded = modulesInspected ? (bool?)optiLoaded : null;
            snapshot.ReShade.Loaded = modulesInspected ? (bool?)reshadeLoaded : null;
            if (optiLoaded && snapshot.OptiScaler.LoadedVersion == null)
                snapshot.OptiScaler.LoadedVersion = snapshot.OptiScaler.InstalledVersion;
            if (reshadeLoaded && snapshot.ReShade.LoadedVersion == null)
                snapshot.ReShade.LoadedVersion = snapshot.ReShade.InstalledVersion;
            if (snapshot.OptiScaler.Present && snapshot.OptiScaler.Proxy != null &&
                !File.Exists(Path.Combine(gameBin, snapshot.OptiScaler.Proxy)))
                snapshot.OptiScaler.ValidInstall = false;
            if (snapshot.ReShade.Present) snapshot.ReShade.ValidInstall = null; // No ReShade ownership manifest.
            snapshot.DirectX = modulesInspected && loaded.Any(x =>
                string.Equals(x, Path.Combine(Environment.SystemDirectory, "d3d11.dll"),
                    StringComparison.OrdinalIgnoreCase))
                ? "D3D11 runtime loaded (renderer API not independently confirmed)" : "unknown";
            if (File.Exists(Path.Combine(gameBin, "winmm.dll")) &&
                File.Exists(Path.Combine(gameBin, "dxgi.dll")) &&
                (!recognized.ContainsKey("winmm.dll") || !recognized.ContainsKey("dxgi.dll") ||
                 recognized["winmm.dll"] == "unknown" || recognized["dxgi.dll"] == "unknown"))
                snapshot.Conflicts.Add("winmm.dll and dxgi.dll are both occupied; one or both owners are unknown");
            if (snapshot.OptiScaler.Present && snapshot.ReShade.Present &&
                string.Equals(snapshot.ReShade.Proxy, "dxgi.dll", StringComparison.OrdinalIgnoreCase))
                snapshot.Conflicts.Add("ReShade dxgi.dll may affect OptiScaler hook order or overlay");
            if (snapshot.OptiScaler.ValidInstall == false)
                snapshot.Conflicts.Add("managed OptiScaler files or manifest failed integrity checks");
            return snapshot;
        }

        private static bool VerifyManifest(string gameBin, Dictionary<string, object> manifest,
            string proxy)
        {
            if (proxy == null || !(proxy.Equals("winmm.dll", StringComparison.OrdinalIgnoreCase) ||
                  proxy.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase)) ||
                !string.Equals(String(manifest, "stage"), "complete", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(String(manifest, "gameBin") ?? ""),
                    Path.GetFullPath(gameBin), StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(String(manifest, "tag")) ||
                !Regex.IsMatch(String(manifest, "archiveSha256") ?? "", "^[a-fA-F0-9]{64}$") ||
                !File.Exists(Path.Combine(gameBin, "OptiScaler.ini"))) return false;
            object value;
            if (!manifest.TryGetValue("postinstallFiles", out value)) return false;
            var files = value as Dictionary<string, object>;
            if (files == null || files.Count == 0 || files.Count > 256 ||
                !files.ContainsKey(proxy)) return false;
            string root = Path.GetFullPath(gameBin).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            foreach (var pair in files)
            {
                if (Path.IsPathRooted(pair.Key) || pair.Key.Contains("..")) return false;
                string path = Path.GetFullPath(Path.Combine(gameBin, pair.Key));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
                // User and game updates may legitimately edit the INI; the
                // manifest records its original hash for safe uninstall only.
                if (pair.Key.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.Equals("OptiScaler.ini.bannerlordfg-original.bak", StringComparison.OrdinalIgnoreCase))
                    continue;
                string expected = Convert.ToString(pair.Value);
                if (!Regex.IsMatch(expected ?? "", "^[a-fA-F0-9]{64}$") ||
                    !string.Equals(Hash(path), expected, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        private static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        private static Dictionary<string, object> Json(string text)
        {
            if (text.Length > 1024 * 1024) throw new InvalidDataException("Manifest too large");
            var data = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
            if (data == null) throw new InvalidDataException("Invalid manifest");
            return data;
        }

        private static string String(Dictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) ? value as string : null;
        }

        internal static Dictionary<string, string> ReadIni(IEnumerable<string> lines)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string section = "";
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                { section = line.Substring(1, line.Length - 2).Trim(); continue; }
                if (line.StartsWith(";") || line.StartsWith("#")) continue;
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Split(';', '#')[0].Trim();
                values[section + "." + key] = value;
            }
            return values;
        }

        internal static string Get(Dictionary<string, string> values, string section, string key)
        {
            string result;
            return values.TryGetValue(section + "." + key, out result) ? result : null;
        }

        internal static bool? Boolean(string value)
        {
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1") return true;
            if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || value == "0") return false;
            return null;
        }

        private static string FileIdentity(string path)
        {
            try
            {
                var file = FileVersionInfo.GetVersionInfo(path);
                return DiagnosticPolicy.IdentifyProxy(file.ProductName, file.OriginalFilename,
                    file.FileDescription);
            }
            catch { return "unknown"; }
        }

        private static string Version(string path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                return string.IsNullOrWhiteSpace(info.ProductVersion) ?
                    (info.FileVersion ?? "unknown") : info.ProductVersion;
            }
            catch { return "unknown"; }
        }
    }
}

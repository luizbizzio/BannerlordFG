// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BannerlordFG.Setup
{
    /// <summary>Pinned OptiScaler downloader. OptiScaler is not bundled; extraction uses SharpCompress.</summary>
    internal static class OptiInstaller
    {
        internal const string Tag = "nightly-20261016";
        internal const string AssetName = "OptiScaler_v10.0.0-pre1_20261016.7z";
        // Pinned hash recovered from the official v1.0.1 release binary (nightly-20261016).
        internal const string PinnedSha256 = "09149a5b17514a0594faf612467267c62ed8851dbc1dde0ade6763f1152e600f";
        // Used ONLY to identify an interrupted installation from the published 1.0.0 build.
        // Never download or install this older release, and never trust a manifest
        // whose tag/hash pair is not explicitly listed here.
        private const string PreviousTag = "nightly-20260926";
        private const string PreviousPinnedSha256 = "464e2ecc5dab477e940186959a7fdee64880f61c63f52ff0f516e5502bc74e94";
        private static bool IsKnownManagedArchive(string tag, string digest)
        {
            return (string.Equals(tag, Tag, StringComparison.Ordinal) &&
                    string.Equals(digest, PinnedSha256, StringComparison.OrdinalIgnoreCase)) ||
                   (string.Equals(tag, PreviousTag, StringComparison.Ordinal) &&
                    string.Equals(digest, PreviousPinnedSha256, StringComparison.OrdinalIgnoreCase));
        }
        internal const string ReleasePage = "https://github.com/optiscaler/OptiScaler-nightly/releases/tag/" + Tag;
        private const string Api = "https://api.github.com/repos/optiscaler/OptiScaler-nightly/releases/tags/" + Tag;
        private const string AssetUrlPrefix = "https://github.com/optiscaler/OptiScaler-nightly/releases/download/" + Tag + "/";
        private static readonly string[] Proxies = { "dxgi.dll", "d3d11.dll", "d3d12.dll", "winmm.dll",
            "version.dll", "wininet.dll", "winhttp.dll" };

        // Some legitimate game folders include Microsoft's dbghelp.dll. It is
        // not an OptiScaler/ReShade injector and will not be overwritten. Unknown
        // or ReShade-owned dbghelp.dll is still treated as a possible conflict.
        private static bool IsMicrosoftDbgHelp(string path)
        {
            if (!Path.GetFileName(path).Equals("dbghelp.dll", StringComparison.OrdinalIgnoreCase))
                return false;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                return string.Equals(info.CompanyName, "Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(info.OriginalFilename, "DBGHELP.DLL", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // Existing user installations are not owned by BannerlordFG. Recognize the
        // official proxy's version metadata instead of labelling it as ReShade.
        internal static bool HasManagedInstall(string gameBin) => File.Exists(GetManifestPath(gameBin));

        internal static string ExistingInstallStatus(string gameBin)
        {
            if (File.Exists(Path.Combine(gameBin, "OptiScaler.ini")))
                return HasManagedInstall(gameBin) ? "BannerlordFG-managed OptiScaler is already installed." :
                    "OptiScaler.ini already exists: preserving the user's external installation.";
            if (Directory.Exists(Path.Combine(gameBin, "OptiScaler")))
                return "OptiScaler directory already exists; installation may be incomplete. No changes made.";
            foreach (string proxy in Proxies)
            {
                string path = Path.Combine(gameBin, proxy);
                if (!File.Exists(path)) continue;
                if (IsMicrosoftDbgHelp(path)) continue;
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    if ((info.OriginalFilename ?? "").IndexOf("OptiScaler", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (info.ProductName ?? "").IndexOf("OptiScaler", StringComparison.OrdinalIgnoreCase) >= 0)
                        return proxy + " belongs to OptiScaler, but OptiScaler.ini is missing. Leaving the existing DLL untouched.";
                }
                catch { /* Unreadable metadata is not proof of ownership. */ }
                return proxy + " is already occupied by an unknown graphics component. Leaving it untouched.";
            }
            return null;
        }

        internal static string ValidateGameBin(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Choose the Bannerlord folder.");
            string p = Path.GetFullPath(raw.Trim().Trim('"'));
            if (Directory.Exists(Path.Combine(p, "bin", "Win64_Shipping_Client")))
                p = Path.Combine(p, "bin", "Win64_Shipping_Client");
            if (!Directory.Exists(p) || !new[] { "Bannerlord.exe", "Bannerlord.Native.exe" }
                .Any(exe => File.Exists(Path.Combine(p, exe))))
                throw new InvalidOperationException("This is not a Bannerlord executable directory.");
            return p;
        }

        internal static bool BannerlordIsRunning()
        {
            return Process.GetProcesses().Any(p =>
            {
                try
                {
                    string name = p.ProcessName;
                    // Only the actual game executables lock the injected graphics DLLs.
                    // The TaleWorlds launcher may remain open after QuitGame and must not
                    // block post-exit uninstall indefinitely.
                    return name.Equals("Bannerlord", StringComparison.OrdinalIgnoreCase) ||
                           name.Equals("Bannerlord.Native", StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
                finally { p.Dispose(); }
            });
        }

        private static Dictionary<string, object> ParseJsonObject(string data)
        {
            var result = new JavaScriptSerializer { MaxJsonLength = 10000000 }.DeserializeObject(data) as Dictionary<string, object>;
            if (result == null) throw new InvalidDataException("Unexpected GitHub API response.");
            return result;
        }

        private static string Str(Dictionary<string, object> obj, string name)
        {
            object val;
            return obj.TryGetValue(name, out val) && val != null ? val.ToString() : "";
        }

        private static Dictionary<string, object> SelectOfficialAsset(Dictionary<string, object> release)
        {
            if (Str(release, "tag_name") != Tag) throw new InvalidDataException("GitHub returned the wrong release tag.");
            object untyped;
            var assets = release.TryGetValue("assets", out untyped) ? untyped as object[] : null;
            if (assets == null) throw new InvalidDataException("GitHub release is missing assets.");
            var matching = assets.OfType<Dictionary<string, object>>().Where(asset =>
            {
                string name = Str(asset, "name");
                string url = Str(asset, "browser_download_url");
                long size;
                bool validSize = long.TryParse(Str(asset, "size"), out size) && size > 1000000;
                return string.Equals(name, AssetName, StringComparison.Ordinal) &&
                    url.StartsWith(AssetUrlPrefix, StringComparison.Ordinal) && validSize;
            }).ToArray();
            if (matching.Length != 1)
                throw new InvalidDataException("Cannot find the pinned official 7z asset. Matching files: " + matching.Length);
            return matching[0];
        }

        // A game module can launch this helper while Bannerlord is open. The helper
        // downloads and verifies the official 7z in its own process, then signals
        // readiness. No files under the game's bin directory are modified here.
        internal sealed class PreparedPackage : IDisposable
        {
            internal string Folder;
            internal string Archive;
            internal string OfficialUrl;
            internal string Sha256;
            internal void Verify()
            {
                if (string.IsNullOrEmpty(Archive) || !File.Exists(Archive) ||
                    !HashFile(Archive).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Prepared release archive was modified or removed.");
            }
            public void Dispose()
            {
                if (!string.IsNullOrEmpty(Folder))
                    try { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); }
                    catch { /* Cache cleanup must not mask an install failure. */ }
            }
        }

        internal static async Task<PreparedPackage> PrepareOfficialPackageAsync(Action<string> log)
        {
            var pkg = new PreparedPackage {
                Folder = Path.Combine(Path.GetTempPath(), "BannerlordFG_" + Guid.NewGuid().ToString("N"))
            };
            Directory.CreateDirectory(pkg.Folder);
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                string json;
                log("Requesting pinned OptiScaler release metadata from GitHub...");
                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "BannerlordFG-setup/1.0.3";
                    client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                    json = await WithDownloadDeadline(client.DownloadStringTaskAsync(Api), client,
                        TimeSpan.FromSeconds(35), "GitHub release metadata");
                }
                var release = SelectOfficialAsset(ParseJsonObject(json));
                log("Release metadata received. Selecting and verifying the official archive...");
                pkg.OfficialUrl = Str(release, "browser_download_url");
                string declaredDigest = Str(release, "digest");
                if (declaredDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
                    !declaredDigest.Substring(7).Equals(PinnedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Official GitHub release asset digest changed: refusing untested build.");
                pkg.Archive = Path.Combine(pkg.Folder, "official-optiscaler.7z");
                log("Downloading pinned official build: " + Tag);
                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "BannerlordFG-setup/1.0.3";
                    int nextPercent = 25;
                    client.DownloadProgressChanged += (sender, args) => {
                        if (args.ProgressPercentage < nextPercent) return;
                        log("Official archive download " + args.ProgressPercentage + "% (" +
                            args.BytesReceived + " bytes received).");
                        nextPercent = ((args.ProgressPercentage / 25) + 1) * 25;
                    };
                    await WithDownloadDeadline(client.DownloadFileTaskAsync(
                        new Uri(pkg.OfficialUrl), pkg.Archive), client,
                        TimeSpan.FromMinutes(4), "OptiScaler release archive");
                }
                pkg.Sha256 = HashFile(pkg.Archive);
                if (!pkg.Sha256.Equals(PinnedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Official archive SHA-256 differs from the pinned build. Installation aborted.");
                string digest = Str(release, "digest");
                if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!digest.Substring(7).Equals(pkg.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("GitHub asset digest mismatch.");
                    log("Downloaded archive SHA256 verified against GitHub metadata.");
                }
                else log("Release digest missing; archive verified against the pinned local SHA256.");
                pkg.Verify();
                return pkg;
            }
            catch
            {
                pkg.Dispose();
                throw;
            }
        }

        private static async Task<T> WithDownloadDeadline<T>(Task<T> operation,
            WebClient client, TimeSpan deadline, string description)
        {
            if (await Task.WhenAny(operation, Task.Delay(deadline)) != operation)
            {
                client.CancelAsync();
                // Observe any later failure from a cancelled WebClient request.
                _ = operation.ContinueWith(task => { var ignored = task.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw new TimeoutException(description + " did not respond within " +
                    (int)deadline.TotalSeconds + " seconds.");
            }
            return await operation;
        }

        private static async Task WithDownloadDeadline(Task operation,
            WebClient client, TimeSpan deadline, string description)
        {
            if (await Task.WhenAny(operation, Task.Delay(deadline)) != operation)
            {
                client.CancelAsync();
                _ = operation.ContinueWith(task => { var ignored = task.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw new TimeoutException(description + " did not complete within " +
                    (int)deadline.TotalSeconds + " seconds.");
            }
            await operation;
        }

        private static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        // Extract the pinned OptiScaler 7z using managed LZMA decoding rather than
        // the OS version of tar.exe (which may have no LZMA codec).
        // Keep the SHA-256 check here even though PreparedPackage.Verify already ran.
        private static void ExtractSafe(string archive, string output)
        {
            if (!HashFile(archive).Equals(PinnedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("7z archive integrity check failed.");
            SafeSevenZipExtractor.Extract(archive, output);
        }

        private static string FindPackageRoot(string extracted)
        {
            var candidates = Directory.GetFiles(extracted, "OptiScaler.ini", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .Where(p => File.Exists(Path.Combine(p, "OptiScaler.dll")) &&
                    Directory.EnumerateFiles(p, "*setup*.bat", SearchOption.TopDirectoryOnly).Any()).ToArray();
            if (candidates.Length != 1)
                throw new InvalidDataException("Unexpected package layout. Refusing to install a different upstream format.");
            return candidates[0];
        }

        private static string PatchIni(string text, string section, string key, string value)
        {
            var re = new Regex(@"(?m)^\[" + Regex.Escape(section) + @"\]\s*\r?\n(?<body>[\s\S]*?)(?=^\[|\z)", RegexOptions.IgnoreCase);
            var match = re.Match(text);
            if (!match.Success) throw new InvalidDataException("Upstream config lacks [" + section + "].");
            var keyRx = new Regex(@"(?m)^[ \t]*" + Regex.Escape(key) + @"[ \t]*=[^\r\n]*", RegexOptions.IgnoreCase);
            var block = match.Groups["body"];
            if (!keyRx.IsMatch(block.Value))
                throw new InvalidDataException("Upstream config lacks [" + section + "] " + key + ".");
            string updated = keyRx.Replace(block.Value, key + " = " + value, 1);
            return text.Substring(0, block.Index) + updated + text.Substring(block.Index + block.Length);
        }

        // Optional tuning keys differ across upstream nightly releases. Never abort a
        // working graphics installation just because sharpening fields were renamed.
        private static string OptionalPatchIni(string text, string section, string key, string value)
        {
            var rx = new Regex(@"(?mi)^\[" + Regex.Escape(section) + @"\][ \t]*\r?\n(?<body>[\s\S]*?)(?=^\[|\z)");
            var match = rx.Match(text);
            if (!match.Success) return text;
            var body = match.Groups["body"];
            var keyRx = new Regex(@"(?mi)^[ \t]*" + Regex.Escape(key) + @"[ \t]*=[^\r\n]*");
            if (!keyRx.IsMatch(body.Value)) return text;
            var modified = keyRx.Replace(body.Value, key + " = " + value, 1);
            return text.Substring(0, body.Index) + modified + text.Substring(body.Index + body.Length);
        }

        // Disable every external keybinding except the two internal live-control keys.
        internal static string ConfigureLiveControlAssignments(string ini)
        {
            string result = Regex.Replace(ini,
                @"(?mi)^(?<prefix>[ \t]*[A-Za-z_][A-Za-z0-9_]*(?:ShortcutKey|Hotkey)[ \t]*=[ \t]*)[^;#\r\n]*(?<suffix>[ \t]*(?:[;#][^\r\n]*)?)$",
                m => m.Groups["prefix"].Value + "-1" + m.Groups["suffix"].Value);
            foreach (string key in new[] { "ShortcutKey", "FGShortcutKey", "FpsShortcutKey", "FpsCycleShortcutKey" })
            {
                var sx = new Regex(@"(?mi)^\[Menu\][ \t]*\r?\n(?<body>[\s\S]*?)(?=^\[|\z)");
                var section = sx.Match(result);
                if (!section.Success) throw new InvalidDataException("OptiScaler.ini missing [Menu]");
                var body = section.Groups["body"];
                var rx = new Regex(@"(?mi)^[ \t]*" + Regex.Escape(key) + @"[ \t]*=[^\r\n]*");
                string replacement = rx.IsMatch(body.Value)
                    ? rx.Replace(body.Value, key + " = -1", 1)
                    : body.Value.TrimEnd('\r', '\n') + Environment.NewLine + key + " = -1" + Environment.NewLine;
                result = result.Substring(0, body.Index) + replacement + result.Substring(body.Index + body.Length);
            }
            result = PatchIni(result, "Menu", "FGShortcutKey", "0x86");
            result = PatchIni(result, "Menu", "FpsShortcutKey", "0x87");
            return result;
        }

        // Run only for verified BannerlordFG-owned installation, with Bannerlord
        // completely closed. External graphics injectors remain untouched.
        internal static void ConfigureExistingManagedLiveControls(string gameBin, Action<string> log)
        {
            if (BannerlordIsRunning()) throw new InvalidOperationException("Close Bannerlord first.");
            if (string.IsNullOrWhiteSpace(gameBin)) throw new ArgumentException("--gamebin is required.");
            gameBin = Path.GetFullPath(gameBin);
            string ini = Path.Combine(gameBin, "OptiScaler.ini");
            if (!HasManagedInstall(gameBin) || !File.Exists(ini) ||
                !File.Exists(Path.Combine(gameBin, "dxgi.dll")))
            {
                log("Existing key migration skipped: no complete managed OptiScaler installation.");
                return;
            }
            // The existence of a manifest alone is not ownership proof. Verify
            // the actual proxy bytes and manifest game path before changing INI.
            var manifest = ParseJsonObject(File.ReadAllText(GetManifestPath(gameBin)));
            if (!Str(manifest, "gameBin").Equals(gameBin, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Manifest belongs to a different game folder; no INI changes made.");
            string expectedProxy = Str(manifest, "movedDxgiSha256");
            if (string.IsNullOrWhiteSpace(expectedProxy))
            {
                object owned;
                if (manifest.TryGetValue("postinstallFiles", out owned) &&
                    owned is Dictionary<string, object> postinstall)
                    expectedProxy = Str(postinstall, "dxgi.dll");
            }
            if (string.IsNullOrWhiteSpace(expectedProxy) ||
                !HashFile(Path.Combine(gameBin, "dxgi.dll")).Equals(expectedProxy, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Cannot verify ownership of dxgi.dll; the OptiScaler INI is unchanged.");
            string before = File.ReadAllText(ini);
            string after = ConfigureLiveControlAssignments(before);
            if (before == after) { log("OptiScaler internal live controls already configured."); return; }
            var backupDir = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "BannerlordFG", "hotkey-backups");
            Directory.CreateDirectory(backupDir);
            string backup = Path.Combine(backupDir, "OptiScaler-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".ini");
            File.Copy(ini, backup, false);
            if (!HashFile(ini).Equals(HashFile(backup), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Backup checksum mismatch; current INI left unchanged.");
            string tmp = ini + ".bfg-shortcuts.tmp";
            try
            {
                File.WriteAllText(tmp, after, new UTF8Encoding(false));
                File.Replace(tmp, ini, null);
                log("Configured internal F23/F24 runtime controls; all other shortcuts disabled. Backup: " + backup);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        private static void Configure(string ini, bool fsr31, Action<string> log, bool forceDlss = false)
        {
            string text = File.ReadAllText(ini);
            if (fsr31) text = PatchIni(text, "Upscalers", "Dx11Upscaler", "fsr31");
            if (fsr31) text = OptionalPatchIni(text, "Sharpness", "OverrideSharpness", "false");
            if (fsr31) text = OptionalPatchIni(text, "CAS", "Enabled", "false");
            else if (forceDlss) text = PatchIni(text, "Upscalers", "Dx11Upscaler", "dlss");
            text = PatchIni(text, "FrameGen", "Enabled", "true");
            text = PatchIni(text, "FrameGen", "FGInput", "Upscaler");
            text = PatchIni(text, "FrameGen", "FGOutput", "XeFG");
            text = PatchIni(text, "FrameGen", "DebugView", "false");
            text = PatchIni(text, "XeFG", "InterpolationCount", "1");
            text = PatchIni(text, "XeFG", "ForceBorderless", "true");
            text = PatchIni(text, "Menu", "OverlayMenu", "true");
            text = ConfigureLiveControlAssignments(text);
            text = PatchIni(text, "Menu", "ShowFps", "true");
            text = PatchIni(text, "Menu", "FpsOverlayType", "0");
            text = PatchIni(text, "Menu", "DisableSplash", "true");
            text = PatchIni(text, "Framerate", "FramerateLimit", "0");
            text = PatchIni(text, "UpscaleRatio", "UpscaleRatioOverrideEnabled", "false");
            text = PatchIni(text, "QualityOverrides", "QualityRatioOverrideEnabled", "false");
            string backup = ini + ".bannerlordfg-original.bak";
            if (File.Exists(backup)) throw new IOException("Config backup already exists. Refusing to overwrite it.");
            File.Copy(ini, backup);
            string pending = ini + ".bannerlordfg-pending";
            try
            {
                File.WriteAllText(pending, text, new UTF8Encoding(false));
                // Atomic replacement where supported, preserving original .bak.
                File.Replace(pending, ini, null);
                log("FG enabled: OptiFG / XeFG. FSR 3.1 upscaler selected: " + fsr31);
                log("All resolution-ratio overrides OFF. Original INI backup: " + backup);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }

        // An interrupted install is recoverable only when its pinned manifest
        // identifies every file it wrote and no unknown content is present.
        // This check is read-only; actual cleanup happens after Bannerlord exits.
        internal static bool CanRecoverIncompleteInstall(string gameBin, out string reason)
        {
            reason = null;
            try
            {
                string manifestPath = GetManifestPath(gameBin);
                if (!File.Exists(manifestPath)) { reason = "No incomplete manifest exists."; return false; }
                var manifest = ParseJsonObject(File.ReadAllText(manifestPath));
                // A manifest may be marked complete even if the user or a failed
                // prior cleanup later removed one of the two required files.
                // Such damaged installs may be repaired with the same ownership
                // checks as an interrupted install. Never take over intact ones.
                if (Str(manifest, "stage").Equals("complete", StringComparison.OrdinalIgnoreCase) &&
                    manifest.ContainsKey("postinstallFiles") &&
                    File.Exists(Path.Combine(gameBin, "dxgi.dll")) &&
                    File.Exists(Path.Combine(gameBin, "OptiScaler.ini")))
                { reason = "The installed graphics files are already present."; return false; }
                // Do not automatically adopt or delete a complete installation.
                // Older INCOMPLETE installations can be cleaned only if they were
                // created with an explicitly known release tag + official archive hash.
                string stage = Str(manifest, "stage");
                if (stage.Length > 0 && !stage.Equals("complete", StringComparison.OrdinalIgnoreCase))
                { reason = "Manifest has an unrecognized installation stage."; return false; }
                if (!Str(manifest, "gameBin").Equals(gameBin, StringComparison.OrdinalIgnoreCase))
                { reason = "Manifest points to a different Bannerlord game folder."; return false; }
                if (!IsKnownManagedArchive(Str(manifest, "tag"), Str(manifest, "archiveSha256")))
                { reason = "Manifest release tag or archive SHA256 is not a recognized BannerlordFG build."; return false; }
                object installed;
                if (!manifest.TryGetValue("installedFiles", out installed) || !(installed is object[]))
                { reason = "An incomplete manifest has no verifiable file ownership list."; return false; }
                string root = Path.GetFullPath(gameBin).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var owned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var obj in (object[])installed)
                {
                    var item = obj as Dictionary<string, object>;
                    if (item == null) { reason = "Invalid owned-file record."; return false; }
                    string rel = Str(item, "relativePath");
                    string sha = Str(item, "sha256");
                    if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel) ||
                        rel.Contains(":") || rel.Split('\\', '/').Any(part => part == "..") ||
                        !Regex.IsMatch(sha, "^[a-fA-F0-9]{64}$") || owned.ContainsKey(rel))
                    { reason = "An incomplete manifest has an unsafe or duplicate file record."; return false; }
                    owned.Add(rel, sha);
                }
                object value;
                if (manifest.TryGetValue("movedDxgiSha256", out value))
                {
                    string sha = Convert.ToString(value);
                    if (!Regex.IsMatch(sha ?? "", "^[a-fA-F0-9]{64}$"))
                    { reason = "Invalid managed DXGI checksum."; return false; }
                    owned["dxgi.dll"] = sha;
                }
                else if (owned.ContainsKey("OptiScaler.dll") &&
                    !File.Exists(Path.Combine(gameBin, "OptiScaler.dll")))
                {
                    // A crash between File.Move and committing movedDxgiSha256.
                    string proxy = Path.Combine(gameBin, "dxgi.dll");
                    if (File.Exists(proxy)) owned["dxgi.dll"] = owned["OptiScaler.dll"];
                }
                if (manifest.TryGetValue("postinstallFiles", out value) &&
                    value is Dictionary<string, object>)
                {
                    foreach (var pair in (Dictionary<string, object>)value)
                    {
                        // Final post-install hashes can differ only for files
                        // already tracked or the explicitly created DXGI/INI backup.
                        if (!owned.ContainsKey(pair.Key) && pair.Key != "dxgi.dll" &&
                            pair.Key != "OptiScaler.ini.bannerlordfg-original.bak")
                        { reason = "A finished manifest contains an unrecognized file."; return false; }
                        string sha = Convert.ToString(pair.Value);
                        if (!Regex.IsMatch(sha ?? "", "^[a-fA-F0-9]{64}$"))
                        { reason = "Invalid final file checksum."; return false; }
                        owned[pair.Key] = sha;
                    }
                }
                if (manifest.TryGetValue("expectedBackupSha256", out value))
                {
                    string sha = Convert.ToString(value);
                    if (!Regex.IsMatch(sha ?? "", "^[a-fA-F0-9]{64}$"))
                    { reason = "Invalid managed backup checksum."; return false; }
                    owned["OptiScaler.ini.bannerlordfg-original.bak"] = sha;
                }
                foreach (var pair in owned)
                {
                    string target = Path.GetFullPath(Path.Combine(gameBin, pair.Key));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    { reason = "A manifest file points outside the game directory."; return false; }
                    for (string cursor = target; cursor.Length > root.Length; cursor = Path.GetDirectoryName(cursor))
                        if (File.Exists(cursor) || Directory.Exists(cursor))
                            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                            { reason = "A managed path contains a reparse point."; return false; }
                    if (File.Exists(target) && !HashFile(target).Equals(pair.Value, StringComparison.OrdinalIgnoreCase) &&
                        !IsOwnedConfig(pair.Key))
                    { reason = "A previously installed binary was modified: " + pair.Key; return false; }
                }
                // Never claim foreign files placed in the OptiScaler directory,
                // nor overwrite a proxy whose bytes are not listed above.
                string nested = Path.Combine(gameBin, "OptiScaler");
                if (Directory.Exists(nested))
                {
                    if ((File.GetAttributes(nested) & FileAttributes.ReparsePoint) != 0)
                    { reason = "OptiScaler directory is a reparse point."; return false; }
                    var directories = new Stack<string>();
                    directories.Push(nested);
                    while (directories.Count != 0)
                    {
                        string directory = directories.Pop();
                        foreach (string child in Directory.GetFileSystemEntries(directory))
                        {
                            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                            { reason = "A graphics directory contains a reparse point."; return false; }
                            if (Directory.Exists(child)) directories.Push(child);
                            else if (!owned.ContainsKey(child.Substring(root.Length)))
                            { reason = "An untracked file exists in the OptiScaler directory: " + child; return false; }
                        }
                    }
                }
                foreach (string candidate in new[] { "dxgi.dll", "OptiScaler.dll", "OptiScaler.ini",
                    "OptiScaler.ini.bannerlordfg-original.bak" })
                    if (File.Exists(Path.Combine(gameBin, candidate)) && !owned.ContainsKey(candidate))
                    { reason = "An untracked graphics/configuration file already exists: " + candidate; return false; }
                return true;
            }
            catch (Exception ex) { reason = "Could not verify interrupted installation: " + ex.Message; return false; }
        }

        internal static void RecoverIncompleteInstall(string gameBin, Action<string> log)
        {
            if (BannerlordIsRunning()) throw new InvalidOperationException("Close Bannerlord before recovering graphics files.");
            string reason;
            if (!CanRecoverIncompleteInstall(gameBin, out reason))
                throw new InvalidDataException("Interrupted installation cannot be recovered automatically: " + reason);
            string manifest = GetManifestPath(gameBin);
            string folder = Path.Combine(AutoFirstRun.Folder, "recovery",
                "interrupted-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string snapshot = Path.Combine(folder, "install-manifest.json");
            File.Copy(manifest, snapshot, false);
            if (!HashFile(manifest).Equals(HashFile(snapshot), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Interrupted-install manifest backup failed verification. No files changed.");
            log("Verified interrupted-install manifest snapshot: " + snapshot);
            Uninstall(gameBin, log); // removes ONLY checksum-matched owned files
            if (File.Exists(manifest))
                throw new IOException("Recovery stopped: modified or unknown files remain. Manifest and backups preserved.");
            if (ExistingInstallStatus(gameBin) != null)
                throw new IOException("Recovery stopped: untracked OptiScaler files remain; do not overwrite them.");
            log("Interrupted installation recovered safely. Reinstall can now proceed.");
        }

        private static string GetManifestPath(string gameBin)
        {
            string manifestDir = AutoFirstRun.Folder;
            return Path.Combine(manifestDir, "install-" + HashShort(gameBin) + ".json");
        }

        // OptiScaler rewrites its INI when the player changes graphics options.
        // A changed configuration still belongs to this manifest, but its edits
        // must survive uninstall. Move a VERIFIED copy to per-attempt recovery
        // storage instead of retaining the manifest forever.
        private static bool IsOwnedConfig(string relativePath)
        {
            return relativePath.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase) ||
                relativePath.Equals("OptiScaler.ini.bannerlordfg-original.bak", StringComparison.OrdinalIgnoreCase);
        }

        private static string BackupChangedConfig(string source, string relativeName,
            string destinationFolder, Action<string> log)
        {
            Directory.CreateDirectory(destinationFolder);
            string destination = Path.Combine(destinationFolder, Path.GetFileName(relativeName));
            if (File.Exists(destination)) throw new IOException("Recovery destination is occupied: " + destination);
            string originalHash = HashFile(source);
            // The original must remain in the game folder until its backup
            // has been copied and independently verified.
            File.Copy(source, destination, false);
            if (!HashFile(destination).Equals(originalHash, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(destination); } catch { }
                throw new IOException("Recovery backup checksum mismatch. Original preserved: " + source);
            }
            File.Delete(source);
            log("Modified managed config preserved in verified recovery backup: " + destination);
            return destination;
        }

        internal static void Uninstall(string gameBin, Action<string> log)
        {
            if (BannerlordIsRunning()) throw new InvalidOperationException("Close Bannerlord before uninstalling.");
            string manifestPath = GetManifestPath(gameBin);
            if (!File.Exists(manifestPath)) throw new FileNotFoundException("BannerlordFG install manifest not found", manifestPath);
            var m = ParseJsonObject(File.ReadAllText(manifestPath));
            if (!Str(m, "gameBin").Equals(gameBin, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Manifest does not belong to this Bannerlord installation.");
            var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            object obj;
            if (m.TryGetValue("postinstallFiles", out obj) && obj is Dictionary<string, object>)
            {
                foreach (var kv in (Dictionary<string, object>)obj) checksums[kv.Key] = Convert.ToString(kv.Value);
            }
            else if (m.TryGetValue("installedFiles", out obj) && obj is object[])
            {
                foreach (var e in (object[])obj)
                {
                    var item = e as Dictionary<string, object>;
                    if (item != null) checksums[Str(item, "relativePath")] = Str(item, "sha256");
                }
            }
            object partialValue;
            if (m.TryGetValue("movedDxgiSha256", out partialValue))
                checksums["dxgi.dll"] = Convert.ToString(partialValue);
            else if (!checksums.ContainsKey("dxgi.dll") && checksums.ContainsKey("OptiScaler.dll") &&
                !File.Exists(Path.Combine(gameBin, "OptiScaler.dll")))
            {
                // An interrupted setup may have moved the official proxy just
                // before writing movedDxgiSha256. Adopt it ONLY if its hash is
                // identical to the manifest-owned OptiScaler.dll at that point.
                string renamed = Path.Combine(gameBin, "dxgi.dll");
                if (File.Exists(renamed) && HashFile(renamed).Equals(
                    checksums["OptiScaler.dll"], StringComparison.OrdinalIgnoreCase))
                    checksums["dxgi.dll"] = checksums["OptiScaler.dll"];
            }
            if (m.TryGetValue("expectedBackupSha256", out partialValue))
                checksums["OptiScaler.ini.bannerlordfg-original.bak"] = Convert.ToString(partialValue);
            if (!m.ContainsKey("postinstallFiles") && m.TryGetValue("configuredIniSha256", out partialValue))
                checksums["OptiScaler.ini"] = Convert.ToString(partialValue);
            string root = Path.GetFullPath(gameBin).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var skipped = new List<string>();
            var removed = 0;
            string recoveryFolder = null;
            foreach (var kv in checksums)
            {
                string target = Path.GetFullPath(Path.Combine(gameBin, kv.Key));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Manifest contains a path outside the game executable folder.");
                if (!File.Exists(target)) continue;
                // Do not traverse symlinked game files or parent directories
                // recorded in a compromised or externally altered manifest.
                for (string cursor = target; cursor.Length > root.Length;
                    cursor = Path.GetDirectoryName(cursor))
                {
                    if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Tracked path is a reparse point: " + target);
                }
                if (!HashFile(target).Equals(kv.Value, StringComparison.OrdinalIgnoreCase))
                {
                    if (IsOwnedConfig(kv.Key))
                    {
                        if (recoveryFolder == null)
                            recoveryFolder = Path.Combine(
                                AutoFirstRun.Folder, "recovery", "uninstall-" +
                                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
                        BackupChangedConfig(target, kv.Key, recoveryFolder, log);
                        removed++;
                    }
                    else skipped.Add(kv.Key + " (modified DLL/asset; NOT removed)");
                    continue;
                }
                File.Delete(target); removed++;
            }
            // Only remove empty directories corresponding to tracked files.
            // Never delete the game-bin root or recurse into user content.
            var ownedDirs = checksums.Keys.Select(k => Path.GetDirectoryName(Path.GetFullPath(Path.Combine(gameBin, k))))
                .Where(d => d.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(d => d.Length);
            foreach (var d in ownedDirs)
                if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            string optiFolder = Path.Combine(gameBin, "OptiScaler");
            if (Directory.Exists(optiFolder))
            {
                foreach (var d in Directory.GetDirectories(optiFolder, "*", SearchOption.AllDirectories)
                    .OrderByDescending(x => x.Length))
                    if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                if (!Directory.EnumerateFileSystemEntries(optiFolder).Any()) Directory.Delete(optiFolder);
            }
            log("Removed " + removed + " files, without overwriting or deleting untracked files.");
            if (skipped.Count > 0)
                foreach (var f in skipped) log("Manual check: " + f);
            string ini = Path.Combine(gameBin, "OptiScaler.ini");
            if (File.Exists(ini)) log("Some OptiScaler files remain; inspect the game folder before installing again.");
            if (skipped.Count == 0 && !checksums.Keys.Any(x => File.Exists(Path.Combine(gameBin, x))))
            {
                File.Delete(manifestPath);
                log("Install manifest removed; clean removal completed for all tracked files.");
                if (recoveryFolder != null)
                    log("User-modified INI files were preserved outside the game: " + recoveryFolder);
            }
            else
            {
                log("Manifest retained: unrecognized or modified tracked files require manual review: " + manifestPath);
                if (recoveryFolder != null)
                    log("Previously recovered user configs: " + recoveryFolder);
            }
        }

        internal static async Task InstallAsync(string gameBin, string vendor, bool useFsr31, Action<string> log, bool unattended = false, PreparedPackage prepared = null)
        {
            if (vendor != "NVIDIA" && vendor != "AMD" && vendor != "Intel")
                throw new InvalidOperationException("Choose NVIDIA, AMD or Intel.");
            if (BannerlordIsRunning()) throw new InvalidOperationException("Close the game before installing.");
            if (File.Exists(Path.Combine(gameBin, "OptiScaler.ini")) || Directory.Exists(Path.Combine(gameBin, "OptiScaler")))
                throw new InvalidOperationException("OptiScaler is already installed. This version will not overwrite it; back up/remove the old installation first.");
            foreach (var proxy in Proxies)
            {
                string proxyPath = Path.Combine(gameBin, proxy);
                if (File.Exists(proxyPath) && !IsMicrosoftDbgHelp(proxyPath))
                    throw new InvalidOperationException("Existing graphics DLL detected: " + proxy + ". Resolve possible ReShade conflicts before installation.");
            }
            if (Directory.GetFiles(gameBin, "*.addon64", SearchOption.TopDirectoryOnly).Any())
                throw new InvalidOperationException("A ReShade addon is present. Uninstall/disable the old BannerlordFG before this test.");
            if (File.Exists(GetManifestPath(gameBin)))
                throw new IOException("A previous BannerlordFG install manifest exists. Use Safe uninstall or inspect it before reinstalling.");
            bool ownsPrepared = prepared == null;
            PreparedPackage pkg = prepared;
            bool copiedFiles = false;
            try
            {
                if (pkg == null) pkg = await PrepareOfficialPackageAsync(log);
                pkg.Verify();
                string folder = pkg.Folder;
                string extracted = Path.Combine(folder, "unpacked");
                Directory.CreateDirectory(extracted);
                string url = pkg.OfficialUrl;
                string actual = pkg.Sha256;
                log("Extracting the verified official 7z after Bannerlord has closed...");
                await Task.Run(() => ExtractSafe(pkg.Archive, extracted));
                string root = FindPackageRoot(extracted);
                var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
                if (files.Length == 0) throw new InvalidDataException("Official package is empty.");
                var relative = files.Select(f => f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar)).ToArray();
                var collisions = relative.Where(f => File.Exists(Path.Combine(gameBin, f))).ToArray();
                if (collisions.Length > 0)
                    throw new IOException("Installation stopped: these target files already exist: " + string.Join(", ", collisions.Take(8)));
                // Record which newly copied files we own; never delete or overwrite files not on this list.
                var manifest = new Dictionary<string, object> {
                    { "gameBin", gameBin }, { "tag", Tag }, { "gpu", vendor },
                    { "source", url }, { "archiveSha256", actual },
                    { "timeUtc", DateTime.UtcNow.ToString("o") },
                    { "installedFiles", relative.Select((f,i) =>
                        new Dictionary<string, string> {{"relativePath",f}, {"sha256", HashFile(files[i])}}).ToArray() }
                };
                string manifestPath = GetManifestPath(gameBin);
                Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
                if (File.Exists(manifestPath)) throw new IOException("A previous BannerlordFG install manifest exists; review it before reinstalling: " + manifestPath);
                File.WriteAllText(manifestPath, new JavaScriptSerializer().Serialize(manifest));
                try
                {
                    for (int i = 0; i < files.Length; ++i)
                    {
                        string target = Path.Combine(gameBin, relative[i]);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(files[i], target, false);
                    }
                }
                catch
                {
                    // Roll back only newly written files with original, unmodified SHA256.
                    for (int i = 0; i < files.Length; ++i)
                    {
                        string target = Path.Combine(gameBin, relative[i]);
                        if (File.Exists(target) && HashFile(target) == HashFile(files[i])) File.Delete(target);
                    }
                    throw;
                }
                copiedFiles = true;
                log("Copied " + files.Length + " original OptiScaler files. Manifest: " + manifestPath);
                if (unattended)
                {
                    // The pinned official setup's noninteractive essentials for a DX11 game:
                    // rename the official OptiScaler.dll proxy to dxgi.dll and retain
                    // default DXGI spoofing on AMD/Intel so Bannerlord exposes DLSS inputs.
                    // We do not run upstream's optional OptiPatcher installer automatically.
                    string originalProxy = Path.Combine(gameBin, "OptiScaler.dll");
                    string renamedProxy = Path.Combine(gameBin, "dxgi.dll");
                    if (!File.Exists(originalProxy) || File.Exists(renamedProxy))
                        throw new IOException("Official injector DLL missing or dxgi.dll already exists. Setup stopped safely.");
                    File.Move(originalProxy, renamedProxy);
                    // Crash/reconfiguration failures must not strand an untracked
                    // renamed injector. Commit its ownership before patching INI.
                    manifest["movedDxgiSha256"] = HashFile(renamedProxy);
                    File.WriteAllText(manifestPath, new JavaScriptSerializer().Serialize(manifest));
                    log("Official OptiScaler.dll installed as dxgi.dll for Bannerlord DX11.");
                }
                else
                {
                    log("Launching the OFFICIAL upstream setup. Select " + vendor + "." +
                        (vendor == "NVIDIA" ? "" : " Answer YES to DLSS Inputs (required for Bannerlord)."));
                    var upstream = Directory.GetFiles(gameBin, "*.bat", SearchOption.TopDirectoryOnly)
                        .Where(f => Path.GetFileName(f).IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                    if (upstream.Length != 1)
                        throw new InvalidDataException("Cannot uniquely identify the official setup.bat. Files have been copied, but no setup was run. Use the release documentation.");
                    // This console belongs to upstream, not to a user-facing BannerlordFG .cmd installer.
                    int exit = await Task.Run(() =>
                    {
                        using (var p = new Process())
                        {
                            p.StartInfo = new ProcessStartInfo {
                                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                                Arguments = "/d /c call \"" + upstream[0] + "\"",
                                WorkingDirectory = gameBin, UseShellExecute = true
                            };
                            if (!p.Start()) throw new IOException("Failed to launch the official setup.");
                            p.WaitForExit();
                            return p.ExitCode;
                        }
                    });
                    if (exit != 0) throw new IOException("Upstream setup exited with code " + exit + ". Files are left for manual recovery; see manifest.");
                }
                string ini = Path.Combine(gameBin, "OptiScaler.ini");
                if (!File.Exists(ini)) throw new IOException("Official setup finished but OptiScaler.ini is missing.");
                if (unattended)
                {
                    manifest["expectedBackupSha256"] = HashFile(ini);
                    File.WriteAllText(manifestPath, new JavaScriptSerializer().Serialize(manifest));
                }
                Configure(ini, useFsr31, log, unattended && vendor == "NVIDIA");
                manifest["configuredIniSha256"] = HashFile(ini);
                File.WriteAllText(manifestPath, new JavaScriptSerializer().Serialize(manifest));
                if (unattended && vendor != "NVIDIA")
                {
                    // The official upstream BAT uses Dxgi=auto for AMD/Intel when
                    // DLSS Inputs are enabled. Set it explicitly in unattended mode.
                    string pre = File.ReadAllText(ini);
                    string configured = PatchIni(pre, "Spoofing", "Dxgi", "true");
                    string temp = ini + ".vendor-pending";
                    File.WriteAllText(temp, configured, new UTF8Encoding(false));
                    File.Replace(temp, ini, null);
                    log("AMD/Intel: DXGI spoofing enabled for DLSS inputs.");
                }
                var finalFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rel in relative)
                {
                    string file = Path.Combine(gameBin, rel);
                    if (File.Exists(file)) finalFiles[rel] = HashFile(file);
                }
                // Track ONLY the proxy created by our own File.Move above.
                // Never adopt an external proxy merely because it appears in the
                // same game directory between initial scan and manifest creation.
                if (unattended)
                {
                    const string createdProxy = "dxgi.dll";
                    string proxyPath = Path.Combine(gameBin, createdProxy);
                    if (File.Exists(proxyPath)) finalFiles[createdProxy] = HashFile(proxyPath);
                }
                string cfgBackup = ini + ".bannerlordfg-original.bak";
                if (File.Exists(cfgBackup)) finalFiles[Path.GetFileName(cfgBackup)] = HashFile(cfgBackup);
                manifest["postinstallFiles"] = finalFiles;
                manifest["stage"] = "complete";
                File.WriteAllText(manifestPath, new JavaScriptSerializer().Serialize(manifest));
                log("Install finished. Select DLSS Quality in Bannerlord as the INPUT, even when FSR handles upscaling.");
                log("Restart Bannerlord. Use the OptiScaler overlay to confirm XeFG is active.");
            }
            catch (Exception ex)
            {
                if (copiedFiles) log("Files were copied before error. Do NOT reinstall blindly; inspect the install manifest and the game folder.");
                log("Install failed safely: " + ex.Message);
                throw;
            }
            finally
            {
                if (ownsPrepared && pkg != null) pkg.Dispose();
            }
        }

        private static string HashShort(string s)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(s.ToLowerInvariant())))
                    .Replace("-", "").Substring(0, 12);
        }
    }
}

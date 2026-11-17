// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BannerlordFG.Common;
using System.Web.Script.Serialization;

namespace BannerlordFG.Setup
{
    /// <summary>Pinned OptiScaler downloader. OptiScaler is not bundled; extraction uses SharpCompress.</summary>
    internal static class OptiInstaller
    {
        internal const string Tag = "nightly-20261116";
        internal const string AssetName = "OptiScaler_v10.0.0-pre1_20261116.7z";
        // SHA-256 published by GitHub for the official 2026-11-16 release asset.
        // Keep this pin immutable; do not silently use newer nightlies.
        internal const string PinnedSha256 = "6cd5704b47ff883666df7fec80ce55d113ecc1097a1db5caa3a1058bc007c2d0";
        // Historical release/digest pairs are accepted ONLY for hash-verified
        // recovery and uninstall. They are never downloaded by this version.
        // v1.1.2's 20261112 installations must remain uninstallable after upgrade.
        private const string V112Tag = "nightly-20261112";
        private const string V112PinnedSha256 = "c32932ca340ffd605325101077b072209514ed0f110dc8b7ab92486eec1a5d93";
        // v1.1.1's 20261109 installations are also recognized for recovery.
        private const string PriorTag = "nightly-20261109";
        private const string PriorPinnedSha256 = "e65cfc7092840cde79b24467bf0b3404bc358e56866478f46811eed06ffaa150";
        private const string OlderTag = "nightly-20261106";
        private const string OlderPinnedSha256 = "5b92e637cdead99c8d994360850407e93876f00b5f0776f2be39a1ee2caa5726";
        private const string PreviousTag = "nightly-20261016";
        private const string PreviousPinnedSha256 = "09149a5b17514a0594faf612467267c62ed8851dbc1dde0ade6763f1152e600f";
        private const string LegacyTag = "nightly-20260926";
        private const string LegacyPinnedSha256 = "464e2ecc5dab477e940186959a7fdee64880f61c63f52ff0f516e5502bc74e94";
        private static bool IsKnownManagedArchive(string tag, string digest)
        {
            return (string.Equals(tag, Tag, StringComparison.Ordinal) &&
                    string.Equals(digest, PinnedSha256, StringComparison.OrdinalIgnoreCase)) ||
                   (string.Equals(tag, V112Tag, StringComparison.Ordinal) &&
                    string.Equals(digest, V112PinnedSha256, StringComparison.OrdinalIgnoreCase)) ||
                   (string.Equals(tag, PriorTag, StringComparison.Ordinal) &&
                    string.Equals(digest, PriorPinnedSha256, StringComparison.OrdinalIgnoreCase)) ||
                   (string.Equals(tag, OlderTag, StringComparison.Ordinal) &&
                    string.Equals(digest, OlderPinnedSha256, StringComparison.OrdinalIgnoreCase)) ||
                   (string.Equals(tag, PreviousTag, StringComparison.Ordinal) &&
                    string.Equals(digest, PreviousPinnedSha256, StringComparison.OrdinalIgnoreCase)) ||
                   (string.Equals(tag, LegacyTag, StringComparison.Ordinal) &&
                    string.Equals(digest, LegacyPinnedSha256, StringComparison.OrdinalIgnoreCase));
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

        // Only the proxy named in the manifest belongs to this installation.
        // Legacy v1.0.4 manifests have no proxyFileName and always used dxgi.dll.
        private static string ManifestProxyName(Dictionary<string, object> manifest)
        {
            string proxy = Str(manifest, "proxyFileName");
            if (string.IsNullOrEmpty(proxy)) return "dxgi.dll";
            if (proxy.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase)) return "dxgi.dll";
            if (proxy.Equals("winmm.dll", StringComparison.OrdinalIgnoreCase)) return "winmm.dll";
            throw new InvalidDataException("Unrecognized managed proxy name: " + proxy);
        }

        internal static string GetManagedProxyName(string gameBin)
        {
            string path = GetManifestPath(gameBin);
            if (!File.Exists(path)) return null;
            return ManifestProxyName(ParseJsonObject(File.ReadAllText(path)));
        }

        internal static bool HasCompleteManagedInstall(string gameBin)
        {
            try
            {
                string path = GetManifestPath(gameBin);
                if (!File.Exists(path) || !File.Exists(Path.Combine(gameBin, "OptiScaler.ini"))) return false;
                var manifest = ParseJsonObject(File.ReadAllText(path));
                if (!Str(manifest, "stage").Equals("complete", StringComparison.OrdinalIgnoreCase) ||
                    !manifest.ContainsKey("postinstallFiles")) return false;
                var plan = VerifiedRemovalPlan(gameBin, path);
                return plan.Count > 0 && plan.All(item => TrackedFileExists(item.Path));
            }
            catch { return false; }
        }

        private static bool LooksLikeOptiScaler(string path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                return (info.OriginalFilename ?? "").IndexOf("OptiScaler", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (info.ProductName ?? "").IndexOf("OptiScaler", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        // Prefer WinMM in Bannerlord (verified to initialize the overlay). DXGI
        // remains the fallback if WinMM is already occupied by another mod.
        // NEVER overwrite a third-party proxy or install both BannerlordFG proxies.
        private static string SelectProxyName(string gameBin)
        {
            bool winmmOccupied = File.Exists(Path.Combine(gameBin, "winmm.dll"));
            bool dxgiOccupied = File.Exists(Path.Combine(gameBin, "dxgi.dll"));
            if (!winmmOccupied) return "winmm.dll";
            if (!dxgiOccupied) return "dxgi.dll";
            throw new IOException("Both winmm.dll and dxgi.dll are occupied; neither will be overwritten.");
        }

        internal static string ExistingInstallStatus(string gameBin)
        {
            if (HasManagedInstall(gameBin))
                return HasCompleteManagedInstall(gameBin)
                    ? "BannerlordFG-managed OptiScaler is already installed."
                    : "BannerlordFG-managed OptiScaler is incomplete; use verified recovery before reinstalling.";
            if (File.Exists(Path.Combine(gameBin, "OptiScaler.ini")))
                return "OptiScaler.ini already exists: preserving the user's external installation.";
            if (Directory.Exists(Path.Combine(gameBin, "OptiScaler")))
                return "OptiScaler directory already exists; installation may be incomplete. No changes made.";

            // Never adopt another standalone OptiScaler or overwrite a foreign
            // proxy. An arbitrary ReShade/proxy DLL occupying *one* filename is
            // permitted; the other filename remains available for BannerlordFG.
            foreach (string proxy in Proxies)
            {
                string path = Path.Combine(gameBin, proxy);
                if (!File.Exists(path)) continue;
                if (LooksLikeOptiScaler(path))
                    return proxy + " already contains OptiScaler without our manifest. Preserve it for manual recovery.";
                // winmm.dll and dxgi.dll are the ONLY possible BannerlordFG
                // proxy filenames. A pre-existing d3d11.dll is an external hook
                // and does not occupy either of our selected proxy names.
                // Do not claim, modify or overwrite it. Continue to reject a
                // standalone OptiScaler (checked above) and all other hooks.
                if (proxy.Equals("winmm.dll", StringComparison.OrdinalIgnoreCase) ||
                    proxy.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase) ||
                    proxy.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase))
                    continue;
                return proxy + " is already occupied by another component. Leaving it untouched.";
            }
            if (File.Exists(Path.Combine(gameBin, "winmm.dll")) &&
                File.Exists(Path.Combine(gameBin, "dxgi.dll")))
                return "Both winmm.dll and dxgi.dll exist. No free supported proxy name; nothing will be overwritten.";
            return null;
        }

        internal static string ValidateGameBin(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Choose the Bannerlord folder.");
            string p = Path.GetFullPath(raw.Trim().Trim('"'));
            if (Directory.Exists(Path.Combine(p, "bin", "Win64_Shipping_Client")))
                p = Path.Combine(p, "bin", "Win64_Shipping_Client");
            if (!Directory.Exists(p) || !new[] { "Bannerlord.exe", "Bannerlord.Native.exe",
                "TaleWorlds.MountAndBlade.Launcher.exe", "Launcher.Native.exe" }
                .Any(exe => File.Exists(Path.Combine(p, exe))))
                throw new InvalidOperationException("This is not a Bannerlord executable directory.");
            return p;
        }

        // Paths of only the DLLs recorded by our manifest. A ReShade DLL (or
        // another external injector) is NOT treated as a managed OptiScaler
        // dependency unless the validated manifest explicitly owns that path.
        private static HashSet<string> ManagedRuntimeDllPaths(string gameBin)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string manifestPath = GetManifestPath(gameBin);
            if (!File.Exists(manifestPath)) return paths;
            var manifest = ParseJsonObject(File.ReadAllText(manifestPath));
            string root = Path.GetFullPath(gameBin).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Action<string> add = relative =>
            {
                if (string.IsNullOrEmpty(relative) ||
                    !relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return;
                string full = Path.GetFullPath(Path.Combine(gameBin, relative));
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) paths.Add(full);
            };
            object installed;
            if (manifest.TryGetValue("installedFiles", out installed) && installed is object[])
                foreach (object item in (object[])installed)
                {
                    var row = item as Dictionary<string, object>;
                    if (row != null) add(Str(row, "relativePath"));
                }
            object post;
            if (manifest.TryGetValue("postinstallFiles", out post))
            {
                var entries = post as Dictionary<string, object>;
                if (entries != null) foreach (string relative in entries.Keys) add(relative);
            }
            add(ManifestProxyName(manifest));
            return paths;
        }

        // Check only this game's recognized EXE (full path, never PID/name alone).
        // A launcher is busy when its in-game module is alive OR a managed DLL
        // remains loaded; an ordinary idle launcher is not a game process.
        internal static bool IsGameProcessBusy(Process process, string gameBin)
        {
            try
            {
                if (process.HasExited ||
                    GpuDetection.ExpectedExecutableName(process.ProcessName) == null) return false;
                if (!GpuDetection.IsExpectedGameProcess(process, gameBin, _ => { }))
                {
                    // If Windows denied both MainModule and WMI, we cannot
                    // prove the DLL isn't loaded. Fail closed, but never block
                    // a process whose path is positively in another directory.
                    try
                    {
                        string actual = process.MainModule.FileName;
                        if (!string.IsNullOrEmpty(actual)) return false;
                    }
                    catch (System.ComponentModel.Win32Exception) { return true; }
                    catch (InvalidOperationException) { return false; }
                    return true;
                }
                bool direct = GpuDetection.IsDirectGameProcess(process.ProcessName);
                bool activeModule = GameHostMarker.IsActive(gameBin, process.Id);
                if (direct || activeModule) return true;
                var managed = ManagedRuntimeDllPaths(gameBin);
                try
                {
                    foreach (ProcessModule module in process.Modules)
                    {
                        string path = Path.GetFullPath(module.FileName);
                        if (managed.Contains(path)) return true;
                    }
                    return GameRunningDecision.IsBusy(false, false, false, true);
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return GameRunningDecision.IsBusy(false, false, false, false);
                }
                catch (InvalidOperationException) { return false; } // exited
            }
            catch (ArgumentException) { return false; } // exited/reused
            catch (InvalidOperationException) { return false; } // exited
            catch (Exception) { return true; } // cannot prove unloaded
        }

        internal static bool IsGameProcessBusy(int pid, string gameBin)
        {
            if (pid <= 0) return false;
            try
            {
                using (Process process = Process.GetProcessById(pid))
                    return IsGameProcessBusy(process, gameBin);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (Exception) { return true; }
        }

        internal static bool BannerlordIsRunning(string gameBin)
        {
            foreach (string name in GpuDetection.GameProcessNames)
            {
                Process[] processes;
                try { processes = Process.GetProcessesByName(name); }
                catch (InvalidOperationException) { return true; }
                catch (System.ComponentModel.Win32Exception) { return true; }
                foreach (Process process in processes)
                {
                    using (process)
                        if (IsGameProcessBusy(process, gameBin)) return true;
                }
            }
            return false;
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
            // Extract into a private temporary folder while Bannerlord is still open.
            // The closed-game phase only copies files and commits the manifest.
            internal string ExtractedRoot;
            internal Dictionary<string, string> ExtractedHashes;
            internal void Verify()
            {
                if (string.IsNullOrEmpty(Archive) || !File.Exists(Archive) ||
                    !HashFile(Archive).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Prepared release archive was modified or removed.");
                if (string.IsNullOrEmpty(ExtractedRoot) || ExtractedHashes == null ||
                    ExtractedHashes.Count == 0 || !Directory.Exists(ExtractedRoot))
                    throw new InvalidDataException("Verified OptiScaler extraction is not ready.");
                string unpacked = Path.GetFullPath(Path.Combine(Folder, "unpacked"))
                    .TrimEnd(Path.DirectorySeparatorChar);
                string extracted = Path.GetFullPath(ExtractedRoot).TrimEnd(Path.DirectorySeparatorChar);
                if (!string.Equals(extracted, unpacked, StringComparison.OrdinalIgnoreCase) &&
                    !extracted.StartsWith(unpacked + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Extracted OptiScaler package escaped its temporary directory.");
                var files = Directory.GetFiles(ExtractedRoot, "*", SearchOption.AllDirectories);
                if (files.Length != ExtractedHashes.Count)
                    throw new InvalidDataException("Prepared OptiScaler file list was modified.");
                foreach (string file in files)
                {
                    string relative = file.Substring(ExtractedRoot.Length).TrimStart(Path.DirectorySeparatorChar);
                    string expected;
                    if (!ExtractedHashes.TryGetValue(relative, out expected) ||
                        !HashFile(file).Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Prepared OptiScaler file changed: " + relative);
                }
            }
            public void Dispose()
            {
                if (!string.IsNullOrEmpty(Folder))
                    try { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); }
                    catch { /* Cache cleanup must not mask an install failure. */ }
            }
        }

        internal static async Task<PreparedPackage> PrepareOfficialPackageAsync(Action<string> log,
            Action<string, int, long, long> progress = null)
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
                if (progress != null) progress("connecting", -1, 0, 0);
                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "BannerlordFG-setup/1.1.3";
                    client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                    json = await WithDownloadDeadline(client.DownloadStringTaskAsync(Api), client,
                        TimeSpan.FromSeconds(35), "GitHub release metadata");
                }
                var release = SelectOfficialAsset(ParseJsonObject(json));
                log("Release metadata received. Selecting and verifying the official archive...");
                pkg.OfficialUrl = Str(release, "browser_download_url");
                long declaredAssetBytes = long.Parse(Str(release, "size")); // Validated by SelectOfficialAsset.
                string declaredDigest = Str(release, "digest");
                if (declaredDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
                    !declaredDigest.Substring(7).Equals(PinnedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Official GitHub release asset digest changed: refusing untested build.");
                pkg.Archive = Path.Combine(pkg.Folder, "official-optiscaler.7z");
                log("Downloading pinned official build: " + Tag);
                if (progress != null) progress("downloading", 0, 0, 0);
                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "BannerlordFG-setup/1.1.3";
                    int nextLogPercent = 25;
                    int lastReportedBucket = -1;
                    client.DownloadProgressChanged += (sender, args) => {
                        // Use real byte counts. A missing Content-Length means
                        // progress is indeterminate; never invent a percentage.
                        // GitHub's release metadata provides a validated size
                        // even when the download response omits Content-Length.
                        long total = args.TotalBytesToReceive > 0
                            ? args.TotalBytesToReceive : declaredAssetBytes;
                        long received = args.BytesReceived;
                        int percent = total > 0
                            ? (int)Math.Min(100L, received * 100L / total) : -1;
                        int bucket = percent >= 0 ? percent / 10 :
                            (int)Math.Min(int.MaxValue, received / (1024L * 1024L));
                        if (bucket != lastReportedBucket)
                        {
                            lastReportedBucket = bucket;
                            if (progress != null)
                                progress("downloading", percent >= 0 ? bucket * 10 : -1,
                                    received, total);
                        }
                        if (percent >= nextLogPercent)
                        {
                            log("Official archive download " + percent + "% (" +
                                received + " bytes received).");
                            nextLogPercent = ((percent / 25) + 1) * 25;
                        }
                    };
                    await WithDownloadDeadline(client.DownloadFileTaskAsync(
                        new Uri(pkg.OfficialUrl), pkg.Archive), client,
                        TimeSpan.FromMinutes(4), "OptiScaler release archive");
                }
                if (progress != null)
                    progress("verifying", -1, new FileInfo(pkg.Archive).Length, 0);
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
                // The 7z extraction used to consume about a minute AFTER the
                // game closed. Users could reopen Bannerlord during that gap,
                // racing the graphics DLL copy and suppressing the relaunch.
                // Unpack the hash-verified archive into temporary storage NOW,
                // without touching the game's bin folder while it is running.
                if (progress != null) progress("extracting", -1, 0, 0);
                log("Extracting verified OptiScaler archive in temporary storage before game shutdown...");
                string unpacked = Path.Combine(pkg.Folder, "unpacked");
                Directory.CreateDirectory(unpacked);
                var extractionTimer = Stopwatch.StartNew();
                await Task.Run(() => ExtractSafe(pkg.Archive, unpacked));
                log("Sequential 7z extraction finished in " + extractionTimer.ElapsedMilliseconds + " ms.");
                var validationTimer = Stopwatch.StartNew();
                pkg.ExtractedRoot = FindPackageRoot(unpacked);
                var extractedFiles = Directory.GetFiles(pkg.ExtractedRoot, "*", SearchOption.AllDirectories);
                if (extractedFiles.Length == 0) throw new InvalidDataException("Official package is empty.");
                pkg.ExtractedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in extractedFiles)
                {
                    string relative = file.Substring(pkg.ExtractedRoot.Length).TrimStart(Path.DirectorySeparatorChar);
                    if (pkg.ExtractedHashes.ContainsKey(relative))
                        throw new InvalidDataException("Duplicate extracted file name: " + relative);
                    pkg.ExtractedHashes.Add(relative, HashFile(file));
                }
                pkg.Verify();
                log("Extracted file hashing and verification finished in " + validationTimer.ElapsedMilliseconds + " ms.");
                log("OptiScaler archive and extracted files verified. Game files remain untouched until shutdown.");
                if (progress != null)
                    progress("verified", 100, new FileInfo(pkg.Archive).Length, 0);
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

        // Applied only while installing a newly copied, manifest-owned upstream
        // INI. Startup, live controls and recovery never reset the user's later
        // logging choices. Preserve unrelated keys and the file's line endings.
        internal static string ConfigureLoggingDefaults(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var sectionRx = new Regex(@"(?mi)^\[Log\][ \t]*\r?\n(?<body>[\s\S]*?)(?=^\[|\z)");
            var sections = sectionRx.Matches(text);
            if (sections.Count > 1)
                throw new InvalidDataException("OptiScaler config has duplicate [Log] sections.");
            string[] keys = { "LogToFile", "LogLevel", "SingleFile" };
            string[] values = { "true", "2", "true" };
            if (sections.Count == 0)
                return text + (text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal)
                    ? newline : "") + "[Log]" + newline +
                    string.Join(newline, keys.Select((key, i) => key + " = " + values[i])) + newline;
            var body = sections[0].Groups["body"];
            string updated = body.Value;
            for (int i = 0; i < keys.Length; i++)
            {
                var keyRx = new Regex(@"(?mi)^[ \t]*" + Regex.Escape(keys[i]) + @"[ \t]*=[^\r\n]*");
                if (keyRx.Matches(updated).Count > 1)
                    throw new InvalidDataException("OptiScaler config has duplicate [Log] " + keys[i] + " keys.");
                if (keyRx.IsMatch(updated))
                    updated = keyRx.Replace(updated, keys[i] + " = " + values[i], 1);
                else
                    updated += (updated.Length > 0 && !updated.EndsWith("\n", StringComparison.Ordinal)
                        ? newline : "") + keys[i] + " = " + values[i] + newline;
            }
            return text.Substring(0, body.Index) + updated + text.Substring(body.Index + body.Length);
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
            if (BannerlordIsRunning(gameBin)) throw new InvalidOperationException("Close Bannerlord first.");
            if (string.IsNullOrWhiteSpace(gameBin)) throw new ArgumentException("--gamebin is required.");
            gameBin = Path.GetFullPath(gameBin);
            string ini = Path.Combine(gameBin, "OptiScaler.ini");
            if (!HasCompleteManagedInstall(gameBin) || !File.Exists(ini))
            {
                log("Existing key migration skipped: no complete managed OptiScaler installation.");
                return;
            }
            // The existence of a manifest alone is not ownership proof. Verify
            // the actual proxy bytes and manifest game path before changing INI.
            var manifest = ParseJsonObject(File.ReadAllText(GetManifestPath(gameBin)));
            if (!Str(manifest, "gameBin").Equals(gameBin, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Manifest belongs to a different game folder; no INI changes made.");
            string proxy = ManifestProxyName(manifest);
            object owned;
            string expectedProxy = null;
            if (manifest.TryGetValue("postinstallFiles", out owned) &&
                owned is Dictionary<string, object> postinstall)
                expectedProxy = Str(postinstall, proxy);
            if (string.IsNullOrWhiteSpace(expectedProxy))
                expectedProxy = Str(manifest, "movedProxySha256");
            if (string.IsNullOrWhiteSpace(expectedProxy) && proxy == "dxgi.dll")
                expectedProxy = Str(manifest, "movedDxgiSha256");
            if (string.IsNullOrWhiteSpace(expectedProxy) ||
                !HashFile(Path.Combine(gameBin, proxy)).Equals(expectedProxy, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Cannot verify ownership of " + proxy + "; the OptiScaler INI is unchanged.");
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
            text = ConfigureLoggingDefaults(text);
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
                if (HasCompleteManagedInstall(gameBin))
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
                string managedProxy = ManifestProxyName(manifest);
                object value;
                if (manifest.TryGetValue("movedProxySha256", out value) ||
                    (managedProxy == "dxgi.dll" && manifest.TryGetValue("movedDxgiSha256", out value)))
                {
                    string sha = Convert.ToString(value);
                    if (!Regex.IsMatch(sha ?? "", "^[a-fA-F0-9]{64}$"))
                    { reason = "Invalid managed proxy checksum."; return false; }
                    owned[managedProxy] = sha;
                }
                else if (owned.ContainsKey("OptiScaler.dll") &&
                    !File.Exists(Path.Combine(gameBin, "OptiScaler.dll")))
                {
                    // A crash between File.Move and recording the proxy hash.
                    // Accept ONLY bytes identical to the manifest-owned OptiScaler.dll.
                    string proxyPath = Path.Combine(gameBin, managedProxy);
                    if (File.Exists(proxyPath) && HashFile(proxyPath).Equals(
                        owned["OptiScaler.dll"], StringComparison.OrdinalIgnoreCase))
                        owned[managedProxy] = owned["OptiScaler.dll"];
                }
                if (manifest.TryGetValue("postinstallFiles", out value) &&
                    value is Dictionary<string, object>)
                {
                    foreach (var pair in (Dictionary<string, object>)value)
                    {
                        // Never adopt an unrelated DXGI proxy when this installation
                        // uses winmm.dll. Only the manifest-selected proxy is owned.
                        if (!owned.ContainsKey(pair.Key) &&
                            !pair.Key.Equals(managedProxy, StringComparison.OrdinalIgnoreCase) &&
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
                foreach (string candidate in new[] { managedProxy, "OptiScaler.dll", "OptiScaler.ini",
                    "OptiScaler.ini.bannerlordfg-original.bak" })
                    if (File.Exists(Path.Combine(gameBin, candidate)) && !owned.ContainsKey(candidate))
                    { reason = "An untracked graphics/configuration file already exists: " + candidate; return false; }
                // The removal worker has stricter schema and path rules. Apply
                // those rules before downloading or asking the game to close.
                VerifiedRemovalPlan(gameBin, manifestPath);
                return true;
            }
            catch (Exception ex) { reason = "Could not verify interrupted installation: " + ex.Message; return false; }
        }

        internal static void RecoverIncompleteInstall(string gameBin, Action<string> log)
        {
            if (BannerlordIsRunning(gameBin)) throw new InvalidOperationException("Close Bannerlord before recovering graphics files.");
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

        private sealed class ManagedRemovalFile
        {
            internal string Relative;
            internal string Path;
            internal string Sha256;
        }

        private static bool TrackedFileExists(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0)
                    throw new InvalidDataException("Tracked file became a directory: " + path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Tracked file became a reparse point: " + path);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        private static bool TrackedDirectoryExists(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) == 0)
                    throw new InvalidDataException("Managed directory became a file: " + path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Managed directory became redirected: " + path);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess,
            FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition,
            FileAttributes flagsAndAttributes, IntPtr templateFile);

        // Opening with DELETE access checks the ACL and existing handles before
        // the first managed file is removed. A later race is still handled by
        // bounded retries and a retained manifest/recovery snapshot.
        private static void CheckDeleteAccess(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("Managed file is read-only: " + path);
            using (SafeFileHandle handle = CreateFile(path, 0x00010000,
                FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
                FileAttributes.Normal, IntPtr.Zero))
            {
                if (!handle.IsInvalid) return;
                int code = Marshal.GetLastWin32Error();
                if (code == 5) throw new UnauthorizedAccessException(
                    "Delete access denied for " + path + ": " + new Win32Exception(code).Message);
                throw new IOException("Delete access unavailable for " + path +
                    " (Windows error " + code + ": " + new Win32Exception(code).Message + ").");
            }
        }

        private static void RetryTransient(Action operation)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { operation(); return; }
                catch (UnauthorizedAccessException) when (attempt < 3)
                { Thread.Sleep(250 * (attempt + 1)); }
                catch (IOException) when (attempt < 3)
                { Thread.Sleep(250 * (attempt + 1)); }
            }
        }

        private static void DeleteVerifiedFile(string path, string expectedHash)
        {
            RetryTransient(() =>
            {
                for (string cursor = path; cursor != null; cursor = Path.GetDirectoryName(cursor))
                    if (File.Exists(cursor) || Directory.Exists(cursor))
                        if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException("Managed path became redirected.");
                if (!TrackedFileExists(path)) return;
                if (!HashFile(path).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Managed file changed during delete retry.");
                File.Delete(path);
                if (TrackedFileExists(path))
                    throw new IOException("Managed file still exists after deletion: " + path);
            });
        }

        private static bool ValidOwnedPath(string relative, string managedProxy)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                relative.IndexOf('/') >= 0 ||
                relative.IndexOf(':') >= 0 || relative.IndexOf('\0') >= 0) return false;
            string[] parts = relative.Replace('/', '\\').Split('\\');
            if (parts.Any(x => x.Length == 0 || x == "." || x == ".." ||
                x.EndsWith(" ", StringComparison.Ordinal) ||
                x.EndsWith(".", StringComparison.Ordinal) ||
                x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return false;
            if (parts.Length == 1)
                return relative.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ||
                    IsOwnedConfig(relative) ||
                    relative.Equals(managedProxy, StringComparison.OrdinalIgnoreCase);
            return parts[0].Equals("OptiScaler", StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeManagedPath(string gameBin, string relative, string managedProxy)
        {
            if (!ValidOwnedPath(relative, managedProxy))
                throw new InvalidDataException("Manifest contains an unexpected managed path: " + relative);
            string root = Path.GetFullPath(gameBin).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Manifest path escapes the game directory.");
            for (string cursor = path; cursor != null;
                cursor = Path.GetDirectoryName(cursor))
            {
                if (File.Exists(cursor) || Directory.Exists(cursor))
                    if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Managed path contains a reparse point: " + relative);
            }
            if (Directory.Exists(path))
                throw new InvalidDataException("Manifest file path names a directory: " + relative);
            return path;
        }

        private static List<ManagedRemovalFile> VerifiedRemovalPlan(string gameBin,
            string manifestPath)
        {
            if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Install manifest is a reparse point.");
            var manifest = ParseJsonObject(File.ReadAllText(manifestPath));
            if (!string.Equals(Path.GetFullPath(Str(manifest, "gameBin")),
                Path.GetFullPath(gameBin), StringComparison.OrdinalIgnoreCase) ||
                !IsKnownManagedArchive(Str(manifest, "tag"), Str(manifest, "archiveSha256")))
                throw new InvalidDataException("Manifest game path or official release checksum is invalid.");
            string stage = Str(manifest, "stage");
            if (stage.Length > 0 && !stage.Equals("complete", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unrecognized manifest stage.");
            string proxy = ManifestProxyName(manifest);
            var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            object value;
            if (manifest.TryGetValue("installedFiles", out value))
            {
                if (!(value is object[] records)) throw new InvalidDataException("Invalid owned-file list.");
                foreach (object record in records)
                {
                    var row = record as Dictionary<string, object>;
                    if (row == null) throw new InvalidDataException("Invalid owned-file record.");
                    string relative = Str(row, "relativePath");
                    string hash = Str(row, "sha256");
                    SafeManagedPath(gameBin, relative, proxy);
                    if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$") || installed.ContainsKey(relative))
                        throw new InvalidDataException("Duplicate or invalid owned-file checksum.");
                    installed.Add(relative, hash);
                }
            }
            if (installed.Count == 0)
                throw new InvalidDataException("Manifest lacks the original owned-file list.");
            var final = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (manifest.TryGetValue("postinstallFiles", out value))
            {
                if (!(value is Dictionary<string, object> post) || post.Count == 0)
                    throw new InvalidDataException("Invalid final owned-file list.");
                foreach (var pair in post)
                {
                    if (installed.Count > 0 && !installed.ContainsKey(pair.Key) &&
                        !pair.Key.Equals(proxy, StringComparison.OrdinalIgnoreCase) &&
                        !pair.Key.Equals("OptiScaler.ini.bannerlordfg-original.bak",
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Final manifest claims an untracked file: " + pair.Key);
                    final.Add(pair.Key, Convert.ToString(pair.Value));
                }
            }
            else
            {
                if (installed.Count == 0) throw new InvalidDataException("Manifest has no owned files.");
                foreach (var pair in installed) final.Add(pair.Key, pair.Value);
            }
            if (manifest.TryGetValue("movedProxySha256", out value) ||
                (proxy == "dxgi.dll" && manifest.TryGetValue("movedDxgiSha256", out value)))
            {
                string hash = Convert.ToString(value);
                if (!installed.TryGetValue("OptiScaler.dll", out string originalProxyHash) ||
                    !string.Equals(hash, originalProxyHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Managed proxy differs from the original owned DLL.");
                if (final.ContainsKey(proxy) && !final[proxy].Equals(hash,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Managed proxy checksums disagree.");
                final[proxy] = hash;
            }
            else if (!final.ContainsKey(proxy) && installed.ContainsKey("OptiScaler.dll") &&
                !TrackedFileExists(SafeManagedPath(gameBin, "OptiScaler.dll", proxy)))
            {
                // A crash can occur between the official DLL rename and the
                // manifest update. Adopt the selected proxy only when its bytes
                // match the originally recorded OptiScaler.dll exactly.
                string renamed = SafeManagedPath(gameBin, proxy, proxy);
                if (TrackedFileExists(renamed))
                {
                    if (!HashFile(renamed).Equals(installed["OptiScaler.dll"],
                        StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Untracked proxy differs from the original managed DLL.");
                    final[proxy] = installed["OptiScaler.dll"];
                }
            }
            if (manifest.TryGetValue("expectedBackupSha256", out value) &&
                !final.ContainsKey("OptiScaler.ini.bannerlordfg-original.bak"))
                final["OptiScaler.ini.bannerlordfg-original.bak"] = Convert.ToString(value);
            if (!manifest.ContainsKey("postinstallFiles") &&
                manifest.TryGetValue("configuredIniSha256", out value))
                final["OptiScaler.ini"] = Convert.ToString(value);
            if (stage.Equals("complete", StringComparison.OrdinalIgnoreCase) &&
                (!final.ContainsKey(proxy) || !final.ContainsKey("OptiScaler.ini")))
                throw new InvalidDataException("Completed manifest lacks its selected proxy or INI.");
            var plan = new List<ManagedRemovalFile>();
            foreach (var pair in final.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                string path = SafeManagedPath(gameBin, pair.Key, proxy);
                if (!Regex.IsMatch(pair.Value ?? "", "^[a-fA-F0-9]{64}$"))
                    throw new InvalidDataException("Invalid managed checksum: " + pair.Key);
                var item = new ManagedRemovalFile { Relative = pair.Key, Path = path,
                    Sha256 = pair.Value };
                if (TrackedFileExists(path) && !HashFile(path).Equals(pair.Value,
                    StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsOwnedConfig(pair.Key))
                        throw new InvalidDataException("Managed file was modified; nothing removed: " + pair.Key);
                }
                plan.Add(item);
            }
            return plan;
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
            DeleteVerifiedFile(source, originalHash);
            log("Modified managed config preserved in verified recovery backup: " + destination);
            return destination;
        }

        private static void ReportPossibleHolders(string path, Action<string> log)
        {
            int found = 0;
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch (Exception ex)
            {
                log("Process-module inspection unavailable: " + ex.GetType().Name);
                return;
            }
            foreach (Process process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (!process.Modules.Cast<ProcessModule>().Any(module =>
                            string.Equals(Path.GetFullPath(module.FileName), path,
                                StringComparison.OrdinalIgnoreCase))) continue;
                        if (found < 8)
                            log("Possible managed-DLL holder: " + process.ProcessName +
                                " PID " + process.Id + ". No process was terminated.");
                        found++;
                    }
                    catch (Exception) { } // Other processes can deny module inspection.
                }
            }
            if (found == 0)
                log("No owning process module was identifiable. An open handle, security filter, or file policy may still block deletion.");
        }

        internal static void Uninstall(string gameBin, Action<string> log)
        {
            UninstallCore(gameBin, log, null);
        }

        // The test seam runs after the real preflight and before an individual
        // real file deletion, allowing deterministic mid-removal lock/ACL tests.
        private static void UninstallCore(string gameBin, Action<string> log,
            Action<string> beforeDeleteForTest)
        {
            if (string.IsNullOrWhiteSpace(gameBin)) throw new ArgumentException("Game directory is required.");
            gameBin = Path.GetFullPath(gameBin);
            if (BannerlordIsRunning(gameBin))
                throw new InvalidOperationException("Close Bannerlord before uninstalling.");
            string manifestPath = GetManifestPath(gameBin);
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("BannerlordFG install manifest not found", manifestPath);
            string state = AutoFirstRun.Folder;
            Directory.CreateDirectory(state);
            if ((File.GetAttributes(state) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Installer state directory is redirected.");
            string lockPath = Path.Combine(state, "uninstall-" + HashShort(gameBin) + ".lock");
            if (File.Exists(lockPath) &&
                (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Uninstall lock path is redirected.");
            using (var exclusive = new FileStream(lockPath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None))
            {
                // Validate ALL manifest entries and current hashes before any
                // game file changes. Missing owned files are normal on retry.
                List<ManagedRemovalFile> plan = VerifiedRemovalPlan(gameBin, manifestPath);
                string managedProxy = ManifestProxyName(ParseJsonObject(File.ReadAllText(manifestPath)));
                foreach (ManagedRemovalFile item in plan)
                {
                    if (!TrackedFileExists(item.Path)) continue;
                    try { RetryTransient(() => CheckDeleteAccess(item.Path)); }
                    catch (Exception ex)
                    {
                        log("Uninstall preflight stopped before any deletion at " + item.Relative +
                            ": " + ex.GetType().Name + ". Manifest retained.");
                        ReportPossibleHolders(item.Path, log);
                        throw;
                    }
                }
                string recoveryRoot = Path.Combine(state, "recovery");
                Directory.CreateDirectory(recoveryRoot);
                if ((File.GetAttributes(recoveryRoot) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Recovery directory is redirected.");
                string recovery = Path.Combine(recoveryRoot, "uninstall-" +
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(recovery);
                string snapshot = Path.Combine(recovery, "install-manifest.json");
                File.Copy(manifestPath, snapshot, false);
                if (!HashFile(manifestPath).Equals(HashFile(snapshot), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Manifest recovery snapshot verification failed; no files changed.");
                string manifestHash = HashFile(snapshot);
                string journal = Path.Combine(recovery, "status.txt");
                File.WriteAllText(journal, "Started; manifest snapshot verified.\n", new UTF8Encoding(false));
                int removed = 0;
                int alreadyMissing = 0;
                string current = "none";
                try
                {
                    foreach (ManagedRemovalFile item in plan)
                    {
                        current = item.Relative;
                        if (BannerlordIsRunning(gameBin))
                            throw new IOException("Bannerlord reopened during uninstall.");
                        // Check again after preflight; another installer may
                        // have replaced a file or introduced a junction.
                        SafeManagedPath(gameBin, item.Relative, managedProxy);
                        if (!TrackedFileExists(item.Path)) { alreadyMissing++; continue; }
                        string now = HashFile(item.Path);
                        beforeDeleteForTest?.Invoke(item.Relative);
                        if (!now.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!IsOwnedConfig(item.Relative))
                                throw new InvalidDataException("Managed file changed during uninstall: " + item.Relative);
                            BackupChangedConfig(item.Path, item.Relative, recovery, log);
                        }
                        else DeleteVerifiedFile(item.Path, item.Sha256);
                        removed++;
                    }
                    if (plan.Any(item => TrackedFileExists(item.Path)))
                        throw new IOException("Managed files remain after removal attempt.");
                    if (!File.Exists(manifestPath) ||
                        !HashFile(manifestPath).Equals(manifestHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Install manifest changed during uninstall.");
                    // Remove only known empty parent directories; never walk
                    // an untracked subtree or follow a newly created junction.
                    foreach (string directory in plan.Select(item => Path.GetDirectoryName(item.Path))
                        .Where(path => !string.Equals(path, gameBin, StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(path => path.Length))
                    {
                        if (!TrackedDirectoryExists(directory)) continue;
                        if (!Directory.EnumerateFileSystemEntries(directory).Any())
                            Directory.Delete(directory);
                    }
                    string optiFolder = Path.Combine(gameBin, "OptiScaler");
                    if (TrackedDirectoryExists(optiFolder) &&
                        Directory.EnumerateFileSystemEntries(optiFolder).Any())
                        throw new IOException("Untracked files remain in OptiScaler; manifest retained for review.");
                    // The original manifest is the retry authority. Remove it
                    // only after every tracked file has been verified absent.
                    if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Install manifest became redirected.");
                    File.Delete(manifestPath);
                    if (File.Exists(manifestPath))
                        throw new IOException("Install manifest remained after final removal.");
                    try { File.WriteAllText(journal, "Complete; removed=" + removed +
                        "; already missing=" + alreadyMissing + ".\n", new UTF8Encoding(false)); }
                    catch (Exception ex) { log("Recovery journal update unavailable: " + ex.GetType().Name); }
                    log("Verified uninstall complete: removed " + removed +
                        " managed files; " + alreadyMissing + " were already missing. " +
                        "Recovery snapshot: " + recovery);
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(journal, "Incomplete at " + current +
                        "; removed=" + removed + "; already missing=" + alreadyMissing +
                        "; error=" + ex.GetType().Name + ". Original manifest retained.\n",
                        new UTF8Encoding(false)); } catch { }
                    log("Uninstall incomplete at " + current + " (" + ex.GetType().Name +
                        "). Original manifest and recovery snapshot retained: " + recovery);
                    if (ex is UnauthorizedAccessException || ex is IOException)
                    {
                        ManagedRemovalFile item = plan.FirstOrDefault(x => x.Relative == current);
                        try
                        {
                            if (item != null && TrackedFileExists(item.Path))
                                ReportPossibleHolders(item.Path, log);
                        }
                        catch (Exception probeError)
                        { log("Possible file holder inspection failed: " + probeError.GetType().Name); }
                    }
                    throw;
                }
            }
        }

        // Official 7z files used only for manual distribution, licensing or docs
        // are not runtime components. Keep every other asset, including future
        // resource formats: guessing a DLL-only allowlist would break XeFG.
        internal static bool IsRuntimePayloadFile(string relativePath)
        {
            string rel = (relativePath ?? "").Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(rel)) return false;
            if (rel.StartsWith("Licenses/", StringComparison.OrdinalIgnoreCase)) return false;
            string file = Path.GetFileName(rel);
            return !file.Equals("setup_windows.bat", StringComparison.OrdinalIgnoreCase) &&
                   !file.Equals("setup_linux.sh", StringComparison.OrdinalIgnoreCase) &&
                   !file.Equals("README.md", StringComparison.OrdinalIgnoreCase) &&
                   !file.Equals("README.txt", StringComparison.OrdinalIgnoreCase);
        }

        private static string[] RuntimePayloadFiles(string root)
        {
            return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => IsRuntimePayloadFile(f.Substring(root.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                .ToArray();
        }

        // Read-only preflight: avoid closing Bannerlord just to discover known
        // collisions. Caller has already completed package.Verify(); never
        // rehash the large package here. No existing files are changed.
        internal static void CheckRuntimeCollisions(string gameBin, PreparedPackage package)
        {
            var root = package.ExtractedRoot;
            var files = RuntimePayloadFiles(root);
            if (files.Length == 0 || !files.Any(f =>
                Path.GetFileName(f).Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase)) ||
                !files.Any(f => Path.GetFileName(f).Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Verified OptiScaler package is missing required runtime files.");
            var collisions = files.Select(f => f.Substring(root.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Where(rel => File.Exists(Path.Combine(gameBin, rel))).ToArray();
            if (collisions.Length > 0)
                throw new IOException("Installation stopped: these runtime files already exist: " +
                    string.Join(", ", collisions.Take(8)) + ". Existing files were not changed.");
        }

        internal static async Task InstallAsync(string gameBin, string vendor, bool useFsr31, Action<string> log, bool unattended = false, PreparedPackage prepared = null)
        {
            if (vendor != "NVIDIA" && vendor != "AMD" && vendor != "Intel")
                throw new InvalidOperationException("Choose NVIDIA, AMD or Intel.");
            if (BannerlordIsRunning(gameBin)) throw new InvalidOperationException("Close the game before installing.");
            string existing = ExistingInstallStatus(gameBin);
            if (existing != null)
                throw new InvalidOperationException("Installation blocked: " + existing);
            string proxyName = SelectProxyName(gameBin);
            log("BannerlordFG selected " + proxyName +
                (proxyName == "winmm.dll" ? " (default)." : " (fallback: winmm.dll is occupied)."));
            log("Other existing graphics DLLs will not be modified.");
            if (File.Exists(GetManifestPath(gameBin)))
                throw new IOException("A previous BannerlordFG install manifest exists. Use Safe uninstall or inspect it before reinstalling.");
            bool ownsPrepared = prepared == null;
            PreparedPackage pkg = prepared;
            bool copiedFiles = false;
            try
            {
                if (pkg == null) pkg = await PrepareOfficialPackageAsync(log);
                pkg.Verify();
                string url = pkg.OfficialUrl;
                string actual = pkg.Sha256;
                // The prepared, hash-checked files were already extracted while
                // the game was open. Keep the post-exit write window short.
                string root = pkg.ExtractedRoot;
                // Manual upstream mode still needs its setup BAT. The automatic
                // game-menu installation skips scripts and licence documents.
                var files = unattended ? RuntimePayloadFiles(root) :
                    Directory.GetFiles(root, "*", SearchOption.AllDirectories);
                if (files.Length == 0) throw new InvalidDataException("Official package is empty.");
                var relative = files.Select(f => f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar)).ToArray();
                // The game may have been manually reopened between the initial
                // post-exit check and the final copy. Never write loaded DLLs.
                if (BannerlordIsRunning(gameBin))
                    throw new IOException("Bannerlord reopened while OptiScaler was being prepared. " +
                        "Close the game before installing; no graphics files were copied.");
                // A second proxy selection protects against an external mod
                // installing into the bin folder during the download.
                if (ExistingInstallStatus(gameBin) != null ||
                    !string.Equals(SelectProxyName(gameBin), proxyName, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Graphics DLL state changed during download. Install cancelled without overwrites.");
                // Revalidate the exact runtime payload after shutdown (TOCTOU).
                // A modified user's DLL or INI must never be overwritten.
                CheckRuntimeCollisions(gameBin, pkg);
                // Record which newly copied files we own; never delete or overwrite files not on this list.
                var manifest = new Dictionary<string, object> {
                    { "gameBin", gameBin }, { "tag", Tag }, { "gpu", vendor },
                    { "proxyFileName", proxyName },
                    { "source", url }, { "archiveSha256", actual },
                    { "timeUtc", DateTime.UtcNow.ToString("o") },
                    { "installedFiles", relative.Select((f,i) =>
                        new Dictionary<string, string> {{"relativePath",f}, {"sha256", HashFile(files[i])}}).ToArray() }
                };
                string manifestPath = GetManifestPath(gameBin);
                Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
                if (File.Exists(manifestPath)) throw new IOException("A previous BannerlordFG install manifest exists; review it before reinstalling: " + manifestPath);
                File.WriteAllText(manifestPath, new JavaScriptSerializer().Serialize(manifest));
                // Copy with CreateNew semantics and track only successful writes.
                // A different installer might create a target after preflight:
                // rollback must NEVER delete a colliding third-party file.
                var created = new List<int>();
                try
                {
                    for (int i = 0; i < files.Length; ++i)
                    {
                        if (BannerlordIsRunning(gameBin))
                            throw new IOException("Bannerlord reopened during file installation. Stopping without modifying loaded DLLs.");
                        string target = Path.Combine(gameBin, relative[i]);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(files[i], target, false);
                        created.Add(i);
                    }
                }
                catch
                {
                    // Roll back only paths WE successfully created, and only
                    // when their hashes still match. Leave the manifest for
                    // hash-checked recovery if another process is using them.
                    foreach (int i in created)
                    {
                        if (BannerlordIsRunning(gameBin)) break;
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
                    // Rename to the selected EARLY proxy. The external DXGI DLL
                    // (for example ReShade) is never moved, overwritten or owned.
                    // Preserve normal DXGI spoofing for DLSS inputs where required.
                    string originalProxy = Path.Combine(gameBin, "OptiScaler.dll");
                    string renamedProxy = Path.Combine(gameBin, proxyName);
                    if (!File.Exists(originalProxy) || File.Exists(renamedProxy))
                        throw new IOException("Official injector DLL missing or " + proxyName + " already exists. Setup stopped safely.");
                    File.Move(originalProxy, renamedProxy);
                    // Crash/reconfiguration failures must not strand an untracked
                    // renamed injector. Commit its ownership before patching INI.
                    manifest["movedProxySha256"] = HashFile(renamedProxy);
                    File.WriteAllText(manifestPath, new JavaScriptSerializer().Serialize(manifest));
                    log("Official OptiScaler.dll installed as " + proxyName + " for Bannerlord DX11.");
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
                // Track ONLY the selected proxy created by our own File.Move.
                // Never adopt an external proxy merely because it appears in the
                // same game directory between initial scan and manifest creation.
                if (unattended)
                {
                    string proxyPath = Path.Combine(gameBin, proxyName);
                    if (File.Exists(proxyPath)) finalFiles[proxyName] = HashFile(proxyPath);
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

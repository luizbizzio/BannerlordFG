// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Reflection;
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
        // Windows PowerShell 5.1 is the executable host. It does not read a
        // BannerlordFG.Maintenance.dll.config bindingRedirect file. Pinning the
        // NuGet package alone also does not redirect older CLR assembly IDs.
        //
        // The CMD helper loads all known local runtime DLLs before this method.
        // We cache those Assembly *objects* and only return them on a failed
        // version bind. The resolver NEVER invokes Assembly.Load/LoadFrom,
        // which would reenter AssemblyResolve and can cause StackOverflowException.
        private static Dictionary<string, Assembly> knownRuntimeAssemblies;
        private static readonly string[] runtimeNames =
        {
            "System.Runtime.CompilerServices.Unsafe",
            "System.Buffers",
            "System.Memory",
            "System.Numerics.Vectors",
            "System.Threading.Tasks.Extensions",
            "Microsoft.Bcl.AsyncInterfaces",
            "System.Text.Encoding.CodePages",
            "SharpCompress"
        };

        public static void RegisterLocalAssemblyBindings()
        {
            if (knownRuntimeAssemblies != null) return;
            string folder = Path.GetDirectoryName(typeof(MaintenanceEntry).Assembly.Location);
            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            var validated = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in runtimeNames)
            {
                string expectedPath = Path.GetFullPath(Path.Combine(folder, name + ".dll"));
                AssemblyName expectedIdentity = AssemblyName.GetAssemblyName(expectedPath);
                Assembly match = null;
                foreach (Assembly assembly in loaded)
                {
                    if (assembly.IsDynamic) continue;
                    // A matching strong-named DLL may already have been loaded
                    // from the .NET GAC. Accept it only if its identity is
                    // identical to the locally validated assembly.
                    if (!string.Equals(assembly.GetName().FullName, expectedIdentity.FullName,
                        StringComparison.OrdinalIgnoreCase)) continue;
                    match = assembly;
                    if (string.Equals(Path.GetFullPath(assembly.Location), expectedPath,
                        StringComparison.OrdinalIgnoreCase)) break;
                }
                if (match == null)
                    throw new FileNotFoundException("Required local runtime assembly not preloaded: " + name, expectedPath);
                validated.Add(name, match);
            }
            // Publish fully populated cache before subscribing. No file IO,
            // LoadFrom, PowerShell callbacks or logging occurs inside the event.
            knownRuntimeAssemblies = validated;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveKnownLocalVersion;
        }

        public static void VerifyRuntimeAssemblies()
        {
            if (knownRuntimeAssemblies == null)
                throw new InvalidOperationException("Register local runtime bindings before preflight.");
            foreach (Assembly assembly in knownRuntimeAssemblies.Values)
                assembly.GetTypes();
        }

        private static Assembly ResolveKnownLocalVersion(object sender, ResolveEventArgs args)
        {
            var cached = knownRuntimeAssemblies;
            if (cached == null || args == null || string.IsNullOrEmpty(args.Name)) return null;
            AssemblyName requested;
            try { requested = new AssemblyName(args.Name); }
            catch (Exception) { return null; }
            Assembly match;
            if (!cached.TryGetValue(requested.Name, out match)) return null;
            // Never hijack assembly requests from unrelated PowerShell modules.
            if (args.RequestingAssembly != null)
            {
                string caller = args.RequestingAssembly.GetName().Name;
                if (!cached.ContainsKey(caller) &&
                    !string.Equals(caller, "BannerlordFG.Maintenance", StringComparison.OrdinalIgnoreCase))
                    return null;
            }
            AssemblyName actual = match.GetName();
            if (requested.Version != null && requested.Version > actual.Version) return null;
            if (!string.Equals(requested.CultureName ?? "", actual.CultureName ?? "",
                    StringComparison.OrdinalIgnoreCase)) return null;
            byte[] wantedToken = requested.GetPublicKeyToken() ?? new byte[0];
            byte[] actualToken = actual.GetPublicKeyToken() ?? new byte[0];
            if (wantedToken.Length != actualToken.Length) return null;
            for (int i = 0; i < wantedToken.Length; i++)
                if (wantedToken[i] != actualToken[i]) return null;
            return match;
        }
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

        // Tests actual LZMA2 7z decoding in the same PowerShell/.NET host used by users.
        // The build runs this BEFORE replacing the installed mod or closing Bannerlord.
        public static void VerifyRuntimeExtraction()
        {
            // Exercise the exact legacy System.Buffers bind observed in the
            // Windows installer log. This must resolve to our validated local
            // assembly before the SharpCompress LZMA2 extraction begins.
            Assembly oldBuffers = Assembly.Load(
                "System.Buffers, Version=4.0.2.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51");
            Assembly cachedBuffers;
            if (knownRuntimeAssemblies == null ||
                !knownRuntimeAssemblies.TryGetValue("System.Buffers", out cachedBuffers) ||
                (!object.ReferenceEquals(oldBuffers, cachedBuffers) &&
                 !oldBuffers.GetName().Version.Equals(new Version(4, 0, 2, 0))))
                throw new InvalidOperationException("Legacy System.Buffers binding failed CLR identity validation.");
            string working = Path.Combine(Path.GetTempPath(), "BannerlordFG-7z-smoke-" + Guid.NewGuid().ToString("N"));
            string destination = Path.Combine(working, "output");
            Directory.CreateDirectory(destination);
            try
            {
                byte[] fixture = Convert.FromBase64String("N3q8ryccAAMWVHw0LQAAAAAAAABGAAAAAAAAALnvX34BAChCYW5uZXJsb3JkRkcgc2V2ZW56aXAgcnVudGltZSBzbW9rZSB0ZXN0CgABBAYAAQktAAcLAQABISEBFgwpAAgKATx1ISUAAAUBERsAYgBmAGcALQB0AGUAcwB0AC4AdAB4AHQAAAAVBgEAIICkgQAA");
                string archive = Path.Combine(working, "fixture.7z");
                File.WriteAllBytes(archive, fixture);
                SafeSevenZipExtractor.Extract(archive, destination);
                string actual = File.ReadAllText(Path.Combine(destination, "bfg-test.txt"));
                if (!string.Equals(actual, "BannerlordFG sevenzip runtime smoke test\n", StringComparison.Ordinal))
                    throw new InvalidDataException("7z runtime smoke-test extracted unexpected content.");
            }
            finally
            {
                if (Directory.Exists(working)) Directory.Delete(working, true);
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

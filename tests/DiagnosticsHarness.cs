// SPDX-License-Identifier: GPL-3.0-only
// Behaviour tests for the production diagnostics classes; no game DLL needed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BannerlordFG.Diagnostics;

internal static class DiagnosticsHarness
{
    private static int passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static string Hash(string path)
    {
        using (var sha = SHA256.Create())
        using (var file = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
    }

    private static void Module(string root, string folder, string id, string dependencies = "")
    {
        string path = Path.Combine(root, folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "SubModule.xml"),
            "<Module><Name value='" + id + " Name'/><Id value='" + id +
            "'/><Version value='v2.1'/><DependedModules>" + dependencies +
            "</DependedModules></Module>");
    }

    private static void TestModules(string root)
    {
        string modules = Path.Combine(root, "Modules");
        Directory.CreateDirectory(modules);
        Module(modules, "Native", "Native");
        Module(modules, "BannerlordFG", "BannerlordFG",
            "<DependedModule Id='Native'/>");
        Module(modules, "Disabled", "OptionalMod");
        var warnings = new List<string>();
        var active = ModuleInventory.Scan(modules, new[] { "Native", "BannerlordFG" }, true,
            warnings.Add, new[] { "Native", "OptionalMod" });
        Check(active.Count == 3 && active[1].Id == "BannerlordFG" &&
            active[1].State == "active" && active[1].LoadOrder == 2 &&
            active[1].Dependencies.SequenceEqual(new[] { "Native" }),
            "Engine active order and dependencies");
        Check(active.Single(x => x.Id == "OptionalMod").State == "disabled" &&
            active.Single(x => x.Id == "OptionalMod").LauncherSelected == true,
            "Installed and launcher-selected is not active");
        var unknown = ModuleInventory.Scan(modules, null, false, warnings.Add);
        Check(unknown.All(x => x.State == "unknown"),
            "Folder presence alone does not imply enablement");
        Check(ModuleInventory.ParseModuleArgument(new[] {
            "_MODULES_*Native*BannerlordFG*_MODULES_" }).SequenceEqual(
            new[] { "Native", "BannerlordFG" }), "Explicit game module argument");
        Check(ModuleInventory.ParseModuleArgument(new[] {
            "_MODULES_*Native*Native*_MODULES_" }) == null,
            "Duplicate module argument rejected");
        Module(modules, "Bad", "Bad");
        File.WriteAllText(Path.Combine(modules, "Bad", "SubModule.xml"), "<Module><Id");
        var malformed = ModuleInventory.Scan(modules, null, false, warnings.Add);
        Check(malformed.Count == 3 && warnings.Any(x => x.Contains("unreadable")),
            "Malformed module metadata is isolated");
        var denied = ModuleInventory.Scan(modules, null, false, warnings.Add, null,
            _ => { throw new UnauthorizedAccessException(); });
        Check(denied.Count == 0 && warnings.Any(x => x.Contains("UnauthorizedAccessException")),
            "Access denied during module enumeration is contained");
    }

    private static void TestGraphics(string root, string reshadeFixture)
    {
        string bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        string manifest = Path.Combine(root, "manifest.json");
        var warnings = new List<string>();
        var missing = GraphicsInventory.Inspect(bin, manifest, new string[0], true, warnings.Add);
        Check(!missing.OptiScaler.Present && !missing.ReShade.Present,
            "Missing OptiScaler and ReShade");
        string proxy = Path.Combine(bin, "winmm.dll");
        File.WriteAllText(proxy, "OptiScaler fixture", Encoding.UTF8);
        File.WriteAllText(Path.Combine(bin, "OptiScaler.ini"),
            "[FrameGen]\nEnabled=true\nFGOutput=XeFG\n[Menu]\nShowFps=false\n[Upscalers]\nDx11Upscaler=fsr31\n");
        var data = new Dictionary<string, object> {
            { "gameBin", bin }, { "stage", "complete" }, { "tag", "fixture-build" },
            { "archiveSha256", new string('a', 64) }, { "proxyFileName", "winmm.dll" },
            { "postinstallFiles", new Dictionary<string, string> { { "winmm.dll", Hash(proxy) } } }
        };
        File.WriteAllText(manifest, new JavaScriptSerializer().Serialize(data));
        var installed = GraphicsInventory.Inspect(bin, manifest, new string[0], true, warnings.Add);
        Check(installed.OptiScaler.ValidInstall == true && installed.OptiScaler.Loaded == false,
            "Verified DLL present but not loaded");
        Check(installed.Backend == "XeFG" && installed.FgConfigured == true &&
            installed.FpsConfigured == false &&
            DiagnosticPolicy.FrameGenerationState(installed.Backend, installed.FgConfigured,
                null).Contains("runtime activity unknown"),
            "Configuration does not claim active XeFG");
        var loaded = GraphicsInventory.Inspect(bin, manifest, new[] { proxy }, true, warnings.Add);
        Check(loaded.OptiScaler.Loaded == true, "Exact proxy loaded in game process");
        var inaccessible = GraphicsInventory.Inspect(bin, manifest, null, false, warnings.Add);
        Check(inaccessible.OptiScaler.Loaded == null, "Module access denied gives unknown load state");
        File.WriteAllText(proxy, "modified");
        var invalid = GraphicsInventory.Inspect(bin, manifest, new[] { proxy }, true, warnings.Add);
        Check(invalid.OptiScaler.ValidInstall == false, "Invalid managed DLL hash detected");
        File.Delete(proxy);
        var absent = GraphicsInventory.Inspect(bin, manifest, new string[0], true, warnings.Add);
        Check(absent.OptiScaler.ValidInstall == false && absent.OptiScaler.Loaded == false,
            "Missing managed proxy detected");
        File.WriteAllText(manifest, "not JSON");
        var badManifest = GraphicsInventory.Inspect(bin, manifest, new string[0], true, warnings.Add);
        Check(badManifest.OptiScaler.ValidInstall == false &&
            warnings.Any(x => x.Contains("unreadable")), "Invalid manifest is contained");
        File.Delete(manifest);
        string reshade = Path.Combine(bin, "d3d11.dll");
        File.Copy(reshadeFixture, reshade);
        var reshadeState = GraphicsInventory.Inspect(bin, manifest, new[] { reshade }, true, warnings.Add);
        Check(reshadeState.ReShade.Present && reshadeState.ReShade.Loaded == true &&
            reshadeState.ReShade.Version.StartsWith("6.8"),
            "ReShade proxy version and loaded state");
        File.Delete(reshade);
        string virtualPath = Path.Combine(root, "ReShade64.dll");
        File.Copy(reshadeFixture, virtualPath);
        var virtualized = GraphicsInventory.Inspect(bin, manifest, new[] { virtualPath },
            true, warnings.Add);
        Check(virtualized.ReShade.Present && virtualized.ReShade.Loaded == true,
            "Loaded ReShade outside game bin is detected by metadata");
    }

    private static void TestLogging(string root)
    {
        string directory = Path.Combine(root, "log");
        var tasks = Enumerable.Range(0, 20).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 25; i++)
                UnifiedLog.Write(directory, "INFO", "Harness", "item " + worker + ":" + i);
        })).ToArray();
        Task.WaitAll(tasks);
        string[] lines = File.ReadAllLines(Path.Combine(directory, UnifiedLog.FileName));
        Check(lines.Length == 500 && lines.All(x => x.Contains("[INFO]") &&
            x.Contains("[Harness]") && x.Contains("session ")),
            "Concurrent appends remain whole and session-tagged");
        Check(lines.Select(x => x.Substring(x.IndexOf("item "))).Distinct().Count() == 500,
            "Concurrent log records are neither lost nor duplicated");
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        UnifiedLog.Write(directory, "INFO", "Harness", "private path " + profile + "\\Documents");
        string final = File.ReadAllLines(Path.Combine(directory, UnifiedLog.FileName)).Last();
        Check(final.Contains("%USERPROFILE%") &&
            final.IndexOf(profile, StringComparison.OrdinalIgnoreCase) < 0,
            "Profile directory is redacted from log messages");
        // A path which is a regular file cannot be a log directory; logging
        // must silently degrade rather than throw into Bannerlord.
        string blocked = Path.Combine(root, "blocked");
        File.WriteAllText(blocked, "x");
        UnifiedLog.Write(blocked, "ERROR", "Harness", "should be contained");
        Check(true, "Logging I/O failure is contained");
    }

    private static void TestWindowsProbe()
    {
        var warnings = new List<string>();
        string windows = WindowsProbe.WindowsVersion(warnings.Add);
        string cpu = WindowsProbe.Cpu(warnings.Add);
        string ram = WindowsProbe.InstalledRam(warnings.Add);
        var gpus = WindowsProbe.Gpus(warnings.Add);
        Check(windows.Contains("build ") && windows.Contains("bit"),
            "Windows native version and architecture probe");
        Check(cpu.Contains("logical processors=") && ram.EndsWith(" GiB"),
            "Windows CPU and installed RAM probes");
        Console.WriteLine("Windows GPU inventory probe returned " + gpus.Count +
            " adapter(s); " + warnings.Count + " optional warning(s).");
        foreach (var gpu in gpus)
            Console.WriteLine("  " + gpu.Vendor + " " + gpu.Model +
                "; driver=" + gpu.Driver + "; dedicated bytes=" +
                (gpu.DedicatedBytes.HasValue ? gpu.DedicatedBytes.Value.ToString() : "unknown"));
    }

    public static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--log-child")
        {
            for (int i = 0; i < int.Parse(args[2]); i++)
                UnifiedLog.Write(args[1], "INFO", "CrossProcess", "child " +
                    System.Diagnostics.Process.GetCurrentProcess().Id + ":" + i + " " +
                    new string('X', 15000));
            return 0;
        }
        string root = Path.Combine(Path.GetTempPath(), "BannerlordFG-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Check(DiagnosticPolicy.HostKind("TaleWorlds.MountAndBlade.Launcher.exe")
                .Contains("original"), "Original launcher classification");
            Check(DiagnosticPolicy.HostKind("Bannerlord.BLSE.LauncherEx.exe")
                .Contains("LauncherEx"), "BLSE LauncherEx classification");
            Check(DiagnosticPolicy.HostKind("Bannerlord.BLSE.Standalone.exe")
                .Contains("Standalone"), "BLSE Standalone classification");
            TestModules(root);
            TestGraphics(root, args[0]);
            TestLogging(root);
            TestWindowsProbe();
            DiagnosticsExtendedTests.Run(root, Check);
            Console.WriteLine("All " + passed + " diagnostics behaviour tests passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

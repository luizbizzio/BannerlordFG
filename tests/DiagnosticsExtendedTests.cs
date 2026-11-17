// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BannerlordFG.Diagnostics;
using BannerlordFG.Setup;
using BannerlordFG.Core;

internal static class DiagnosticsExtendedTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        VendorTests(check);
        GameVersionTests(root, check);
        LauncherTests(check);
        LoggingRotationTests(root, check);
        ExportTests(root, check);
        ExplorerWorkflowTests(root, check);
        NativeMenuTests(check);
    }

    private static void NativeMenuTests(Action<bool, string> check)
    {
        check(NativeMenuLayout.Rows(true, false).SequenceEqual(new[] {
            NativeMenuRow.FrameGeneration, NativeMenuRow.FpsCounter,
            NativeMenuRow.Uninstall, NativeMenuRow.ExportDiagnostics }),
            "Installed native menu puts OptiScaler above Diagnostics");
        check(NativeMenuLayout.Rows(false, false).SequenceEqual(new[] {
            NativeMenuRow.Install, NativeMenuRow.ExportDiagnostics }),
            "Not-installed native menu puts Install above Diagnostics");
        check(NativeMenuLayout.Rows(false, true).SequenceEqual(new[] {
            NativeMenuRow.Repair, NativeMenuRow.ExportDiagnostics }),
            "Incomplete native menu puts Repair above Diagnostics");
        check(NativeMenuLayout.Rows(true, false).SequenceEqual(new[] {
            NativeMenuRow.FrameGeneration, NativeMenuRow.FpsCounter,
            NativeMenuRow.Uninstall, NativeMenuRow.ExportDiagnostics }),
            "Reopened installed menu keeps the same row order");
        var graphics = new GraphicsSnapshot {
            OptiBuild = "nightly-20261116", Backend = "XeFG", FgConfigured = true };
        graphics.OptiScaler.Present = true;
        graphics.OptiScaler.Loaded = true;
        graphics.OptiScaler.LoadedVersion = "10.0.0-dev";
        DiagnosticsRuntimeContext.SetGraphicsStatus(graphics);
        string status = DiagnosticsRuntimeContext.GraphicsStatus();
        check(status.Contains("10.0.0-dev") && status.Contains("Loaded") &&
            status.Contains("XeFG set") && status.Contains("activity unknown") &&
            !status.Contains("active"),
            "Native status distinguishes loaded and configured from active");
        graphics.FgConfigured = null;
        DiagnosticsRuntimeContext.SetGraphicsStatus(graphics);
        check(DiagnosticsRuntimeContext.GraphicsStatus().Contains("XeFG unknown"),
            "Unknown XeFG configuration stays unknown in native status");
    }

    private static void VendorTests(Action<bool, string> check)
    {
        check(VendorIdentity.FromPciVendorId(0x8086) == "Intel" &&
            VendorIdentity.FromPnpDeviceId("PCI\\VEN_8086&DEV_7D55") == "Intel",
            "Intel PCI vendor is Intel");
        check(VendorIdentity.FromPciVendorId(0x10DE) == "NVIDIA" &&
            VendorIdentity.FromPnpDeviceId("PCI\\VEN_10DE&DEV_2F00") == "NVIDIA",
            "NVIDIA PCI vendor is NVIDIA");
        check(VendorIdentity.FromPciVendorId(0x1002) == "AMD" &&
            VendorIdentity.FromPnpDeviceId("PCI\\VEN_1002&DEV_1234") == "AMD",
            "AMD PCI vendor is AMD");
        check(VendorIdentity.FromPciVendorId(0x1234) == "unknown" &&
            VendorIdentity.FromPnpDeviceId("PCI\\VEN_1234&DEV_5678") == "unknown" &&
            VendorIdentity.FromPnpDeviceId("Intel Arc 140T") == "unknown",
            "Unknown PCI vendor is not guessed from model name");
        var spoofed = new Dictionary<ulong, GpuDetection.Adapter> {
            { 7, new GpuDetection.Adapter { Luid = 7, Vendor = "AMD",
                VendorId = 0x1002, DeviceId = 0x1234,
                Description = "Intel(R) Arc(TM) 140T GPU" } }
        };
        var physical = new[] { new Dictionary<string, string> {
            { "Name", "Intel(R) Arc(TM) 140T GPU" },
            { "PNPDeviceID", "PCI\\VEN_8086&DEV_7D55" },
            { "DriverVersion", "32.0.101.8132" } } };
        var merged = WindowsProbe.MergeGpus(spoofed, physical);
        check(merged.Count == 1 && merged[0].Vendor == "Intel" &&
            merged[0].VendorSource == "Windows PCI PNPDeviceID",
            "Windows PCI identity overrides a spoofed DXGI vendor");
        var found = WindowsProbe.Gpus(_ => { });
        check(found.Where(x => x.Model != null && x.Model.IndexOf("Intel",
            StringComparison.OrdinalIgnoreCase) >= 0 && x.VendorSource != "unknown")
            .All(x => x.Vendor == "Intel"),
            "Live Windows GPU inventory preserves Intel identity");
    }

    private static void WriteModule(string root, string id, string version)
    {
        string directory = Path.Combine(root, "Modules", id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SubModule.xml"),
            "<Module><Id value='" + id + "'/><Version value='" + version + "'/></Module>");
    }

    private static void GameVersionTests(string root, Action<bool, string> check)
    {
        string game = Path.Combine(root, "version-fixture");
        string bin = Path.Combine(game, "bin", "Win64_Shipping_Client");
        Directory.CreateDirectory(bin);
        WriteModule(game, "Native", "v1.5.7");
        WriteModule(game, "SandBoxCore", "v1.5.7");
        var version = GameVersionDetector.Detect(bin);
        check(version.Version == "v1.5.7" && version.Source == "Native/SubModule.xml",
            "Bannerlord version comes from Native module metadata");
        WriteModule(game, "SandBoxCore", "v1.5.6");
        check(GameVersionDetector.Detect(bin).Version == "unknown",
            "Conflicting TaleWorlds module versions remain unknown");
        File.WriteAllText(Path.Combine(game, "Modules", "Native", "SubModule.xml"), "<bad");
        check(GameVersionDetector.Detect(bin).Version == "unknown",
            "Invalid Native metadata cannot become game version");
        string missing = Path.Combine(root, "missing-version", "bin", "Win64_Shipping_Client");
        Directory.CreateDirectory(missing);
        check(GameVersionDetector.Detect(missing).Version == "unknown",
            "Missing game metadata remains unknown");
    }

    private static void LauncherTests(Action<bool, string> check)
    {
        const string original = "TaleWorlds.MountAndBlade.Launcher.exe";
        var vanilla = DiagnosticPolicy.IdentifyLauncher(original, "unknown", new string[0]);
        check(vanilla.Launcher == original && vanilla.Blse == "unknown" &&
            vanilla.Source.Contains("hosting"),
            "Original launcher can host the running game");
        var blseHost = DiagnosticPolicy.IdentifyLauncher("Bannerlord.BLSE.LauncherEx.exe",
            "unknown", new string[0]);
        check(blseHost.Blse == "yes" && blseHost.Launcher.Contains("LauncherEx"),
            "BLSE LauncherEx executable is positive evidence");
        var blseParent = DiagnosticPolicy.IdentifyLauncher("Bannerlord.exe",
            "Bannerlord.BLSE.Launcher.exe", new string[0]);
        check(blseParent.Blse == "yes" && blseParent.Source.Contains("parent"),
            "Verified BLSE parent is positive evidence");
        var blseRuntime = DiagnosticPolicy.IdentifyLauncher(original, "unknown",
            new[] { "Bannerlord.BLSE.Standalone" });
        check(blseRuntime.Blse == "yes" && blseRuntime.Source.Contains("runtime"),
            "Loaded BLSE assembly is positive evidence");
        check(DiagnosticPolicy.IdentifyLauncher("Bannerlord.exe", "unknown",
            new string[0]).Blse == "unknown",
            "BLSE remains unknown without evidence");
    }

    private static void LoggingRotationTests(string root, Action<bool, string> check)
    {
        string directory = Path.Combine(root, "rotation");
        Directory.CreateDirectory(directory);
        var children = new List<Process>();
        try
        {
            string executable = typeof(DiagnosticsExtendedTests).Assembly.Location;
            for (int index = 0; index < 3; index++)
                children.Add(Process.Start(new ProcessStartInfo(executable,
                    "--log-child \"" + directory + "\" 200") {
                    UseShellExecute = false, CreateNoWindow = true }));
            foreach (Process child in children)
            {
                check(child.WaitForExit(30000) && child.ExitCode == 0,
                    "Concurrent process log worker completed");
            }
        }
        finally { foreach (Process child in children) child?.Dispose(); }
        string[] paths = LogPaths(directory);
        string[] lines = paths.Where(File.Exists).SelectMany(File.ReadAllLines).ToArray();
        check(lines.Length == 600 && lines.All(x => x.Contains("[CrossProcess]") &&
            x.Contains("child ") && x.Length > 15000),
            "Cross-process rotation retains complete records");
        for (int index = 0; index < 1300; index++)
            UnifiedLog.Write(directory, "INFO", "Rotation", "record " + index + " " +
                new string('Y', 15000));
        paths = LogPaths(directory);
        check(File.Exists(paths[4]) && !File.Exists(Path.Combine(directory,
            UnifiedLog.FileName + ".5")) &&
            paths.Where(File.Exists).All(x => new FileInfo(x).Length <= UnifiedLog.MaxFileBytes),
            "Five log files retain the newest records under 5 MB each");
        string failure;
        string blocked = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blocked, "x");
        check(!UnifiedLog.TryWrite(blocked, "ERROR", "Harness", "important", out failure) &&
            !string.IsNullOrEmpty(failure), "Log write failure is reported to caller");
        string queued = Path.Combine(root, "queued");
        for (int i = 0; i < 10; i++)
            UnifiedLog.QueueWrite(queued, "INFO", "Harness", "repeated");
        UnifiedLog.QueueWrite(queued, "ERROR", "Harness", "important error");
        check(UnifiedLog.FlushPending(queued, TimeSpan.FromSeconds(10)),
            "Queued game-thread logging drains");
        string[] queuedLines = File.ReadAllLines(Path.Combine(queued, UnifiedLog.FileName));
        check(queuedLines.Count(x => x.EndsWith("repeated")) == 1 &&
            queuedLines.Any(x => x.Contains("repeated 9 additional")) &&
            queuedLines.Any(x => x.Contains("[ERROR]") && x.EndsWith("important error")),
            "Repeated INFO messages coalesce and ERROR remains visible");
    }

    private static string[] LogPaths(string directory)
    {
        return Enumerable.Range(0, 5).Select(i => Path.Combine(directory,
            UnifiedLog.FileName + (i == 0 ? "" : "." + i))).ToArray();
    }

    private static DiagnosticsBundle SampleBundle()
    {
        var bundle = new DiagnosticsBundle();
        bundle.SystemInfo["windows"] = "test Windows";
        bundle.GameInfo["bannerlordVersion"] = "v1.5.7";
        bundle.GraphicsStatus["frameGenerationActive"] = "unknown";
        bundle.GraphicsStatus["frameGenerationConfigured"] = "on";
        bundle.GraphicsStatus["optiScaler"] = new SortedDictionary<string, object> {
            { "installedVersion", "10.0.0-dev" }, { "loadedVersion", "10.0.0-dev" } };
        bundle.GraphicsStatus["reShade"] = new SortedDictionary<string, object> {
            { "installedVersion", "6.8.0" }, { "loadedVersion", "6.8.0" } };
        bundle.ActiveSource = "game _MODULES_ argument";
        bundle.Warnings.Add("Runtime XeFG readback unavailable");
        return bundle;
    }

    private static string Entry(ZipArchive archive, string name)
    {
        using (var reader = new StreamReader(archive.GetEntry(name).Open(), Encoding.UTF8))
            return reader.ReadToEnd();
    }

    private static Dictionary<string, object> IssueReport(ZipArchive archive)
    {
        return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
            Entry(archive, "diagnostic-warnings.json"));
    }

    private static string[] IssueLines(Dictionary<string, object> report, string key)
    {
        return ((IEnumerable)report[key]).Cast<string>().ToArray();
    }

    private static void ExportTests(string root, Action<bool, string> check)
    {
        string logDir = Path.Combine(root, "export-logs");
        string gameBin = Path.Combine(root, "export-game", "bin", "Win64_Shipping_Client");
        Directory.CreateDirectory(logDir);
        Directory.CreateDirectory(gameBin);
        string personal = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        File.WriteAllText(Path.Combine(logDir, UnifiedLog.FileName),
            "2026-11-17T04:00:00Z [ERROR] [Harness] failed at " +
            personal + "\\Documents\\private.txt token=abc " +
            "SteamID=76561198012345678 serial=XYZ\n" +
            "2026-11-17T04:00:01Z [WARN] [Harness] missing metadata\n" +
            "Authorization: Bearer abc.DEF-123 user=player@example.com " +
            "\"password\": \"two private words\" apiKey=do-not-share\n" +
            "2026-11-17T04:00:02Z [INFO] [game PID 42 session " +
            UnifiedLog.CurrentSessionId + "] [Harness] current info\n" +
            "2026-11-17T04:00:03Z [ERROR] [game PID 42 session " +
            UnifiedLog.CurrentSessionId + "] [Harness] current failure\n" +
            "2026-11-17T04:00:04Z [WARN] [game PID 42 session " +
            UnifiedLog.CurrentSessionId + "] [Harness] current warning\n" +
            "2026-11-17T04:00:05Z [INFO] [Harness] literal [ERROR] in ordinary text\n" +
            "2026-11-17T04:00:06Z [ERROR] [Harness] text session " +
            UnifiedLog.CurrentSessionId + "] cannot prove origin\n" +
            "2026-11-16T04:00:07Z [ERROR] [setup PID 7 session aaaaaaaaaaaa] [Uninstall] old failure in current file\n");
        File.WriteAllText(Path.Combine(logDir, UnifiedLog.FileName + ".1"),
            "2026-11-16T04:00:00Z [ERROR] [setup PID 7 session aaaaaaaaaaaa] [Uninstall] resolved failure\n" +
            "2026-11-16T04:00:01Z [WARN] [setup PID 7 session aaaaaaaaaaaa] [Uninstall] old warning\n" +
            "2026-11-17T04:00:08Z [WARN] [game PID 42 session " +
            UnifiedLog.CurrentSessionId + "] [Harness] current warning in backup\n");
        string optiSource = "OptiScaler started path=" + personal +
            "\\Games\\private.ini frame=123 token=private-opti\n";
        string reshadeSource = "ReShade started path=C:\\Users\\Example Person\\AppData\\Local\\ReShade\\preset.ini hook=ready password=private-reshade\n";
        File.WriteAllText(Path.Combine(gameBin, "OptiScaler.log"), optiSource);
        File.WriteAllText(Path.Combine(gameBin, "ReShade.log"), reshadeSource);
        var fixedTime = new DateTime(2026, 11, 17, 4, 0, 0, DateTimeKind.Utc);
        var result = DiagnosticsExportService.ExportFromPaths(logDir, gameBin, fixedTime,
            SampleBundle());
        check(result.Success && File.Exists(result.ZipPath) &&
            Path.GetDirectoryName(result.ZipPath) == Path.Combine(logDir, "Diagnostics"),
            "Explicit ZIP export uses the Diagnostics directory");
        using (var archive = ZipFile.OpenRead(result.ZipPath))
        {
            string[] names = archive.Entries.Select(x => x.FullName).ToArray();
            check(new[] { "system-info.json", "game-info.json", "active-mods.json",
                "graphics-status.json", "diagnostic-warnings.json",
                "logs/BannerlordFG.log", "logs/BannerlordFG.log.1",
                "logs/OptiScaler.log", "logs/ReShade.log" }.All(names.Contains),
                "ZIP contains the requested structured reports and known logs");
            string optiExport = Entry(archive, "logs/OptiScaler.log");
            string reshadeExport = Entry(archive, "logs/ReShade.log");
            check(optiExport.Contains("OptiScaler started") && optiExport.Contains("frame=123") &&
                reshadeExport.Contains("ReShade started") && reshadeExport.Contains("hook=ready") &&
                !optiExport.Contains(personal) && !optiExport.Contains("private-opti") &&
                !reshadeExport.Contains("Example Person") &&
                !reshadeExport.Contains("private-reshade") &&
                File.ReadAllText(Path.Combine(gameBin, "OptiScaler.log")) == optiSource &&
                File.ReadAllText(Path.Combine(gameBin, "ReShade.log")) == reshadeSource,
                "All exported third-party logs redact private data and preserve technical fields and sources");
            string log = Entry(archive, "logs/BannerlordFG.log");
            check(!log.Contains(personal) && !log.Contains("abc") &&
                !log.Contains("76561198012345678") && !log.Contains("XYZ") &&
                !log.Contains("player@example.com") &&
                !log.Contains("two private words") && !log.Contains("do-not-share") &&
                log.Contains("[local path redacted]"),
                "Export redacts paths, credentials, Steam IDs and serials");
            string issues = Entry(archive, "diagnostic-warnings.json");
            check(issues.Contains("[ERROR]") && issues.Contains("[WARN]") &&
                !issues.Contains(personal) && !issues.Contains("76561198012345678"),
                "ZIP records redacted errors and diagnostic warnings");
            var report = IssueReport(archive);
            check(IssueLines(report, "errors").Any(x => x.Contains("current failure")) &&
                IssueLines(report, "warnings").Any(x => x.Contains("current warning")) &&
                !IssueLines(report, "errors").Any(x => x.Contains("resolved failure")) &&
                IssueLines(report, "historicalErrors").Any(x => x.Contains("resolved failure")) &&
                IssueLines(report, "historicalErrors").Any(x => x.Contains("old failure in current file")) &&
                IssueLines(report, "historicalWarnings").Any(x => x.Contains("old warning")) &&
                IssueLines(report, "warnings").Any(x => x.Contains("current warning in backup")) &&
                IssueLines(report, "unknownSessionErrors").Any(x => x.Contains("failed at")) &&
                IssueLines(report, "unknownSessionErrors").Any(x => x.Contains("cannot prove origin")) &&
                !IssueLines(report, "errors").Any(x => x.Contains("cannot prove origin")) &&
                !IssueLines(report, "errors").Any(x => x.Contains("ordinary text")) &&
                IssueLines(report, "unknownSessionWarnings").Any(x => x.Contains("missing metadata")) &&
                (string)report["currentSession"] == UnifiedLog.CurrentSessionId,
                "Current, historical and unknown-session issues stay separate across log backups");
            check(((Dictionary<string, object>)report["optiScalerLogStatus"])["logs/OptiScaler.log"]
                .Equals("unknown"), "OptiScaler log age remains unknown without status evidence");
            string graphics = Entry(archive, "graphics-status.json");
            check(graphics.Contains("10.0.0-dev") && graphics.Contains("6.8.0") &&
                graphics.Contains("\"frameGenerationActive\":\"unknown\""),
                "Installed and loaded versions remain distinct from active FG");
        }
        var absentBundle = SampleBundle();
        absentBundle.GraphicsStatus["optiScaler"] = new SortedDictionary<string, object> {
            { "installed", "not detected" }, { "loaded", "no" } };
        var historicalLog = DiagnosticsExportService.ExportFromPaths(logDir, gameBin,
            fixedTime.AddMilliseconds(50), absentBundle);
        using (var archive = ZipFile.OpenRead(historicalLog.ZipPath))
        {
            var report = IssueReport(archive);
            check(historicalLog.Success && archive.GetEntry("logs/OptiScaler.log") != null &&
                ((Dictionary<string, object>)report["optiScalerLogStatus"])["logs/OptiScaler.log"]
                    .Equals("historical") &&
                Entry(archive, "graphics-status.json").Contains("\"installed\":\"not detected\"") &&
                Entry(archive, "graphics-status.json").Contains("\"loaded\":\"no\""),
                "Leftover OptiScaler log is historical while install and load remain separate");
        }
        var loadedBundle = SampleBundle();
        loadedBundle.GraphicsStatus["optiScaler"] = new SortedDictionary<string, object> {
            { "installed", "not detected" }, { "loaded", "yes" } };
        var loadedLog = DiagnosticsExportService.ExportFromPaths(logDir, gameBin,
            fixedTime.AddMilliseconds(75), loadedBundle);
        using (var archive = ZipFile.OpenRead(loadedLog.ZipPath))
            check(((Dictionary<string, object>)IssueReport(archive)["optiScalerLogStatus"])
                ["logs/OptiScaler.log"].Equals("loaded; log age unknown"),
                "Loaded OptiScaler is never classified as historical from installation status");
        string optiLog = Path.Combine(gameBin, "OptiScaler.log");
        File.Delete(optiLog);
        var withoutOpti = DiagnosticsExportService.ExportFromPaths(logDir, gameBin,
            fixedTime.AddMilliseconds(100), SampleBundle());
        using (var archive = ZipFile.OpenRead(withoutOpti.ZipPath))
            check(withoutOpti.Success && archive.GetEntry("logs/OptiScaler.log") == null &&
                withoutOpti.Warnings.Any(x => x.Contains("OptiScaler.log unavailable")),
                "Missing OptiScaler.log warns without blocking the ZIP");
        File.WriteAllText(optiLog, "OptiScaler resumed\n");
        using (new FileStream(optiLog, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var lockedOpti = DiagnosticsExportService.ExportFromPaths(logDir, gameBin,
                fixedTime.AddMilliseconds(200), SampleBundle());
            check(lockedOpti.Success && lockedOpti.Warnings.Any(x =>
                x.Contains("OptiScaler.log unavailable")),
                "Locked OptiScaler.log warns without blocking the ZIP");
        }
        string oversized = new string('x', 5 * 1024 * 1024 + 100) + "\nTAIL_MARKER\n";
        File.WriteAllText(optiLog, oversized);
        long originalLength = new FileInfo(optiLog).Length;
        var trimmed = DiagnosticsExportService.ExportFromPaths(logDir, gameBin,
            fixedTime.AddMilliseconds(300), SampleBundle());
        using (var archive = ZipFile.OpenRead(trimmed.ZipPath))
            check(trimmed.Success && Entry(archive, "logs/OptiScaler.log").Contains("TAIL_MARKER") &&
                trimmed.Warnings.Any(x => x.Contains("truncated")) &&
                new FileInfo(optiLog).Length == originalLength &&
                File.ReadAllText(optiLog) == oversized,
                "Large OptiScaler.log export keeps the tail and never changes the source");
        string variants = PrivacyRedactor.RedactLine(
            "Steam user STEAM_0:1:12345 [U:1:4567] \"token\": \"private-key\" " +
            "Serial Number: HARDWARE123");
        check(!variants.Contains("STEAM_0:1:12345") && !variants.Contains("[U:1:4567]") &&
            !variants.Contains("private-key") && !variants.Contains("HARDWARE123"),
            "Alternative Steam IDs and quoted credentials are redacted");
        string locked = Path.Combine(gameBin, "ReShade.log");
        using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var partial = DiagnosticsExportService.ExportFromPaths(logDir, gameBin,
                fixedTime.AddSeconds(1), SampleBundle());
            check(partial.Success && partial.Warnings.Any(x => x.Contains("ReShade.log unavailable")),
                "Locked third-party log is skipped with a warning");
        }
        string incompleteDir = Path.Combine(root, "incomplete-export");
        Directory.CreateDirectory(incompleteDir);
        var incomplete = DiagnosticsExportService.ExportFromPaths(incompleteDir, gameBin,
            fixedTime.AddSeconds(2), new DiagnosticsBundle());
        using (var archive = ZipFile.OpenRead(incomplete.ZipPath))
            check(incomplete.Success && archive.GetEntry("diagnostic-warnings.json") != null &&
                archive.GetEntry("graphics-status.json") != null,
                "Incomplete diagnostics still produce a valid ZIP");
        string invalidDirectory = Path.Combine(root, "zip-path-is-file");
        File.WriteAllText(invalidDirectory, "x");
        var failed = DiagnosticsExportService.ExportFromPaths(invalidDirectory, gameBin,
            fixedTime, SampleBundle());
        check(!failed.Success && failed.Error.Contains("failed") && failed.ZipPath == null,
            "ZIP creation failure is returned without a partial result");
    }

    private static void ExplorerWorkflowTests(string root, Action<bool, string> check)
    {
        string zip = Path.Combine(root, "selection path & spaces.zip");
        File.WriteAllText(zip, "fixture");
        ProcessStartInfo captured = null;
        var opened = ExplorerSelection.Open(zip, info => { captured = info; return true; });
        check(opened.Opened && captured != null &&
            captured.Arguments == "/select,\"" + Path.GetFullPath(zip) + "\"" &&
            captured.FileName.Equals(Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.Windows), "explorer.exe"),
                StringComparison.OrdinalIgnoreCase) && !captured.UseShellExecute,
            "Explorer receives the exact quoted ZIP path without a shell");
        var refused = ExplorerSelection.Open(zip, _ => throw new IOException("test failure"));
        check(!refused.Opened && refused.Error.Contains("IOException") && File.Exists(zip),
            "Explorer launch failure leaves the ZIP intact");
        check(!ExplorerSelection.Open(Path.Combine(root, "missing.zip"), _ => true).Opened,
            "Explorer is not launched for a missing ZIP");

        var pending = new TaskCompletionSource<DiagnosticsExportResult>();
        int revealCount = 0;
        string revealed = null;
        var flow = DiagnosticsExportService.ExportAndRevealCoreAsync(() => pending.Task,
            path => { revealCount++; revealed = path; return new ExplorerOpenResult { Opened = true }; });
        check(!flow.IsCompleted && revealCount == 0,
            "Explorer waits for the ZIP export to complete");
        pending.SetResult(new DiagnosticsExportResult { Success = true, ZipPath = zip });
        var completed = flow.GetAwaiter().GetResult();
        check(completed.Success && completed.ExplorerOpened && revealCount == 1 && revealed == zip,
            "Successful export reveals only its newly returned ZIP");
        var failed = DiagnosticsExportService.ExportAndRevealCoreAsync(
            () => Task.FromResult(new DiagnosticsExportResult { Error = "ZIP failed" }),
            path => { revealCount++; return new ExplorerOpenResult { Opened = true }; })
            .GetAwaiter().GetResult();
        check(!failed.Success && revealCount == 1,
            "Failed ZIP export never opens Explorer");
        var noExplorer = DiagnosticsExportService.ExportAndRevealCoreAsync(
            () => Task.FromResult(new DiagnosticsExportResult { Success = true, ZipPath = zip }),
            path => new ExplorerOpenResult { Error = "Explorer unavailable" })
            .GetAwaiter().GetResult();
        check(noExplorer.Success && !noExplorer.ExplorerOpened &&
            noExplorer.ExplorerError.Contains("unavailable") && File.Exists(zip),
            "Explorer failure does not turn a completed ZIP into a failed export");

        DateTime now = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
        var gate = new TaskCompletionSource<DiagnosticsExportResult>();
        int starts = 0;
        var workflow = new DiagnosticsExportWorkflow(() => { starts++; return gate.Task; }, () => now);
        check(workflow.TryStart() && !workflow.TryStart() && starts == 1 &&
            !workflow.TryTakeCompleted(out _),
            "Repeated native action callbacks start only one export");
        gate.SetResult(new DiagnosticsExportResult { Success = true, ZipPath = zip });
        check(workflow.TryTakeCompleted(out var done) && done.Success &&
            !workflow.TryTakeCompleted(out _) && !workflow.TryStart(),
            "One completed export is consumed once and immediate repeats are ignored");
        now = now.AddSeconds(3);
        check(workflow.TryStart() && starts == 2,
            "A later deliberate export remains available");
    }
}

// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BannerlordFG.Diagnostics
{
    public sealed class DiagnosticsExportResult
    {
        public bool Success { get; internal set; }
        public string ZipPath { get; internal set; }
        public string Error { get; internal set; }
        public string[] Warnings { get; internal set; } = new string[0];
        public bool ExplorerOpened { get; internal set; }
        public string ExplorerError { get; internal set; }
    }

    // Deliberately has no startup hook. A later native-menu action can await
    // ExportAsync() without doing disk, WMI or ZIP work on the render thread.
    public static class DiagnosticsExportService
    {
        private const int MaxExportLogBytes = 5 * 1024 * 1024;
        private static readonly DateTimeOffset ZipEntryTime =
            new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly Regex IssueLevel = new Regex(
            @"^\S+ \[(WARN|ERROR)\] ", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex LogSession = new Regex(
            @"^\S+ \[(?:WARN|ERROR)\] \[[^\]\r\n]* PID \d+ session ([0-9a-fA-F]{12})\] \[",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // The menu uses this wrapper. ExportAsync remains reusable by callers
        // that need a ZIP without opening a window.
        public static Task<DiagnosticsExportResult> ExportAndRevealAsync()
        {
            return ExportAndRevealCoreAsync(ExportAsync, ExplorerSelection.Open);
        }

        internal static async Task<DiagnosticsExportResult> ExportAndRevealCoreAsync(
            Func<Task<DiagnosticsExportResult>> export,
            Func<string, ExplorerOpenResult> reveal)
        {
            DiagnosticsExportResult result = await export().ConfigureAwait(false);
            if (result == null) return new DiagnosticsExportResult { Error = "Diagnostics export returned no result." };
            if (!result.Success) return result;
            try
            {
                ExplorerOpenResult opened = await Task.Run(() => reveal(result.ZipPath))
                    .ConfigureAwait(false);
                result.ExplorerOpened = opened != null && opened.Opened;
                if (!result.ExplorerOpened)
                    result.ExplorerError = opened?.Error ?? "Explorer did not open.";
            }
            catch (Exception ex)
            {
                // The ZIP has already been closed and saved. Explorer is optional.
                result.ExplorerError = "Explorer could not open: " + ex.GetType().Name;
            }
            return result;
        }

        public static Task<DiagnosticsExportResult> ExportAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    string logDir = Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData), "BannerlordFG");
                    string gameBin;
                    using (Process process = Process.GetCurrentProcess())
                        gameBin = Path.GetDirectoryName(process.MainModule.FileName);
                    // Only the current Bannerlord executable directory is
                    // accepted by this public API. Tests use the internal seam.
                    if (!IsBannerlordBin(gameBin))
                        return new DiagnosticsExportResult {
                            Error = "Current process is not a verified Bannerlord executable host." };
                    return ExportFromPaths(logDir, gameBin, DateTime.UtcNow, null);
                }
                catch (Exception ex)
                {
                    return new DiagnosticsExportResult {
                        Error = "Diagnostics export could not start: " + ex.GetType().Name };
                }
            });
        }

        internal static DiagnosticsExportResult ExportFromPaths(string logDir, string gameBin,
            DateTime utc, DiagnosticsBundle supplied)
        {
            var result = new DiagnosticsExportResult();
            string temporary = null;
            try
            {
                if (string.IsNullOrWhiteSpace(logDir) || string.IsNullOrWhiteSpace(gameBin))
                    throw new ArgumentException("Diagnostic directories unavailable");
                DiagnosticsBundle bundle = supplied ?? DiagnosticsBundle.Collect(gameBin);
                string destination = Path.Combine(logDir, "Diagnostics");
                Directory.CreateDirectory(destination);
                temporary = Path.Combine(destination, ".export-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, false, Encoding.UTF8))
                {
                    bool optiLogIncluded = false;
                    var historicalErrors = new List<string>();
                    var historicalWarnings = new List<string>();
                    var unknownSessionErrors = new List<string>();
                    var unknownSessionWarnings = new List<string>();
                    var optiLogStatus = new SortedDictionary<string, object>(StringComparer.Ordinal);
                    AddJson(zip, "system-info.json", bundle.SystemInfo);
                    AddJson(zip, "game-info.json", bundle.GameInfo);
                    AddJson(zip, "active-mods.json", new SortedDictionary<string, object>(StringComparer.Ordinal) {
                        { "activeSource", bundle.ActiveSource }, { "modules", bundle.Modules } });
                    AddJson(zip, "graphics-status.json", bundle.GraphicsStatus);
                    foreach (var item in KnownLogs(logDir, gameBin).OrderBy(x => x.Key, StringComparer.Ordinal))
                    {
                        if (!File.Exists(item.Value)) continue;
                        try
                        {
                            if (IsRedirectedLog(item.Value, logDir, gameBin))
                            {
                                bundle.Warnings.Add(item.Key + " skipped because it is a redirected path.");
                                continue;
                            }
                            string content = ReadRedactedLog(item.Value, bundle.Warnings, item.Key);
                            if (item.Key.StartsWith("logs/BannerlordFG.log", StringComparison.Ordinal))
                                CollectRecordedIssues(content, bundle, historicalErrors,
                                    historicalWarnings, unknownSessionErrors, unknownSessionWarnings);
                            AddText(zip, item.Key, content);
                            if (item.Key.StartsWith("logs/OptiScaler", StringComparison.Ordinal))
                            {
                                optiLogIncluded = true;
                                optiLogStatus[item.Key] = OptiLogStatus(bundle);
                            }
                        }
                        catch (Exception ex)
                        {
                            bundle.Warnings.Add(item.Key + " unavailable: " + ex.GetType().Name);
                        }
                    }
                    if (!optiLogIncluded)
                        bundle.Warnings.Add("OptiScaler.log unavailable in the known game log locations.");
                    AddJson(zip, "diagnostic-warnings.json",
                        new SortedDictionary<string, object>(StringComparer.Ordinal) {
                            { "errors", bundle.Errors }, { "warnings", bundle.Warnings },
                            { "currentSession", UnifiedLog.CurrentSessionId },
                            { "historicalErrors", historicalErrors },
                            { "historicalWarnings", historicalWarnings },
                            { "unknownSessionErrors", unknownSessionErrors },
                            { "unknownSessionWarnings", unknownSessionWarnings },
                            { "optiScalerLogStatus", optiLogStatus } });
                }
                string stem = "BannerlordFG-Diagnostics-" + utc.ToUniversalTime().ToString(
                    "yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "Z";
                for (int sequence = 0; sequence < 100; sequence++)
                {
                    string candidate = Path.Combine(destination, stem +
                        (sequence == 0 ? "" : "-" + sequence.ToString("D2", CultureInfo.InvariantCulture)) + ".zip");
                    if (File.Exists(candidate)) continue;
                    try
                    {
                        File.Move(temporary, candidate);
                        temporary = null;
                        result.Success = true;
                        result.ZipPath = candidate;
                        result.Warnings = bundle.Warnings.Select(PrivacyRedactor.RedactValue).ToArray();
                        try { UnifiedLog.Write(logDir, "INFO", "Export",
                            "Diagnostics ZIP created with " + result.Warnings.Length + " warning(s)."); }
                        catch { } // A saved ZIP remains a successful export.
                        return result;
                    }
                    catch (IOException) { if (!File.Exists(candidate)) throw; }
                }
                throw new IOException("Too many diagnostics ZIPs have the same timestamp");
            }
            catch (Exception ex)
            {
                result.Error = "Diagnostics ZIP creation failed: " + ex.GetType().Name;
                try { UnifiedLog.Write(logDir, "ERROR", "Export", result.Error); } catch { }
                return result;
            }
            finally
            {
                if (temporary != null)
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static bool IsBannerlordBin(string gameBin)
        {
            if (string.IsNullOrWhiteSpace(gameBin)) return false;
            string host;
            using (Process process = Process.GetCurrentProcess())
                host = Path.GetFileName(process.MainModule.FileName);
            return DiagnosticPolicy.HostKind(host) != "unknown host" &&
                File.Exists(Path.Combine(gameBin, host)) &&
                Directory.Exists(Path.GetFullPath(Path.Combine(gameBin, "..", "..", "Modules", "Native")));
        }

        private static bool IsRedirectedLog(string path, string logDir, string gameBin)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return true;
            string parent = Path.GetDirectoryName(path);
            if (!string.Equals(parent, logDir, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(parent, gameBin, StringComparison.OrdinalIgnoreCase) &&
                (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return true;
            return false;
        }

        private static IEnumerable<KeyValuePair<string, string>> KnownLogs(string logDir, string gameBin)
        {
            yield return new KeyValuePair<string, string>("logs/BannerlordFG.log",
                Path.Combine(logDir, UnifiedLog.FileName));
            for (int index = 1; index <= UnifiedLog.BackupCount; index++)
                yield return new KeyValuePair<string, string>("logs/BannerlordFG.log." + index,
                    Path.Combine(logDir, UnifiedLog.FileName + "." + index));
            yield return new KeyValuePair<string, string>("logs/OptiScaler.log",
                Path.Combine(gameBin, "OptiScaler.log"));
            yield return new KeyValuePair<string, string>("logs/OptiScaler/OptiScaler.log",
                Path.Combine(gameBin, "OptiScaler", "OptiScaler.log"));
            yield return new KeyValuePair<string, string>("logs/ReShade.log",
                Path.Combine(gameBin, "ReShade.log"));
        }

        private static string ReadRedactedLog(string path, List<string> warnings, string entry)
        {
            var builder = new StringBuilder();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > MaxExportLogBytes)
                {
                    stream.Seek(stream.Length - MaxExportLogBytes, SeekOrigin.Begin);
                    warnings.Add(entry + " was truncated to its most recent 5 MB for export.");
                }
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    if (stream.Position > 0) reader.ReadLine(); // Skip partial first line.
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        builder.Append(PrivacyRedactor.RedactLine(line)).Append('\n');
                }
            }
            return builder.ToString();
        }

        private static void CollectRecordedIssues(string content, DiagnosticsBundle bundle,
            List<string> historicalErrors, List<string> historicalWarnings,
            List<string> unknownSessionErrors, List<string> unknownSessionWarnings)
        {
            foreach (string line in content.Split('\n'))
            {
                Match level = IssueLevel.Match(line);
                if (!level.Success) continue;
                bool error = level.Groups[1].Value == "ERROR";
                Match match = LogSession.Match(line);
                List<string> destination;
                if (!match.Success)
                    destination = error ? unknownSessionErrors : unknownSessionWarnings;
                else if (string.Equals(match.Groups[1].Value, UnifiedLog.CurrentSessionId,
                    StringComparison.OrdinalIgnoreCase))
                    destination = error ? bundle.Errors : bundle.Warnings;
                else
                    destination = error ? historicalErrors : historicalWarnings;
                AddRecentIssue(destination, line);
            }
        }

        private static string OptiLogStatus(DiagnosticsBundle bundle)
        {
            if (!bundle.GraphicsStatus.TryGetValue("optiScaler", out object status) ||
                !(status is IDictionary<string, object> component)) return "unknown";
            string installed = component.TryGetValue("installed", out object installValue)
                ? installValue as string : null;
            string loaded = component.TryGetValue("loaded", out object loadValue)
                ? loadValue as string : null;
            if (loaded == "yes") return "loaded; log age unknown";
            if (installed == "not detected" && loaded == "no") return "historical";
            if (installed == "verified" || installed == "detected; integrity unknown" ||
                installed == "incomplete or invalid") return "installed; log age unknown";
            return "unknown";
        }

        private static void AddRecentIssue(List<string> destination, string line)
        {
            if (destination.Contains(line)) return;
            destination.Add(line);
            if (destination.Count > 100) destination.RemoveAt(0);
        }

        private static void AddJson(ZipArchive zip, string name, object value)
        {
            var json = new JavaScriptSerializer().Serialize(Sanitize(value));
            AddText(zip, name, json + "\n");
        }

        private static object Sanitize(object value)
        {
            if (value is string text) return PrivacyRedactor.RedactValue(text);
            if (value is IDictionary<string, object> dictionary)
            {
                var clean = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (var entry in dictionary) clean[entry.Key] = Sanitize(entry.Value);
                return clean;
            }
            if (value is IEnumerable sequence)
            {
                var clean = new List<object>();
                foreach (object item in sequence) clean.Add(Sanitize(item));
                return clean;
            }
            return value;
        }

        private static void AddText(ZipArchive zip, string name, string text)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = ZipEntryTime;
            using (Stream output = entry.Open())
            using (var writer = new StreamWriter(output, new UTF8Encoding(false)))
                writer.Write(text);
        }
    }

    internal sealed class ExplorerOpenResult
    {
        internal bool Opened;
        internal string Error;
    }

    internal static class ExplorerSelection
    {
        // No shell command is constructed. Explorer receives one quoted,
        // absolute file path with its standard /select switch.
        internal static ExplorerOpenResult Open(string zipPath)
        {
            return Open(zipPath, StartExplorer);
        }

        internal static ExplorerOpenResult Open(string zipPath,
            Func<ProcessStartInfo, bool> start)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(zipPath))
                    return new ExplorerOpenResult { Error = "ZIP path is unavailable." };
                string full = Path.GetFullPath(zipPath);
                if (!full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(full) || full.IndexOf('"') >= 0)
                    return new ExplorerOpenResult { Error = "Saved ZIP is unavailable." };
                string explorer = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.Windows), "explorer.exe");
                if (!File.Exists(explorer))
                    return new ExplorerOpenResult { Error = "Windows File Explorer is unavailable." };
                var info = new ProcessStartInfo {
                    FileName = explorer,
                    Arguments = "/select,\"" + full + "\"",
                    WorkingDirectory = Path.GetDirectoryName(full),
                    UseShellExecute = false,
                    CreateNoWindow = false
                };
                return start(info)
                    ? new ExplorerOpenResult { Opened = true }
                    : new ExplorerOpenResult { Error = "Windows File Explorer did not start." };
            }
            catch (Exception ex)
            {
                return new ExplorerOpenResult { Error = "Windows File Explorer could not open: " + ex.GetType().Name };
            }
        }

        private static bool StartExplorer(ProcessStartInfo info)
        {
            using (Process process = Process.Start(info)) return process != null;
        }
    }

    // One request at a time for the native action. A short post-click guard
    // absorbs duplicate UI callbacks without blocking a later deliberate export.
    internal sealed class DiagnosticsExportWorkflow
    {
        private readonly object _gate = new object();
        private readonly Func<Task<DiagnosticsExportResult>> _work;
        private readonly Func<DateTime> _utcNow;
        private Task<DiagnosticsExportResult> _pending;
        private DateTime _nextAllowedUtc;

        internal DiagnosticsExportWorkflow(Func<Task<DiagnosticsExportResult>> work,
            Func<DateTime> utcNow = null)
        {
            _work = work ?? throw new ArgumentNullException(nameof(work));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        internal bool TryStart()
        {
            lock (_gate)
            {
                DateTime now = _utcNow();
                if (_pending != null || now < _nextAllowedUtc) return false;
                _nextAllowedUtc = now.AddSeconds(2);
                try { _pending = _work() ?? Task.FromResult(new DiagnosticsExportResult {
                    Error = "Diagnostics export did not start." }); }
                catch (Exception ex) { _pending = Task.FromResult(new DiagnosticsExportResult {
                    Error = "Diagnostics export could not start: " + ex.GetType().Name }); }
                return true;
            }
        }

        internal bool TryTakeCompleted(out DiagnosticsExportResult result)
        {
            Task<DiagnosticsExportResult> task;
            lock (_gate)
            {
                if (_pending == null || !_pending.IsCompleted)
                { result = null; return false; }
                task = _pending;
                _pending = null;
            }
            try { result = task.GetAwaiter().GetResult() ?? new DiagnosticsExportResult {
                Error = "Diagnostics export returned no result." }; }
            catch (Exception ex) { result = new DiagnosticsExportResult {
                Error = "Diagnostics export failed: " + ex.GetType().Name }; }
            return true;
        }
    }
}

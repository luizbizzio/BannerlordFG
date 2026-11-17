// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace BannerlordFG.Diagnostics
{
    // Shared by the game and maintenance DLL. A short-lived, exclusive lock
    // file serializes append and rotation even across elevated processes.
    internal static class UnifiedLog
    {
        internal const string FileName = "BannerlordFG.log";
        internal const long MaxFileBytes = 5L * 1024L * 1024L;
        internal const int BackupCount = 4;
        private static readonly object Gate = new object();
        private static readonly object QueueGate = new object();
        private static readonly Queue<PendingRecord> Pending = new Queue<PendingRecord>();
        private static readonly ManualResetEventSlim QueueIdle = new ManualResetEventSlim(true);
        private static readonly string Session = Guid.NewGuid().ToString("N").Substring(0, 12);
        internal static string CurrentSessionId => Session;
        private static readonly int Pid = SafePid();
        private static readonly string ProcessName = SafeProcessName();
        private static bool _draining;
        private static string _lastQueuedKey;
        private static DateTime _lastQueuedAt;
        private static int _suppressed;
        private static string _lastQueuedLevel;
        private static string _lastQueuedSource;
        private static string _lastQueuedDirectory;

        private sealed class PendingRecord
        {
            internal string Directory;
            internal DateTime TimeUtc;
            internal string Level;
            internal string Source;
            internal string Message;
        }

        internal static void Write(string directory, string source, string message)
        {
            Write(directory, "INFO", source, message);
        }

        internal static void Write(string directory, string level, string source, string message)
        {
            string failure;
            if (!TryWrite(directory, level, source, message, out failure))
                ReportFailure(level, failure);
        }

        internal static bool TryWrite(string directory, string level, string source,
            string message, out string failure)
        {
            return TryWriteAt(directory, DateTime.UtcNow, level, source, message, out failure);
        }

        private static bool TryWriteAt(string directory, DateTime timeUtc, string level,
            string source, string message, out string failure)
        {
            failure = null;
            try
            {
                if (string.IsNullOrWhiteSpace(directory))
                    throw new ArgumentException("Log directory unavailable");
                byte[] bytes = new UTF8Encoding(false).GetBytes(Format(timeUtc, level, source, message));
                if (bytes.Length > MaxFileBytes) throw new InvalidDataException("Log record exceeds size limit");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, FileName);
                string lockPath = path + ".lock";
                lock (Gate)
                {
                    FileStream exclusive = null;
                    for (int attempt = 0; attempt < 50; attempt++)
                    {
                        try
                        {
                            exclusive = new FileStream(lockPath, FileMode.OpenOrCreate,
                                FileAccess.ReadWrite, FileShare.None);
                            break;
                        }
                        catch (IOException) { if (attempt == 49) throw; Thread.Sleep(10); }
                    }
                    using (exclusive) AppendWithRotation(path, bytes);
                    return true;
                }
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name + ": " + Clean(ex.Message);
                return false;
            }
        }

        private static void AppendWithRotation(string path, byte[] bytes)
        {
            if (File.Exists(path))
            {
                long length = new FileInfo(path).Length;
                if (length > MaxFileBytes)
                {
                    TrimOversizedLegacyLog(path);
                    length = new FileInfo(path).Length;
                }
                if (length + bytes.Length > MaxFileBytes) Rotate(path);
            }
            using (var stream = new FileStream(path, FileMode.OpenOrCreate,
                FileAccess.Write, FileShare.Read))
            {
                // A pre-upgrade writer can append without our lock. Never
                // cross the cap even if it raced our length check.
                if (stream.Length + bytes.Length > MaxFileBytes)
                    throw new IOException("Current log changed during rotation");
                stream.Seek(0, SeekOrigin.End);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }

        private static void Rotate(string path)
        {
            string oldest = path + "." + BackupCount;
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int index = BackupCount - 1; index >= 1; index--)
            {
                string source = path + "." + index;
                if (File.Exists(source)) File.Move(source, path + "." + (index + 1));
            }
            if (File.Exists(path)) File.Move(path, path + ".1");
        }

        private static void TrimOversizedLegacyLog(string path)
        {
            string temporary = path + ".trim-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var destination = new FileStream(temporary, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    source.Seek(source.Length - MaxFileBytes, SeekOrigin.Begin);
                    // Discard a partial first record, retaining recent whole
                    // lines within the five megabyte retention limit.
                    int value;
                    while ((value = source.ReadByte()) >= 0 && value != '\n') { }
                    source.CopyTo(destination);
                }
                File.Replace(temporary, path, null);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        // Game-thread callers enqueue; a single ThreadPool drain performs all
        // disk I/O and rotation. Maintenance callers use synchronous Write.
        internal static void QueueWrite(string directory, string level, string source, string message)
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                lock (QueueGate)
                {
                    string key = directory + "\0" + level + "\0" + source + "\0" + message;
                    if (!string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase) &&
                        key == _lastQueuedKey && now - _lastQueuedAt < TimeSpan.FromSeconds(2))
                    {
                        _suppressed++;
                        _lastQueuedAt = now;
                        return;
                    }
                    EnqueueSuppressed(now);
                    _lastQueuedKey = key;
                    _lastQueuedAt = now;
                    _lastQueuedLevel = level;
                    _lastQueuedSource = source;
                    _lastQueuedDirectory = directory;
                    Pending.Enqueue(new PendingRecord { Directory = directory, TimeUtc = now, Level = level,
                        Source = source, Message = message });
                    StartDrain();
                }
            }
            catch (Exception ex) { ReportFailure(level, ex.GetType().Name); }
        }

        private static void EnqueueSuppressed(DateTime now)
        {
            if (_suppressed == 0) return;
            Pending.Enqueue(new PendingRecord { Directory = _lastQueuedDirectory, TimeUtc = now, Level = _lastQueuedLevel,
                Source = _lastQueuedSource,
                Message = "Previous message repeated " + _suppressed + " additional time(s)." });
            _suppressed = 0;
        }

        private static void StartDrain()
        {
            if (_draining) return;
            _draining = true;
            QueueIdle.Reset();
            if (!ThreadPool.QueueUserWorkItem(_ => Drain()))
            {
                _draining = false;
                QueueIdle.Set();
                ReportFailure("ERROR", "ThreadPool declined log drain");
            }
        }

        private static void Drain()
        {
            try
            {
                while (true)
                {
                    PendingRecord record;
                    lock (QueueGate)
                    {
                        if (Pending.Count == 0) { _draining = false; QueueIdle.Set(); return; }
                        record = Pending.Dequeue();
                    }
                    string failure;
                    if (!TryWriteAt(record.Directory, record.TimeUtc, record.Level, record.Source,
                        record.Message, out failure)) ReportFailure(record.Level, failure);
                }
            }
            catch (Exception ex)
            {
                lock (QueueGate) { _draining = false; QueueIdle.Set(); }
                ReportFailure("ERROR", "Log drain failed: " + ex.GetType().Name);
            }
        }

        internal static bool FlushPending(string directory, TimeSpan timeout)
        {
            lock (QueueGate)
            {
                EnqueueSuppressed(DateTime.UtcNow);
                if (Pending.Count > 0) StartDrain();
            }
            return QueueIdle.Wait(timeout);
        }

        private static string Format(DateTime timeUtc, string level, string source, string message)
        {
            string severity = level == "WARN" || level == "ERROR" ? level : "INFO";
            return timeUtc.ToString("o") + " [" + severity + "] [" + Clean(ProcessName) +
                " PID " + Pid + " session " + Session + "] [" + Clean(source) + "] " +
                Clean(message) + Environment.NewLine;
        }

        internal static string Clean(string value)
        {
            string result = (value ?? "unknown").Replace("\r", "\\r").Replace("\n", "\\n");
            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(profile))
                {
                    int start;
                    int search = 0;
                    while ((start = result.IndexOf(profile, search,
                        StringComparison.OrdinalIgnoreCase)) >= 0)
                    {
                        result = result.Substring(0, start) + "%USERPROFILE%" +
                            result.Substring(start + profile.Length);
                        search = start + "%USERPROFILE%".Length;
                    }
                }
            }
            catch { }
            return result.Length <= 16384 ? result : result.Substring(0, 16384) + " [truncated]";
        }

        private static void ReportFailure(string level, string failure)
        {
            string detail = "BannerlordFG logging failed (" + (failure ?? "unknown") + ").";
            try { Trace.TraceError(detail); } catch { }
            if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase))
                try { Console.Error.WriteLine(detail); } catch { }
        }

        private static int SafePid()
        {
            try { using (var process = Process.GetCurrentProcess()) return process.Id; }
            catch { return 0; }
        }

        private static string SafeProcessName()
        {
            try { using (var process = Process.GetCurrentProcess()) return process.ProcessName; }
            catch { return "unknown"; }
        }
    }
}

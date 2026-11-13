// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace BannerlordFG.Diagnostics
{
    // Compiled into both the game module and the maintenance DLL. Never hold
    // an open write handle while another process (or PowerShell) is running.
    internal static class UnifiedLog
    {
        internal const string FileName = "BannerlordFG.log";

        internal static void Write(string directory, string source, string message)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, FileName);
                string record = DateTime.UtcNow.ToString("o") + " [" + source +
                    "] [PID " + Process.GetCurrentProcess().Id + "] " + message + Environment.NewLine;
                byte[] bytes = new UTF8Encoding(false).GetBytes(record);
                // FileShare.Read gives each append exclusive ownership while
                // still allowing log viewers when the file is not being written.
                // Retries cover concurrent game, elevated installer and CMD appends.
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    try
                    {
                        using (var stream = new FileStream(path, FileMode.OpenOrCreate,
                            FileAccess.Write, FileShare.Read))
                        {
                            stream.Seek(0, SeekOrigin.End);
                            stream.Write(bytes, 0, bytes.Length);
                        }
                        return;
                    }
                    catch (IOException) { if (attempt == 99) throw; Thread.Sleep(25); }
                }
            }
            catch { /* Never crash the game or installer because logging failed. */ }
        }
    }
}

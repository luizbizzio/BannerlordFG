// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Management;

namespace BannerlordFG.Diagnostics
{
    internal static class LauncherDetector
    {
        internal static string VerifiedParent(string gameBin, int pid, Action<string> warning)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = " + pid))
                {
                    searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                    using (ManagementObjectCollection rows = searcher.Get())
                        foreach (ManagementBaseObject row in rows)
                            using (row)
                            {
                                int parentId = Convert.ToInt32(row["ParentProcessId"]);
                                using (Process parent = Process.GetProcessById(parentId))
                                using (Process child = Process.GetCurrentProcess())
                                {
                                    if (parent.StartTime > child.StartTime) return "unknown";
                                    string path = parent.MainModule.FileName;
                                    if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),
                                        Path.GetFullPath(gameBin), StringComparison.OrdinalIgnoreCase))
                                        return "unknown";
                                    string name = Path.GetFileName(path);
                                    return DiagnosticPolicy.HostKind(name).IndexOf("launcher",
                                        StringComparison.OrdinalIgnoreCase) >= 0 ? name : "unknown";
                                }
                            }
                }
            }
            catch (Exception ex) { warning("Parent launcher unavailable: " + ex.GetType().Name); }
            return "unknown";
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace BannerlordFG.Diagnostics
{
    internal static class DiagnosticsRuntimeContext
    {
        private static readonly object Gate = new object();
        private static string[] _activeIds;
        private static string _activeSource = "unknown";
        private static string _graphicsStatus = "OptiScaler status unknown; activity unknown";

        internal static void SetActiveModules(IEnumerable<string> ids, string source)
        {
            lock (Gate)
            {
                _activeIds = ids == null ? null : ids.ToArray();
                _activeSource = _activeIds == null ? "unknown" : source ?? "unknown";
            }
        }

        internal static string[] ActiveModules(out string source)
        {
            lock (Gate)
            {
                source = _activeSource;
                return _activeIds == null ? null : (string[])_activeIds.Clone();
            }
        }

        internal static void SetGraphicsStatus(GraphicsSnapshot snapshot)
        {
            if (snapshot == null) return;
            string version = snapshot.OptiScaler.LoadedVersion ??
                snapshot.OptiScaler.InstalledVersion ??
                (snapshot.OptiBuild == "unknown" ? "version unknown" : snapshot.OptiBuild);
            string load = snapshot.OptiScaler.Loaded == true ? "Loaded" :
                snapshot.OptiScaler.Loaded == false ? "not loaded" : "load unknown";
            string xefg = snapshot.FgConfigured == true &&
                string.Equals(snapshot.Backend, "XeFG", StringComparison.OrdinalIgnoreCase)
                ? "XeFG set" : "XeFG unknown";
            lock (Gate)
                _graphicsStatus = "OptiScaler " + version + "; " +
                    load + "; " + xefg + "; activity unknown";
        }

        internal static string GraphicsStatus()
        {
            lock (Gate) return _graphicsStatus;
        }
    }
}

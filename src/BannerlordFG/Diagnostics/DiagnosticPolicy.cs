// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace BannerlordFG.Diagnostics
{
    internal sealed class ComponentEvidence
    {
        internal bool Present;
        internal bool? Loaded;
        internal bool? ValidInstall;
        internal string Proxy;
        internal string Version;
        internal string InstalledVersion;
        internal string LoadedVersion;
    }

    internal sealed class LauncherEvidence
    {
        internal string Launcher = "unknown";
        internal string Blse = "unknown";
        internal string Source = "unknown";
    }

    internal static class DiagnosticPolicy
    {
        internal static string InstalledState(ComponentEvidence component)
        {
            if (component == null || !component.Present) return "not detected";
            if (component.ValidInstall == true) return "verified";
            if (component.ValidInstall == false) return "incomplete or invalid";
            return "detected; integrity unknown";
        }

        internal static string LoadedState(ComponentEvidence component)
        {
            if (component == null || !component.Loaded.HasValue) return "unknown";
            return component.Loaded.Value ? "yes" : "no";
        }

        internal static string HostKind(string executable)
        {
            string name = (executable ?? "").ToLowerInvariant();
            if (name == "bannerlord.blse.launcherex.exe") return "BLSE LauncherEx host";
            if (name == "bannerlord.blse.standalone.exe") return "BLSE Standalone host";
            if (name == "bannerlord.blse.launcher.exe") return "BLSE Launcher host";
            if (name == "taleworlds.mountandblade.launcher.exe" || name == "launcher.native.exe")
                return "original Bannerlord launcher host";
            if (name == "bannerlord.exe" || name == "bannerlord.native.exe")
                return "direct Bannerlord game host";
            return "unknown host";
        }

        internal static LauncherEvidence IdentifyLauncher(string hostExecutable,
            string verifiedParentExecutable, IEnumerable<string> loadedAssemblyNames)
        {
            var result = new LauncherEvidence();
            string host = hostExecutable ?? "";
            string parent = verifiedParentExecutable ?? "";
            bool hostBlse = host.StartsWith("Bannerlord.BLSE.", StringComparison.OrdinalIgnoreCase) &&
                HostKind(host) != "unknown host";
            bool parentBlse = parent.StartsWith("Bannerlord.BLSE.", StringComparison.OrdinalIgnoreCase) &&
                HostKind(parent) != "unknown host";
            bool runtimeBlse = (loadedAssemblyNames ?? Enumerable.Empty<string>()).Any(name =>
                name != null && (name.StartsWith("Bannerlord.BLSE.", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Bannerlord.BLSE", StringComparison.OrdinalIgnoreCase)));
            if (hostBlse)
            {
                result.Launcher = host;
                result.Blse = "yes";
                result.Source = "current executable";
            }
            else if (parentBlse)
            {
                result.Launcher = parent;
                result.Blse = "yes";
                result.Source = "verified parent executable";
            }
            else if (HostKind(host).IndexOf("launcher", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Launcher = host;
                result.Source = "current executable hosting BannerlordFG";
            }
            else if (HostKind(parent).IndexOf("launcher", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Launcher = parent;
                result.Source = "verified parent executable";
            }
            if (runtimeBlse)
            {
                result.Blse = "yes";
                if (!hostBlse && !parentBlse) result.Source += "; BLSE runtime assembly loaded";
            }
            return result;
        }

        internal static string FrameGenerationState(string configuredBackend,
            bool? configuredEnabled, bool? runtimeConfirmed)
        {
            if (runtimeConfirmed == true) return "active (runtime confirmed)";
            if (configuredEnabled == true)
                return "configured on; runtime activity unknown";
            if (configuredEnabled == false) return "configured off";
            return "unknown";
        }

        internal static string IdentifyProxy(string productName, string originalFilename,
            string description)
        {
            string text = string.Join(" ", new[] { productName, originalFilename, description }
                .Where(x => !string.IsNullOrEmpty(x))).ToLowerInvariant();
            if (text.Contains("optiscaler")) return "OptiScaler";
            if (text.Contains("reshade")) return "ReShade";
            return "unknown";
        }
    }
}

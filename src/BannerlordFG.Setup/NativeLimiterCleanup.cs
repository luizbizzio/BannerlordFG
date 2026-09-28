// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BannerlordFG.Setup
{
    // Compatibility cleanup for the short-lived v0.3.12 Unlimited experiment.
    // v0.3.13 always keeps Bannerlord's supported finite 30..360 FPS range.
    internal static class NativeLimiterCleanup
    {
        private static readonly Regex Limit = new Regex(
            @"(?mi)^(?<prefix>[ \t]*max_framerate[ \t]*=[ \t]*)(?<value>[0-9]+(?:\.[0-9]+)?)(?<suffix>[ \t]*(?:#.*)?\r?)$",
            RegexOptions.Compiled);
        private static string Marker => Path.Combine(AutoFirstRun.Folder, "native-unlimited.marker");
        private static string Config => Path.Combine(
            Environment.GetEnvironmentVariable("BFG_VERIFIED_USER_DOCUMENTS") ??
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Mount and Blade II Bannerlord", "Configs", "engine_config.txt");

        internal static void ApplyRequestedAfterExit(Action<string> log) => RestoreFiniteAndRetire(log);
        internal static void RestoreAfterUninstall(Action<string> log) => RestoreFiniteAndRetire(log);

        private static void RestoreFiniteAndRetire(Action<string> log)
        {
            if (!File.Exists(Marker)) return;
            try
            {
                float original;
                bool valid = float.TryParse(File.ReadAllText(Marker).Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out original) && original >= 30f && original <= 360f;
                if (valid && File.Exists(Config))
                {
                    string text = File.ReadAllText(Config);
                    Match m = Limit.Match(text);
                    if (m.Success)
                    {
                        float current = float.Parse(m.Groups["value"].Value, CultureInfo.InvariantCulture);
                        if (current < 30f || current > 360f)
                        {
                            Rewrite(text, original.ToString(CultureInfo.InvariantCulture));
                            log("Restored finite Bannerlord FPS limit after deprecated v0.3.12 Unlimited mode.");
                        }
                    }
                }
                File.Delete(Marker);
            }
            catch (Exception e) { log("Finite limiter migration deferred: " + e.Message); }
        }

        private static void Rewrite(string original, string value)
        {
            string patched = Limit.Replace(original,
                m => m.Groups["prefix"].Value + value + m.Groups["suffix"].Value, 1);
            string temp = Config + ".bannerlordfg.tmp";
            try
            {
                File.WriteAllText(temp, patched, new UTF8Encoding(false));
                File.Replace(temp, Config, Config + ".bannerlordfg-last.bak");
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BannerlordFG.Setup
{
    // Called only AFTER Bannerlord has exited normally. If the user changed the
    // game FPS setting outside BannerlordFG, leave it alone, preserving autonomy.
    internal static class FpsCounterCleanup
    {
        private static readonly Regex Flag = new Regex(
            @"(?mi)^(?<before>[ \t]*show_framerate[ \t]*=[ \t]*)(?<value>[01])(?<after>[ \t]*(?:#.*)?\r?)$",
            RegexOptions.Compiled);
        private static string Marker => Path.Combine(AutoFirstRun.Folder, "fps-counter-owned.txt");
        private static string Config => Path.Combine((Environment.GetEnvironmentVariable("BFG_VERIFIED_USER_DOCUMENTS") ??
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)), "Mount and Blade II Bannerlord",
            "Configs", "engine_config.txt");
        internal static void ApplyRequestedAfterExit(Action<string> log)
        {
            if (!File.Exists(Marker)) return;
            try
            {
                string[] values = File.ReadAllLines(Marker);
                if (values.Length < 2 || !int.TryParse(values[1], out int desired) ||
                    (desired != 0 && desired != 1)) return;
                if (!File.Exists(Config)) { log("FPS counter config missing after shutdown."); return; }
                string current = File.ReadAllText(Config);
                if (!Flag.IsMatch(current)) { log("show_framerate is missing after shutdown."); return; }
                string patched = Flag.Replace(current, m => m.Groups["before"].Value +
                    desired + m.Groups["after"].Value, 1);
                if (patched == current) return;
                string tmp = Config + ".bannerlordfg.tmp";
                try
                {
                    File.WriteAllText(tmp, patched, new UTF8Encoding(false));
                    File.Replace(tmp, Config, Config + ".bannerlordfg-backup");
                }
                finally { if (File.Exists(tmp)) File.Delete(tmp); }
                log("Applied native show_framerate after game shutdown; available on next launch.");
            }
            catch (Exception ex) { log("FPS counter post-exit setup failed: " + ex.Message); }
        }

        internal static void RestoreAfterUninstall(Action<string> log)
        {
            if (!File.Exists(Marker)) return;
            try
            {
                string[] values = File.ReadAllLines(Marker);
                int original, applied;
                if (values.Length != 2 || !int.TryParse(values[0], out original) ||
                    !int.TryParse(values[1], out applied) || original < 0 || original > 1 ||
                    applied < 0 || applied > 1)
                { log("Invalid FPS marker; leaving the user's engine configuration untouched."); return; }
                if (!File.Exists(Config))
                { log("engine_config.txt was moved; FPS marker preserved for manual cleanup."); return; }
                string data = File.ReadAllText(Config);
                var m = Flag.Match(data);
                if (!m.Success)
                { log("show_framerate not found; FPS marker preserved."); return; }
                int current = m.Groups["value"].Value == "1" ? 1 : 0;
                if (current == applied && current != original)
                {
                    string updated = Flag.Replace(data, x => x.Groups["before"].Value + original +
                        x.Groups["after"].Value, 1);
                    string tmp = Config + ".bannerlordfg.tmp";
                    try
                    {
                        File.WriteAllText(tmp, updated, new UTF8Encoding(false));
                        File.Replace(tmp, Config, Config + ".bannerlordfg-backup");
                    }
                    finally { if (File.Exists(tmp)) File.Delete(tmp); }
                    log("Original show_framerate setting restored during FG uninstall.");
                }
                else log("FPS setting independently changed; leaving it untouched.");
                File.Delete(Marker);
                // This backup name is reserved for our own single-setting edits.
                string backup = Config + ".bannerlordfg-backup";
                if (File.Exists(backup)) File.Delete(backup);
            }
            catch (Exception ex) { log("FPS setting cleanup deferred: " + ex.Message); }
        }
    }
}

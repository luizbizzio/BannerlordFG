// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BannerlordFG.Core
{
    /// <summary>
    /// Toggles Bannerlord's own show_framerate flag. This is the GAME-rendered
    /// FPS counter, not an estimate of generated display frames. The engine
    /// reads its config at boot; no unverified live engine hook is attempted.
    /// </summary>
    internal sealed class NativeFpsCounter
    {
        private bool? _requestedValue;
        // A tiny ownership manifest lets the UNINSTALL helper restore the
        // player's initial counter preference without touching any other
        // engine configuration lines.
        private static string Marker => Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "BannerlordFG", "fps-counter-owned.txt");
        public string ConfigPath => Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord",
            "Configs", "engine_config.txt");
        private static readonly Regex Flag = new Regex(
            @"(?mi)^(?<before>[ \t]*show_framerate[ \t]*=[ \t]*)(?<value>[01])(?<after>[ \t]*(?:#.*)?\r?)$",
            RegexOptions.Compiled);
        public bool IsAvailable
        {
            get
            {
                try { return File.Exists(ConfigPath) && Flag.IsMatch(File.ReadAllText(ConfigPath)); }
                catch { return false; }
            }
        }
        public bool IsEnabled
        {
            get
            {
                if (_requestedValue.HasValue) return _requestedValue.Value;
                try
                {
                    var m = Flag.Match(File.ReadAllText(ConfigPath));
                    return m.Success && m.Groups["value"].Value == "1";
                }
                catch { return false; }
            }
        }
        public bool TrySet(bool enabled, out string result)
        {
            result = null;
            try
            {
                if (!File.Exists(ConfigPath))
                { result = "Bannerlord engine_config.txt is missing."; return false; }
                string input = File.ReadAllText(ConfigPath);
                var current = Flag.Match(input);
                if (!current.Success)
                { result = "Bannerlord engine_config.txt has no show_framerate setting."; return false; }
                int old = current.Groups["value"].Value == "1" ? 1 : 0;
                int original = old;
                if (File.Exists(Marker))
                {
                    var lines = File.ReadAllLines(Marker);
                    if (lines.Length != 2 || !int.TryParse(lines[0], out original) ||
                        (original != 0 && original != 1))
                        throw new InvalidDataException("Invalid FPS ownership marker; no file changed.");
                }
                string replacement = Flag.Replace(input, m =>
                    m.Groups["before"].Value + (enabled ? "1" : "0") + m.Groups["after"].Value, 1);
                if (replacement != input)
                {
                    string tmp = ConfigPath + ".bannerlordfg.tmp";
                    try
                    {
                        File.WriteAllText(tmp, replacement, new UTF8Encoding(false));
                        File.Replace(tmp, ConfigPath, ConfigPath + ".bannerlordfg-backup");
                    }
                    finally { if (File.Exists(tmp)) File.Delete(tmp); }
                }
                if (original == (enabled ? 1 : 0))
                {
                    if (File.Exists(Marker)) File.Delete(Marker);
                    // The player returned to the original preference. There
                    // is no longer anything for the uninstall helper to own.
                    string backup = ConfigPath + ".bannerlordfg-backup";
                    if (File.Exists(backup)) File.Delete(backup);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Marker));
                    File.WriteAllLines(Marker, new[] { original.ToString(), enabled ? "1" : "0" });
                }
                _requestedValue = enabled;
                result = "Native game FPS counter " + (enabled ? "enabled" : "disabled") +
                    ". The engine may require a restart to show this change.";
                return true;
            }
            catch (Exception ex)
            { result = "Could not update the native FPS counter: " + ex.Message; return false; }
        }
        // Vanilla Options may re-save engine_config.txt after the user changes
        // our visual-only control. Restore only our requested show_framerate
        // setting, leaving every other setting exactly as the game wrote it.
        public void ReapplyRequested()
        {
            if (!_requestedValue.HasValue) return;
            string ignored;
            TrySet(_requestedValue.Value, out ignored);
        }
    }
}

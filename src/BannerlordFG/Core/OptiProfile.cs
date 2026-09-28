// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BannerlordFG.Core
{
    /// <summary>
    /// The nightly-20260926 INI contract. Backend selection is NEXT-LAUNCH;
    /// OptiScaler's documented FG and FPS overlay hotkeys are runtime controls.
    /// </summary>
    public static class OptiProfile
    {
        private static readonly Regex ShortcutAssignment = new Regex(
            @"(?mi)^(?<prefix>[ \t]*[A-Za-z_][A-Za-z0-9_]*(?:ShortcutKey|Hotkey)[ \t]*=[ \t]*)[^;#\r\n]*(?<suffix>[ \t]*(?:[;#][^\r\n]*)?)$",
            RegexOptions.Compiled);
        private static readonly Regex MenuSection = new Regex(
            @"(?mi)^\[Menu\][ \t]*\r?\n(?<body>[\s\S]*?)(?=^\[|\z)", RegexOptions.Compiled);
        public static string PatchText(string input, FgSettings setting)
        {
            if (string.IsNullOrWhiteSpace(input)) throw new InvalidDataException("Missing OptiScaler.ini.");
            setting.Validate(true);
            string t = input;
            // Default DX11 image backend follows the tested Bannerlord FSR 3.1 path.
            // The game still exposes the DLSS-branded input as its quality selector.
            t = Put(t, "Upscalers", "Dx11Upscaler", "fsr31");
            // Disable the extra CAS/RCAS pass without imposing a sharpness override.
            // These fields are optional because upstream OptiScaler may rename them.
            t = OptionalPut(t, "CAS", "Enabled", "false");
            // Retire sharpness overrides left by v0.3.15/v0.3.16.
            t = OptionalPut(t, "Sharpness", "OverrideSharpness", "false");
            t = Put(t, "FrameGen", "Enabled", setting.Enabled ? "true" : "false");
            t = Put(t, "FrameGen", "FGInput", "Upscaler");
            t = Put(t, "FrameGen", "FGOutput", "XeFG");
            t = Put(t, "FrameGen", "DebugView", "false");
            t = Put(t, "XeFG", "InterpolationCount", (setting.Multiplier - 1).ToString());
            t = Put(t, "XeFG", "ForceBorderless", "true");
            // All user-facing shortcuts are disabled. F23/F24 are reserved as
            // *internal* live control channels for FG and the FPS overlay.
            // They cannot be -1 while runtime toggling depends on them.
            t = Put(t, "Menu", "OverlayMenu", "true");
            t = Put(t, "Menu", "ShowFps", setting.ShowFpsOverlay ? "true" : "false");
            t = Put(t, "Menu", "FpsOverlayType", "0"); // Just FPS, post-FG unless DebugView is active.
            t = Put(t, "Menu", "DisableSplash", "true");
            t = Put(t, "UpscaleRatio", "UpscaleRatioOverrideEnabled", "false");
            t = Put(t, "QualityOverrides", "QualityRatioOverrideEnabled", "false");
            // No OptiLimiter: Base FPS is owned by the game engine. Avoid 2 limiters.
            t = Put(t, "Framerate", "FramerateLimit", "0");
            // Cover new shortcut keys in upstream nightlies as well.
            return ConfigureLiveShortcutsInText(t);
        }

        // Retain no user-facing keys; only F23 and F24 are reserved internally.
        public static string ConfigureLiveShortcutsInText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Missing OptiScaler.ini.");
            string result = ShortcutAssignment.Replace(text,
                m => m.Groups["prefix"].Value + "-1" + m.Groups["suffix"].Value);
            foreach (var key in new[] { "ShortcutKey", "FGShortcutKey", "FpsShortcutKey", "FpsCycleShortcutKey" })
                result = SetOrAppendMenuKey(result, key, "-1");
            result = SetOrAppendMenuKey(result, "FGShortcutKey", "0x86"); // F23
            result = SetOrAppendMenuKey(result, "FpsShortcutKey", "0x87"); // F24
            return result;
        }

        public static bool LiveControlKeysAtLaunch(string iniPath)
        {
            try
            {
                if (!File.Exists(iniPath)) return false;
                string ini = File.ReadAllText(iniPath);
                return string.Equals(Get(ini, "Menu", "FGShortcutKey"), "0x86", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Get(ini, "Menu", "FpsShortcutKey"), "0x87", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string SetOrAppendMenuKey(string ini, string key, string value)
        {
            var section = MenuSection.Match(ini);
            if (!section.Success) throw new InvalidDataException("OptiScaler.ini missing [Menu]");
            var body = section.Groups["body"];
            var rx = new Regex(@"(?mi)^[ \t]*" + Regex.Escape(key) + @"[ \t]*=[^\r\n]*");
            var replacement = rx.IsMatch(body.Value)
                ? rx.Replace(body.Value, key + " = " + value, 1)
                : body.Value.TrimEnd('\r', '\n') + Environment.NewLine + key + " = " + value + Environment.NewLine;
            return ini.Substring(0, body.Index) + replacement + ini.Substring(body.Index + body.Length);
        }

        public static void ConfigureLiveShortcutsSafely(string iniPath)
        {
            if (!File.Exists(iniPath)) return;
            string before = File.ReadAllText(iniPath);
            string after = ConfigureLiveShortcutsInText(before);
            if (before == after) return;
            string tmp = iniPath + ".bfg-shortcuts.tmp";
            try
            {
                File.WriteAllText(tmp, after, new UTF8Encoding(false));
                File.Replace(tmp, iniPath, null);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        // Determine readiness from the INI as it existed when the game launched,
        // NOT from modifications performed after OptiScaler initialized.
        public static bool ConfiguredAtLaunch(string iniPath)
        {
            try
            {
                if (!File.Exists(iniPath)) return false;
                string ini = File.ReadAllText(iniPath);
                return string.Equals(Get(ini, "FrameGen", "Enabled"), "true", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Get(ini, "FrameGen", "FGInput"), "Upscaler", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Get(ini, "FrameGen", "FGOutput"), "XeFG", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        public static bool? ReadFgEnabledAtLaunch(string iniPath)
        {
            try
            {
                if (!File.Exists(iniPath)) return null;
                string raw = Get(File.ReadAllText(iniPath), "FrameGen", "Enabled");
                return bool.TryParse(raw, out bool enabled) ? (bool?)enabled : null;
            }
            catch { return null; }
        }

        public static bool OverlayEnabledAtLaunch(string iniPath)
        {
            try { return File.Exists(iniPath) && string.Equals(Get(File.ReadAllText(iniPath),
                "Menu", "ShowFps"), "true", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        private static string Get(string text, string section, string key)
        {
            var sx = new Regex(@"(?mi)^\[" + Regex.Escape(section) + @"\][ \t]*\r?\n(?<body>[\s\S]*?)(?=^\[|\z)");
            var m = sx.Match(text);
            if (!m.Success) return null;
            var kx = new Regex(@"(?mi)^[ \t]*" + Regex.Escape(key) + @"[ \t]*=[ \t]*(?<value>[^\r\n;#]*)");
            var match = kx.Match(m.Groups["body"].Value);
            return match.Success ? match.Groups["value"].Value.Trim() : null;
        }
        private static string OptionalPut(string text, string section, string key, string val)
        {
            try { return Put(text, section, key, val); }
            catch (InvalidDataException) { return text; }
        }

        public static string Put(string text, string section, string key, string val)
        {
            var sx = new Regex(@"(?mi)^\[" + Regex.Escape(section) + @"\][ \t]*\r?\n(?<body>[\s\S]*?)(?=^\[|\z)");
            var m = sx.Match(text);
            if (!m.Success) throw new InvalidDataException("OptiScaler.ini missing [" + section + "]");
            var body = m.Groups["body"];
            var kx = new Regex(@"(?mi)^[ \t]*" + Regex.Escape(key) + @"[ \t]*=[^\r\n]*");
            if (!kx.IsMatch(body.Value))
                throw new InvalidDataException("OptiScaler.ini missing " + section + "." + key);
            var replacement = kx.Replace(body.Value, key + " = " + val, 1);
            return text.Substring(0, body.Index) + replacement + text.Substring(body.Index + body.Length);
        }
        public static void WriteSafely(string iniPath, FgSettings setting)
        {
            if (!File.Exists(iniPath)) throw new FileNotFoundException("OptiScaler is not installed", iniPath);
            string original = File.ReadAllText(iniPath);
            string updated = PatchText(original, setting);
            if (string.Equals(original, updated, StringComparison.Ordinal)) return;
            string tmp = iniPath + ".bfg.tmp";
            try
            {
                File.WriteAllText(tmp, updated, new UTF8Encoding(false));
                File.Replace(tmp, iniPath, iniPath + ".bfg-last.bak");
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }
}

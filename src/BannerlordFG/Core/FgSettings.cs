// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Globalization;
using System.IO;
using System.Xml;

namespace BannerlordFG.Core
{
    /// <summary>Pure settings; no Bannerlord or OptiScaler assembly dependency.</summary>
    public sealed class FgSettings
    {
        public bool Enabled { get; set; }
        public int Multiplier { get; set; } = 2;
        /// <summary>Legacy field retained for settings compatibility; native Bannerlord limiter remains 30..360.</summary>
        public int BaseFps { get; set; } = 0;
        // Migration guard: old releases persisted BaseFps=60 without user intent.
        // NEVER cap the game unless the player explicitly moves the new slider.
        public bool ExplicitBaseLimit { get; set; } = false;
        public float? SavedGameDlss { get; set; }
        public float? SavedGameFps { get; set; }
        public float? LastCompatibleDlss { get; set; }
        public bool PendingRestart { get; set; }
        // OptiScaler's post-FG overlay. Fresh installations default to On.
        public bool ShowFpsOverlay { get; set; } = true;

        public void Validate(bool multiFrameAvailable)
        {
            Multiplier = 2; // Only 2x is validated. Migrate any older 3x/4x setting.
            if (BaseFps != 0 && (BaseFps < 15 || BaseFps > 360 || BaseFps % 5 != 0))
                throw new ArgumentOutOfRangeException(nameof(BaseFps), "Choose 0 or 15..360 in steps of 5.");
        }
        public string DisplayBaseFps => BaseFps <= 0 ? "Game managed" : BaseFps.ToString(CultureInfo.InvariantCulture);
        public int? EstimatedOutput => !Enabled || BaseFps == 0 ? (int?)null : checked(BaseFps * Multiplier);
    }

    public static class FgSettingsStore
    {
        public static string DefaultPath => Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "BannerlordFG", "preferences.xml");

        public static FgSettings Load(string path = null)
        {
            string file = path ?? DefaultPath;
            if (!File.Exists(file)) return new FgSettings();
            var doc = new XmlDocument(); doc.Load(file);
            var n = doc.DocumentElement;
            if (n == null || n.Name != "BannerlordFG") throw new InvalidDataException("Invalid preference XML.");
            var cfg = new FgSettings();
            cfg.Enabled = Attr(n, "enabled", "false") == "true";
            cfg.Multiplier = int.Parse(Attr(n, "multiplier", "2"), CultureInfo.InvariantCulture);
            cfg.BaseFps = int.Parse(Attr(n, "baseFps", "0"), CultureInfo.InvariantCulture);
            cfg.ExplicitBaseLimit = Attr(n, "explicitBaseLimit", "false") == "true";
            cfg.SavedGameDlss = NullableFloat(n, "originalDlss");
            cfg.SavedGameFps = NullableFloat(n, "originalFps");
            cfg.LastCompatibleDlss = NullableFloat(n, "lastCompatibleDlss");
            cfg.PendingRestart = Attr(n, "pendingRestart", "false") == "true";
            cfg.ShowFpsOverlay = Attr(n, "showFpsOverlay", "true") == "true";
            cfg.Validate(true); return cfg;
        }
        private static string Attr(XmlElement n, string name, string fallback) =>
            n.HasAttribute(name) ? n.GetAttribute(name) : fallback;
        private static float? NullableFloat(XmlElement n, string name) => n.HasAttribute(name) &&
            float.TryParse(n.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
                ? (float?)f : null;

        public static void Save(FgSettings cfg, string path = null)
        {
            cfg.Validate(true);
            string target = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target)));
            var d = new XmlDocument(); var e = d.CreateElement("BannerlordFG"); d.AppendChild(e);
            void A(string k, object v) { if (v != null) e.SetAttribute(k, Convert.ToString(v, CultureInfo.InvariantCulture)); }
            A("version", "2"); A("enabled", cfg.Enabled ? "true" : "false");
            A("multiplier", cfg.Multiplier); A("baseFps", cfg.BaseFps); A("explicitBaseLimit", cfg.ExplicitBaseLimit ? "true" : "false");
            A("originalDlss", cfg.SavedGameDlss); A("originalFps", cfg.SavedGameFps);
            A("lastCompatibleDlss", cfg.LastCompatibleDlss);
            A("pendingRestart", cfg.PendingRestart ? "true" : "false");
            A("showFpsOverlay", cfg.ShowFpsOverlay ? "true" : "false");
            var tmp = target + ".tmp"; d.Save(tmp);
            if (File.Exists(target)) File.Replace(tmp, target, target + ".bak");
            else File.Move(tmp, target);
        }
    }
}

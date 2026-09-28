// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Linq;
using System.Collections.Generic;
using TaleWorlds.Engine.Options;

namespace BannerlordFG.Core
{
    /// <summary>Small, typed bridge to the engine. Deliberately does not guess which integer is DLSS Quality.</summary>
    public sealed class GameGraphicsBridge
    {
        private const NativeOptions.NativeOptionsType Dlss = NativeOptions.NativeOptionsType.DLSS;
        private const NativeOptions.NativeOptionsType Limit = NativeOptions.NativeOptionsType.FrameLimiter;
        public float CurrentDlss => NativeOptions.GetConfig(Dlss);
        public float CurrentFrameLimiter => NativeOptions.GetConfig(Limit);

        public bool ApplyEnabled(FgSettings s, out string warning, bool preferQualityOnFirstInstall = false)
        {
            warning = null;
            if (!NativeOptions.GetIsDLSSAvailable())
            { warning = "DLSS input is unavailable. Check the OptiScaler install/GPU spoofing."; return false; }
            if (!s.SavedGameDlss.HasValue) s.SavedGameDlss = CurrentDlss;
            float dlss = CurrentDlss;
            if (!preferQualityOnFirstInstall && dlss > 0)
                s.LastCompatibleDlss = dlss; // Keep a preset already chosen by the player.
            else if (!preferQualityOnFirstInstall && s.LastCompatibleDlss.HasValue)
                dlss = s.LastCompatibleDlss.Value; // Normal live toggles preserve the previous choice.
            else
            {
                // A new OptiScaler installation must ignore stale preferences from
                // older versions (which may have saved the Performance index).
                // Resolve Quality against the running game's option table.
                if (!TryFindQualityPreset(out dlss))
                {
                    warning = "DLSS was Off, but no usable Quality preset could be resolved. " +
                        "Select the highest-quality DLSS preset once before enabling FG.";
                    return false;
                }
                s.LastCompatibleDlss = dlss;
            }
            NativeOptions.SetConfig(Dlss, dlss);
            NativeOptions.ApplyConfigChanges(false);
            // Render pipeline/upscaler changes may require restart; do not claim live FG state.
            return true;
        }
        private static bool TryFindQualityPreset(out float index)
        {
            index = 0f;
            try
            {
                // Native selection IDs are not stable across game releases.
                // Prefer the named Quality preset; if localization hides that
                // English label, use the last valid choice only when it is not
                // Off, Performance or DLAA (which is not temporal upscaling).
                var items = NativeOptions.GraphicsOptions.Cast<IOptionData>()
                    .Concat(NativeOptions.VideoOptions.Cast<IOptionData>());
                foreach (var option in items)
                {
                    if (!(option.GetOptionType() is NativeOptions.NativeOptionsType type) || type != Dlss)
                        continue;
                    var selection = option as ISelectionOptionData;
                    if (selection == null) continue;
                    var labels = selection.GetSelectableOptionNames().ToList();
                    // Skip index zero (None). Deliberately do not use an old
                    // LastCompatibleDlss value for the first-install path.
                    int normalQuality = -1;
                    for (int i = labels.Count - 1; i >= 1; --i)
                    {
                        string label = labels[i].Data ?? string.Empty;
                        if (label.IndexOf("Quality", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        // Ultra Quality is higher than Quality when available.
                        // Do not mistake Ultra Performance or DLAA for an upscaler.
                        if (label.IndexOf("Ultra", StringComparison.OrdinalIgnoreCase) >= 0)
                        { index = i; return true; }
                        if (normalQuality < 0) normalQuality = i;
                    }
                    if (normalQuality >= 1) { index = normalQuality; return true; }
                    // Some translations do not expose an English Quality label.
                    // In the supported Bannerlord menu the final positive item
                    // is highest quality; reject obvious unsafe mismatches.
                    if (labels.Count > 1)
                    {
                        string last = labels[labels.Count - 1].Data ?? string.Empty;
                        if (last.IndexOf("Performance", StringComparison.OrdinalIgnoreCase) < 0 &&
                            last.IndexOf("Off", StringComparison.OrdinalIgnoreCase) < 0 &&
                            last.IndexOf("None", StringComparison.OrdinalIgnoreCase) < 0 &&
                            last.IndexOf("DLAA", StringComparison.OrdinalIgnoreCase) < 0)
                        { index = labels.Count - 1; return true; }
                    }
                }
            }
            catch { /* Unknown game option ABI: fail safely rather than guess an enum. */ }
            return false;
        }

        public void Enforce(FgSettings s)
        {
            // Caller checks the backend's ACTUAL launch-time state. The
            // preference may already be disabled pending a restart.
            if (CurrentDlss > 0) s.LastCompatibleDlss = CurrentDlss;
            else if (s.LastCompatibleDlss.HasValue)
            {
                NativeOptions.SetConfig(Dlss, s.LastCompatibleDlss.Value);
                NativeOptions.ApplyConfigChanges(false);
            }
        }
        // One-time repair for v0.3.0, which incorrectly forced the vanilla
        // frame limiter to 60. A player who changed it afterwards is left alone.
        // New releases NEVER modify the native limiter during FG operation.
        public bool RepairOldFrameCap(FgSettings s)
        {
            if (s.ExplicitBaseLimit || !s.SavedGameFps.HasValue) return false;
            float current = CurrentFrameLimiter;
            if (Math.Abs(current - s.BaseFps) < 0.1f &&
                Math.Abs(current - s.SavedGameFps.Value) > 0.1f)
            {
                NativeOptions.SetConfig(Limit, s.SavedGameFps.Value);
                NativeOptions.ApplyConfigChanges(false);
            }
            s.SavedGameFps = null;
            return true;
        }

        // The user's original native limiter is saved only after they deliberately
        // choose a finite Base FPS in the v0.3.10 slider. At 70/Unlimited we don't
        // touch the native limiter, regardless of an old persisted BaseFps=60.
        public void ApplyExplicitBaseLimit(FgSettings s, int fps)
        {
            if (fps < 15 || fps > 360 || fps % 5 != 0)
                throw new ArgumentOutOfRangeException(nameof(fps));
            if (!s.SavedGameFps.HasValue) s.SavedGameFps = CurrentFrameLimiter;
            NativeOptions.SetConfig(Limit, fps);
            NativeOptions.ApplyConfigChanges(false);
        }
        public void RestoreOriginal(FgSettings s)
        {
            bool changes = false;
            if (s.SavedGameDlss.HasValue)
            {
                NativeOptions.SetConfig(Dlss, s.SavedGameDlss.Value);
                changes = true;
            }
            if (s.SavedGameFps.HasValue)
            {
                NativeOptions.SetConfig(Limit, s.SavedGameFps.Value);
                changes = true;
            }
            if (changes) NativeOptions.ApplyConfigChanges(false);
            s.SavedGameDlss = null; s.SavedGameFps = null;
        }
        public void RestoreExplicitBaseLimit(FgSettings s)
        {
            if (!s.SavedGameFps.HasValue) return;
            NativeOptions.SetConfig(Limit, s.SavedGameFps.Value);
            NativeOptions.ApplyConfigChanges(false);
            s.SavedGameFps = null;
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Globalization;
using System.IO;
using TaleWorlds.Engine.Options;

namespace BannerlordFG.Core
{
    // Compatibility cleanup for v0.3.12 only. BannerlordFG no longer exposes
    // an Unlimited mode because Bannerlord validates/assumes a finite 30..360 cap.
    internal static class NativeUnlimitedLimiter
    {
        private static readonly string Marker = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BannerlordFG", "native-unlimited.marker");

        internal static void RestoreFiniteAndRetire()
        {
            if (!File.Exists(Marker)) return;
            try
            {
                float saved;
                if (float.TryParse(File.ReadAllText(Marker), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out saved) && saved >= 30f && saved <= 360f)
                {
                    var type = NativeOptions.NativeOptionsType.FrameLimiter;
                    float current = NativeOptions.GetConfig(type);
                    if (current < 30f || current > 360f)
                    {
                        NativeOptions.SetConfig(type, saved);
                        NativeOptions.ApplyConfigChanges(false);
                    }
                }
            }
            catch { /* Cleanup must never prevent Bannerlord from starting. */ }
            finally
            {
                try { File.Delete(Marker); } catch { }
            }
        }
    }
}

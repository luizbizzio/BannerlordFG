// SPDX-License-Identifier: GPL-3.0-only
using System;

namespace BannerlordFG.Core
{
    /// <summary>
    /// Bind these descriptions to Bannerlord OptionsVM's Performance group once the
    /// exact game build's OptionsProvider/OptionGroupVM ABI has been inspected.
    /// Deliberately does NOT ship a fictitious UI patch that could crash the game.
    /// </summary>
    public static class NativeMenuContract
    {
        public const string InsertAfterGroup = "Resolution Scaling";
        public const string GroupId = "bannerlordfg.frame_generation";
        public static readonly string[] Settings = {
            "Frame Generation", "Multiplier", "Show FPS Counter", "OptiScaler" };
        public static readonly int[] AllowedMultipliers = { 2 };
        public const string NativeUpscalerManagedCaption = "Managed by BannerlordFG";
        public const string NativeVideoLimiterManagedCaption = "Managed by BannerlordFG";
        // Compatibility-only conversion. The player's vanilla limiter is the
        // only live FPS control; no code may turn a sentinel into Unlimited.
        public static bool IsBaseLimitAllowed(int fps) =>
            (fps >= 30 && fps <= 360 && fps % 5 == 0);
        public static int SliderPositionToFps(int position)
        {
            if (position < 0 || position > 66) throw new ArgumentOutOfRangeException(nameof(position));
            return 30 + 5 * position;
        }
        public static int FpsToSliderPosition(int fps)
        {
            if (!IsBaseLimitAllowed(fps)) throw new ArgumentOutOfRangeException(nameof(fps));
            return (fps - 30) / 5;
        }
    }
}

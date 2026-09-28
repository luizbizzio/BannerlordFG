// SPDX-License-Identifier: GPL-3.0-only
using TaleWorlds.Engine.Options;
namespace BannerlordFG.Core
{
    internal static class NativeUpscalingLock
    {
        internal static bool IsScalingOption(IOptionData data)
        {
            if (data == null) return false;
            object type = data.GetOptionType();
            if (!(type is NativeOptions.NativeOptionsType)) return false;
            switch ((NativeOptions.NativeOptionsType)type)
            {
                case NativeOptions.NativeOptionsType.ResolutionScale:
                case NativeOptions.NativeOptionsType.DynamicResolution:
                case NativeOptions.NativeOptionsType.DynamicResolutionTarget:
                    return true;
                default: return false;
            }
        }
    }
}

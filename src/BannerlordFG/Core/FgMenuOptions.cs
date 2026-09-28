// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using TaleWorlds.Engine.Options;

namespace BannerlordFG.Core
{
    // These items are attached ONLY to our visual-only OptionGroupVM, never to the
    // game's OptionCategory/OptionGroup provider. Bannerlord's enum-based save/reset
    // code must not receive our string identifiers.
    internal abstract class FgVisualOptionData : IOptionData
    {
        protected readonly FgController Controller;
        protected readonly Func<bool> Installed;
        protected readonly Action<string> Log;
        private readonly string _id;
        protected FgVisualOptionData(string id, FgController controller, Func<bool> installed, Action<string> log)
        { _id = id; Controller = controller; Installed = installed; Log = log; }
        public abstract float GetDefaultValue();
        public virtual void Commit() { }
        public abstract float GetValue(bool isApplied);
        public abstract void SetValue(float value);
        public object GetOptionType() => _id;
        public bool IsNative() => false;
        public bool IsAction() => false;
        public virtual ValueTuple<string, bool> GetIsDisabledAndReasonID() =>
            Installed() ? new ValueTuple<string, bool>(string.Empty, false) :
                new ValueTuple<string, bool>("Install the graphics component first.", true);
    }

    // Compatibility-only multiplier contract; never rendered as a control.
    internal sealed class FgMultiplierData : FgVisualOptionData, ISelectionOptionData
    {
        public FgMultiplierData(FgController controller, Func<bool> installed, Action<string> log)
            : base("BannerlordFG.Multiplier", controller, installed, log) { }
        public static bool IsArcAdapter() => false; // 3x/4x are not offered in this release.
        public int GetSelectableOptionsLimit() => 1;
        public IEnumerable<SelectionData> GetSelectableOptionNames() =>
            new[] { new SelectionData(false, "2x") };
        public override float GetDefaultValue() => 0f;
        public override float GetValue(bool isApplied) => 0f;
        public override void SetValue(float value) { /* Fixed 2x; no INI writes. */ }
        public override ValueTuple<string, bool> GetIsDisabledAndReasonID() =>
            new ValueTuple<string, bool>("Only validated 2x is available.", true);
    }

    // Display adapter for Bannerlord's native DLSS input. The source selection
    // still uses native numeric IDs (0 = None), but the visual dropdown starts
    // at ID 1 while FG requires a temporal upscaler. Underlying vanilla data
    // remains in the provider and is never rewritten/reordered.
    internal sealed class FgUpscalingQualityData : FgVisualOptionData, ISelectionOptionData
    {
        private readonly ISelectionOptionData _native;
        private readonly NativeOptions.NativeOptionsType _type = NativeOptions.NativeOptionsType.DLSS;

        public FgUpscalingQualityData(ISelectionOptionData native, FgController controller,
            Func<bool> installed, Action<string> log)
            : base("BannerlordFG.QualityNoOff", controller, installed, log)
        {
            _native = native ?? throw new ArgumentNullException(nameof(native));
        }

        public int GetSelectableOptionsLimit() => Math.Max(0, _native.GetSelectableOptionsLimit() - 1);
        public IEnumerable<SelectionData> GetSelectableOptionNames() =>
            _native.GetSelectableOptionNames().Skip(1).ToArray();
        public override float GetDefaultValue() => GetValue(false);
        public override float GetValue(bool isApplied) =>
            Math.Max(0f, Math.Min(GetSelectableOptionsLimit() - 1f,
                NativeOptions.GetConfig(_type) - 1f));

        public override void SetValue(float value)
        {
            if (!Installed() || !Controller.NativeDlssManaged) return;
            int index = (int)Math.Round(value);
            int count = GetSelectableOptionsLimit();
            if (index < 0 || index >= count) return;
            float nativeIndex = index + 1f; // UI 0=Quality -> native 1; never native 0=None.
            try
            {
                NativeOptions.SetConfig(_type, nativeIndex);
                NativeOptions.ApplyConfigChanges(false);
                Controller.Settings.LastCompatibleDlss = nativeIndex;
                FgSettingsStore.Save(Controller.Settings);
            }
            catch (Exception ex) { Log("Native upscaler quality selection failed: " + ex); }
        }
    }

    // Match the native Off/On selector used by the FPS overlay.  The display
    // reflects the state requested by this mod, NOT confirmed OptiScaler
    // readback: upstream exposes no in-process status API to this module.
    internal sealed class FgEnabledData : FgVisualOptionData, ISelectionOptionData
    {
        private readonly Action _requestRestart;
        private readonly Action _refreshUpscalingLocks;
        public FgEnabledData(FgController controller, Func<bool> installed,
            Action requestRestart, Action refreshUpscalingLocks, Action<string> log)
            : base("BannerlordFG.Toggle", controller, installed, log)
        {
            _requestRestart = requestRestart;
            _refreshUpscalingLocks = refreshUpscalingLocks;
        }
        public int GetSelectableOptionsLimit() => 2;
        public IEnumerable<SelectionData> GetSelectableOptionNames() =>
            new[] { new SelectionData(false, "Off"), new SelectionData(false, "On") };
        public override float GetDefaultValue() => 1f;
        public override float GetValue(bool isApplied) => Controller.FgAssumedEnabled ? 1f : 0f;
        public override void SetValue(float value)
        {
            if (!Installed()) return;
            bool desired = value >= 0.5f;
            if (desired == Controller.FgAssumedEnabled) return; // VM refresh must never flip FG.
            bool canToggleLive = Controller.CanToggleLive;
            string message;
            bool success = canToggleLive ? Controller.TryToggleLive(out message) :
                (desired ? Controller.TryEnable(out message) : Controller.TryDisable(out message));
            Log("Frame Generation " + (success ? "requested" : "failed") + ": " + message);
            if (!success)
            {
                TaleWorlds.Library.InformationManager.DisplayMessage(
                    new TaleWorlds.Library.InformationMessage(
                        "BannerlordFG: cannot change FG. See runtime.log."));
            }
            else
            {
                _refreshUpscalingLocks?.Invoke();
                if (!canToggleLive) _requestRestart?.Invoke(); // Backend not ready at launch.
            }
            NativePerformancePatch.ScheduleSelectionRefresh();
        }
    }

    // This selector owns OptiScaler's post-FG overlay, NOT engine_config.txt.
    // Runtime shortcuts are internal implementation details; keyboard
    // injection is best effort because the project has no status/readback API.
    internal sealed class FgShowFpsData : FgVisualOptionData, ISelectionOptionData
    {
        public FgShowFpsData(FgController controller, Func<bool> installed, Action<string> log)
            : base("BannerlordFG.ShowFps", controller, installed, log) { }
        public int GetSelectableOptionsLimit() => 2;
        public IEnumerable<SelectionData> GetSelectableOptionNames() =>
            new[] { new SelectionData(false, "Off"), new SelectionData(false, "On") };
        public override float GetDefaultValue() => 1;
        public override float GetValue(bool isApplied) => Controller.OverlayAssumedEnabled ? 1 : 0;
        public override void SetValue(float value)
        {
            bool desired = value > 0.5f;
            string message;
            bool ok = Controller.TrySetFpsOverlay(desired, out message);
            Log("OptiScaler FPS overlay " + (ok ? "requested" : "failed") + ": " + message);
            if (!ok || (!Controller.OverlayAssumedEnabled && desired) ||
                (Controller.OverlayAssumedEnabled && !desired))
                TaleWorlds.Library.InformationManager.DisplayMessage(
                    new TaleWorlds.Library.InformationMessage(
                        "BannerlordFG: FPS change not applied live; see runtime.log."));
            NativePerformancePatch.ScheduleSelectionRefresh();
        }
        public override ValueTuple<string, bool> GetIsDisabledAndReasonID() =>
            Installed() ? new ValueTuple<string, bool>(string.Empty, false) :
                new ValueTuple<string, bool>("Install the graphics component first.", true);
    }

}

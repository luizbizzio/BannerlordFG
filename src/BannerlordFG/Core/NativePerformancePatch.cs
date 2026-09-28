// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.Engine.Options;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Options;
using TaleWorlds.MountAndBlade.ViewModelCollection.GameOptions;

namespace BannerlordFG.Core
{
    /// <summary>
    /// Harmony-powered native Performance integration.
    /// Insert an empty provider group before Bannerlord builds its option viewmodels.
    /// Add visual-only action/selection rows after viewmodel construction. Never
    /// put our string-based identifiers into the game's enum-based save/reset path.
    /// Constructor and getter hooks recover screens created before the provider hook.
    /// </summary>
    internal sealed class NativePerformancePatch
    {
        private const string HarmonyId = "org.bannerlordfg.nativeperformance.100";
        private const string IdInstall = "BannerlordFG.Install";
        private const string IdToggle = "BannerlordFG.Toggle";
        private const string IdMultiplier = "BannerlordFG.Multiplier";
        private const string IdUninstall = "BannerlordFG.Uninstall";
        private const string IdFpsCounter = "BannerlordFG.ShowFps";

        private static NativePerformancePatch _active;
        [ThreadStatic] private static bool _attaching;
        [ThreadStatic] private static bool _replacingQuality;

        private readonly FgController _controller;
        private readonly Func<bool> _isInstalled;
        private readonly Action _install;
        private readonly Action _uninstall;
        private readonly Action _requestRestart;
        private readonly Action<string> _log;
        // The original strong HashSets retained every screen ever opened. These
        // weak-key tables preserve identity without extending a VM's lifetime.
        // Owners are ephemeron values: even if OptionsVM refers back to its group,
        // that cycle does not keep a dead UI alive.
        private sealed class Marker { }
        private sealed class GroupOwner { internal OptionsVM Vm; }
        private readonly ConditionalWeakTable<TextObject, Marker> _ourNames =
            new ConditionalWeakTable<TextObject, Marker>();
        private readonly ConditionalWeakTable<OptionGroupVM, Marker> _ownGroups =
            new ConditionalWeakTable<OptionGroupVM, Marker>();
        private readonly ConditionalWeakTable<OptionGroupVM, GroupOwner> _groupOwners =
            new ConditionalWeakTable<OptionGroupVM, GroupOwner>();
        private readonly ConditionalWeakTable<OptionGroup, Marker> _providerGroups =
            new ConditionalWeakTable<OptionGroup, Marker>();
        // WeakReferences are needed only when iterating to refresh live menus.
        // Compact dead entries on every 16th registration and every refresh.
        private readonly List<WeakReference<OptionGroupVM>> _liveGroupRefs =
            new List<WeakReference<OptionGroupVM>>();
        private int _errorCount;
        private int _providerHitCount;
        private int _attachHitCount;
        private int _refreshSelectionsOnNextTick;
        private Harmony _harmony;

        private NativePerformancePatch(FgController controller, Func<bool> isInstalled,
            Action install, Action uninstall, Action requestRestart, Action<string> log)
        {
            _controller = controller;
            _isInstalled = isInstalled;
            _install = install;
            _uninstall = uninstall;
            _requestRestart = requestRestart;
            _log = log;
        }

        internal static bool Start(FgController controller, Func<bool> isInstalled,
            Action install, Action uninstall, Action requestRestart, Action<string> log)
        {
            if (_active != null) return true;
            var instance = new NativePerformancePatch(controller, isInstalled, install, uninstall, requestRestart, log);
            try
            {
                Type vm = typeof(OptionsVM);
                Type group = typeof(OptionGroupVM);
                var provider = AccessTools.Method(typeof(OptionsProvider),
                    "GetPerformanceOptionGroups", new[] { typeof(bool) });
                var vmFactory = AccessTools.Method(vm, "GetOptionItem", new[] { typeof(IOptionData) });
                var vmGetter = AccessTools.PropertyGetter(vm, "PerformanceOptions");
                var vmRefresh = AccessTools.Method(vm, "RefreshValues", Type.EmptyTypes);
                var groupConstructor = group.GetConstructor(new[] { typeof(TextObject), vm,
                    typeof(IEnumerable<IOptionData>) });
                var groupRefresh = AccessTools.Method(group, "RefreshValues", Type.EmptyTypes);
                var groupManaged = AccessTools.Method(group, "GetManagedOptions", Type.EmptyTypes);
                var groupChanged = AccessTools.Method(group, "IsChanged", Type.EmptyTypes);
                var groupCancel = AccessTools.Method(group, "Cancel", Type.EmptyTypes);
                // The user's real TaleWorlds.MountAndBlade.ViewModelCollection.dll has
                // OptionGroupVM.RefreshValues / GetManagedOptions / IsChanged / Cancel,
                // but NO OptionGroupVM.ResetData(). ResetData exists on the *category*.
                // v0.3.7 made the nonexistent group method mandatory, so it disabled
                // the entire UI before any Harmony patch was installed.
                var categoryManaged = AccessTools.Method(typeof(GroupedOptionCategoryVM),
                    "GetManagedOptions", Type.EmptyTypes);
                var categoryInitialize = AccessTools.Method(typeof(GroupedOptionCategoryVM),
                    "InitializeOptions", Type.EmptyTypes);
                var getConfig = AccessTools.Method(vm, "GetConfig", new[] { typeof(IOptionData) });
                var setConfig = AccessTools.Method(vm, "SetConfig", new[] { typeof(IOptionData), typeof(float) });
                var vmConstructors = vm.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
                // The stock 30..360 native slider and its labels are not patched.
                var nativeDisabled = AccessTools.Method(typeof(NativeOptionData),
                    "GetIsDisabledAndReasonID", Type.EmptyTypes);
                var rowUpdate = AccessTools.Method(typeof(GenericOptionDataVM),
                    "UpdateEnableState", Type.EmptyTypes);

                // Every required API below exists in the two game DLLs uploaded by
                // the player (verified against their CLI metadata). Log the EXACT
                // absent member instead of showing another misleading generic ABI
                // error if a future Bannerlord update changes an API.
                var missing = new List<string>();
                if (provider == null) missing.Add("OptionsProvider.GetPerformanceOptionGroups(bool)");
                if (vmFactory == null) missing.Add("OptionsVM.GetOptionItem(IOptionData)");
                if (vmGetter == null) missing.Add("OptionsVM.PerformanceOptions.get");
                if (vmRefresh == null) missing.Add("OptionsVM.RefreshValues()");
                if (groupConstructor == null) missing.Add("OptionGroupVM(TextObject,OptionsVM,IEnumerable<IOptionData>)");
                if (groupRefresh == null) missing.Add("OptionGroupVM.RefreshValues()");
                if (groupManaged == null) missing.Add("OptionGroupVM.GetManagedOptions()");
                if (groupChanged == null) missing.Add("OptionGroupVM.IsChanged()");
                if (groupCancel == null) missing.Add("OptionGroupVM.Cancel()");
                if (categoryManaged == null) missing.Add("GroupedOptionCategoryVM.GetManagedOptions()");
                if (categoryInitialize == null) missing.Add("GroupedOptionCategoryVM.InitializeOptions()");
                if (getConfig == null) missing.Add("OptionsVM.GetConfig(IOptionData)");
                if (setConfig == null) missing.Add("OptionsVM.SetConfig(IOptionData,float)");
                if (vmConstructors.Length == 0) missing.Add("OptionsVM public constructors");
                if (typeof(ActionOptionData).GetConstructor(new[] { typeof(string), typeof(Action) }) == null)
                    missing.Add("ActionOptionData(string,Action)");
                if (typeof(ActionOptionDataVM).GetConstructor(new[] { typeof(Action), vm,
                        typeof(IOptionData), typeof(TextObject), typeof(TextObject), typeof(TextObject) }) == null)
                    missing.Add("ActionOptionDataVM(Action,OptionsVM,IOptionData,TextObject,TextObject,TextObject)");
                if (missing.Count != 0)
                    throw new MissingMethodException("Missing required Options API: " + string.Join("; ", missing));

                instance._harmony = new Harmony(HarmonyId);
                _active = instance;
                // Patch the provider and group constructor FIRST; the native screen can
                // be instantiated as soon as the title-screen data is requested.
                instance._harmony.Patch(provider,
                    postfix: new HarmonyMethod(typeof(NativePerformancePatch), nameof(ProviderPostfix)));
                instance._harmony.Patch(groupConstructor,
                    postfix: new HarmonyMethod(typeof(NativePerformancePatch), nameof(GroupConstructedPostfix)));
                instance._harmony.Patch(groupRefresh,
                    postfix: new HarmonyMethod(typeof(NativePerformancePatch), nameof(GroupRefreshPostfix)));
                instance._harmony.Patch(groupManaged,
                    prefix: new HarmonyMethod(typeof(NativePerformancePatch), nameof(GroupManagedPrefix)));
                instance._harmony.Patch(groupChanged,
                    prefix: new HarmonyMethod(typeof(NativePerformancePatch), nameof(GroupChangedPrefix)));
                instance._harmony.Patch(groupCancel,
                    prefix: new HarmonyMethod(typeof(NativePerformancePatch), nameof(GroupNoopPrefix)));
                instance._harmony.Patch(categoryManaged,
                    prefix: new HarmonyMethod(typeof(NativePerformancePatch), nameof(CategoryManagedPrefix)));
                instance._harmony.Patch(categoryInitialize,
                    postfix: new HarmonyMethod(typeof(NativePerformancePatch), nameof(CategoryInitializePostfix)));
                if (nativeDisabled != null)
                    try
                    {
                        instance._harmony.Patch(nativeDisabled, postfix: new HarmonyMethod(
                            typeof(NativePerformancePatch), nameof(NativeDisabledPostfix)));
                    }
                    catch (Exception ex) { log("Optional upscaling disabled-state hook unavailable: " + ex.Message); }
                if (rowUpdate != null)
                    try
                    {
                        instance._harmony.Patch(rowUpdate, postfix: new HarmonyMethod(
                            typeof(NativePerformancePatch), nameof(NativeRowEnabledPostfix)));
                    }
                    catch (Exception ex) { log("Optional native row lock unavailable: " + ex.Message); }
                instance._harmony.Patch(getConfig,
                    prefix: new HarmonyMethod(typeof(NativePerformancePatch), nameof(GetConfigPrefix)));
                instance._harmony.Patch(setConfig,
                    prefix: new HarmonyMethod(typeof(NativePerformancePatch), nameof(SetConfigPrefix)));
                // Existing OptionsVM fallback; the provider route above is primary.
                foreach (var constructor in vmConstructors)
                    instance._harmony.Patch(constructor,
                        postfix: new HarmonyMethod(typeof(NativePerformancePatch), nameof(OptionsConstructedPostfix)));
                instance._harmony.Patch(vmGetter,
                    postfix: new HarmonyMethod(typeof(NativePerformancePatch), nameof(PerformanceGetterPostfix)));
                instance._harmony.Patch(vmRefresh,
                    postfix: new HarmonyMethod(typeof(NativePerformancePatch), nameof(OptionsRefreshPostfix)));
                log("Harmony native Performance v1.0.0 installed: Off/On FG selector, silent fixed 2x and two internal live-control keys. Native limiter preserved.");
                return true;
            }
            catch (Exception ex)
            {
                try { instance._harmony?.UnpatchAll(HarmonyId); } catch { }
                if (ReferenceEquals(_active, instance)) _active = null;
                log("FG native UI could not install safely; vanilla Options preserved: " + ex);
                return false;
            }
        }

        private void Error(string what, Exception ex)
        {
            if (++_errorCount <= 12) _log("FG native UI " + what + ": " + ex);
        }

        // Called from the normal Bannerlord tick: keep dependent native rows
        // visibly disabled while FG owns the temporal upscale input.
        internal static void PumpPendingUi()
        {
            var p = _active;
            if (p == null) return;
            try
            {
                if (p._refreshSelectionsOnNextTick > 0 && --p._refreshSelectionsOnNextTick == 0)
                    p.RefreshSelectionRows();
            }
            catch (Exception ex) { p.Error("deferred native UI update failed", ex); }
        }

        internal static void ScheduleSelectionRefresh()
        {
            if (_active != null) _active._refreshSelectionsOnNextTick = 2;
        }

        private void RememberOwner(OptionGroupVM group, OptionsVM vm)
        {
            if (group == null || vm == null) return;
            _groupOwners.GetValue(group, _ => new GroupOwner()).Vm = vm;
        }

        private bool TryGetOwner(OptionGroupVM group, out OptionsVM vm)
        {
            vm = null;
            if (group == null || !_groupOwners.TryGetValue(group, out GroupOwner owner))
                return false;
            vm = owner.Vm;
            return vm != null;
        }

        private void RegisterOurGroup(OptionGroupVM group, OptionsVM vm)
        {
            if (group == null) return;
            if (!_ownGroups.TryGetValue(group, out Marker _))
            {
                _ownGroups.GetValue(group, key => new Marker());
                _liveGroupRefs.Add(new WeakReference<OptionGroupVM>(group));
                if (_liveGroupRefs.Count % 16 == 0) CompactDeadGroups();
            }
            RememberOwner(group, vm);
        }

        private void CompactDeadGroups()
        {
            _liveGroupRefs.RemoveAll(reference => !reference.TryGetTarget(out OptionGroupVM _));
        }

        private OptionGroupVM[] LiveGroups()
        {
            CompactDeadGroups();
            // Snapshot before iterating: vm.PerformanceOptions can invoke Harmony
            // hooks that register another group during a refresh. Never enumerate
            // the mutable weak-reference list across those callbacks.
            var current = new List<OptionGroupVM>(_liveGroupRefs.Count);
            foreach (var reference in _liveGroupRefs)
                if (reference.TryGetTarget(out OptionGroupVM group)) current.Add(group);
            return current.ToArray();
        }

        private void RefreshSelectionRows()
        {
            foreach (var group in LiveGroups())
            {
                if (group?.Options == null) continue;
                foreach (var row in group.Options)
                {
                    string id = row?.GetOptionData()?.GetOptionType() as string;
                    if (id != IdFpsCounter && id != IdToggle) continue;
                    try
                    {
                        AccessTools.Method(row.GetType(), "RefreshValues", Type.EmptyTypes)?
                            .Invoke(row, null);
                    }
                    catch (Exception ex) { Error("selection refresh failed", ex); }
                }
            }
        }

        private static bool IsOurOption(IOptionData data)
        {
            if (data == null) return false;
            try
            {
                return data.GetOptionType() is string id &&
                    id.StartsWith("BannerlordFG.", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private bool IsOurGroup(OptionGroupVM group)
        {
            return group != null && _ownGroups.TryGetValue(group, out Marker _);
        }

        // This data-level insertion MUST run before GroupedOptionCategoryVM builds its
        // MBBindingList and before Gauntlet binds to the Performance page.
        // The placeholder has no IOptionData: vanilla never sees our custom IDs.
        private static void ProviderPostfix(bool __0, ref IEnumerable<OptionGroup> __result)
        {
            var p = _active;
            if (p == null || __result == null || __0) return; // SP only
            try
            {
                var groups = __result.ToList();
                if (groups.Any(g => g != null && p._providerGroups.TryGetValue(g, out Marker _))) { __result = groups; return; }
                var title = new TextObject("{=BFGFrameGeneration}Frame Generation");
                var blankGroup = new OptionGroup(title, Enumerable.Empty<IOptionData>());
                p._ourNames.GetValue(title, _ => new Marker());
                p._providerGroups.GetValue(blankGroup, _ => new Marker());
                int insert = FindScalingIndex(groups) + 1;
                if (insert <= 0) insert = groups.Count;
                groups.Insert(insert, blankGroup);
                __result = groups;
                if (++p._providerHitCount <= 4)
                    p._log("FG provider inserted an EMPTY group before OptionsVM creation at index " +
                        insert + " (total=" + groups.Count + ").");
            }
            catch (Exception ex) { p.Error("provider insertion failed", ex); }
        }

        private static int FindScalingIndex(IEnumerable<OptionGroup> source)
        {
            int index = 0;
            foreach (var group in source)
            {
                try
                {
                    if (group != null && group.Options != null)
                        foreach (var row in group.Options)
                            if (row?.GetOptionType() is NativeOptions.NativeOptionsType type &&
                                type == NativeOptions.NativeOptionsType.DLSS) return index;
                    if (group?.GroupName?.ToString().IndexOf("Resolution Scaling",
                        StringComparison.OrdinalIgnoreCase) >= 0) return index;
                }
                catch { /* Other mods may have unusual option data; keep searching. */ }
                index++;
            }
            return -1;
        }

        private static int FindScalingIndex(IEnumerable<OptionGroupVM> source)
        {
            int index = 0;
            foreach (var group in source)
            {
                try
                {
                    if (group != null && group.Options != null)
                        foreach (var row in group.Options)
                            if (row?.GetOptionData()?.GetOptionType() is NativeOptions.NativeOptionsType type &&
                                type == NativeOptions.NativeOptionsType.DLSS) return index;
                    if (group?.Name?.IndexOf("Resolution Scaling",
                        StringComparison.OrdinalIgnoreCase) >= 0) return index;
                }
                catch { }
                index++;
            }
            return -1;
        }

        // __0 is the EXACT TextObject passed by our empty provider group.
        private static void GroupConstructedPostfix(OptionGroupVM __instance, TextObject __0,
            OptionsVM __1)
        {
            var p = _active;
            if (p == null || __instance == null) return;
            try
            {
                // The vanilla DLSS/FSR row is only adapted in the visible VM.
                // The original game option/provider and its numeric IDs remain intact.
                p.TryHideNoneInUpscaling(__instance, __1);
                if (__0 == null || !p._ourNames.TryGetValue(__0, out Marker _)) return;
                p.RegisterOurGroup(__instance, __1);
                p.PopulateVisualRows(__instance, __1, "group constructor");
            }
            catch (Exception ex) { p.Error("group constructor postfix failed", ex); }
        }

        private void TryHideNoneInUpscaling(OptionGroupVM group, OptionsVM vm)
        {
            if (_replacingQuality || !_isInstalled() || !_controller.NativeDlssManaged ||
                group?.Options == null || vm == null || IsOurGroup(group)) return;
            try
            {
                _replacingQuality = true;
                var rows = group.Options;
                for (int i = 0; i < rows.Count; ++i)
                {
                    var data = rows[i]?.GetOptionData();
                    if (!(data?.GetOptionType() is NativeOptions.NativeOptionsType kind) ||
                        kind != NativeOptions.NativeOptionsType.DLSS) continue;
                    if (!(data is ISelectionOptionData original)) return;
                    var entries = original.GetSelectableOptionNames().ToArray();
                    if (entries.Length < 2 || original.GetSelectableOptionsLimit() != entries.Length) return;
                    RememberOwner(group, vm);
                    var adapter = new FgUpscalingQualityData(original, _controller, _isInstalled, _log);
                    var row = CreateChoice(vm, adapter,
                        "{=BFGUpscalerQuality}NVIDIA DLSS / FSR Quality",
                        "{=BFGUpscalerNoNone}Frame Generation requires DLSS or FSR. None is unavailable while FG is active.");
                    rows.RemoveAt(i);
                    rows.Insert(i, row);
                    _log("Native upscaling VM hides None while FG is active; original native selection data preserved.");
                    return;
                }
            }
            catch (Exception ex) { Error("failed to replace native upscaling dropdown", ex); }
            finally { _replacingQuality = false; }
        }

        private void PopulateVisualRows(OptionGroupVM group, OptionsVM vm, string origin)
        {
            if (group == null || vm == null) return;
            var rows = group.Options;
            if (rows == null)
            {
                rows = new MBBindingList<GenericOptionDataVM>();
                group.Options = rows;
            }
            if (rows.Count != 0) return;
            bool installed = _isInstalled();
            if (installed)
            {
                rows.Add(CreateChoice(vm,
                    new FgEnabledData(_controller, _isInstalled, _requestRestart,
                        RefreshUpscalingLocks, _log),
                    "{=BFGToggleName}Frame Generation",
                    "{=BFGToggleDesc}Frame Generation requires DLSS or FSR upscaling. OptiScaler must initialize once; after that, switch FG live without restarting. The selector shows the last requested state, not direct backend readback."));
                rows.Add(CreateAction(vm, IdMultiplier, ExplainMultiplier,
                    "{=BFGMultiplier}Multiplier", "2x",
                    "{=BFGFixedMultiplier}Fixed at 2x. Additional multipliers are not supported."));
            }
            // A default-on checkbox without a working injector is misleading.
            // Never render OptiScaler's FPS setting until our complete managed
            // installation is present. The vanilla game's FPS display is separate.
            if (installed)
            {
                try
                {
                    rows.Add(CreateChoice(vm, new FgShowFpsData(_controller, _isInstalled,
                        _log), "{=BFGShowFps}Show FPS Counter",
                        "{=BFGShowFpsDesc}Show or hide OptiScaler post-FG FPS. Independent game, driver and Steam FPS counters are not controlled here."));
                }
                catch (Exception ex) { Error("OptiScaler FPS counter row unavailable", ex); }
            }
            // Keep OptiScaler installation as the final row.
            if (!installed)
                rows.Add(CreateAction(vm, IdInstall, _install,
                    "{=BFGOptiScaler}OptiScaler",
                    "{=BFGInstall}Install",
                    "{=BFGInstallDesc}Install OptiScaler for frame generation. Game exit is required before graphics files can be copied."));
            else
                rows.Add(CreateAction(vm, IdUninstall, _uninstall,
                    "{=BFGOptiScaler}OptiScaler",
                    "{=BFGUninstall}Uninstall",
                    "{=BFGUninstallDesc}Uninstall only the OptiScaler files managed by BannerlordFG after the game exits."));
            if (++_attachHitCount <= 8)
                _log("Frame Generation native UI rows populated via " + origin +
                    " (installed=" + installed + ", rowCount=" + rows.Count + ").");
        }

        private static GenericOptionDataVM CreateAction(OptionsVM vm, string id,
            Action callback, string caption, string label, string description)
        {
            var option = new ActionOptionData(id, callback);
            return new ActionOptionDataVM(callback, vm, option,
                new TextObject(caption), new TextObject(label), new TextObject(description));
        }

        private static GenericOptionDataVM CreateChoice(OptionsVM vm, ISelectionOptionData data,
            string caption, string description)
        {
            Type t = typeof(GenericOptionDataVM).Assembly.GetType(
                "TaleWorlds.MountAndBlade.ViewModelCollection.GameOptions.StringOptionDataVM", true);
            var c = t.GetConstructor(new[] { typeof(OptionsVM), typeof(ISelectionOptionData),
                typeof(TextObject), typeof(TextObject) });
            if (c == null) throw new MissingMethodException("StringOptionDataVM(OptionsVM,ISelectionOptionData,TextObject,TextObject)");
            return (GenericOptionDataVM)c.Invoke(new object[] { vm, data,
                new TextObject(caption), new TextObject(description) });
        }

        // If vanilla recreates the row binding list on refresh, repopulate ours.
        private static void GroupRefreshPostfix(OptionGroupVM __instance)
        {
            var p = _active;
            if (p == null || __instance == null) return;
            if (!p.IsOurGroup(__instance))
            {
                // Only touch groups that contain the native DLSS row; never the FPS slider.
                if (p.TryGetOwner(__instance, out OptionsVM owner))
                    p.TryHideNoneInUpscaling(__instance, owner);
                return;
            }
            try
            {
                // Keep the exact owner captured from the original constructor,
                // rather than guessing private field names in TaleWorlds DLLs.
                if (__instance.Options != null && __instance.Options.Count > 0) return;
                if (p.TryGetOwner(__instance, out OptionsVM vm))
                    p.PopulateVisualRows(__instance, vm, "group refresh");
            }
            catch (Exception ex) { p.Error("group refresh failed", ex); }
        }

        private static void CategoryInitializePostfix(GroupedOptionCategoryVM __instance)
        {
            var p = _active;
            if (p == null || __instance?.Groups == null) return;
            try
            {
                var vm = AccessTools.Field(typeof(GroupedOptionCategoryVM), "_options")?.GetValue(__instance) as OptionsVM;
                if (vm == null) return;
                foreach (var group in __instance.Groups)
                {
                    p.TryHideNoneInUpscaling(group, vm);
                    if (!p.IsOurGroup(group)) continue;
                    p.RememberOwner(group, vm);
                    p.PopulateVisualRows(group, vm, "category InitializeOptions");
                }
            }
            catch (Exception ex) { p.Error("category initialize failed", ex); }
        }

        private static void OptionsConstructedPostfix(OptionsVM __instance)
        {
            var p = _active;
            if (p != null) p.TryAttachExisting(__instance, null, "OptionsVM constructor");
        }

        private static void PerformanceGetterPostfix(OptionsVM __instance,
            ref GroupedOptionCategoryVM __result)
        {
            var p = _active;
            if (p != null) p.TryAttachExisting(__instance, __result, "PerformanceOptions getter");
        }

        private static void OptionsRefreshPostfix(OptionsVM __instance)
        {
            var p = _active;
            if (p != null) p.TryAttachExisting(__instance, null, "OptionsVM refresh");
        }

        private void TryAttachExisting(OptionsVM vm, GroupedOptionCategoryVM category, string origin)
        {
            if (vm == null || _attaching) return;
            _attaching = true;
            try
            {
                if (category == null)
                    category = AccessTools.Field(typeof(OptionsVM), "_performanceOptionCategory")?.GetValue(vm)
                        as GroupedOptionCategoryVM;
                if (category?.Groups == null) return;
                var groups = category.Groups;
                foreach (var group in groups)
                {
                    if (IsOurGroup(group))
                    {
                        PopulateVisualRows(group, vm, origin + ": existing group");
                        return;
                    }
                }
                // A group created before the Harmony constructor hook may already
                // be present. This is a fallback, not a localized-name lookup
                // for the normal provider path.
                foreach (var group in groups)
                {
                    if (group != null && group.Name != null &&
                        group.Name.ToString().StartsWith("Frame Generation", StringComparison.OrdinalIgnoreCase) &&
                        (group.Options == null || group.Options.Count == 0))
                    {
                        RegisterOurGroup(group, vm);
                        PopulateVisualRows(group, vm, origin + ": recovered provider group");
                        return;
                    }
                }
                // Only if the vanilla provider was evaluated before our hook:
                // add a visual-only group to the existing VM. No custom model data.
                var title = new TextObject("{=BFGFrameGeneration}Frame Generation");
                _ourNames.GetValue(title, _ => new Marker());
                var extra = new OptionGroupVM(title, vm, Enumerable.Empty<IOptionData>());
                RegisterOurGroup(extra, vm);
                PopulateVisualRows(extra, vm, origin + ": fallback");
                int insert = FindScalingIndex(groups) + 1;
                if (insert <= 0) insert = groups.Count;
                groups.Insert(insert, extra);
                _log("FG fallback inserted a native VM group into cached OptionsVM at " + insert + ".");
            }
            catch (Exception ex) { Error("cached VM attach failed through " + origin, ex); }
            finally { _attaching = false; }
        }

        private void ExplainMultiplier()
        {
            // Read-only 2x value: clicking deliberately has no effect, message or INI write.
        }

        private void RefreshUpscalingLocks()
        {
            try
            {
                // The provider route creates the original vanilla native rows.
                // Update their disabled state without recreating the category.
                var refreshed = new HashSet<OptionsVM>();
                foreach (var group in LiveGroups())
                {
                    if (!TryGetOwner(group, out OptionsVM vm) || !refreshed.Add(vm)) continue;
                    var performance = vm.PerformanceOptions;
                    if (performance == null) continue;
                    foreach (var row in performance.AllOptions)
                        if (NativeUpscalingLock.IsScalingOption(row?.GetOptionData()))
                            row.UpdateEnableState();
                }
            }
            catch (Exception ex) { Error("scaling refresh failed", ex); }
        }

        private static void NativeDisabledPostfix(NativeOptionData __instance,
            ref ValueTuple<string, bool> __result)
        {
            if (_active?._controller.NativeDlssManaged == true && _active._isInstalled() &&
                NativeUpscalingLock.IsScalingOption(__instance))
                __result = new ValueTuple<string, bool>("Managed by BannerlordFG", true);
        }

        private static void NativeRowEnabledPostfix(GenericOptionDataVM __instance)
        {
            var p = _active;
            if (p == null || !p._controller.NativeDlssManaged || !p._isInstalled() || __instance == null ||
                !NativeUpscalingLock.IsScalingOption(__instance.GetOptionData())) return;
            try
            {
                // Dynamic lookup: future Bannerlord builds may rename VM fields.
                // If unavailable, the OptionsVM.SetConfig Harmony guard still
                // prevents actual upscaling changes while framegen is enabled.
                var prop = AccessTools.Property(__instance.GetType(), "IsEnabled") ??
                           AccessTools.Property(typeof(GenericOptionDataVM), "IsEnabled");
                if (prop != null && prop.CanWrite) prop.SetValue(__instance, false, null);
            }
            catch (Exception ex) { p.Error("visual upscaling lock unavailable", ex); }
        }

        // A visual-only group must never enter vanilla enum-based save/reset paths.
        private static bool GroupManagedPrefix(OptionGroupVM __instance, ref List<IOptionData> __result)
        {
            var p = _active;
            if (p == null || __instance == null) return true;
            if (p.IsOurGroup(__instance))
            {
                __result = new List<IOptionData>();
                return false;
            }
            if (__instance.Options == null || !__instance.Options.Any(
                row => row != null && IsOurOption(row.GetOptionData()))) return true;
            __result = __instance.Options.Select(row => row?.GetOptionData())
                .Where(data => data != null && !IsOurOption(data) && !data.IsNative() && !data.IsAction())
                .ToList();
            return false;
        }
        private static bool GroupChangedPrefix(OptionGroupVM __instance, ref bool __result)
        {
            var p = _active;
            if (p == null || !p.IsOurGroup(__instance)) return true;
            __result = false;
            return false;
        }
        private static bool GroupNoopPrefix(OptionGroupVM __instance)
        {
            var p = _active;
            return p == null || !p.IsOurGroup(__instance);
        }
        private static bool CategoryManagedPrefix(GroupedOptionCategoryVM __instance,
            ref IEnumerable<IOptionData> __result)
        {
            var p = _active;
            if (p == null || __instance?.Groups == null || !__instance.Groups.Any(p.IsOurGroup))
                return true;
            try
            {
                var category = AccessTools.Field(typeof(GroupedOptionCategoryVM), "_category")?
                    .GetValue(__instance) as OptionCategory;
                if (category == null) return true;
                var original = (category.BaseOptions ?? Enumerable.Empty<IOptionData>())
                    .Concat((category.Groups ?? Enumerable.Empty<OptionGroup>())
                        .SelectMany(g => g.Options ?? Enumerable.Empty<IOptionData>()));
                __result = original.Where(x => x != null && !IsOurOption(x) &&
                    !x.IsNative() && !x.IsAction()).ToList();
                return false;
            }
            catch (Exception ex) { p.Error("managed-option isolation failed", ex); return true; }
        }
        private static bool GetConfigPrefix(IOptionData __0, ref float __result)
        {
            if (!IsOurOption(__0)) return true;
            __result = __0.GetValue(false);
            return false;
        }
        private static bool SetConfigPrefix(IOptionData __0, float __1)
        {
            // Quality/Balanced/Performance remain user-selectable during FG.
            // The game exposes these presets under the DLSS-labelled INPUT,
            // even though OptiScaler performs the actual FSR 3.1 upscaling.
            if (__0?.GetOptionType() is NativeOptions.NativeOptionsType kind &&
                kind == NativeOptions.NativeOptionsType.DLSS &&
                _active?._controller.NativeDlssManaged == true && _active._isInstalled())
            {
                if (__1 <= 0f)
                {
                    _active._log("Upscaler Off rejected while frame generation needs an active temporal input.");
                    return false;
                }
                _active._controller.Settings.LastCompatibleDlss = __1;
                FgSettingsStore.Save(_active._controller.Settings);
                return true;
            }
            if (NativeUpscalingLock.IsScalingOption(__0) &&
                _active?._controller.NativeDlssManaged == true && _active._isInstalled())
            {
                _active?._log("Vanilla upscaling change blocked while FG is enabled.");
                return false;
            }
            if (!IsOurOption(__0)) return true;
            try { __0.SetValue(__1); }
            catch (Exception ex) { _active?.Error("custom visual option update failed", ex); }
            return false;
        }
    }
}

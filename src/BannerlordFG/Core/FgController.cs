// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using TaleWorlds.Engine.Options;

namespace BannerlordFG.Core
{
    /// <summary>
    /// Core of the proposed native Performance panel. No dependency on MCM.
    /// UI adapter must call these methods only AFTER OptiScaler has been installed.
    /// The packaged profile reserves F23/F24 as internal runtime controls.
    /// Key requests are best effort: there is no upstream status/readback API.
    /// </summary>
    public sealed class FgController
    {
        public FgSettings Settings { get; private set; }
        private readonly GameGraphicsBridge _graphics = new GameGraphicsBridge();
        private readonly string _gameBin;
        private bool _safeGraphicsApplied;
        private readonly bool _liveBackendReadyAtLaunch;
        private bool _liveAssumedEnabled;
        private bool _overlayAssumedEnabled;
        private readonly bool _liveKeysReadyAtLaunch;

        public FgController(string gameBin)
        {
            _gameBin = gameBin ?? throw new ArgumentNullException(nameof(gameBin));
            Settings = FgSettingsStore.Load();
            string iniPath = Path.Combine(_gameBin, "OptiScaler.ini");
            _liveBackendReadyAtLaunch = OptiProfile.ConfiguredAtLaunch(iniPath);
            _liveAssumedEnabled = _liveBackendReadyAtLaunch;
            _overlayAssumedEnabled = OptiProfile.OverlayEnabledAtLaunch(iniPath);
            _liveKeysReadyAtLaunch = OptiProfile.LiveControlKeysAtLaunch(iniPath);
            // Old installer versions enabled XeFG without updating preferences.xml.
            // The INI at process launch is the source of truth, not a stale checkbox.
            bool? activeAtLaunch = OptiProfile.ReadFgEnabledAtLaunch(iniPath);
            if (activeAtLaunch.HasValue && Settings.Enabled != activeAtLaunch.Value)
            {
                Settings.Enabled = activeAtLaunch.Value;
                Settings.PendingRestart = false;
                FgSettingsStore.Save(Settings);
            }
            // The custom Base FPS control has been removed. Restore a cap
            // previously imposed by older BannerlordFG versions once.
            if (Settings.SavedGameFps.HasValue)
            {
                _graphics.RestoreExplicitBaseLimit(Settings);
                Settings.ExplicitBaseLimit = false;
                Settings.BaseFps = 0;
                FgSettingsStore.Save(Settings);
            }
            else if (Settings.ExplicitBaseLimit || Settings.BaseFps != 0 || Settings.Multiplier != 2)
            {
                Settings.ExplicitBaseLimit = false;
                Settings.BaseFps = 0;
                Settings.Multiplier = 2;
                FgSettingsStore.Save(Settings);
            }
        }

        public bool CanInstallProfile => File.Exists(Path.Combine(_gameBin, "OptiScaler.ini"));
        public float CurrentNativeDlss => _graphics.CurrentDlss;
        public bool CanToggleLive => _liveBackendReadyAtLaunch && _liveKeysReadyAtLaunch;
        public bool LiveAssumedEnabled => _liveAssumedEnabled;
        // UI state tracks successful requests; OptiScaler has no state readback.
        public bool FgAssumedEnabled => CanToggleLive ? _liveAssumedEnabled : Settings.Enabled;
        public bool OverlayAssumedEnabled => _overlayAssumedEnabled;
        public bool TryToggleLive(out string message)
        {
            if (!CanToggleLive)
            {
                message = "Runtime controls were not initialized when the game launched. Configure the managed profile and launch once again.";
                return false;
            }
            if (!_liveAssumedEnabled)
            {
                string preparation;
                if (!_graphics.ApplyEnabled(Settings, out preparation))
                {
                    message = "Cannot restore required upscaling: " + preparation;
                    return false;
                }
                _safeGraphicsApplied = true;
            }
            bool sent = OptiLiveHotkeys.RequestFgToggle(out message);
            if (sent) _liveAssumedEnabled = !_liveAssumedEnabled;
            // Keep the backend loaded across repeated A/B tests. No INI toggle,
            // game shutdown or reset of native upscaling occurs here.
            return sent;
        }
        public bool TrySetFpsOverlay(bool enabled, out string message)
        {
            if (!CanInstallProfile) { message = "Install OptiScaler first."; return false; }
            var next = CloneSettings(Settings);
            next.ShowFpsOverlay = enabled;
            try
            {
                OptiProfile.WriteSafely(Path.Combine(_gameBin, "OptiScaler.ini"), next);
                Settings.ShowFpsOverlay = enabled;
                FgSettingsStore.Save(Settings);
                if (_overlayAssumedEnabled != enabled && _liveKeysReadyAtLaunch)
                {
                    bool sent = OptiLiveHotkeys.RequestOverlayToggle(out message);
                    if (sent) _overlayAssumedEnabled = enabled;
                    else message += " Preference saved for next launch; live request was not sent.";
                }
                else if (_overlayAssumedEnabled == enabled)
                    message = "FPS overlay already matches the selected state.";
                else message = "No runtime FPS shortcut was initialized; setting saved for next launch.";
                return true;
            }
            catch (Exception ex) { message = "Cannot save FPS overlay setting: " + ex.Message; return false; }
        }
        public bool NativeDlssManaged => _liveBackendReadyAtLaunch ? _liveAssumedEnabled : Settings.Enabled;
        public bool NativeLimiterManaged => false; // do not lock the vanilla limiter
        public bool AllowDlssOff => !NativeDlssManaged;
        public bool TryEnable(out string message, bool preferQualityOnFirstInstall = false)
        {
            if (!CanInstallProfile) { message = "Install OptiScaler first."; return false; }
            Settings.Validate(FgMultiplierData.IsArcAdapter());
            if (!_graphics.ApplyEnabled(Settings, out message, preferQualityOnFirstInstall)) return false;
            try
            {
                var next = CloneSettings(Settings);
                next.Enabled = true;
                OptiProfile.WriteSafely(Path.Combine(_gameBin, "OptiScaler.ini"), next);
                Settings.Enabled = true;
                // The game's normal frame limiter is exclusively user-controlled.
                Settings.PendingRestart = true;
                FgSettingsStore.Save(Settings);
                _safeGraphicsApplied = true;
                message = "Frame generation configured. A restart is required only if the backend was not initialized at launch.";
                return true;
            }
            catch (Exception ex)
            {
                // Never claim FG is active when OptiScaler configuration failed.
                _graphics.RestoreOriginal(Settings);
                Settings.Enabled = false;
                FgSettingsStore.Save(Settings);
                message = "Failed to configure OptiScaler: " + ex.Message;
                return false;
            }
        }

        public bool TryDisable(out string message)
        {
            try
            {
                var next = CloneSettings(Settings);
                next.Enabled = false;
                if (CanInstallProfile) OptiProfile.WriteSafely(Path.Combine(_gameBin, "OptiScaler.ini"), next);
                Settings.Enabled = false;
                // When internal live keys are unavailable, the backend remains
                // active until the game exits. Restoring native DLSS=None NOW
                // would remove XeFG's input and can crash the current session.
                // Defer restoring vanilla graphics until startup with FG off.
                Settings.PendingRestart = true;
                FgSettingsStore.Save(Settings);
                message = "FG disable saved for next launch. Upscaling remains active until the game exits.";
                return true;
            }
            catch (Exception ex)
            {
                message = "Cannot safely disable FG: " + ex.Message;
                return false;
            }
        }

        // Called at startup ONLY after reading the new INI. Never disable
        // temporal upscaling while a live XeFG backend still depends on it.
        public bool TryRestoreVanillaAfterFgStopped(out string message)
        {
            message = null;
            if (_liveBackendReadyAtLaunch || Settings.Enabled ||
                (!Settings.SavedGameDlss.HasValue && !Settings.SavedGameFps.HasValue)) return false;
            try
            {
                _graphics.RestoreOriginal(Settings);
                FgSettingsStore.Save(Settings);
                _safeGraphicsApplied = false;
                message = "Vanilla graphics restored after launching with FG disabled.";
                return true;
            }
            catch (Exception ex)
            {
                message = "Vanilla graphics restoration deferred: " + ex.Message;
                return false;
            }
        }

        // Complete a verified uninstall only on the next clean game launch.
        // The maintenance worker signals this AFTER removing tracked files;
        // clicking Uninstall by itself never turns FG off or causes a fake restart.
        public bool TryCompleteUninstall(out string message)
        {
            try
            {
                if (CanInstallProfile)
                {
                    message = "OptiScaler still exists; cannot finalize uninstall.";
                    return false;
                }
                _graphics.RestoreOriginal(Settings);
                Settings.Enabled = false;
                Settings.PendingRestart = false;
                Settings.LastCompatibleDlss = null; // Fresh install should start with Quality, not stale Performance.
                FgSettingsStore.Save(Settings);
                message = "Verified OptiScaler uninstall: original game graphics restored.";
                return true;
            }
            catch (Exception ex)
            {
                message = "OptiScaler was removed, but restoring original settings failed: " + ex.Message;
                return false;
            }
        }

        // The separate Base FPS limiter was removed in v0.3.11 and stays removed.
        // The player's native Video > Frame Limiter remains authoritative.

        public bool TrySetMultiplier(int value, bool certifiedOnThisGpu, out string message)
        {
            if (value != 2)
            { message = "Only fixed 2x is supported."; return false; }
            message = "Multiplier is fixed at 2x; no configuration change or restart is necessary.";
            return true;
        }

        public void MaintainVanillaSettings()
        {
            if (!_safeGraphicsApplied || !NativeDlssManaged) return;
            _graphics.Enforce(Settings);
        }

        public bool TryApplySavedAtStartup(out string message)
        {
            message = null;
            if (!Settings.Enabled) return false;
            if (!CanInstallProfile) { message = "OptiScaler missing: native settings left untouched."; return false; }
            Settings.Validate(FgMultiplierData.IsArcAdapter());
            bool success = _graphics.ApplyEnabled(Settings, out message);
            if (success)
            {
                _safeGraphicsApplied = true;
                // No BannerlordFG-specific FPS cap is applied at startup.
                // Reserve F23/F24 only; preserve the post-FG FPS preference.
                try { OptiProfile.WriteSafely(Path.Combine(_gameBin, "OptiScaler.ini"), Settings); }
                catch (Exception ex) { message += " OptiScaler config update failed: " + ex.Message; }
                FgSettingsStore.Save(Settings);
            }
            return success;
        }

        private static FgSettings CloneSettings(FgSettings s) => new FgSettings
        {
            Enabled = s.Enabled, Multiplier = s.Multiplier, BaseFps = s.BaseFps,
            ExplicitBaseLimit = s.ExplicitBaseLimit,
            SavedGameDlss = s.SavedGameDlss, SavedGameFps = s.SavedGameFps,
            LastCompatibleDlss = s.LastCompatibleDlss, PendingRestart = s.PendingRestart,
            ShowFpsOverlay = s.ShowFpsOverlay
        };
    }
}

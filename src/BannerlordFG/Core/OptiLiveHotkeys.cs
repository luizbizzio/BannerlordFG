// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace BannerlordFG.Core
{
    /// <summary>
    /// OptiScaler exposes configurable FG and FPS runtime shortcuts.
    /// This package reserves F23 and F24 for internal control only. This is only a key request, NOT an IPC
    /// or a readback of the graphics backend's actual state.
    /// </summary>
    internal static class OptiLiveHotkeys
    {
        private const int VK_F23 = 0x86;
        private const int VK_F24 = 0x87;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private static int _pendingKey;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte virtualKey, byte scan, uint flags, UIntPtr extraInfo);

        internal static bool RequestFgToggle(out string result) => Send(VK_F23, "internal F23", out result);
        internal static bool RequestOverlayToggle(out string result) => Send(VK_F24, "internal F24", out result);

        private static bool Send(int virtualKey, string keyName, out string result)
        {
            try
            {
                var foreground = GetForegroundWindow();
                uint foregroundPid;
                if (foreground == IntPtr.Zero || GetWindowThreadProcessId(foreground, out foregroundPid) == 0 ||
                    foregroundPid != (uint)Process.GetCurrentProcess().Id)
                {
                    result = "Game is not foreground. Press " + keyName + " in-game instead.";
                    return false;
                }
                // GetAsyncKeyState is frame-polled; a same-tick press/release may
                // never be observed. Hold the injected internal key for 100 ms.
                if (Interlocked.CompareExchange(ref _pendingKey, virtualKey, 0) != 0)
                {
                    result = "An internal live-control request is already pending.";
                    return false;
                }
                try
                {
                    keybd_event((byte)virtualKey, 0, 0, UIntPtr.Zero);
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { Thread.Sleep(100); }
                        finally
                        {
                            try { keybd_event((byte)virtualKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); }
                            finally { Interlocked.Exchange(ref _pendingKey, 0); }
                        }
                    });
                }
                catch
                {
                    keybd_event((byte)virtualKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    Interlocked.Exchange(ref _pendingKey, 0);
                    throw;
                }
                result = "Requested " + keyName + ". OptiScaler has no external state-readback API.";
                return true;
            }
            catch (Exception ex)
            {
                result = "Cannot inject " + keyName + ": " + ex.Message;
                return false;
            }
        }
    }
}

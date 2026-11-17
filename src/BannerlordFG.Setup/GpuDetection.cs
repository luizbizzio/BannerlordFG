// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using BannerlordFG.Common;

namespace BannerlordFG.Setup
{
    // First identify the adapter actually doing Bannerlord's 3D rendering.
    // GPU Engine counter instances expose a process PID and the adapter's LUID;
    // PDH supplies live utilization values (WMI formatted counters may report
    // zero even when GPU Engine PDH reports high use); DXGI resolves the LUID.
    // This is independent of the
    // often incorrect adapter names shown in Bannerlord's video settings.
    // If readings are missing/ambiguous, use only the single-vendor fallback.
    // Never substitute a vendor ranking for renderer evidence.
    internal static class GpuDetection
    {
        private const uint NvidiaVendorId = 0x10DE;
        private const uint AmdVendorId = 0x1002;
        private const uint IntelVendorId = 0x8086;
        private static readonly Regex EngineName = new Regex(
            @"^pid_(\d+)_luid_0x([0-9a-f]+)_0x([0-9a-f]+)_phys_\d+_eng_\d+_engtype_(3D|Graphics|Render)(?:_\d+)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // PDH's language-neutral path is important on non-English Windows.
        // All counters are collected together so the renderer can be compared
        // against another GPU during the *same* sampling interval.
        private const uint PdhFmtDouble = 0x00000200;

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        private struct PdhDoubleValue
        {
            [FieldOffset(0)] public uint Status;
            [FieldOffset(8)] public double Value;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint PdhOpenQueryW(string source, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath,
            IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll", ExactSpelling = true)]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", ExactSpelling = true)]
        private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format,
            out uint counterType, out PdhDoubleValue result);

        [DllImport("pdh.dll", ExactSpelling = true)]
        private static extern uint PdhCloseQuery(IntPtr query);

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DxgiAdapterDesc1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public Luid AdapterLuid;
            public uint Flags;
        }

        // COM inheritance is intentionally flattened. IUnknown's first three
        // vtable slots are implicit; these stubs occupy the inherited DXGIObject
        // and DXGIFactory slots, placing EnumAdapters1 at vtable index 12.
        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            void SetPrivateData();
            void SetPrivateDataInterface();
            void GetPrivateData();
            void GetParent();
            void EnumAdapters();
            void MakeWindowAssociation();
            void GetWindowAssociation();
            void CreateSwapChain();
            void CreateSoftwareAdapter();
            [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
            [PreserveSig] bool IsCurrent();
        }

        // IDXGIObject has four methods, IDXGIAdapter has three more;
        // IDXGIAdapter1::GetDesc1 is therefore at vtable index 10.
        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            void SetPrivateData();
            void SetPrivateDataInterface();
            void GetPrivateData();
            void GetParent();
            void EnumOutputs();
            void GetDesc();
            void CheckInterfaceSupport();
            [PreserveSig] int GetDesc1(out DxgiAdapterDesc1 desc);
        }

        [DllImport("dxgi.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int CreateDXGIFactory1(ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

        internal sealed class Adapter
        {
            internal string Vendor;
            internal string Description;
            internal uint VendorId;
            internal uint DeviceId;
            internal ulong DedicatedBytes;
            internal ulong Luid;
        }

        private static string VendorOf(uint id)
        {
            if (id == NvidiaVendorId) return "NVIDIA";
            if (id == AmdVendorId) return "AMD";
            if (id == IntelVendorId) return "Intel";
            return null;
        }

        internal static Dictionary<ulong, Adapter> EnumerateDxgiAdapters()
        {
            var adapters = new Dictionary<ulong, Adapter>();
            IDXGIFactory1 factory = null;
            try
            {
                Guid iid = typeof(IDXGIFactory1).GUID;
                int hr = CreateDXGIFactory1(ref iid, out factory);
                if (hr < 0 || factory == null) Marshal.ThrowExceptionForHR(hr);
                for (uint index = 0; index < 64; index++)
                {
                    IDXGIAdapter1 current = null;
                    try
                    {
                        hr = factory.EnumAdapters1(index, out current);
                        if (hr == unchecked((int)0x887A0002)) break; // DXGI_ERROR_NOT_FOUND
                        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                        if (current == null) break;
                        DxgiAdapterDesc1 desc;
                        hr = current.GetDesc1(out desc);
                        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                        string vendor = VendorOf(desc.VendorId);
                        ulong luid = ((ulong)(uint)desc.AdapterLuid.HighPart << 32) |
                                     desc.AdapterLuid.LowPart;
                        // Include every hardware vendor for inventory; software
                        // adapters and zero LUIDs cannot identify a renderer.
                        if (luid != 0 && (desc.Flags & 2) == 0)
                            adapters[luid] = new Adapter { Vendor = vendor ?? "unknown",
                                Description = desc.Description, VendorId = desc.VendorId,
                                DeviceId = desc.DeviceId,
                                DedicatedBytes = desc.DedicatedVideoMemory.ToUInt64(), Luid = luid };
                    }
                    finally
                    {
                        if (current != null) Marshal.ReleaseComObject(current);
                    }
                }
            }
            finally
            {
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
            return adapters;
        }

        // The Bannerlord C# module can execute in TaleWorlds.MountAndBlade.Launcher.exe:
        // SubModule.CaptureCurrentLaunch explicitly documents this. An elevated
        // installer receives that host PID, NOT necessarily a Bannerlord.exe PID.
        // Only exact, same-directory vanilla or BLSE executables may contribute
        // GPU Engine samples. A matching process name is not authentication.
        internal static readonly string[] GameProcessNames = {
            "Bannerlord", "Bannerlord.Native", "TaleWorlds.MountAndBlade.Launcher",
            "Bannerlord.BLSE.Launcher", "Bannerlord.BLSE.LauncherEx",
            "Bannerlord.BLSE.Standalone", "Launcher.Native"
        };

        internal static string ExpectedExecutableName(string processName)
        {
            foreach (string name in GameProcessNames)
                if (string.Equals(processName, name, StringComparison.OrdinalIgnoreCase))
                    return name + ".exe";
            return null;
        }

        // A native game EXE is strong evidence of the renderer. Vanilla/BLSE
        // launchers (including Launcher.Native) can ALSO host the game, but only
        // the live in-game caller or a live module marker proves that state.
        internal static bool IsDirectGameProcess(string name)
        {
            return string.Equals(name, "Bannerlord", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Bannerlord.Native", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Bannerlord.BLSE.Standalone", StringComparison.OrdinalIgnoreCase);
        }

        // Prefer MainModule; under some Windows/driver/UAC combinations its path
        // is unavailable while Win32_Process.ExecutablePath is accessible.
        // Both sources must resolve to the exact executable in the validated
        // game binary directory. Never accept a PID solely by its name.
        internal static bool IsExpectedGameProcess(Process process, string gameBin,
            Action<string> log)
        {
            int pid;
            string image;
            try
            {
                if (process.HasExited) return false;
                image = ExpectedExecutableName(process.ProcessName);
                if (image == null) return false;
                pid = process.Id;
            }
            catch (InvalidOperationException) { return false; }

            string expected = Path.GetFullPath(Path.Combine(gameBin, image));
            string mainPath = null;
            string mainError = null;
            try
            {
                mainPath = process.MainModule.FileName;
                if (SameExecutablePath(mainPath, expected)) return true;
                // MainModule provided a definitive path, not a permission error.
                // Never let a later WMI query contradict this path (PID reuse).
                if (!string.IsNullOrEmpty(mainPath)) return false;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                mainError = ex.GetType().Name + ": " + ex.Message;
            }
            catch (InvalidOperationException ex)
            {
                mainError = ex.GetType().Name + ": " + ex.Message;
            }

            string wmiPath = null;
            string wmiError = null;
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, ExecutablePath FROM Win32_Process WHERE ProcessId = " +
                    pid.ToString(CultureInfo.InvariantCulture)))
                {
                    searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                    using (ManagementObjectCollection processes = searcher.Get())
                    {
                        foreach (ManagementBaseObject entry in processes)
                        {
                            using (entry)
                            {
                                string processName = Convert.ToString(entry["Name"]);
                                if (!string.Equals(processName, image,
                                    StringComparison.OrdinalIgnoreCase)) continue;
                                wmiPath = Convert.ToString(entry["ExecutablePath"]);
                                if (SameExecutablePath(wmiPath, expected))
                                {
                                    log("GPU render probe: " + image + " PID " + pid +
                                        " verified via Win32_Process.ExecutablePath" +
                                        (mainError == null ? "." : " (MainModule unavailable)."));
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch (ManagementException ex) { wmiError = ex.GetType().Name + ": " + ex.Message; }
            catch (System.Runtime.InteropServices.COMException ex) { wmiError = ex.GetType().Name + ": " + ex.Message; }
            catch (UnauthorizedAccessException ex) { wmiError = ex.GetType().Name + ": " + ex.Message; }

            log("GPU render probe: rejected " + image + " PID " + pid +
                "; expected='" + expected + "'; MainModule='" + (mainPath ?? mainError ?? "unavailable") +
                "'; WMI='" + (wmiPath ?? wmiError ?? "unavailable") + "'.");
            return false;
        }

        private static bool SameExecutablePath(string actual, string expected)
        {
            if (string.IsNullOrWhiteSpace(actual)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(actual), expected,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }

        private static HashSet<int> FindGameProcessIds(string gameBin, int originalPid,
            Action<string> log)
        {
            var ids = new HashSet<int>();
            // The caller may be the game's launcher-named rendering host. Do
            // not discard it, but do not assume it is rendering either. PDH
            // must still observe GPU usage for any verified candidate PID.
            if (originalPid > 0)
            {
                try
                {
                    using (Process p = Process.GetProcessById(originalPid))
                    {
                        string image = p.ProcessName;
                        log("GPU render probe: original caller PID " + originalPid +
                            " is '" + image + "'.");
                        if (IsExpectedGameProcess(p, gameBin, log)) ids.Add(p.Id);
                    }
                }
                catch (ArgumentException) { log("GPU render probe: caller PID already exited."); }
                catch (InvalidOperationException) { log("GPU render probe: caller process exited during inspection."); }
            }

            int candidates = 0;
            // It is possible to have a separate renderer process. Collect all
            // validated game-host processes, not just the caller, and compare
            // their PDH usage before selecting a GPU.
            foreach (string name in GameProcessNames)
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    using (p)
                    {
                        candidates++;
                        if (!ids.Contains(p.Id) && IsExpectedGameProcess(p, gameBin, log))
                            ids.Add(p.Id);
                    }
                }
            }
            log("GPU render probe: discovery found " + candidates +
                " matching-name process(es), " + ids.Count +
                " verified game host PID(s) (caller PID " + originalPid + ").");
            return ids;
        }

        // A telemetry name encodes high-LUID then low-LUID in hex. Do not
        // confuse phys_N with an NVIDIA/AMD/Intel brand or GPU index.
        internal static bool TryParseEngine(string name, HashSet<int> gamePids, out ulong luid)
        {
            int pid;
            return TryParseEngine(name, gamePids, out pid, out luid);
        }

        internal static bool TryParseEngine(string name, HashSet<int> gamePids,
            out int pid, out ulong luid)
        {
            pid = 0;
            luid = 0;
            Match m = EngineName.Match(name ?? "");
            if (!m.Success) return false;
            uint high, low;
            if (!int.TryParse(m.Groups[1].Value, out pid) || !gamePids.Contains(pid) ||
                !uint.TryParse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out high) ||
                !uint.TryParse(m.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out low))
                return false;
            luid = ((ulong)high << 32) | low;
            return luid != 0;
        }

        private sealed class EngineCounter
        {
            public int Pid;
            public ulong Luid;
            public IntPtr Handle;
        }

        private static List<RendererDecision.Activity> SampleGameRenderUsage(
            HashSet<int> gamePids, Action<string> log)
        {
            // WMI is used ONLY to enumerate instance names. On actual Windows
            // hybrid-GPU hardware its formatted UtilizationPercentage can be
            // zero despite 70%+ activity reported by Get-Counter (PDH).
            var instances = new Dictionary<string, RendererDecision.Activity>(StringComparer.OrdinalIgnoreCase);
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"))
            {
                searcher.Options.Timeout = TimeSpan.FromSeconds(4);
                using (ManagementObjectCollection rows = searcher.Get())
                {
                    foreach (ManagementBaseObject row in rows)
                    {
                        using (row)
                        {
                            string name = Convert.ToString(row["Name"]);
                            int pid;
                            ulong luid;
                            if (TryParseEngine(name, gamePids, out pid, out luid))
                                instances[name] = new RendererDecision.Activity { Pid = pid, Luid = luid };
                        }
                    }
                }
            }
            var usage = new List<RendererDecision.Activity>();
            if (instances.Count == 0)
            {
                log("GPU render probe: no matching Bannerlord render engine instances; checking for an unambiguous single-vendor fallback.");
                return usage;
            }

            IntPtr query = IntPtr.Zero;
            try
            {
                uint status = PdhOpenQueryW(null, IntPtr.Zero, out query);
                if (status != 0 || query == IntPtr.Zero)
                    throw new InvalidOperationException("PDH GPU query failed: 0x" + status.ToString("X8"));

                var counters = new List<EngineCounter>();
                foreach (var instance in instances)
                {
                    // AddEnglishCounter is independent of Windows display language.
                    // No shell commands, admin dialogs, or GPU-selection UI.
                    string path = "\\GPU Engine(" + instance.Key + ")\\Utilization Percentage";
                    IntPtr counter;
                    if (PdhAddEnglishCounterW(query, path, IntPtr.Zero, out counter) == 0 &&
                        counter != IntPtr.Zero)
                        counters.Add(new EngineCounter { Pid = instance.Value.Pid,
                            Luid = instance.Value.Luid, Handle = counter });
                }
                if (counters.Count == 0)
                {
                    log("GPU render probe: PDH had no readable GPU Engine counters; checking for an unambiguous single-vendor fallback.");
                    return usage;
                }

                // Two collections are required by rate counters; matching the
                // Get-Counter -SampleInterval 1 -MaxSamples 2 test on Windows.
                if (PdhCollectQueryData(query) != 0)
                    throw new InvalidOperationException("PDH first GPU sample failed.");
                Thread.Sleep(1000);
                if (PdhCollectQueryData(query) != 0)
                    throw new InvalidOperationException("PDH second GPU sample failed.");

                int readable = 0;
                foreach (EngineCounter entry in counters)
                {
                    uint counterType;
                    PdhDoubleValue sample;
                    if (PdhGetFormattedCounterValue(entry.Handle, PdhFmtDouble,
                        out counterType, out sample) != 0 || sample.Status > 1)
                        continue;
                    double percent = sample.Value;
                    if (double.IsNaN(percent) || double.IsInfinity(percent) || percent <= 0)
                        continue;
                    readable++;
                    usage.Add(new RendererDecision.Activity { Pid = entry.Pid,
                        Luid = entry.Luid, Percent = percent });
                }
                log("GPU render probe via PDH: " + instances.Count + " renderer instances, " +
                    counters.Count + " counters added, " + readable +
                    " with positive utilization.");
                return usage;
            }
            finally
            {
                if (query != IntPtr.Zero) PdhCloseQuery(query);
            }
        }

        internal static string TryDetectActiveGpu(string gameBin, int originalPid, Action<string> log)
        {
            Adapter adapter = TryDetectActiveAdapter(gameBin, originalPid, log, false);
            return adapter == null || adapter.Vendor == "unknown" ? null : adapter.Vendor;
        }

        internal static Adapter TryDetectActiveAdapter(string gameBin, int originalPid,
            Action<string> log, bool includeUnknownVendor = true)
        {
            try
            {
                var pids = FindGameProcessIds(gameBin, originalPid, log);
                if (pids.Count == 0)
                {
                    log("GPU render probe unavailable: no verified Bannerlord renderer host is running; checking for an unambiguous single-vendor fallback.");
                    return null;
                }
                Dictionary<ulong, Adapter> adapters = EnumerateDxgiAdapters();
                if (adapters.Count == 0)
                {
                    log("GPU render probe unavailable: DXGI did not expose a supported adapter LUID.");
                    return null;
                }
                // Keep GPU Engine samples attached to their original PID. The
                // old implementation merged all PIDs by GPU LUID, so an idle
                // Intel launcher could outvote an NVIDIA/AMD game renderer.
                List<RendererDecision.Activity> measured = SampleGameRenderUsage(pids, log);
                var hosts = new List<RendererDecision.Host>();
                foreach (int pid in pids)
                {
                    try
                    {
                        using (Process process = Process.GetProcessById(pid))
                        {
                            // Revalidate the path after PDH sampling: a PID may
                            // have exited and been reused during the 1s sample.
                            if (!IsExpectedGameProcess(process, gameBin, log)) continue;
                            bool marked = GameHostMarker.IsActive(gameBin, pid);
                            hosts.Add(new RendererDecision.Host {
                                Pid = pid,
                                DirectGame = IsDirectGameProcess(process.ProcessName),
                                ActiveModule = marked,
                                // Avoid trusting a reused launcher PID. The
                                // in-game caller must still own the live marker.
                                InGameCaller = pid == originalPid && marked
                            });
                        }
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
                var choice = RendererDecision.Select(hosts,
                    measured.Where(x => adapters.ContainsKey(x.Luid) &&
                        (includeUnknownVendor || adapters[x.Luid].Vendor != "unknown")));
                if (choice == null)
                {
                    log("GPU render probe had no uniquely identified game-renderer PID/LUID; " +
                        "checking for an unambiguous single-vendor fallback.");
                    return null;
                }
                Adapter actual = adapters[choice.Luid];
                log("Active Bannerlord 3D renderer detected via PDH GPU Engine PID " +
                    choice.Pid + " and DXGI adapter LUID 0x" + choice.Luid.ToString("X16") +
                    ": " + actual.Description + " (" + actual.Vendor + ", activity " +
                    choice.Percent.ToString("F1", CultureInfo.InvariantCulture) + "%).");
                return actual;
            }
            catch (Exception ex)
            {
                // GPU telemetry, WMI, the driver and DXGI are optional. Never
                // fail installation just because this read-only probe fails.
                log("GPU render probe unavailable (" + ex.GetType().Name + "); checking for an unambiguous single-vendor fallback.");
                return null;
            }
        }

        // When process telemetry is unavailable, do NOT choose the most powerful
        // GPU or a preferred vendor. Only a single installed vendor is safe.
        // Multi-vendor systems require a verified game-renderer PID/LUID.
        internal static string DetectOnlyUnambiguousVendor(Action<string> log)
        {
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_VideoController"))
            {
                searcher.Options.Timeout = TimeSpan.FromSeconds(10);
                var vendors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (ManagementObjectCollection rows = searcher.Get())
                {
                    foreach (ManagementBaseObject row in rows)
                    {
                        using (row)
                        {
                            string pnp = Convert.ToString(row["PNPDeviceID"]) ?? "";
                            string name = Convert.ToString(row["Name"]) ?? "";
                            if (pnp.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0)
                                vendors.Add("NVIDIA");
                            else if (pnp.IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0)
                                vendors.Add("AMD");
                            else if (pnp.IndexOf("VEN_8086", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0)
                                vendors.Add("Intel");
                        }
                    }
                }
                if (vendors.Count == 1)
                {
                    string vendor = vendors.First();
                    log("GPU fallback: single installed GPU vendor " + vendor +
                        "; active renderer not directly verified.");
                    return vendor;
                }
                if (vendors.Count > 1)
                    throw new InvalidOperationException(
                        "Multiple GPU vendors detected but the active game renderer could not be verified by PID and DXGI LUID. " +
                        "No GPU was selected. Start Bannerlord on the intended GPU and retry installation.");
                throw new InvalidOperationException(
                    "No supported NVIDIA, AMD or Intel GPU found through Win32_VideoController.");
            }
        }
    }
}

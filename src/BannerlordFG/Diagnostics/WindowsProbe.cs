// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using BannerlordFG.Setup;

namespace BannerlordFG.Diagnostics
{
    internal sealed class GpuDescriptor
    {
        internal string Vendor;
        internal string VendorSource;
        internal string Model;
        internal ulong? DedicatedBytes;
        internal string Driver;
        internal ulong? Luid;
    }

    // Run once on a ThreadPool worker. WMI may stall or be denied on some PCs;
    // no query here is permitted on the game's rendering or UI thread.
    internal static class WindowsProbe
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersionInfo
        {
            internal int Size;
            internal int Major;
            internal int Minor;
            internal int Build;
            internal int Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Csd;
            internal ushort ServicePackMajor;
            internal ushort ServicePackMinor;
            internal ushort SuiteMask;
            internal byte ProductType;
            internal byte Reserved;
        }

        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int RtlGetVersion(ref OsVersionInfo info);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPhysicallyInstalledSystemMemory(out ulong kilobytes);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern uint GetActiveProcessorCount(ushort groupNumber);

        internal static string WindowsVersion(Action<string> warning)
        {
            string version = "unknown", edition = "unknown";
            try
            {
                var info = new OsVersionInfo { Size = Marshal.SizeOf(typeof(OsVersionInfo)) };
                if (RtlGetVersion(ref info) == 0)
                    version = info.Major + "." + info.Minor + " build " + info.Build;
            }
            catch (Exception ex) { warning("Windows version API unavailable: " + ex.GetType().Name); }
            try
            {
                foreach (Dictionary<string, string> row in Query(
                    "SELECT Caption FROM Win32_OperatingSystem", 4))
                {
                    edition = Value(row, "Caption"); break;
                }
            }
            catch (Exception ex) { warning("Windows edition query unavailable: " + ex.GetType().Name); }
            return edition + "; " + version + "; " +
                (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit");
        }

        internal static string Cpu(Action<string> warning)
        {
            string model = "unknown";
            uint logical = 0;
            try { logical = GetActiveProcessorCount(0xFFFF); }
            catch (Exception ex) { warning("Logical processor API unavailable: " + ex.GetType().Name); }
            try
            {
                uint wmiLogical = 0;
                foreach (Dictionary<string, string> row in Query(
                    "SELECT Name, NumberOfLogicalProcessors FROM Win32_Processor", 4))
                {
                    if (model == "unknown") model = Value(row, "Name");
                    if (uint.TryParse(Value(row, "NumberOfLogicalProcessors"),
                        out uint count)) wmiLogical += count;
                }
                if (logical == 0) logical = wmiLogical;
            }
            catch (Exception ex) { warning("CPU query unavailable: " + ex.GetType().Name); }
            return model + "; logical processors=" + (logical > 0 ? logical.ToString() : "unknown");
        }

        internal static string InstalledRam(Action<string> warning)
        {
            try
            {
                ulong kilobytes;
                if (GetPhysicallyInstalledSystemMemory(out kilobytes) && kilobytes > 0)
                    return (kilobytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " GiB";
            }
            catch (Exception ex) { warning("Installed RAM API unavailable: " + ex.GetType().Name); }
            return "unknown";
        }

        internal static List<GpuDescriptor> Gpus(Action<string> warning)
        {
            Dictionary<ulong, GpuDetection.Adapter> adapters = null;
            try { adapters = GpuDetection.EnumerateDxgiAdapters(); }
            catch (Exception ex) { warning("DXGI adapter inventory unavailable: " + ex.GetType().Name); }
            List<Dictionary<string, string>> rows = null;
            try { rows = Query("SELECT Name, PNPDeviceID, DriverVersion FROM Win32_VideoController", 5); }
            catch (Exception ex) { warning("GPU driver query unavailable: " + ex.GetType().Name); }
            return MergeGpus(adapters, rows);
        }

        internal static List<GpuDescriptor> MergeGpus(
            Dictionary<ulong, GpuDetection.Adapter> adapters,
            IEnumerable<Dictionary<string, string>> windowsDevices)
        {
            var result = new List<GpuDescriptor>();
            if (adapters != null)
                result.AddRange(adapters.Values.Select(x => new GpuDescriptor {
                    // OptiScaler can spoof DXGI's vendor ID inside the game.
                    // Use it for LUID matching, not physical manufacturer.
                    Vendor = "unknown", VendorSource = "unknown", Model = x.Description,
                    DedicatedBytes = x.DedicatedBytes > 0 ? (ulong?)x.DedicatedBytes : null,
                    Driver = "unknown", Luid = x.Luid }));
            var matchedLuids = new HashSet<ulong>();
            foreach (Dictionary<string, string> row in windowsDevices ??
                Enumerable.Empty<Dictionary<string, string>>())
            {
                string pnp = Value(row, "PNPDeviceID");
                string model = Value(row, "Name");
                string driver = Value(row, "DriverVersion");
                GpuDescriptor match = null;
                if (adapters != null && pnp != "unknown")
                    foreach (GpuDetection.Adapter adapter in adapters.Values)
                    {
                        string key = "VEN_" + adapter.VendorId.ToString("X4") +
                            "&DEV_" + adapter.DeviceId.ToString("X4");
                        if (pnp.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            !matchedLuids.Contains(adapter.Luid))
                        {
                            match = result.FirstOrDefault(x => x.Luid == adapter.Luid);
                            break;
                        }
                    }
                if (match == null && model != "unknown")
                    match = result.FirstOrDefault(x => string.Equals(x.Model, model,
                        StringComparison.OrdinalIgnoreCase) && x.Luid.HasValue &&
                        !matchedLuids.Contains(x.Luid.Value));
                if (match != null)
                {
                    match.Driver = driver;
                    match.Vendor = VendorIdentity.FromPnpDeviceId(pnp);
                    match.VendorSource = match.Vendor == "unknown" ? "unknown" :
                        "Windows PCI PNPDeviceID";
                    if (match.Luid.HasValue) matchedLuids.Add(match.Luid.Value);
                }
                else if (model != "unknown" && pnp.IndexOf("VEN_",
                    StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string vendor = VendorIdentity.FromPnpDeviceId(pnp);
                    result.Add(new GpuDescriptor {
                        Vendor = vendor,
                        VendorSource = vendor == "unknown" ? "unknown" : "Windows PCI PNPDeviceID",
                        Model = model, Driver = driver });
                }
            }
            // DXGI can expose the same physical adapter under more than one
            // LUID. WMI's PCI device rows distinguish that from two actual
            // cards with the same model; keep each matched physical row.
            if (matchedLuids.Count > 0 && adapters != null)
                result.RemoveAll(x => x.Luid.HasValue && !matchedLuids.Contains(x.Luid.Value) &&
                    adapters.Values.Any(a => matchedLuids.Contains(a.Luid) &&
                        a.VendorId == adapters[x.Luid.Value].VendorId &&
                        a.DeviceId == adapters[x.Luid.Value].DeviceId &&
                        string.Equals(a.Description, adapters[x.Luid.Value].Description,
                            StringComparison.OrdinalIgnoreCase)));
            return result;
        }

        private static List<Dictionary<string, string>> Query(string text, int timeoutSeconds)
        {
            var result = new List<Dictionary<string, string>>();
            using (var searcher = new ManagementObjectSearcher(text))
            {
                searcher.Options.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
                using (ManagementObjectCollection rows = searcher.Get())
                    foreach (ManagementBaseObject row in rows)
                        using (row)
                        {
                            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (PropertyData property in row.Properties)
                                values[property.Name] = Convert.ToString(property.Value, CultureInfo.InvariantCulture);
                            result.Add(values);
                        }
            }
            return result;
        }

        private static string Value(Dictionary<string, string> row, string key)
        {
            string value;
            row.TryGetValue(key, out value);
            return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        }
    }
}

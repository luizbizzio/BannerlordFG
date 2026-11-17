// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BannerlordFG.Diagnostics
{
    internal static class VendorIdentity
    {
        private static readonly Regex PciVendor = new Regex(@"(?i)\bVEN_([0-9A-F]{4})\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static string FromPciVendorId(uint id)
        {
            if (id == 0x10DE) return "NVIDIA";
            if (id == 0x1002) return "AMD";
            if (id == 0x8086) return "Intel";
            return "unknown";
        }

        internal static string FromPnpDeviceId(string pnpDeviceId)
        {
            Match match = PciVendor.Match(pnpDeviceId ?? "");
            uint id;
            return match.Success && uint.TryParse(match.Groups[1].Value,
                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)
                ? FromPciVendorId(id) : "unknown";
        }
    }
}

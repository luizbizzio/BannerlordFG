// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;

namespace BannerlordFG.Core
{
    internal enum NativeMenuRow
    {
        FrameGeneration,
        FpsCounter,
        ExportDiagnostics,
        Install,
        Repair,
        Uninstall
    }

    // The native VM uses this small, deterministic row plan. It contains no
    // TaleWorlds objects, so state transitions can be exercised outside the game.
    internal static class NativeMenuLayout
    {
        internal static IReadOnlyList<NativeMenuRow> Rows(bool installed, bool repairNeeded)
        {
            var rows = new List<NativeMenuRow>();
            if (installed)
            {
                rows.Add(NativeMenuRow.FrameGeneration);
                rows.Add(NativeMenuRow.FpsCounter);
            }
            rows.Add(installed ? NativeMenuRow.Uninstall :
                repairNeeded ? NativeMenuRow.Repair : NativeMenuRow.Install);
            rows.Add(NativeMenuRow.ExportDiagnostics);
            return rows;
        }
    }
}

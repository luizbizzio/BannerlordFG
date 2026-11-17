// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace BannerlordFG.Common
{
    // Pure decision policy, separated from Windows PDH/WMI/DXGI to allow real
    // behavioural tests with synthetic PID/LUID activity (without a game GPU).
    internal static class RendererDecision
    {
        internal sealed class Host
        {
            internal int Pid;
            internal bool DirectGame;
            internal bool ActiveModule;
            internal bool InGameCaller;
        }

        internal sealed class Activity
        {
            internal int Pid;
            internal ulong Luid;
            internal double Percent;
        }

        internal sealed class Choice
        {
            internal int Pid;
            internal ulong Luid;
            internal double Percent;
        }

        // A validated PID alone is not evidence of the game. The in-game
        // installer caller, an actual game executable, or a live module marker
        // provides that evidence. GPU brand never influences this decision.
        internal static Choice Select(IEnumerable<Host> hosts, IEnumerable<Activity> activity)
        {
            if (hosts == null || activity == null) return null;
            var readings = activity.Where(x => x != null && x.Pid > 0 && x.Luid != 0 &&
                x.Percent > 0 && !double.IsNaN(x.Percent) && !double.IsInfinity(x.Percent))
                .GroupBy(x => new { x.Pid, x.Luid })
                .Select(g => new Choice { Pid = g.Key.Pid, Luid = g.Key.Luid,
                    Percent = g.Sum(x => x.Percent) }).ToArray();

            var candidates = new List<KeyValuePair<int, Choice>>();
            foreach (Host host in hosts.Where(x => x != null && x.Pid > 0))
            {
                // Direct executable is the strongest evidence when a launcher
                // and the actual game are different running processes.
                int priority = host.DirectGame ? 3 : host.ActiveModule ? 2 :
                    host.InGameCaller ? 1 : 0;
                if (priority == 0) continue;
                Choice[] measured = readings.Where(x => x.Pid == host.Pid)
                    .OrderByDescending(x => x.Percent).ToArray();
                if (measured.Length == 0 || measured[0].Percent < 0.5) continue;
                if (measured.Length > 1 && measured[1].Percent > 0.2 &&
                    measured[0].Percent < measured[1].Percent * 2.0) continue;
                candidates.Add(new KeyValuePair<int, Choice>(priority, measured[0]));
            }
            if (candidates.Count == 0) return null;
            int highest = candidates.Max(x => x.Key);
            Choice[] ranked = candidates.Where(x => x.Key == highest)
                .Select(x => x.Value).OrderByDescending(x => x.Percent).ToArray();
            if (ranked.Length > 1 && ranked[0].Luid != ranked[1].Luid &&
                ranked[1].Percent > 0.2 && ranked[0].Percent < ranked[1].Percent * 2.0)
                return null;
            return ranked[0];
        }
    }

    internal static class GameRunningDecision
    {
        // Only call this after confirming the absolute executable path.
        // Failed module inspection is uncertain and must not permit DLL writes.
        internal static bool IsBusy(bool directGame, bool activeGameModule,
            bool managedRuntimeDllLoaded, bool modulesInspected)
        {
            return directGame || activeGameModule || managedRuntimeDllLoaded || !modulesInspected;
        }
    }
}

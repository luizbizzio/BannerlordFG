// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BannerlordFG.Common
{
    // A process-scoped presence marker, not a lock. It exists only while the
    // actual BannerlordFG module is loaded. An idle BLSE/vanilla launcher has
    // no marker after the game's module unloads, even if its EXE stays open.
    internal static class GameHostMarker
    {
        private static string Name(string gameBin, int pid)
        {
            string path = Path.GetFullPath(gameBin).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar).ToUpperInvariant();
            byte[] digest;
            using (var sha = SHA256.Create()) digest = sha.ComputeHash(Encoding.UTF8.GetBytes(path));
            return "Local\\BannerlordFG-gamehost-" + BitConverter.ToString(digest, 0, 12)
                .Replace("-", "") + "-" + pid;
        }

        internal static Mutex Register(string gameBin, int pid)
        {
            if (pid <= 0) throw new ArgumentOutOfRangeException("pid");
            return new Mutex(false, Name(gameBin, pid));
        }

        internal static bool IsActive(string gameBin, int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (Mutex present = Mutex.OpenExisting(Name(gameBin, pid))) return true;
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return true; } // fail closed
        }
    }
}

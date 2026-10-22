// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpCompress.Archives.SevenZip;

namespace BannerlordFG.Setup
{
    /// <summary>
    /// Managed 7z extraction. The outer caller verifies the pinned SHA-256 first.
    /// A separate class keeps format support independent of Windows tar.exe.
    /// </summary>
    internal static class SafeSevenZipExtractor
    {
        private const long MaxUnpackedBytes = 1500000000L;
        private const int MaxEntries = 10000;
        private const int BufferSize = 65536;
        private static readonly HashSet<string> ReservedNames = new HashSet<string>(
            new[] { "CON", "PRN", "AUX", "NUL" }.Concat(
                Enumerable.Range(1, 9).SelectMany(i => new[] { "COM" + i, "LPT" + i })),
            StringComparer.OrdinalIgnoreCase);

        private static string ValidateEntryName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidDataException("Archive contains an empty entry name.");
            string name = raw.Replace('\\', '/');
            while (name.StartsWith("./", StringComparison.Ordinal)) name = name.Substring(2);
            if (name.StartsWith("/", StringComparison.Ordinal) || name.Contains(":"))
                throw new InvalidDataException("Archive contains an absolute/drive path.");
            string[] parts = name.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                throw new InvalidDataException("Archive contains an empty entry path.");
            foreach (string part in parts)
            {
                if (part == "." || part == ".." || part.TrimEnd(' ', '.') != part ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    part.Any(c => c < 32 || c == '<' || c == '>' || c == '"' ||
                        c == '|' || c == '?' || c == '*') ||
                    ReservedNames.Contains(part.Split('.')[0]))
                    throw new InvalidDataException("Archive contains an unsafe entry path: " + raw);
            }
            return Path.Combine(parts);
        }

        private static void CheckPath(string destinationRoot, string fullPath)
        {
            string prefix = destinationRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Archive entry escapes extraction directory.");
            // Reject existing junctions/symlinks in parent paths before writing.
            string current = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(current) && current.Length >= destinationRoot.Length)
            {
                if (Directory.Exists(current) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Archive extraction traverses a reparse point.");
                if (string.Equals(current, destinationRoot, StringComparison.OrdinalIgnoreCase)) break;
                current = Path.GetDirectoryName(current);
            }
        }

        internal static void Extract(string archivePath, string outputDirectory)
        {
            string root = Path.GetFullPath(outputDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Archive output directory is a reparse point.");

            // Stage and validate the entire list before writing the first byte.
            using (var archive = SevenZipArchive.OpenArchive(archivePath))
            {
                var entries = archive.Entries.ToArray();
                if (entries.Length == 0 || entries.Length > MaxEntries)
                    throw new InvalidDataException("Unexpected number of entries in official 7z archive.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long expectedTotal = 0;
                var paths = new List<string>(entries.Length);
                foreach (var entry in entries)
                {
                    if (entry.LinkTarget != null || entry.IsEncrypted || !entry.IsComplete)
                        throw new InvalidDataException("Official archive contains a link, encrypted, or incomplete entry.");
                    string relative = ValidateEntryName(entry.Key);
                    string fullPath = Path.GetFullPath(Path.Combine(root, relative));
                    CheckPath(root, fullPath);
                    if (!seen.Add(relative.TrimEnd(Path.DirectorySeparatorChar)))
                        throw new InvalidDataException("Official archive contains duplicate entry names.");
                    if (entry.Size < 0 || entry.Size > MaxUnpackedBytes - expectedTotal)
                        throw new InvalidDataException("Official archive exceeds the unpacked size limit.");
                    expectedTotal += entry.Size;
                    paths.Add(fullPath);
                }

                // Reject file/directory conflicts before writing, including implied parents.
                var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < entries.Length; ++i)
                    if (!entries[i].IsDirectory) filePaths.Add(paths[i]);
                foreach (string path in paths)
                {
                    string parent = Path.GetDirectoryName(path);
                    while (parent != null && parent.Length > root.Length)
                    {
                        if (filePaths.Contains(parent))
                            throw new InvalidDataException("Archive includes a file/directory path collision.");
                        parent = Path.GetDirectoryName(parent);
                    }
                }

                long actualTotal = 0;
                byte[] buffer = new byte[BufferSize];
                for (int i = 0; i < entries.Length; ++i)
                {
                    var entry = entries[i];
                    string target = paths[i];
                    CheckPath(root, target);
                    if (entry.IsDirectory)
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    string folder = Path.GetDirectoryName(target);
                    Directory.CreateDirectory(folder);
                    CheckPath(root, target);
                    using (var source = entry.OpenEntryStream())
                    using (var destination = new FileStream(target, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, BufferSize))
                    {
                        int count;
                        long entryBytes = 0;
                        while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            entryBytes += count;
                            actualTotal += count;
                            if (entryBytes > entry.Size || actualTotal > MaxUnpackedBytes)
                                throw new InvalidDataException("Archive extraction exceeded its size bounds.");
                            destination.Write(buffer, 0, count);
                        }
                        if (entryBytes != entry.Size)
                            throw new InvalidDataException("Archive entry size does not match metadata.");
                    }
                }
            }
        }
    }
}

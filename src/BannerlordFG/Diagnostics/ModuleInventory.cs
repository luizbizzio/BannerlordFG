// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace BannerlordFG.Diagnostics
{
    internal sealed class ModuleRecord
    {
        internal string Id;
        internal string Name;
        internal string Version;
        internal string[] Dependencies;
        internal string State;
        internal int? LoadOrder;
        internal bool? LauncherSelected;
    }

    // Files visible to the game process include MO2's virtualized Modules tree.
    // A folder alone never establishes whether a module was selected or loaded.
    internal static class ModuleInventory
    {
        internal static List<string> ParseModuleArgument(IEnumerable<string> arguments)
        {
            var matches = (arguments ?? Enumerable.Empty<string>()).Where(x => x != null &&
                x.StartsWith("_MODULES_*", StringComparison.Ordinal) &&
                x.EndsWith("*_MODULES_", StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1) return null;
            string value = matches[0];
            if (value.Length < 20) return null;
            string[] ids = value.Substring(10, value.Length - 20)
                .Split(new[] { '*' }, StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length == 0 || ids.Length > 512 ||
                ids.Any(x => !ValidId(x)) ||
                ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length ||
                !ids.Contains("Native", StringComparer.OrdinalIgnoreCase) ||
                !ids.Contains("BannerlordFG", StringComparer.OrdinalIgnoreCase))
                return null;
            return ids.ToList();
        }

        internal static bool ValidId(string id)
        {
            return !string.IsNullOrWhiteSpace(id) && id.Length <= 128 &&
                id.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.');
        }

        internal static List<ModuleRecord> Scan(string modulesDirectory,
            IList<string> activeIds, bool activeVerified, Action<string> warning,
            IList<string> launcherSelected = null, Func<string, string[]> listDirectories = null)
        {
            var records = new Dictionary<string, ModuleRecord>(StringComparer.OrdinalIgnoreCase);
            string[] folders;
            try { folders = listDirectories == null ? Directory.GetDirectories(modulesDirectory) :
                listDirectories(modulesDirectory); }
            catch (Exception ex) { warning("Module enumeration unavailable: " + ex.GetType().Name); return new List<ModuleRecord>(); }
            foreach (string folder in folders.Take(1024))
            {
                string xmlPath = Path.Combine(folder, "SubModule.xml");
                if (!File.Exists(xmlPath)) continue;
                try
                {
                    if (new FileInfo(xmlPath).Length > 1024 * 1024)
                        throw new InvalidDataException("Module metadata is too large");
                    var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 };
                    var document = new XmlDocument { XmlResolver = null };
                    using (var reader = XmlReader.Create(xmlPath, settings)) document.Load(reader);
                    XmlNode root = document.DocumentElement;
                    if (root == null || root.Name != "Module") throw new InvalidDataException("Invalid module root");
                    string id = Value(root, "Id");
                    if (!ValidId(id)) throw new InvalidDataException("Missing or invalid module ID");
                    if (records.ContainsKey(id)) { warning("Duplicate module ID: " + id); continue; }
                    var dependencies = new List<string>();
                    XmlNode nodes = root.SelectSingleNode("DependedModules");
                    if (nodes != null)
                        foreach (XmlNode node in nodes.ChildNodes)
                        {
                            string dep = node.Attributes?["Id"]?.Value;
                            if (ValidId(dep) && !dependencies.Contains(dep, StringComparer.OrdinalIgnoreCase))
                                dependencies.Add(dep);
                        }
                    records.Add(id, new ModuleRecord { Id = id, Name = Value(root, "Name") ?? id,
                        Version = Value(root, "Version") ?? "unknown", Dependencies = dependencies.ToArray(),
                        State = "unknown" });
                }
                catch (Exception ex)
                {
                    warning("Module metadata unreadable (" + Path.GetFileName(folder) + "): " + ex.GetType().Name);
                }
            }
            if (folders.Length > 1024) warning("Module inventory capped at 1024 folders.");
            if (activeVerified && activeIds != null)
            {
                for (int index = 0; index < activeIds.Count; index++)
                {
                    string id = activeIds[index];
                    if (!ValidId(id)) continue;
                    ModuleRecord record;
                    if (!records.TryGetValue(id, out record))
                    {
                        record = new ModuleRecord { Id = id, Name = id, Version = "unknown",
                            Dependencies = new string[0] };
                        records.Add(id, record);
                    }
                    record.State = "active";
                    record.LoadOrder = index + 1;
                }
                foreach (ModuleRecord record in records.Values)
                    if (record.State != "active") record.State = "disabled";
            }
            if (launcherSelected != null)
                foreach (ModuleRecord record in records.Values)
                    record.LauncherSelected = launcherSelected.Contains(record.Id,
                        StringComparer.OrdinalIgnoreCase);
            return records.Values.OrderBy(x => x.LoadOrder ?? int.MaxValue)
                .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string Value(XmlNode root, string element)
        {
            string value = root.SelectSingleNode(element)?.Attributes?["value"]?.Value?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}

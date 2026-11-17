// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace BannerlordFG.Diagnostics
{
    internal sealed class GameVersionEvidence
    {
        internal string Version = "unknown";
        internal string Source = "unknown";
        internal string Warning;
    }

    // Assembly and launcher file versions commonly remain 1.0.0.0 across
    // Bannerlord releases. Native TaleWorlds module metadata carries the
    // installed game version and is cross-checked when other base modules exist.
    internal static class GameVersionDetector
    {
        private static readonly string[] BaseModules = {
            "Native", "SandBoxCore", "Sandbox", "StoryMode" };
        private static readonly Regex VersionPattern = new Regex(@"^v?\d+\.\d+\.\d+(?:\.\d+)?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        internal static GameVersionEvidence Detect(string gameBin)
        {
            var result = new GameVersionEvidence();
            string root;
            try { root = Path.GetFullPath(Path.Combine(gameBin, "..", "..", "Modules")); }
            catch (Exception ex) { result.Warning = "Game module directory unavailable: " + ex.GetType().Name; return result; }
            var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool invalidMetadata = false;
            foreach (string id in BaseModules)
            {
                string path = Path.Combine(root, id, "SubModule.xml");
                if (!File.Exists(path)) continue;
                try
                {
                    if (new FileInfo(path).Length > 1024 * 1024)
                        throw new InvalidDataException("Module metadata too large");
                    var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 };
                    var document = new XmlDocument { XmlResolver = null };
                    using (var reader = XmlReader.Create(path, settings)) document.Load(reader);
                    XmlNode module = document.DocumentElement;
                    if (module == null || module.Name != "Module" ||
                        !string.Equals(module.SelectSingleNode("Id")?.Attributes?["value"]?.Value,
                            id, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Base module ID mismatch");
                    string version = module.SelectSingleNode("Version")?.Attributes?["value"]?.Value;
                    if (string.IsNullOrWhiteSpace(version) || !VersionPattern.IsMatch(version.Trim()))
                        throw new InvalidDataException("Invalid base module version");
                    versions[id] = version.Trim();
                }
                catch (Exception ex)
                {
                    invalidMetadata = true;
                    result.Warning = id + " version metadata unavailable: " + ex.GetType().Name;
                }
            }
            if (invalidMetadata) return result;
            if (versions.Count == 0) return result;
            if (versions.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            {
                result.Warning = "Installed TaleWorlds base module versions disagree.";
                return result;
            }
            if (versions.ContainsKey("Native"))
            {
                result.Version = versions["Native"];
                result.Source = "Native/SubModule.xml";
            }
            else if (versions.Count >= 2)
            {
                result.Version = versions.Values.First();
                result.Source = "matching TaleWorlds base module metadata";
            }
            else result.Warning = "Native version metadata missing; one base module is insufficient.";
            return result;
        }
    }
}

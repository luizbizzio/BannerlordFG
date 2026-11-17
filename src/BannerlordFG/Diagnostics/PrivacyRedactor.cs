// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Text.RegularExpressions;

namespace BannerlordFG.Diagnostics
{
    internal static class PrivacyRedactor
    {
        private static readonly Regex LocalPath = new Regex(
            @"(?i)(?:\b[A-Z]:[\\/]|\\\\|%(?:USERPROFILE|LOCALAPPDATA|APPDATA)%[\\/])(?:(?![ \t]+[A-Z_][A-Z0-9_-]*[ \t]*=)[^\r\n])*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Secret = new Regex(
            @"(?i)(?<label>\b(?:token|password|passwd|secret|api[_-]?key|apikey|steam[_ -]?id|serial(?:[ _-]?number)?)\b[""']?\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s;,""'}]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Bearer = new Regex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Email = new Regex(
            @"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SteamId = new Regex(@"(?i)\bSTEAM_[0-5]:[01]:\d+\b|\[U:1:\d+\]|\b\d{17}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static string RedactLine(string line)
        {
            string result = RedactSecrets(line ?? "");
            result = LocalPath.Replace(result, "[local path redacted]");
            return RedactUser(result);
        }

        internal static string RedactValue(string value)
        {
            if (value == null) return "unknown";
            string result = RedactSecrets(value);
            if (LocalPath.IsMatch(result)) result = "[local path redacted]";
            return RedactUser(result);
        }

        private static string RedactSecrets(string value)
        {
            string cleaned = Secret.Replace(value,
                match => match.Groups["label"].Value + "[redacted]");
            cleaned = Bearer.Replace(cleaned, "Bearer [redacted]");
            cleaned = SteamId.Replace(cleaned, "[Steam ID redacted]");
            return Email.Replace(cleaned, "[email redacted]");
        }

        private static string RedactUser(string value)
        {
            string user = Environment.UserName;
            return string.IsNullOrWhiteSpace(user) || user.Length < 3 ? value :
                Regex.Replace(value, Regex.Escape(user), "[user]", RegexOptions.IgnoreCase);
        }
    }
}

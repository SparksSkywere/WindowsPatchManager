using System.Text.RegularExpressions;

namespace ApplicationUpdater.Helpers;

public static class VersionComparer
{
    /// <summary>
    /// Returns true if available is greater than current. Unknown/unparseable versions
    /// return null (caller decides).
    /// </summary>
    public static bool? IsNewer(string? current, string? available)
    {
        if (string.IsNullOrWhiteSpace(available) ||
            available.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
            available is "-" or "—")
            return null;

        if (string.IsNullOrWhiteSpace(current) ||
            current.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return true;

        var c = Normalize(current);
        var a = Normalize(available);

        if (Version.TryParse(c, out var cv) && Version.TryParse(a, out var av))
            return av > cv;

        // Numeric segment compare for non-standard versions
        var cParts = Split(c);
        var aParts = Split(a);
        int len = Math.Max(cParts.Count, aParts.Count);
        for (int i = 0; i < len; i++)
        {
            long cn = i < cParts.Count ? cParts[i] : 0;
            long an = i < aParts.Count ? aParts[i] : 0;
            if (an != cn)
                return an > cn;
        }

        return false;
    }

    private static string Normalize(string version)
    {
        version = version.Trim();
        // Take first token if space-separated ("1.2.3 (x64)")
        var space = version.IndexOf(' ');
        if (space > 0)
            version = version[..space];

        // Strip leading 'v'
        if (version.StartsWith('v') || version.StartsWith('V'))
            version = version[1..];

        // Keep only digits, dots, and hyphens that separate numbers
        version = Regex.Replace(version, @"[^\d\.]+", ".");
        version = Regex.Replace(version, @"\.+", ".");
        version = version.Trim('.');

        // Version.Parse needs at most 4 components; pad if needed
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "0";
        if (parts.Length == 1)
            return $"{parts[0]}.0";
        if (parts.Length > 4)
            version = string.Join('.', parts.Take(4));

        return version;
    }

    private static List<long> Split(string version)
    {
        return Regex.Matches(version, @"\d+")
            .Select(m => long.TryParse(m.Value, out var n) ? n : 0)
            .ToList();
    }
}

using System.Text.RegularExpressions;

namespace ApplicationUpdater.Helpers;

/// <summary>
/// Parses fixed-width table output produced by winget list / winget upgrade.
/// </summary>
public static class WingetTableParser
{
    public sealed record Row(IReadOnlyDictionary<string, string> Columns);

    public static IReadOnlyList<Row> Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return [];

        var lines = output
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        // Skip progress / spinner noise and find header
        int headerIndex = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Contains("Name", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("Id", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("Version", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("Available", StringComparison.OrdinalIgnoreCase)))
            {
                headerIndex = i;
                break;
            }
        }

        if (headerIndex < 0)
            return ParseLoose(lines);

        var headerLine = lines[headerIndex];
        var columns = DetectColumns(headerLine);
        if (columns.Count == 0)
            return ParseLoose(lines);

        var rows = new List<Row>();
        for (int i = headerIndex + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (IsSeparator(line) || IsNoise(line))
                continue;

            // Summary lines at the end of winget upgrade
            if (line.StartsWith("No installed package", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Available upgrades", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("The following packages", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("upgrades available", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("  "))
            {
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < columns.Count; c++)
            {
                var (name, start) = columns[c];
                int end = c + 1 < columns.Count ? columns[c + 1].Start : line.Length;
                if (start >= line.Length)
                {
                    values[name] = string.Empty;
                    continue;
                }

                end = Math.Min(end, line.Length);
                values[name] = line[start..end].Trim();
            }

            if (values.TryGetValue("Name", out var nameVal) && !string.IsNullOrWhiteSpace(nameVal))
                rows.Add(new Row(values));
        }

        return rows;
    }

    private static List<(string Name, int Start)> DetectColumns(string headerLine)
    {
        // Match known column headers in order
        string[] known = ["Name", "Id", "Version", "Available", "Source", "Match"];
        var found = new List<(string Name, int Start)>();

        foreach (var col in known)
        {
            // Word-boundary-ish: column name at start or after spaces
            int idx = IndexOfColumn(headerLine, col);
            if (idx >= 0)
                found.Add((col, idx));
        }

        return found.OrderBy(c => c.Start).ToList();
    }

    private static int IndexOfColumn(string header, string column)
    {
        // Prefer exact token match
        var pattern = $@"(?<![A-Za-z]){Regex.Escape(column)}(?![A-Za-z])";
        var match = Regex.Match(header, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Index : -1;
    }

    private static bool IsSeparator(string line) =>
        line.Length > 3 && line.All(ch => ch is '-' or '=' or ' ' or '─' or '━');

    private static bool IsNoise(string line)
    {
        if (line.StartsWith("   -", StringComparison.Ordinal)) return true;
        if (line.Contains('%') && line.Contains("MB")) return true;
        if (line.StartsWith("Failed when searching", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.StartsWith("An unexpected error", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Fallback: split on 2+ spaces when header detection fails.
    /// </summary>
    private static IReadOnlyList<Row> ParseLoose(IEnumerable<string> lines)
    {
        var rows = new List<Row>();
        bool pastHeader = false;

        foreach (var line in lines)
        {
            if (!pastHeader)
            {
                if (line.Contains("Name", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("Id", StringComparison.OrdinalIgnoreCase))
                {
                    pastHeader = true;
                }
                continue;
            }

            if (IsSeparator(line) || IsNoise(line))
                continue;

            var parts = Regex.Split(line.Trim(), @"\s{2,}").Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            if (parts.Length < 2)
                continue;

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"] = parts[0],
                ["Id"] = parts.Length > 1 ? parts[1] : string.Empty,
                ["Version"] = parts.Length > 2 ? parts[2] : string.Empty,
                ["Available"] = parts.Length > 3 ? parts[3] : string.Empty,
                ["Source"] = parts.Length > 4 ? parts[4] : string.Empty
            };
            rows.Add(new Row(dict));
        }

        return rows;
    }
}

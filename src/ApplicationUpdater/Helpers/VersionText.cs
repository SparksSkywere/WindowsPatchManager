namespace ApplicationUpdater.Helpers;

public static class VersionText
{
    public static bool IsUnknown(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return true;

        var v = version.Trim();
        return v.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
               v is "-" or "—" or "?" or "N/A" or "n/a";
    }

    public static string DisplayOr(string? version, string fallback) =>
        IsUnknown(version) ? fallback : version!.Trim();
}

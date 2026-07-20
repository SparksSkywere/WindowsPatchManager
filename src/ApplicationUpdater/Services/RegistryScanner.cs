using ApplicationUpdater.Models;
using Microsoft.Win32;

namespace ApplicationUpdater.Services;

public static class RegistryScanner
{
    private static readonly string[] UninstallRoots =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    private static readonly string[] SkipNameKeywords =
    [
        "hotfix", "security update", "kb", "update for",
        "redistributable", "runtime", "microsoft visual c++",
        "service pack", "language pack", ".net framework",
        "microsoft .net", "update helper", "installer"
    ];

    public static IReadOnlyList<ProgramInfo> Scan()
    {
        var results = new List<ProgramInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in UninstallRoots)
        {
            ScanHive(Registry.LocalMachine, root, results, seen);
            ScanHive(Registry.CurrentUser, root, results, seen);
        }

        return results;
    }

    private static void ScanHive(
        RegistryKey hive,
        string rootPath,
        List<ProgramInfo> results,
        HashSet<string> seen)
    {
        try
        {
            using var root = hive.OpenSubKey(rootPath);
            if (root is null) return;

            foreach (var subName in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(subName);
                    if (key is null) continue;

                    var name = key.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    // SystemComponent = 1 often means hidden system package
                    if (key.GetValue("SystemComponent") is int sc && sc == 1)
                        continue;

                    if (key.GetValue("ParentKeyName") is string parent && !string.IsNullOrWhiteSpace(parent))
                        continue;

                    if (!IsValidProgram(name))
                        continue;

                    if (!seen.Add(name))
                        continue;

                    results.Add(new ProgramInfo
                    {
                        Name = name.Trim(),
                        Version = (key.GetValue("DisplayVersion") as string)?.Trim() ?? "Unknown",
                        Publisher = (key.GetValue("Publisher") as string)?.Trim() ?? string.Empty,
                        InstallLocation = (key.GetValue("InstallLocation") as string)?.Trim() ?? string.Empty,
                        Source = PackageSource.Registry
                    });
                }
                catch
                {
                    // ignore individual key errors
                }
            }
        }
        catch
        {
            // hive not accessible
        }
    }

    private static bool IsValidProgram(string name)
    {
        var lower = name.ToLowerInvariant();
        return SkipNameKeywords.All(k => !lower.Contains(k, StringComparison.Ordinal));
    }
}

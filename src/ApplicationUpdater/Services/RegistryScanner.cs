using System.IO;
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

                    var version = (key.GetValue("DisplayVersion") as string)?.Trim();
                    if (string.IsNullOrWhiteSpace(version))
                        version = (key.GetValue("Version") as string)?.Trim();
                    if (string.IsNullOrWhiteSpace(version) && key.GetValue("VersionMajor") is int major)
                    {
                        var minor = key.GetValue("VersionMinor") is int mi ? mi : 0;
                        version = $"{major}.{minor}";
                    }

                    var publisher = (key.GetValue("Publisher") as string)?.Trim() ?? string.Empty;
                    var installLocation = (key.GetValue("InstallLocation") as string)?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(installLocation))
                        installLocation = (key.GetValue("InstallSource") as string)?.Trim() ?? string.Empty;
                    // DisplayIcon often points at the main EXE — keep folder as location fallback
                    if (string.IsNullOrWhiteSpace(installLocation) &&
                        key.GetValue("DisplayIcon") is string icon &&
                        !string.IsNullOrWhiteSpace(icon))
                    {
                        var iconPath = icon.Split(',')[0].Trim().Trim('"');
                        if (File.Exists(iconPath))
                            installLocation = Path.GetDirectoryName(iconPath) ?? string.Empty;
                    }

                    var uninstallString = (key.GetValue("UninstallString") as string)?.Trim() ?? string.Empty;

                    // Steam / store games often omit InstallLocation; recover from uninstall or key name
                    if (string.IsNullOrWhiteSpace(installLocation) &&
                        !string.IsNullOrWhiteSpace(uninstallString))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(
                            uninstallString, "\"([^\"]+\\.(?:exe|bat|cmd))\"");
                        if (m.Success)
                        {
                            var file = m.Groups[1].Value;
                            // steam.exe is the uninstaller host, not the game folder — skip as location
                            if (File.Exists(file) &&
                                !file.Contains(@"\steam\steam.exe", StringComparison.OrdinalIgnoreCase) &&
                                !file.EndsWith(@"\steam.exe", StringComparison.OrdinalIgnoreCase))
                                installLocation = Path.GetDirectoryName(file) ?? string.Empty;
                        }
                    }

                    var origin = string.Empty;
                    var blob = $"{installLocation}\n{uninstallString}\n{subName}";
                    if (blob.Contains("steamapps", StringComparison.OrdinalIgnoreCase) ||
                        blob.Contains("Steam App", StringComparison.OrdinalIgnoreCase) ||
                        blob.Contains("steam://", StringComparison.OrdinalIgnoreCase))
                        origin = "Steam";
                    else if (blob.Contains(@"\Epic Games\", StringComparison.OrdinalIgnoreCase) ||
                             blob.Contains("EpicGames", StringComparison.OrdinalIgnoreCase))
                        origin = "Epic Games";
                    else if (blob.Contains(@"\GOG Galaxy\", StringComparison.OrdinalIgnoreCase) ||
                             blob.Contains(@"\GOG Games\", StringComparison.OrdinalIgnoreCase))
                        origin = "GOG";

                    results.Add(new ProgramInfo
                    {
                        Name = name.Trim(),
                        Version = string.IsNullOrWhiteSpace(version) ? "Unknown" : version,
                        Publisher = publisher,
                        InstallLocation = installLocation,
                        PackageId = subName, // uninstall key id — useful for matching
                        Source = PackageSource.Registry,
                        Origin = origin,
                        Notes = string.IsNullOrWhiteSpace(uninstallString) ? null : uninstallString
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

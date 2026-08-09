using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationUpdater.Models;
using Microsoft.Win32;

namespace ApplicationUpdater.Helpers;

/// <summary>
/// Detects where an app came from (Steam, Epic, GOG, …) and fills missing
/// Version / Publisher from registry paths, store manifests, and EXE file info.
/// </summary>
public static partial class AppOriginEnricher
{
    private static readonly (string Needle, string Label, string Publisher)[] PathHints =
    [
        (@"\steamapps\common\", "Steam", "Valve"),
        (@"\steam\steamapps\", "Steam", "Valve"),
        (@"\steamlibrary\", "Steam", "Valve"),
        (@"\epic games\", "Epic Games", "Epic Games, Inc."),
        (@"\epicgames\", "Epic Games", "Epic Games, Inc."),
        (@"\gog galaxy\games\", "GOG", "GOG.com"),
        (@"\gog games\", "GOG", "GOG.com"),
        (@"\ubisoft game launcher\games\", "Ubisoft", "Ubisoft"),
        (@"\ubisoft\games\", "Ubisoft", "Ubisoft"),
        (@"\origin games\", "EA App", "Electronic Arts"),
        (@"\ea games\", "EA App", "Electronic Arts"),
        (@"\electronic arts\", "EA App", "Electronic Arts"),
        (@"\battle.net\", "Battle.net", "Blizzard Entertainment"),
        (@"\blizzard\", "Battle.net", "Blizzard Entertainment"),
        (@"\riot games\", "Riot Games", "Riot Games"),
        (@"\xboxgames\", "Xbox", "Microsoft Corporation"),
        (@"\microsoft.gamingapp", "Xbox", "Microsoft Corporation"),
        (@"\itch\", "itch.io", "itch.io"),
        (@"\legendary\", "Epic Games", "Epic Games, Inc."),
        (@"\heroic\prefixes", "Heroic", "Heroic Games Launcher"),
        (@"\amazon games\", "Amazon Games", "Amazon"),
        (@"\rockstar games\", "Rockstar", "Rockstar Games"),
        (@"\bethesda.net launcher\", "Bethesda", "Bethesda Softworks"),
        (@"\windowsapps\", "Microsoft Store", "Microsoft Corporation")
    ];

    private static readonly (string Needle, string Label, string Publisher)[] PublisherHints =
    [
        ("valve", "Steam", "Valve"),
        ("steam", "Steam", "Valve"),
        ("epic games", "Epic Games", "Epic Games, Inc."),
        ("gog.com", "GOG", "GOG.com"),
        ("cd projekt", "GOG", "GOG.com"),
        ("ubisoft", "Ubisoft", "Ubisoft"),
        ("electronic arts", "EA App", "Electronic Arts"),
        ("ea swiss", "EA App", "Electronic Arts"),
        ("blizzard", "Battle.net", "Blizzard Entertainment"),
        ("activision", "Battle.net", "Activision"),
        ("riot games", "Riot Games", "Riot Games"),
        ("xbox game studios", "Xbox", "Microsoft Corporation"),
        ("microsoft corporation", "Microsoft", "Microsoft Corporation"),
        ("amazon", "Amazon Games", "Amazon"),
        ("rockstar", "Rockstar", "Rockstar Games")
    ];

    private static readonly Regex SteamAppIdRegex = new(
        @"Steam\s+App\s+(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SteamUninstallRegex = new(
        @"steam://uninstall/(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>name/path/appid → (version, publisher, installPath)</summary>
    private static Dictionary<string, SteamEntry>? _steamApps;
    private static Dictionary<string, (string Version, string Publisher, string Location)>? _epicApps;

    private readonly record struct SteamEntry(string Version, string Publisher, string Location, string AppId);

    public static void EnrichAll(IList<ProgramInfo> programs)
    {
        // Rebuild indexes each scan so library moves / new installs are picked up.
        _steamApps = null;
        _epicApps = null;
        EnsureSteamIndex();
        EnsureEpicIndex();

        foreach (var p in programs)
            Enrich(p);
    }

    public static void Enrich(ProgramInfo p)
    {
        // Prefer install folder; fall back to uninstall string / notes (often has steam.exe path)
        var location = FirstNonEmpty(p.InstallLocation, ExtractPathHint(p.Notes), ExtractPathHint(p.PackageId));
        var publisher = p.Publisher ?? string.Empty;
        var name = p.Name ?? string.Empty;
        var id = p.PackageId ?? string.Empty;
        var notes = p.Notes ?? string.Empty;

        // 1) Origin / store label
        if (string.IsNullOrWhiteSpace(p.Origin) ||
            p.Origin.Equals("registry", StringComparison.OrdinalIgnoreCase) ||
            p.Origin.Equals("winget", StringComparison.OrdinalIgnoreCase) ||
            p.Origin.Equals("windows", StringComparison.OrdinalIgnoreCase) ||
            p.Origin.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            var origin = DetectOrigin(location, publisher, name, id, notes, p.Source);
            if (!string.IsNullOrWhiteSpace(origin))
                p.Origin = origin;
        }

        // 2) Publisher fill-in (do not overwrite real studios like Re-Logic)
        if (string.IsNullOrWhiteSpace(p.Publisher) || p.Publisher is "—" or "-" or "Unknown")
        {
            p.Publisher = InferPublisher(location, name, id, p.Origin) ?? string.Empty;
        }

        // 3) Version fill-in when Unknown
        if (VersionText.IsUnknown(p.Version))
        {
            var ver = TryResolveVersion(p, location);
            if (!string.IsNullOrWhiteSpace(ver) && !VersionText.IsUnknown(ver))
                p.Version = ver.Trim();
        }

        // 4) If we discovered a better install path, keep it for future runs
        if (string.IsNullOrWhiteSpace(p.InstallLocation) &&
            !string.IsNullOrWhiteSpace(location) &&
            Directory.Exists(location))
        {
            p.InstallLocation = location;
        }
    }

    private static string DetectOrigin(
        string location,
        string publisher,
        string name,
        string packageId,
        string notes,
        PackageSource source)
    {
        // Steam App ID in package id / uninstall notes — strongest signal
        if (TryGetSteamAppId(packageId, notes, out _))
            return "Steam";

        var loc = (location ?? string.Empty).Replace('/', '\\').ToLowerInvariant();
        var blob = $"{loc}\n{notes}\n{packageId}".ToLowerInvariant();

        foreach (var (needle, label, _) in PathHints)
        {
            if (blob.Contains(needle, StringComparison.Ordinal))
                return label;
        }

        // steam.exe in uninstall string
        if (blob.Contains(@"\steam\steam.exe", StringComparison.Ordinal) ||
            blob.Contains("steam.exe", StringComparison.Ordinal) && blob.Contains("steam://", StringComparison.Ordinal))
            return "Steam";

        var pub = publisher.ToLowerInvariant();
        foreach (var (needle, label, _) in PublisherHints)
        {
            if (pub.Contains(needle, StringComparison.Ordinal))
                return label;
        }

        // winget package id heuristics
        if (packageId.Contains("Steam", StringComparison.OrdinalIgnoreCase) ||
            packageId.StartsWith("Valve.", StringComparison.OrdinalIgnoreCase))
            return "Steam";
        if (packageId.Contains("Epic", StringComparison.OrdinalIgnoreCase))
            return "Epic Games";
        if (packageId.Contains("GOG", StringComparison.OrdinalIgnoreCase))
            return "GOG";
        if (packageId.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase))
            return "Ubisoft";
        if (packageId.StartsWith("ElectronicArts.", StringComparison.OrdinalIgnoreCase) ||
            packageId.StartsWith("EA.", StringComparison.OrdinalIgnoreCase))
            return "EA App";
        if (packageId.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) ||
            packageId.Contains("BattleNet", StringComparison.OrdinalIgnoreCase))
            return "Battle.net";
        if (packageId.StartsWith("XP", StringComparison.OrdinalIgnoreCase) && packageId.Length > 10)
            return "Microsoft Store";

        var n = name.ToLowerInvariant();
        if (n.Contains("steam") && (n.Contains("game") || loc.Contains("steam")))
            return "Steam";

        return source switch
        {
            PackageSource.Winget => "winget",
            PackageSource.Chocolatey => "chocolatey",
            PackageSource.GitHub => "GitHub",
            PackageSource.WindowsUpdate => "Windows Update",
            PackageSource.Driver => "Driver",
            PackageSource.MicrosoftStore => "Microsoft Store",
            PackageSource.Wsl => "WSL",
            PackageSource.Office => "Microsoft Office",
            PackageSource.Registry => "Windows",
            _ => "Windows"
        };
    }

    private static string? InferPublisher(string location, string name, string packageId, string? origin)
    {
        if (!string.IsNullOrWhiteSpace(origin))
        {
            // Prefer path-based publisher for the origin label when it's a store
            foreach (var (needle, label, publisher) in PathHints)
            {
                if (label.Equals(origin, StringComparison.OrdinalIgnoreCase))
                {
                    // For Steam, "Valve" is only correct for Valve titles — leave empty so studio stays
                    // if we already had one; when empty, still show Valve only for pure Valve apps.
                    if (label.Equals("Steam", StringComparison.OrdinalIgnoreCase))
                        break;
                    return publisher;
                }
            }

            foreach (var (_, label, publisher) in PublisherHints)
            {
                if (label.Equals(origin, StringComparison.OrdinalIgnoreCase) &&
                    !label.Equals("Steam", StringComparison.OrdinalIgnoreCase))
                    return publisher;
            }
        }

        var loc = location.Replace('/', '\\').ToLowerInvariant();
        foreach (var (needle, _, publisher) in PathHints)
        {
            if (loc.Contains(needle, StringComparison.Ordinal) &&
                !needle.Contains("steam", StringComparison.OrdinalIgnoreCase))
                return publisher;
        }

        // Package id vendor segment: Vendor.Product
        if (packageId.Contains('.') && !packageId.Contains('\\'))
        {
            var vendor = packageId.Split('.')[0];
            if (vendor.Length is > 1 and < 40 && !vendor.Equals("ms", StringComparison.OrdinalIgnoreCase))
                return SplitCamel(vendor);
        }

        return null;
    }

    private static string? TryResolveVersion(ProgramInfo p, string location)
    {
        // Steam by app id (package id / uninstall notes) — most reliable for ARP "Steam App NNNN"
        if (_steamApps is not null && TryGetSteamAppId(p.PackageId, p.Notes, out var appId))
        {
            if (_steamApps.TryGetValue("app:" + appId, out var byId) &&
                !string.IsNullOrWhiteSpace(byId.Version))
                return byId.Version;
        }

        // Steam by install path / name
        if (_steamApps is not null)
        {
            if (!string.IsNullOrWhiteSpace(location))
            {
                var key = NormalizePath(location);
                // Prefer longest path match
                SteamEntry? best = null;
                var bestLen = -1;
                foreach (var kv in _steamApps)
                {
                    if (kv.Key.StartsWith("app:", StringComparison.Ordinal)) continue;
                    if (key.Length == 0) continue;
                    if (key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                        key.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                        kv.Key.Contains(key, StringComparison.OrdinalIgnoreCase))
                    {
                        // Avoid matching short name keys like "g" via path Contains
                        if (!kv.Key.Contains('\\') && kv.Key.Length < 4)
                            continue;
                        var score = kv.Key.Length;
                        if (score > bestLen)
                        {
                            bestLen = score;
                            best = kv.Value;
                        }
                    }
                }

                if (best is { } hit && !string.IsNullOrWhiteSpace(hit.Version))
                    return hit.Version;
            }

            var nameKey = NormalizeName(p.Name);
            if (!string.IsNullOrEmpty(nameKey) &&
                _steamApps.TryGetValue(nameKey, out var steamHit) &&
                !string.IsNullOrWhiteSpace(steamHit.Version))
                return steamHit.Version;
        }

        // Epic manifests
        if (_epicApps is not null)
        {
            if (!string.IsNullOrWhiteSpace(location))
            {
                var key = NormalizePath(location);
                foreach (var kv in _epicApps)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Value.Location) &&
                        (key.Contains(NormalizePath(kv.Value.Location), StringComparison.OrdinalIgnoreCase) ||
                         NormalizePath(kv.Value.Location).Contains(key, StringComparison.OrdinalIgnoreCase)))
                        return kv.Value.Version;
                }
            }

            var nameKey = NormalizeName(p.Name);
            if (_epicApps.TryGetValue(nameKey, out var epicHit))
                return epicHit.Version;
        }

        // File version from install folder EXE
        var fromExe = TryFileVersion(location, p.Name);
        if (!string.IsNullOrWhiteSpace(fromExe))
            return fromExe;

        return null;
    }

    private static string? TryFileVersion(string? installLocation, string appName)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
            return null;

        try
        {
            var exes = Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.AllDirectories).Take(40))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(f =>
                {
                    var fn = Path.GetFileName(f);
                    return !fn.Contains("uninstall", StringComparison.OrdinalIgnoreCase) &&
                           !fn.Contains("crash", StringComparison.OrdinalIgnoreCase) &&
                           !fn.Contains("redist", StringComparison.OrdinalIgnoreCase) &&
                           !fn.Contains("vcredist", StringComparison.OrdinalIgnoreCase) &&
                           !fn.Contains("unitycrash", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(f =>
                {
                    var score = 0;
                    var fn = Path.GetFileNameWithoutExtension(f);
                    if (NormalizeName(fn) == NormalizeName(appName)) score += 100;
                    if (NormalizeName(appName).Contains(NormalizeName(fn))) score += 40;
                    if (NormalizeName(fn).Contains(NormalizeName(appName))) score += 30;
                    try { score += (int)Math.Min(File.GetLastWriteTimeUtc(f).Ticks % 1000, 20); } catch { /* ignore */ }
                    return score;
                })
                .Take(8);

            foreach (var exe in exes)
            {
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(exe);
                    var ver = info.ProductVersion ?? info.FileVersion;
                    if (!string.IsNullOrWhiteSpace(ver) && !VersionText.IsUnknown(ver))
                    {
                        ver = ver.Trim();
                        if (ver.Length > 40)
                            ver = ver[..40];
                        return ver;
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // access denied / etc.
        }

        return null;
    }

    private static void EnsureSteamIndex()
    {
        if (_steamApps is not null) return;
        _steamApps = new Dictionary<string, SteamEntry>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string? steamRoot = null;
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                steamRoot = k?.GetValue("SteamPath") as string
                            ?? k?.GetValue("SteamExe") as string;
            }

            if (string.IsNullOrWhiteSpace(steamRoot))
            {
                using var lm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                               ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
                steamRoot = lm?.GetValue("InstallPath") as string;
            }

            if (!string.IsNullOrWhiteSpace(steamRoot) &&
                steamRoot.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                steamRoot = Path.GetDirectoryName(steamRoot);

            if (string.IsNullOrWhiteSpace(steamRoot) || !Directory.Exists(steamRoot))
                return;

            steamRoot = steamRoot.Replace('/', '\\').TrimEnd('\\');
            var libraryRoots = new List<string> { Path.Combine(steamRoot, "steamapps") };

            foreach (var vdfName in new[] { "libraryfolders.vdf", "config\\libraryfolders.vdf" })
            {
                var vdf = Path.Combine(steamRoot, "steamapps", vdfName.Contains('\\') ? Path.GetFileName(vdfName) : vdfName);
                if (vdfName.StartsWith("config", StringComparison.Ordinal))
                    vdf = Path.Combine(steamRoot, "config", "libraryfolders.vdf");

                if (!File.Exists(vdf))
                    continue;

                try
                {
                    var text = File.ReadAllText(vdf);
                    // Steam VDF may use "path"\t"C:\\..." or "path""C:\\..." (tabs/no space)
                    foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
                    {
                        var path = m.Groups[1].Value.Replace(@"\\", @"\");
                        var apps = Path.Combine(path, "steamapps");
                        if (Directory.Exists(apps))
                            libraryRoots.Add(apps);
                    }
                }
                catch
                {
                    // ignore bad vdf
                }
            }

            foreach (var lib in libraryRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(lib))
                        continue;

                    foreach (var manifest in Directory.EnumerateFiles(lib, "appmanifest_*.acf"))
                    {
                        try
                        {
                            var content = File.ReadAllText(manifest);
                            var name = MatchVdf(content, "name");
                            var buildId = MatchVdf(content, "buildid");
                            var lastUpdated = MatchVdf(content, "LastUpdated");
                            var installDir = MatchVdf(content, "installdir");
                            var appId = MatchVdf(content, "appid");
                            if (string.IsNullOrWhiteSpace(appId))
                            {
                                // appmanifest_105600.acf
                                var fn = Path.GetFileNameWithoutExtension(manifest);
                                if (fn.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase))
                                    appId = fn["appmanifest_".Length..];
                            }

                            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(appId))
                                continue;

                            var version = !string.IsNullOrWhiteSpace(buildId)
                                ? $"build {buildId}"
                                : (!string.IsNullOrWhiteSpace(lastUpdated) ? $"build {lastUpdated}" : "Steam");

                            var installPath = string.Empty;
                            if (!string.IsNullOrWhiteSpace(installDir))
                                installPath = Path.Combine(lib, "common", installDir);

                            var entry = new SteamEntry(version, "Valve", installPath, appId ?? string.Empty);

                            if (!string.IsNullOrWhiteSpace(name))
                                _steamApps[NormalizeName(name)] = entry;

                            if (!string.IsNullOrWhiteSpace(installPath))
                                _steamApps[NormalizePath(installPath)] = entry;

                            if (!string.IsNullOrWhiteSpace(appId))
                                _steamApps["app:" + appId] = entry;
                        }
                        catch
                        {
                            // ignore bad manifest
                        }
                    }
                }
                catch
                {
                    // library drive not visible (e.g. elevated vs mapped drive) — keep other libs
                }
            }
        }
        catch
        {
            _steamApps ??= new Dictionary<string, SteamEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void EnsureEpicIndex()
    {
        if (_epicApps is not null) return;
        _epicApps = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var manifests = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(manifests))
                return;

            foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    var displayName = root.TryGetProperty("DisplayName", out var dn) ? dn.GetString() : null;
                    var version = root.TryGetProperty("AppVersionString", out var av) ? av.GetString() : null;
                    if (string.IsNullOrWhiteSpace(version))
                        version = root.TryGetProperty("AppVersion", out var av2) ? av2.GetString() : null;
                    var location = root.TryGetProperty("InstallLocation", out var il) ? il.GetString() : null;
                    var catalog = root.TryGetProperty("CatalogNamespace", out var cn) ? cn.GetString() : null;

                    if (string.IsNullOrWhiteSpace(displayName))
                        continue;

                    var ver = string.IsNullOrWhiteSpace(version) ? "Epic" : version!;
                    var pub = "Epic Games, Inc.";
                    var key = NormalizeName(displayName);
                    _epicApps[key] = (ver, pub, location ?? "");
                    if (!string.IsNullOrWhiteSpace(location))
                        _epicApps[NormalizePath(location)] = (ver, pub, location);
                    if (!string.IsNullOrWhiteSpace(catalog))
                        _epicApps[NormalizeName(catalog)] = (ver, pub, location ?? "");
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            _epicApps = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool TryGetSteamAppId(string? packageId, string? notes, out string appId)
    {
        appId = string.Empty;
        foreach (var text in new[] { packageId, notes })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var m = SteamAppIdRegex.Match(text);
            if (m.Success)
            {
                appId = m.Groups[1].Value;
                return true;
            }

            m = SteamUninstallRegex.Match(text);
            if (m.Success)
            {
                appId = m.Groups[1].Value;
                return true;
            }
        }

        return false;
    }

    private static string? MatchVdf(string content, string key)
    {
        // Allow tabs/no space: "name"\t\t"Terraria" or "name""Terraria"
        var m = Regex.Match(
            content,
            $"\"{Regex.Escape(key)}\"\\s*\"([^\"]*)\"",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string NormalizePath(string path) =>
        path.Trim().TrimEnd('\\', '/').Replace('/', '\\').ToLowerInvariant();

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var chars = name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
        return new string(chars);
    }

    private static string SplitCamel(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        return CamelSplitRegex().Replace(s, " $1").Trim();
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        return string.Empty;
    }

    /// <summary>Pull a filesystem path out of an uninstall string or icon path.</summary>
    private static string? ExtractPathHint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Quoted path
        var q = Regex.Match(text, "\"([^\"]+\\.(?:exe|bat|cmd|msi))\"");
        if (q.Success)
        {
            var file = q.Groups[1].Value;
            if (File.Exists(file))
                return Path.GetDirectoryName(file);
            // steam.exe → still useful as blob for DetectOrigin; folder may not be the game
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrWhiteSpace(dir))
                return dir;
        }

        // Unquoted path starting with drive letter
        var u = Regex.Match(text, @"([A-Za-z]:\\[^\s\""]+)");
        if (u.Success)
        {
            var path = u.Groups[1].Value.TrimEnd(',', ';');
            if (File.Exists(path))
                return Path.GetDirectoryName(path);
            if (Directory.Exists(path))
                return path;
        }

        return null;
    }

    [GeneratedRegex("([A-Z])")]
    private static partial Regex CamelSplitRegex();
}

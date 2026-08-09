using ApplicationUpdater.Helpers;
using ApplicationUpdater.Models;

namespace ApplicationUpdater.Services;

public sealed class ProgramDetectorService
{
    private readonly ConfigService _config;
    private readonly WingetService _winget;
    private readonly ChocolateyService _chocolatey;
    private readonly WslUpdateService _wsl;
    private readonly OfficeUpdateService _office;
    private readonly LogService _log;
    private readonly UnknownVersionStore _unknownVersions;

    public ProgramDetectorService(
        ConfigService config,
        WingetService winget,
        ChocolateyService chocolatey,
        WslUpdateService wsl,
        OfficeUpdateService office,
        LogService log,
        UnknownVersionStore unknownVersions)
    {
        _config = config;
        _winget = winget;
        _chocolatey = chocolatey;
        _wsl = wsl;
        _office = office;
        _log = log;
        _unknownVersions = unknownVersions;
    }

    public async Task<IReadOnlyList<ProgramInfo>> ScanAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var combined = new List<ProgramInfo>();

        progress?.Report(new ScanProgress { Message = "Scanning with winget...", Percent = 10 });
        if (_config.Config.UpdateSources.Winget.Enabled)
        {
            try
            {
                var wingetPrograms = await _winget.ListInstalledAsync(ct).ConfigureAwait(false);
                combined.AddRange(wingetPrograms);
            }
            catch (Exception ex)
            {
                _log.Error($"winget scan failed: {ex.Message}");
            }
        }

        progress?.Report(new ScanProgress { Message = "Scanning with Chocolatey...", Percent = 40 });
        if (_config.Config.UpdateSources.Chocolatey.Enabled)
        {
            try
            {
                var chocoPrograms = await _chocolatey.ListInstalledAsync(ct).ConfigureAwait(false);
                combined.AddRange(chocoPrograms);
            }
            catch (Exception ex)
            {
                _log.Error($"Chocolatey scan failed: {ex.Message}");
            }
        }

        progress?.Report(new ScanProgress { Message = "Scanning Windows registry...", Percent = 55 });
        try
        {
            var registryPrograms = RegistryScanner.Scan();
            combined.AddRange(registryPrograms);
            _log.Info($"Registry scan returned {registryPrograms.Count} programs.");
        }
        catch (Exception ex)
        {
            _log.Error($"Registry scan failed: {ex.Message}");
        }

        progress?.Report(new ScanProgress { Message = "Scanning Windows Subsystem for Linux…", Percent = 70 });
        try
        {
            var wslItems = await _wsl.ScanAsync(progress, ct).ConfigureAwait(false);
            combined.AddRange(wslItems);
        }
        catch (Exception ex)
        {
            _log.Error($"WSL scan failed: {ex.Message}");
        }

        progress?.Report(new ScanProgress { Message = "Scanning Microsoft Office…", Percent = 80 });
        try
        {
            var officeItems = await _office.ScanAsync(progress, ct).ConfigureAwait(false);
            combined.AddRange(officeItems);
        }
        catch (Exception ex)
        {
            _log.Error($"Office scan failed: {ex.Message}");
        }

        progress?.Report(new ScanProgress { Message = "Detecting stores & versions…", Percent = 90 });
        var merged = Deduplicate(combined);
        ApplyKnownVersionFixes(merged);
        try
        {
            AppOriginEnricher.EnrichAll(merged);
            _log.Info("Enriched origins/versions (Steam, Epic, publishers, EXE versions).");
        }
        catch (Exception ex)
        {
            _log.Warn($"Origin enrichment skipped: {ex.Message}");
        }

        _log.Info($"Scan complete: {merged.Count} unique programs.");
        progress?.Report(new ScanProgress { Message = $"Found {merged.Count} programs", Percent = 100 });
        return merged;
    }

    /// <summary>
    /// Marks programs with available updates using bulk winget/choco queries.
    /// This is the critical fix vs. the Python per-package probe approach.
    /// </summary>
    public async Task<IReadOnlyList<ProgramInfo>> CheckUpdatesAsync(
        IReadOnlyList<ProgramInfo> programs,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Work on clones so UI can rebind cleanly
        var list = programs.Select(p => p.Clone()).ToList();

        // Reset update flags
        foreach (var p in list)
        {
            p.UpdateAvailable = false;
            p.AvailableVersion = string.Empty;
        }

        var upgradeMapById = new Dictionary<string, ProgramInfo>(StringComparer.OrdinalIgnoreCase);
        var upgradeMapByName = new Dictionary<string, ProgramInfo>(StringComparer.OrdinalIgnoreCase);

        progress?.Report(new ScanProgress { Message = "Querying winget for upgrades...", Percent = 20 });
        if (_config.Config.UpdateSources.Winget.Enabled)
        {
            try
            {
                var upgrades = await _winget.ListUpgradesAsync(ct).ConfigureAwait(false);
                foreach (var u in upgrades)
                {
                    if (!string.IsNullOrWhiteSpace(u.PackageId))
                        upgradeMapById[u.PackageId] = u;
                    if (!string.IsNullOrWhiteSpace(u.Name))
                        upgradeMapByName[NormalizeName(u.Name)] = u;
                }
            }
            catch (Exception ex)
            {
                _log.Error($"winget upgrade check failed: {ex.Message}");
            }
        }

        progress?.Report(new ScanProgress { Message = "Querying Chocolatey for outdated packages...", Percent = 45 });
        if (_config.Config.UpdateSources.Chocolatey.Enabled)
        {
            try
            {
                var outdated = await _chocolatey.ListOutdatedAsync(ct).ConfigureAwait(false);
                foreach (var u in outdated)
                {
                    if (!string.IsNullOrWhiteSpace(u.PackageId) && !upgradeMapById.ContainsKey(u.PackageId))
                        upgradeMapById[u.PackageId] = u;
                    var key = NormalizeName(u.Name);
                    if (!upgradeMapByName.ContainsKey(key))
                        upgradeMapByName[key] = u;
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Chocolatey outdated check failed: {ex.Message}");
            }
        }

        // WSL platform + distro packages (flags set on rows; do not put in upgradeMap —
        // available labels like "Latest" / "N package(s)" are not semver and must not
        // go through ShouldOfferUpdate matching).
        progress?.Report(new ScanProgress { Message = "Checking WSL updates...", Percent = 60 });
        try
        {
            var wslUpdates = await _wsl.CheckUpdatesAsync(list, progress, ct).ConfigureAwait(false);
            MergeSpecialSourceRows(list, wslUpdates);
        }
        catch (Exception ex)
        {
            _log.Error($"WSL update check failed: {ex.Message}");
        }

        // Microsoft Office Click-to-Run
        progress?.Report(new ScanProgress { Message = "Checking Microsoft Office updates...", Percent = 70 });
        try
        {
            var officeUpdates = await _office.CheckUpdatesAsync(progress, ct).ConfigureAwait(false);
            MergeSpecialSourceRows(list, officeUpdates);
        }
        catch (Exception ex)
        {
            _log.Error($"Office update check failed: {ex.Message}");
        }

        progress?.Report(new ScanProgress { Message = "Matching upgrades to installed programs...", Percent = 80 });

        // If scan list is empty, still surface upgrades as their own rows
        if (list.Count == 0)
        {
            foreach (var upgrade in upgradeMapById.Values)
            {
                if (_config.IsExcluded(upgrade) || !ShouldOfferUpdate(upgrade, upgrade))
                    continue;
                list.Add(upgrade.Clone());
            }
        }
        else
        {
            foreach (var program in list)
            {
                if (_config.IsExcluded(program))
                    continue;

                // WSL / Office rows are already finalized by their dedicated services.
                if (program.Source is PackageSource.Wsl or PackageSource.Office)
                    continue;

                ProgramInfo? upgrade = null;

                // Exact package ID only — loose Contains matching caused false positives.
                if (!string.IsNullOrWhiteSpace(program.PackageId) &&
                    upgradeMapById.TryGetValue(program.PackageId, out var byId))
                {
                    upgrade = byId;
                }
                else if (TryMatchExactWingetId(program.PackageId, upgradeMapById, out var byExactId))
                {
                    upgrade = byExactId;
                }
                else if (upgradeMapByName.TryGetValue(NormalizeName(program.Name), out var byName))
                {
                    upgrade = byName;
                }

                if (upgrade is null)
                    continue;

                // Already at (or past) this available version, or not actually newer.
                if (!ShouldOfferUpdate(program, upgrade))
                {
                    // Still show a known version when we previously fixed Unknown.
                    if (VersionText.IsUnknown(program.Version) &&
                        TryResolveFixedVersion(program, out var fixedVer))
                        program.Version = fixedVer;
                    else if (VersionText.IsUnknown(program.Version) &&
                             !VersionText.IsUnknown(upgrade.Version))
                        program.Version = upgrade.Version.Trim();

                    program.UpdateAvailable = false;
                    program.AvailableVersion = string.Empty;
                    if (string.IsNullOrWhiteSpace(program.PackageId))
                        program.PackageId = upgrade.PackageId;
                    continue;
                }

                program.UpdateAvailable = true;
                program.AvailableVersion = upgrade.AvailableVersion;
                if (VersionText.IsUnknown(program.Version) &&
                    !VersionText.IsUnknown(upgrade.Version) &&
                    !string.Equals(upgrade.Version, upgrade.AvailableVersion, StringComparison.OrdinalIgnoreCase))
                {
                    // Prefer winget's reported installed column when it has a real value.
                    program.Version = upgrade.Version;
                }

                if (string.IsNullOrWhiteSpace(program.PackageId) ||
                    program.PackageId.StartsWith('{') ||
                    (program.PackageId.Contains('\\') && upgrade.PackageId.Contains('.')))
                    program.PackageId = upgrade.PackageId;
                // Prefer package-manager source for install
                if (program.Source is PackageSource.Registry or PackageSource.Unknown)
                    program.Source = upgrade.Source;
            }

            // Add upgrades that didn't match any installed row (still useful / updatable)
            var existingIds = new HashSet<string>(
                list.Where(p => !string.IsNullOrWhiteSpace(p.PackageId)).Select(p => p.PackageId),
                StringComparer.OrdinalIgnoreCase);
            var existingNames = new HashSet<string>(
                list.Select(p => NormalizeName(p.Name)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var upgrade in upgradeMapById.Values)
            {
                if (existingIds.Contains(upgrade.PackageId))
                    continue;

                // Name already on the list: mark only if strictly newer
                var nameKey = NormalizeName(upgrade.Name);
                if (existingNames.Contains(nameKey))
                {
                    var row = list.FirstOrDefault(p => NormalizeName(p.Name) == nameKey);
                    if (row is not null && !row.UpdateAvailable && !_config.IsExcluded(row) &&
                        ShouldOfferUpdate(row, upgrade))
                    {
                        row.UpdateAvailable = true;
                        row.AvailableVersion = upgrade.AvailableVersion;
                        if (string.IsNullOrWhiteSpace(row.PackageId) ||
                            row.PackageId.StartsWith('{') ||
                            row.PackageId.Contains('\\'))
                            row.PackageId = upgrade.PackageId;
                        if (row.Source is PackageSource.Registry or PackageSource.Unknown)
                            row.Source = upgrade.Source;
                    }
                    continue;
                }

                if (_config.IsExcluded(upgrade))
                    continue;
                if (!ShouldOfferUpdate(upgrade, upgrade))
                    continue;
                list.Add(upgrade.Clone());
            }
        }

        // Final pass: drop any remaining false positives (equal versions / remembered installs)
        foreach (var program in list)
        {
            if (!program.UpdateAvailable)
                continue;

            if (_config.IsExcluded(program))
            {
                program.UpdateAvailable = false;
                program.AvailableVersion = string.Empty;
                continue;
            }

            // WSL/Office use non-semver available labels ("Latest", "N package(s)", "Channel latest")
            if (program.Source is PackageSource.Wsl or PackageSource.Office)
                continue;

            if (VersionText.IsUnknown(program.AvailableVersion) ||
                VersionComparer.IsNewer(GetEffectiveVersion(program), program.AvailableVersion) != true)
            {
                // Equal / not newer — clear the update flag
                if (VersionText.IsUnknown(program.Version) &&
                    TryResolveFixedVersion(program, out var fixedVer))
                    program.Version = fixedVer;
                program.UpdateAvailable = false;
                program.AvailableVersion = string.Empty;
            }
        }

        // Prefer winget rows over Chocolatey duplicates for the same product name
        list = PreferWingetOverChocolateyDuplicates(list);

        var count = list.Count(p => p.UpdateAvailable);
        _log.Info($"Update check complete: {count} update(s) available.");
        progress?.Report(new ScanProgress { Message = $"{count} update(s) available", Percent = 100 });
        return list;
    }

    /// <summary>
    /// Insert or refresh rows for non-winget sources (WSL, Office) that use stable package ids.
    /// </summary>
    private static void MergeSpecialSourceRows(List<ProgramInfo> list, IReadOnlyList<ProgramInfo> special)
    {
        foreach (var item in special)
        {
            if (string.IsNullOrWhiteSpace(item.PackageId))
            {
                list.Add(item.Clone());
                continue;
            }

            var existing = list.FirstOrDefault(p =>
                string.Equals(p.PackageId, item.PackageId, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                list.Add(item.Clone());
                continue;
            }

            existing.Source = item.Source;
            existing.Origin = string.IsNullOrWhiteSpace(item.Origin) ? existing.Origin : item.Origin;
            existing.Publisher = string.IsNullOrWhiteSpace(item.Publisher) ? existing.Publisher : item.Publisher;
            existing.Notes = item.Notes ?? existing.Notes;
            if (!VersionText.IsUnknown(item.Version))
                existing.Version = item.Version;
            if (item.UpdateAvailable)
            {
                existing.UpdateAvailable = true;
                existing.AvailableVersion = item.AvailableVersion;
            }
            else if (existing.Source is PackageSource.Wsl or PackageSource.Office)
            {
                // Keep special-source rows honest when their own check clears the flag
                existing.UpdateAvailable = false;
                existing.AvailableVersion = string.Empty;
            }
        }
    }

    private static List<ProgramInfo> Deduplicate(List<ProgramInfo> programs)
    {
        // Prefer store/wsl/office-specific sources, then winget > chocolatey > registry
        int Priority(PackageSource s) => s switch
        {
            PackageSource.MicrosoftStore => 1,
            PackageSource.Wsl => 1,
            PackageSource.Office => 1,
            PackageSource.Winget => 2,
            PackageSource.Chocolatey => 3,
            PackageSource.Registry => 4,
            _ => 5
        };

        var ordered = programs
            .OrderBy(p => Priority(p.Source))
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byId = new Dictionary<string, ProgramInfo>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, ProgramInfo>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ProgramInfo>();

        foreach (var program in ordered)
        {
            if (!string.IsNullOrWhiteSpace(program.PackageId))
            {
                if (byId.ContainsKey(program.PackageId))
                    continue;
                byId[program.PackageId] = program;
            }

            var nameKey = NormalizeName(program.Name);
            if (byName.TryGetValue(nameKey, out var existing))
            {
                // Merge useful fields from lower-priority rows onto the kept entry
                if (string.IsNullOrWhiteSpace(existing.PackageId) && !string.IsNullOrWhiteSpace(program.PackageId))
                    existing.PackageId = program.PackageId;
                // Prefer Steam App / ARP package ids that carry app numbers for origin enrich
                else if (!string.IsNullOrWhiteSpace(program.PackageId) &&
                         program.PackageId.Contains("Steam App", StringComparison.OrdinalIgnoreCase) &&
                         !existing.PackageId.Contains("Steam App", StringComparison.OrdinalIgnoreCase))
                    existing.PackageId = program.PackageId;

                if (string.IsNullOrWhiteSpace(existing.Publisher) && !string.IsNullOrWhiteSpace(program.Publisher))
                    existing.Publisher = program.Publisher;
                if (string.IsNullOrWhiteSpace(existing.InstallLocation) && !string.IsNullOrWhiteSpace(program.InstallLocation))
                    existing.InstallLocation = program.InstallLocation;
                if (VersionText.IsUnknown(existing.Version) && !VersionText.IsUnknown(program.Version))
                    existing.Version = program.Version;
                if ((string.IsNullOrWhiteSpace(existing.Origin) ||
                     existing.Origin is "winget" or "registry" or "Windows" or "unknown") &&
                    !string.IsNullOrWhiteSpace(program.Origin) &&
                    program.Origin is not ("winget" or "registry" or "Windows" or "unknown"))
                    existing.Origin = program.Origin;
                if (string.IsNullOrWhiteSpace(existing.Notes) && !string.IsNullOrWhiteSpace(program.Notes))
                    existing.Notes = program.Notes;
                continue;
            }

            byName[nameKey] = program;
            result.Add(program);
        }

        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var chars = name.ToLowerInvariant()
            .Where(ch => char.IsLetterOrDigit(ch))
            .ToArray();
        return new string(chars);
    }

    /// <summary>
    /// Match registry/ARP package ids to winget ids only when they are clearly the same
    /// (exact, or ARP path ending with the winget id). No broad Contains matching.
    /// </summary>
    private static bool TryMatchExactWingetId(
        string? packageId,
        Dictionary<string, ProgramInfo> upgradeMapById,
        out ProgramInfo? upgrade)
    {
        upgrade = null;
        if (string.IsNullOrWhiteSpace(packageId))
            return false;

        if (upgradeMapById.TryGetValue(packageId, out upgrade))
            return true;

        // ARP\Machine\X64\Vendor.Product → Vendor.Product
        var leaf = packageId;
        var slash = packageId.LastIndexOf('\\');
        if (slash >= 0 && slash < packageId.Length - 1)
            leaf = packageId[(slash + 1)..].Trim();

        if (!string.IsNullOrWhiteSpace(leaf) &&
            upgradeMapById.TryGetValue(leaf, out upgrade))
            return true;

        // Only accept upgrade ids that appear as a full trailing segment (not substring of another product)
        foreach (var kv in upgradeMapById)
        {
            if (kv.Key.Length < 3 || !kv.Key.Contains('.'))
                continue;
            if (packageId.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))
            {
                upgrade = kv.Value;
                return true;
            }

            if (packageId.EndsWith("\\" + kv.Key, StringComparison.OrdinalIgnoreCase))
            {
                upgrade = kv.Value;
                return true;
            }

            if (packageId.EndsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
            {
                if (packageId.Length == kv.Key.Length)
                {
                    upgrade = kv.Value;
                    return true;
                }

                var sepIndex = packageId.Length - kv.Key.Length - 1;
                if (sepIndex >= 0 && packageId[sepIndex] is '\\' or '/' or ' ')
                {
                    upgrade = kv.Value;
                    return true;
                }
            }
        }

        return false;
    }

    private void ApplyKnownVersionFixes(List<ProgramInfo> programs)
    {
        foreach (var program in programs)
        {
            if (!VersionText.IsUnknown(program.Version))
                continue;

            if (TryResolveFixedVersion(program, out var fixedVersion))
                program.Version = fixedVersion;
        }
    }

    /// <summary>
    /// True only when available is strictly newer than the effective installed version.
    /// Suppresses re-offers after a successful install (remembered version) and equal versions.
    /// </summary>
    private bool ShouldOfferUpdate(ProgramInfo program, ProgramInfo upgrade)
    {
        if (upgrade is null)
            return false;

        var available = upgrade.AvailableVersion;
        if (VersionText.IsUnknown(available))
            return false;

        // winget row: installed column already equals available → not an update
        if (!VersionText.IsUnknown(upgrade.Version) &&
            string.Equals(upgrade.Version.Trim(), available.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        // Previously installed this (or newer) available version successfully
        if (IsVersionSatisfied(program, upgrade, available))
            return false;

        var current = GetEffectiveVersion(program, upgrade);

        // Still unknown installed (include-unknown): offer only when enabled and not already fixed
        if (VersionText.IsUnknown(current))
            return _config.Config.UpdateBehavior.IncludeUnknown;

        // Strict: available must be greater than current
        return VersionComparer.IsNewer(current, available) == true;
    }

    private bool IsVersionSatisfied(ProgramInfo program, ProgramInfo upgrade, string available)
    {
        if (!string.IsNullOrWhiteSpace(program.PackageId) &&
            _unknownVersions.IsSatisfied(program.PackageId, available))
            return true;

        if (!string.IsNullOrWhiteSpace(upgrade.PackageId) &&
            _unknownVersions.IsSatisfied(upgrade.PackageId, available))
            return true;

        if (!string.IsNullOrWhiteSpace(program.Name) &&
            _unknownVersions.IsSatisfied(program.Name, available))
            return true;

        if (!string.IsNullOrWhiteSpace(upgrade.Name) &&
            _unknownVersions.IsSatisfied(upgrade.Name, available))
            return true;

        return false;
    }

    /// <summary>Best known installed version: ARP/scan value, else remembered fix, else winget installed column.</summary>
    private string GetEffectiveVersion(ProgramInfo program, ProgramInfo? upgrade = null)
    {
        if (!VersionText.IsUnknown(program.Version))
            return program.Version;

        if (TryResolveFixedVersion(program, out var fixedVer))
            return fixedVer;

        if (upgrade is not null && !VersionText.IsUnknown(upgrade.Version))
            return upgrade.Version;

        return program.Version;
    }

    private bool TryResolveFixedVersion(ProgramInfo program, out string version)
    {
        if (!string.IsNullOrWhiteSpace(program.PackageId) &&
            _unknownVersions.TryGetInstalledVersion(program.PackageId, out version))
            return true;

        if (!string.IsNullOrWhiteSpace(program.Name) &&
            _unknownVersions.TryGetInstalledVersion(program.Name, out version))
            return true;

        version = string.Empty;
        return false;
    }

    private static List<ProgramInfo> PreferWingetOverChocolateyDuplicates(List<ProgramInfo> list)
    {
        // If both winget and Chocolatey offer the same product with an update, keep winget only.
        var wingetUpdateNames = new HashSet<string>(
            list.Where(p => p.UpdateAvailable && p.Source == PackageSource.Winget)
                .Select(p => NormalizeName(p.Name)),
            StringComparer.OrdinalIgnoreCase);

        if (wingetUpdateNames.Count == 0)
            return list;

        foreach (var program in list)
        {
            if (!program.UpdateAvailable || program.Source != PackageSource.Chocolatey)
                continue;

            if (wingetUpdateNames.Contains(NormalizeName(program.Name)))
            {
                program.UpdateAvailable = false;
                program.AvailableVersion = string.Empty;
            }
        }

        return list;
    }
}

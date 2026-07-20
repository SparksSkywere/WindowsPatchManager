using ApplicationUpdater.Models;

namespace ApplicationUpdater.Services;

public sealed class ProgramDetectorService
{
    private readonly ConfigService _config;
    private readonly WingetService _winget;
    private readonly ChocolateyService _chocolatey;
    private readonly LogService _log;

    public ProgramDetectorService(
        ConfigService config,
        WingetService winget,
        ChocolateyService chocolatey,
        LogService log)
    {
        _config = config;
        _winget = winget;
        _chocolatey = chocolatey;
        _log = log;
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

        progress?.Report(new ScanProgress { Message = "Scanning Windows registry...", Percent = 70 });
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

        progress?.Report(new ScanProgress { Message = "Merging results...", Percent = 90 });
        var merged = Deduplicate(combined);
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

        progress?.Report(new ScanProgress { Message = "Querying Chocolatey for outdated packages...", Percent = 55 });
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

        progress?.Report(new ScanProgress { Message = "Matching upgrades to installed programs...", Percent = 80 });

        // If scan list is empty, still surface upgrades as their own rows
        if (list.Count == 0)
        {
            list.AddRange(upgradeMapById.Values.Select(u => u.Clone()));
        }
        else
        {
            foreach (var program in list)
            {
                if (_config.IsExcluded(program))
                    continue;

                ProgramInfo? upgrade = null;

                if (!string.IsNullOrWhiteSpace(program.PackageId) &&
                    upgradeMapById.TryGetValue(program.PackageId, out var byId))
                {
                    upgrade = byId;
                }
                else if (upgradeMapByName.TryGetValue(NormalizeName(program.Name), out var byName))
                {
                    upgrade = byName;
                }

                if (upgrade is null)
                    continue;

                program.UpdateAvailable = true;
                program.AvailableVersion = upgrade.AvailableVersion;
                if (string.IsNullOrWhiteSpace(program.PackageId))
                    program.PackageId = upgrade.PackageId;
                // Prefer package-manager source for install
                if (program.Source is PackageSource.Registry or PackageSource.Unknown)
                    program.Source = upgrade.Source;
            }

            // Add upgrades that didn't match any installed row (still useful / updatable)
            var existingIds = new HashSet<string>(
                list.Where(p => !string.IsNullOrWhiteSpace(p.PackageId)).Select(p => p.PackageId),
                StringComparer.OrdinalIgnoreCase);

            foreach (var upgrade in upgradeMapById.Values)
            {
                if (existingIds.Contains(upgrade.PackageId))
                    continue;
                if (_config.IsExcluded(upgrade))
                    continue;
                list.Add(upgrade.Clone());
            }
        }

        // Filter exclusions from final update flags
        foreach (var program in list)
        {
            if (program.UpdateAvailable && _config.IsExcluded(program))
            {
                program.UpdateAvailable = false;
                program.AvailableVersion = string.Empty;
            }
        }

        var count = list.Count(p => p.UpdateAvailable);
        _log.Info($"Update check complete: {count} update(s) available.");
        progress?.Report(new ScanProgress { Message = $"{count} update(s) available", Percent = 100 });
        return list;
    }

    private static List<ProgramInfo> Deduplicate(List<ProgramInfo> programs)
    {
        // Prefer winget > chocolatey > registry
        int Priority(PackageSource s) => s switch
        {
            PackageSource.Winget => 1,
            PackageSource.Chocolatey => 2,
            PackageSource.Registry => 3,
            _ => 4
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
                // Merge package id onto registry entry if we already kept a better source under different keying
                if (string.IsNullOrWhiteSpace(existing.PackageId) && !string.IsNullOrWhiteSpace(program.PackageId))
                    existing.PackageId = program.PackageId;
                if (string.IsNullOrWhiteSpace(existing.Publisher) && !string.IsNullOrWhiteSpace(program.Publisher))
                    existing.Publisher = program.Publisher;
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
}

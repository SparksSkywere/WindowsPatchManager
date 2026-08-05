using System.IO;
using System.Text;
using ApplicationUpdater.Helpers;
using ApplicationUpdater.Models;

namespace ApplicationUpdater.Services;

public sealed class UpdateInstallerService
{
    private readonly ConfigService _config;
    private readonly WingetService _winget;
    private readonly ChocolateyService _chocolatey;
    private readonly LogService _log;
    private readonly UnknownVersionStore _unknownVersions;

    public UpdateInstallerService(
        ConfigService config,
        WingetService winget,
        ChocolateyService chocolatey,
        LogService log,
        UnknownVersionStore unknownVersions)
    {
        _config = config;
        _winget = winget;
        _chocolatey = chocolatey;
        _log = log;
        _unknownVersions = unknownVersions;
    }

    public async Task<IReadOnlyDictionary<string, UpdateResult>> InstallUpdatesAsync(
        IReadOnlyList<ProgramInfo> programs,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        var toUpdate = programs
            .Where(p => p.UpdateAvailable && !string.IsNullOrWhiteSpace(p.PackageId))
            .Where(p => !_config.IsExcluded(p))
            .ToList();

        // Prefer winget over Chocolatey when the same product appears from both sources
        // (logs showed Visual Studio Build Tools racing and one failing).
        toUpdate = DeduplicateByProduct(toUpdate);

        if (toUpdate.Count == 0)
        {
            _log.Warn("No updatable programs selected (missing package IDs or none available).");
            return new Dictionary<string, UpdateResult>();
        }

        if (_config.Config.General.CreateBackups)
            CreateBackup(toUpdate);

        var suppressShortcuts = !_config.Config.General.AllowInstallerDesktopShortcuts;
        var desktopBefore = suppressShortcuts
            ? DesktopShortcutHelper.SnapshotDesktopShortcuts()
            : null;

        var results = new Dictionary<string, UpdateResult>(StringComparer.OrdinalIgnoreCase);
        var maxConcurrent = Math.Clamp(_config.Config.UpdateBehavior.MaxConcurrentUpdates, 1, 4);
        var completed = 0;
        var total = toUpdate.Count;
        var gate = new SemaphoreSlim(maxConcurrent);
        var lockObj = new object();

        _log.Info($"Installing {total} update(s) with concurrency {maxConcurrent}...");
        if (suppressShortcuts)
            _log.Info("Installer desktop shortcuts will be removed after the run (Options → allow if you want them kept).");

        var tasks = toUpdate.Select(async program =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                var wasUnknown = VersionText.IsUnknown(program.Version);
                var targetVersion = program.AvailableVersion;
                var result = await InstallSingleAsync(program, ct).ConfigureAwait(false);

                if (result.Success)
                {
                    // Always replace Unknown (or keep available) after a successful update.
                    if (wasUnknown || VersionText.IsUnknown(program.Version))
                    {
                        if (!string.IsNullOrWhiteSpace(targetVersion) && !VersionText.IsUnknown(targetVersion))
                            program.Version = targetVersion.Trim();
                    }
                    else if (!string.IsNullOrWhiteSpace(targetVersion) && !VersionText.IsUnknown(targetVersion))
                    {
                        program.Version = targetVersion.Trim();
                    }

                    program.UpdateAvailable = false;

                    if (wasUnknown && !VersionText.IsUnknown(program.Version))
                    {
                        RememberUnknownFix(program);
                        _log.Info($"Replaced unknown version for {program.Name} with {program.Version}.");
                    }
                    else if (!VersionText.IsUnknown(program.Version))
                    {
                        // Still record so a later ARP "Unknown" does not re-offer the same version.
                        RememberUnknownFix(program);
                    }
                }

                lock (lockObj)
                {
                    results[program.DisplayKey] = result;
                    completed++;
                    progress?.Report(new UpdateProgress
                    {
                        ProgramName = program.Name,
                        Success = result.Success,
                        Completed = completed,
                        Total = total,
                        Message = result.Success
                            ? (string.IsNullOrWhiteSpace(program.Version) ? "OK" : program.Version)
                            : result.ErrorMessage
                    });
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (suppressShortcuts && desktopBefore is not null)
        {
            var removed = DesktopShortcutHelper.RemoveNewDesktopShortcuts(desktopBefore);
            if (removed.Count > 0)
                _log.Info($"Removed {removed.Count} new desktop shortcut(s): {string.Join(", ", removed)}");
        }

        var ok = results.Values.Count(r => r.Success);
        _log.Info($"Update run finished: {ok}/{results.Count} succeeded.");
        return results;
    }

    public Task<UpdateResult> InstallSingleAsync(ProgramInfo program, CancellationToken ct = default)
    {
        return program.Source switch
        {
            PackageSource.Chocolatey => _chocolatey.UpgradeAsync(program, ct),
            _ => _winget.UpgradeAsync(program, ct) // winget default, including registry matched to winget id
        };
    }

    private void RememberUnknownFix(ProgramInfo program)
    {
        if (VersionText.IsUnknown(program.Version))
            return;

        if (!string.IsNullOrWhiteSpace(program.PackageId))
            _unknownVersions.Remember(program.PackageId, program.Version);
        if (!string.IsNullOrWhiteSpace(program.Name))
            _unknownVersions.Remember(program.Name, program.Version);
    }

    /// <summary>
    /// Keep one row per product name, preferring winget over Chocolatey/registry.
    /// </summary>
    private static List<ProgramInfo> DeduplicateByProduct(List<ProgramInfo> programs)
    {
        static int Priority(PackageSource s) => s switch
        {
            PackageSource.Winget => 0,
            PackageSource.Chocolatey => 1,
            PackageSource.Registry => 2,
            _ => 3
        };

        static string Normalize(string name)
        {
            var chars = name.ToLowerInvariant()
                .Where(ch => char.IsLetterOrDigit(ch))
                .ToArray();
            return new string(chars);
        }

        var ordered = programs
            .OrderBy(p => Priority(p.Source))
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ProgramInfo>();

        foreach (var p in ordered)
        {
            if (!string.IsNullOrWhiteSpace(p.PackageId) && !seenIds.Add(p.PackageId))
                continue;

            var nameKey = Normalize(p.Name);
            if (!string.IsNullOrEmpty(nameKey) && !seenNames.Add(nameKey))
                continue;

            result.Add(p);
        }

        return result;
    }

    private void CreateBackup(IReadOnlyList<ProgramInfo> programs)
    {
        try
        {
            var backupRoot = _config.Config.General.BackupDirectory;
            if (!Path.IsPathRooted(backupRoot))
                backupRoot = Path.Combine(_config.AppDataDirectory, backupRoot);

            Directory.CreateDirectory(backupRoot);
            var file = Path.Combine(backupRoot, $"pre_update_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"Backup created: {DateTime.Now:O}");
            sb.AppendLine("Programs to be updated:");
            sb.AppendLine();

            foreach (var p in programs)
            {
                sb.AppendLine($"Name: {p.Name}");
                sb.AppendLine($"Current Version: {p.Version}");
                sb.AppendLine($"Available Version: {p.AvailableVersion}");
                sb.AppendLine($"Source: {p.SourceDisplay}");
                sb.AppendLine($"Package ID: {p.PackageId}");
                sb.AppendLine(new string('-', 50));
            }

            File.WriteAllText(file, sb.ToString());
            _log.Info($"Backup manifest written to {file}");
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not create backup manifest: {ex.Message}");
        }
    }
}

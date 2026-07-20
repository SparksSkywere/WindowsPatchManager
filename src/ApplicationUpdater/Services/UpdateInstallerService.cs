using System.IO;
using System.Text;
using ApplicationUpdater.Models;

namespace ApplicationUpdater.Services;

public sealed class UpdateInstallerService
{
    private readonly ConfigService _config;
    private readonly WingetService _winget;
    private readonly ChocolateyService _chocolatey;
    private readonly LogService _log;

    public UpdateInstallerService(
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

    public async Task<IReadOnlyDictionary<string, UpdateResult>> InstallUpdatesAsync(
        IReadOnlyList<ProgramInfo> programs,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        var toUpdate = programs
            .Where(p => p.UpdateAvailable && !string.IsNullOrWhiteSpace(p.PackageId))
            .Where(p => !_config.IsExcluded(p))
            .ToList();

        if (toUpdate.Count == 0)
        {
            _log.Warn("No updatable programs selected (missing package IDs or none available).");
            return new Dictionary<string, UpdateResult>();
        }

        if (_config.Config.General.CreateBackups)
            CreateBackup(toUpdate);

        var results = new Dictionary<string, UpdateResult>(StringComparer.OrdinalIgnoreCase);
        var maxConcurrent = Math.Clamp(_config.Config.UpdateBehavior.MaxConcurrentUpdates, 1, 4);
        var completed = 0;
        var total = toUpdate.Count;
        var gate = new SemaphoreSlim(maxConcurrent);
        var lockObj = new object();

        _log.Info($"Installing {total} update(s) with concurrency {maxConcurrent}...");

        var tasks = toUpdate.Select(async program =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                var result = await InstallSingleAsync(program, ct).ConfigureAwait(false);
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
                        Message = result.Success ? "OK" : result.ErrorMessage
                    });
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

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

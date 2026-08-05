using System.IO;
using System.Text.Json;
using ApplicationUpdater.Models;

namespace ApplicationUpdater.Services;

/// <summary>
/// Facade used by UI and CLI.
/// </summary>
public sealed class PatchManagerService
{
    private readonly ConfigService _config;
    private readonly ProgramDetectorService _detector;
    private readonly UpdateInstallerService _installer;
    private readonly WingetService _winget;
    private readonly GitHubUpdateService _github;
    private readonly WindowsUpdateService _windowsUpdate;
    private readonly LogService _log;

    public IReadOnlyList<ProgramInfo> Programs { get; private set; } = [];
    public IReadOnlyList<ProgramInfo> Drivers { get; private set; } = [];
    public IReadOnlyList<ProgramInfo> WindowsUpdates { get; private set; } = [];

    public PatchManagerService(
        ConfigService config,
        ProgramDetectorService detector,
        UpdateInstallerService installer,
        WingetService winget,
        GitHubUpdateService github,
        WindowsUpdateService windowsUpdate,
        LogService log)
    {
        _config = config;
        _detector = detector;
        _installer = installer;
        _winget = winget;
        _github = github;
        _windowsUpdate = windowsUpdate;
        _log = log;
    }

    public ConfigService Config => _config;
    public LogService Log => _log;

    public async Task<IReadOnlyList<ProgramInfo>> ScanAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var list = (await _detector.ScanAsync(progress, ct).ConfigureAwait(false)).ToList();

        // Merge GitHub-tracked projects into the programs list
        var gh = await _github.CheckTrackedAsync(progress, ct).ConfigureAwait(false);
        foreach (var g in gh)
        {
            var existing = list.FirstOrDefault(p =>
                string.Equals(p.PackageId, g.PackageId, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                list.Add(g);
            else
            {
                existing.Source = PackageSource.GitHub;
                existing.AvailableVersion = g.AvailableVersion;
                existing.UpdateAvailable = g.UpdateAvailable;
                existing.DownloadUrl = g.DownloadUrl;
                existing.GitHubOwner = g.GitHubOwner;
                existing.GitHubRepo = g.GitHubRepo;
                existing.LastUpdated ??= g.LastUpdated;
            }
        }

        Programs = list;
        return Programs;
    }

    public async Task<IReadOnlyList<ProgramInfo>> CheckUpdatesAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (Programs.Count == 0)
            await ScanAsync(progress, ct).ConfigureAwait(false);

        Programs = await _detector.CheckUpdatesAsync(Programs, progress, ct).ConfigureAwait(false);

        // Refresh GitHub release metadata
        var gh = await _github.CheckTrackedAsync(progress, ct).ConfigureAwait(false);
        var merged = Programs.ToList();
        foreach (var g in gh)
        {
            var existing = merged.FirstOrDefault(p =>
                string.Equals(p.PackageId, g.PackageId, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                merged.Add(g);
            else
            {
                existing.AvailableVersion = g.AvailableVersion;
                existing.UpdateAvailable = g.UpdateAvailable || existing.UpdateAvailable;
                existing.DownloadUrl = g.DownloadUrl ?? existing.DownloadUrl;
                existing.Source = existing.Source == PackageSource.Unknown ? PackageSource.GitHub : existing.Source;
                if (existing.Source != PackageSource.GitHub && g.UpdateAvailable)
                {
                    // Keep winget path if present; still show GH note
                    existing.Notes = $"GitHub latest: {g.AvailableVersion}";
                }
            }
        }

        Programs = merged;
        return Programs.Where(p => p.UpdateAvailable).ToList();
    }

    public async Task<IReadOnlyList<ProgramInfo>> ScanDriversAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        Drivers = await _windowsUpdate.SearchDriverUpdatesAsync(progress, ct).ConfigureAwait(false);
        return Drivers;
    }

    public async Task<IReadOnlyList<ProgramInfo>> ScanWindowsUpdatesAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        WindowsUpdates = await _windowsUpdate.SearchSoftwareUpdatesAsync(progress, ct).ConfigureAwait(false);
        return WindowsUpdates;
    }

    public Task<IReadOnlyList<ProgramInfo>> GetUpdateHistoryAsync(
        bool driversOnly,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
        => _windowsUpdate.GetInstallHistoryAsync(driversOnly, progress, ct);

    public async Task<IReadOnlyList<ProgramInfo>> SearchWingetAsync(string query, CancellationToken ct = default)
    {
        var list = (await _winget.SearchAsync(query, ct).ConfigureAwait(false)).ToList();
        foreach (var p in list)
            p.Origin = string.IsNullOrWhiteSpace(p.Origin) ? "winget" : p.Origin;
        return list;
    }

    public async Task<IReadOnlyList<ProgramInfo>> ListWingetInstalledAsync(CancellationToken ct = default)
    {
        var list = (await _winget.ListInstalledAsync(ct).ConfigureAwait(false)).ToList();
        try { Helpers.AppOriginEnricher.EnrichAll(list); } catch { /* non-fatal */ }
        return list;
    }

    public Task<UpdateResult> InstallPackageAsync(ProgramInfo program, CancellationToken ct = default)
        => _winget.InstallPackageAsync(program, null, ct);

    public Task<UpdateResult> UninstallPackageAsync(ProgramInfo program, CancellationToken ct = default)
        => _winget.UninstallPackageAsync(program, ct);

    public async Task<IReadOnlyDictionary<string, UpdateResult>> UpdateAsync(
        IReadOnlyList<ProgramInfo> programs,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, UpdateResult>(StringComparer.OrdinalIgnoreCase);
        var list = programs.ToList();
        var total = list.Count;
        var completed = 0;

        foreach (var program in list)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new UpdateProgress
            {
                ProgramName = program.Name,
                ProgramKey = program.DisplayKey,
                Completed = completed,
                Total = total,
                ItemPercent = 0,
                IsStarting = true,
                OverallPercent = total == 0 ? 0 : (int)(completed * 100.0 / total),
                Message = "Starting…"
            });

            UpdateResult result;
            if (program.Source is PackageSource.GitHub)
                result = await _github.InstallAsync(program, progress, completed, total, ct).ConfigureAwait(false);
            else if (program.Source is PackageSource.WindowsUpdate or PackageSource.Driver)
                result = await _windowsUpdate.InstallAsync(program, progress, completed, total, ct).ConfigureAwait(false);
            else
            {
                var map = await _installer.InstallUpdatesAsync([program], new Progress<UpdateProgress>(p =>
                {
                    progress?.Report(new UpdateProgress
                    {
                        ProgramName = p.ProgramName,
                        ProgramKey = program.DisplayKey,
                        Success = p.Success,
                        Completed = completed + (p.Completed > 0 ? 1 : 0),
                        Total = total,
                        ItemPercent = p.Completed > 0 ? 100 : 50,
                        OverallPercent = total == 0 ? 0 : (int)((completed + (p.Completed > 0 ? 1 : 0.5)) * 100.0 / total),
                        Message = p.Message
                    });
                }), ct).ConfigureAwait(false);

                result = map.Values.FirstOrDefault() ?? new UpdateResult
                {
                    Program = program,
                    Success = false,
                    ErrorMessage = "No result",
                    StartTime = DateTime.Now,
                    EndTime = DateTime.Now
                };

                if (result.Success)
                    program.LastUpdated = DateTime.Now;
            }

            results[program.DisplayKey] = result;
            completed++;
        }

        return results;
    }

    public Task<UpdateResult> UpdateOneAsync(ProgramInfo program, CancellationToken ct = default)
        => program.Source switch
        {
            PackageSource.GitHub => _github.InstallAsync(program, null, 0, 1, ct),
            PackageSource.WindowsUpdate or PackageSource.Driver =>
                _windowsUpdate.InstallAsync(program, null, 0, 1, ct),
            _ => _installer.InstallSingleAsync(program, ct)
        };

    public void Export(string path)
    {
        var data = Programs.Concat(Drivers).Concat(WindowsUpdates).Select(p => new
        {
            p.Name,
            p.Version,
            p.AvailableVersion,
            p.UpdateAvailable,
            p.LastUpdated,
            Source = p.SourceDisplay,
            Category = p.Category.ToString(),
            p.PackageId,
            p.Publisher
        });

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        _log.Success($"Exported list to {path}");
    }
}

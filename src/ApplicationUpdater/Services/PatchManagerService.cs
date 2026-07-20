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
    private readonly LogService _log;

    public IReadOnlyList<ProgramInfo> Programs { get; private set; } = [];

    public PatchManagerService(
        ConfigService config,
        ProgramDetectorService detector,
        UpdateInstallerService installer,
        LogService log)
    {
        _config = config;
        _detector = detector;
        _installer = installer;
        _log = log;
    }

    public ConfigService Config => _config;
    public LogService Log => _log;

    public async Task<IReadOnlyList<ProgramInfo>> ScanAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        Programs = await _detector.ScanAsync(progress, ct).ConfigureAwait(false);
        return Programs;
    }

    public async Task<IReadOnlyList<ProgramInfo>> CheckUpdatesAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (Programs.Count == 0)
            await ScanAsync(progress, ct).ConfigureAwait(false);

        Programs = await _detector.CheckUpdatesAsync(Programs, progress, ct).ConfigureAwait(false);
        return Programs.Where(p => p.UpdateAvailable).ToList();
    }

    public Task<IReadOnlyDictionary<string, UpdateResult>> UpdateAsync(
        IReadOnlyList<ProgramInfo> programs,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
        => _installer.InstallUpdatesAsync(programs, progress, ct);

    public Task<UpdateResult> UpdateOneAsync(ProgramInfo program, CancellationToken ct = default)
        => _installer.InstallSingleAsync(program, ct);

    public void Export(string path)
    {
        var data = Programs.Select(p => new
        {
            p.Name,
            p.Version,
            p.Publisher,
            Source = p.SourceDisplay,
            p.PackageId,
            p.UpdateAvailable,
            p.AvailableVersion
        });

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        _log.Success($"Exported {Programs.Count} programs to {path}");
    }
}

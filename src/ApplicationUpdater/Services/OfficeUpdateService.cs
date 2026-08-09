using System.IO;
using ApplicationUpdater.Helpers;
using ApplicationUpdater.Models;
using Microsoft.Win32;

namespace ApplicationUpdater.Services;

/// <summary>
/// Microsoft 365 / Office Click-to-Run updates via OfficeC2RClient.exe.
/// </summary>
public sealed class OfficeUpdateService
{
    public const string PackageId = "office:clicktorun";

    private readonly ConfigService _config;
    private readonly LogService _log;

    public OfficeUpdateService(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
    }

    public bool IsSourceEnabled => _config.Config.UpdateSources.MicrosoftOffice.Enabled;

    public Task<IReadOnlyList<ProgramInfo>> ScanAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => ScanInternal(progress, ct), ct);

    public Task<IReadOnlyList<ProgramInfo>> CheckUpdatesAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => CheckInternal(progress, ct), ct);

    public Task<UpdateResult> UpgradeAsync(
        ProgramInfo program,
        IProgress<UpdateProgress>? progress = null,
        int completed = 0,
        int total = 1,
        CancellationToken ct = default)
        => Task.Run(() => UpgradeInternal(program, progress, completed, total, ct), ct);

    private IReadOnlyList<ProgramInfo> ScanInternal(
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        if (!IsSourceEnabled)
            return [];

        progress?.Report(new ScanProgress { Message = "Scanning Microsoft Office…", Percent = 20 });
        ct.ThrowIfCancellationRequested();

        var info = ReadOfficeInfo();
        if (info is null)
        {
            _log.Info("Microsoft Office Click-to-Run not detected.");
            progress?.Report(new ScanProgress { Message = "Office not installed", Percent = 100 });
            return [];
        }

        progress?.Report(new ScanProgress { Message = $"Found {info.DisplayName}", Percent = 100 });
        _log.Info($"Office C2R detected: {info.DisplayName} {info.Version}");
        return [ToProgramInfo(info, updateAvailable: false, availableVersion: string.Empty)];
    }

    private IReadOnlyList<ProgramInfo> CheckInternal(
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        if (!IsSourceEnabled)
            return [];

        progress?.Report(new ScanProgress { Message = "Checking Microsoft Office updates…", Percent = 30 });
        ct.ThrowIfCancellationRequested();

        var info = ReadOfficeInfo();
        if (info is null)
        {
            progress?.Report(new ScanProgress { Message = "Office not installed", Percent = 100 });
            return [];
        }

        // Click-to-Run does not expose a simple offline "is update available" API without
        // contacting the CDN. Offer a channel refresh so the user can apply updates on demand;
        // OfficeC2RClient exits cleanly when already current.
        var program = ToProgramInfo(info, updateAvailable: true, availableVersion: "Channel latest");
        program.Notes = "Microsoft 365 / Office Click-to-Run (OfficeC2RClient /update)";

        progress?.Report(new ScanProgress { Message = "Office update check ready", Percent = 100 });
        _log.Info($"Office C2R update offered for {info.DisplayName} ({info.Version}).");
        return [program];
    }

    private UpdateResult UpgradeInternal(
        ProgramInfo program,
        IProgress<UpdateProgress>? progress,
        int completed,
        int total,
        CancellationToken ct)
    {
        var result = new UpdateResult
        {
            Program = program,
            StartTime = DateTime.Now
        };

        try
        {
            var client = FindOfficeC2RClient();
            if (client is null)
                throw new InvalidOperationException("OfficeC2RClient.exe was not found.");

            progress?.Report(new UpdateProgress
            {
                ProgramName = program.Name,
                ProgramKey = program.DisplayKey,
                Completed = completed,
                Total = total,
                ItemPercent = 20,
                IsStarting = true,
                Message = "Updating Microsoft Office…"
            });

            _log.Info($"Running Office Click-to-Run update: {client}");

            // displaylevel=false keeps UI minimal; forceappshutdown closes Word/Excel if needed.
            var args = new List<string>
            {
                "/update",
                "user",
                "displaylevel=false",
                "forceappshutdown=true",
                "updatepromptuser=false"
            };

            var proc = ProcessRunner.RunAsync(
                client,
                args,
                new ProcessRunOptions
                {
                    TimeoutSeconds = 3600,
                    ShowWindow = false,
                    Elevate = false
                },
                ct).GetAwaiter().GetResult();

            // Retry elevated if needed
            if (!IsOfficeSuccess(proc))
            {
                proc = ProcessRunner.RunAsync(
                    client,
                    args,
                    new ProcessRunOptions
                    {
                        TimeoutSeconds = 3600,
                        ShowWindow = true,
                        Elevate = true
                    },
                    ct).GetAwaiter().GetResult();
            }

            result.Output = proc.CombinedOutput;
            result.Success = IsOfficeSuccess(proc);

            if (result.Success)
            {
                var refreshed = ReadOfficeInfo();
                if (refreshed is not null)
                    program.Version = refreshed.Version;
                program.UpdateAvailable = false;
                program.AvailableVersion = string.Empty;
                program.LastUpdated = DateTime.Now;
                _log.Success($"Microsoft Office update finished ({program.Version}).");
            }
            else
            {
                result.ErrorMessage = string.IsNullOrWhiteSpace(proc.CombinedOutput)
                    ? $"Office update failed (exit {proc.ExitCode})."
                    : proc.CombinedOutput.Trim().Split('\n').LastOrDefault()?.Trim()
                      ?? $"Office update failed (exit {proc.ExitCode}).";
                _log.Error(result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            _log.Error($"Office update error: {ex.Message}");
        }

        result.EndTime = DateTime.Now;
        progress?.Report(new UpdateProgress
        {
            ProgramName = program.Name,
            ProgramKey = program.DisplayKey,
            Completed = completed + 1,
            Total = total,
            ItemPercent = 100,
            Success = result.Success,
            Message = result.Success ? "Updated" : result.ErrorMessage
        });
        return result;
    }

    private static ProgramInfo ToProgramInfo(OfficeInfo info, bool updateAvailable, string availableVersion) => new()
    {
        Name = info.DisplayName,
        PackageId = PackageId,
        Version = info.Version,
        Publisher = "Microsoft Corporation",
        Source = PackageSource.Office,
        Origin = "Microsoft Office",
        UpdateAvailable = updateAvailable,
        AvailableVersion = availableVersion,
        InstallLocation = info.ClientPath ?? string.Empty,
        Notes = string.IsNullOrWhiteSpace(info.Channel) ? "Click-to-Run" : $"Channel: {info.Channel}"
    };

    private OfficeInfo? ReadOfficeInfo()
    {
        try
        {
            // Machine-wide Click-to-Run configuration
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration")
                ?? Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\WOW6432Node\Microsoft\Office\ClickToRun\Configuration");

            if (key is null)
                return null;

            var version = (key.GetValue("VersionToReport") as string)
                          ?? (key.GetValue("ClientVersionToReport") as string)
                          ?? string.Empty;
            var products = (key.GetValue("ProductReleaseIds") as string) ?? string.Empty;
            var channel = (key.GetValue("UpdateChannel") as string)
                          ?? (key.GetValue("CDNBaseUrl") as string)
                          ?? string.Empty;

            if (string.IsNullOrWhiteSpace(version) && string.IsNullOrWhiteSpace(products))
            {
                // Still try client path — some installs only expose ClientFolder
            }

            var client = FindOfficeC2RClient();
            if (client is null && string.IsNullOrWhiteSpace(version))
                return null;

            var display = InferDisplayName(products);
            var channelLabel = InferChannelLabel(channel);

            return new OfficeInfo(
                display,
                string.IsNullOrWhiteSpace(version) ? "Installed" : version.Trim(),
                channelLabel,
                client);
        }
        catch (Exception ex)
        {
            _log.Warn($"Office registry read failed: {ex.Message}");
            return null;
        }
    }

    private static string InferDisplayName(string productReleaseIds)
    {
        var ids = productReleaseIds ?? string.Empty;
        if (ids.Contains("O365", StringComparison.OrdinalIgnoreCase) ||
            ids.Contains("Microsoft365", StringComparison.OrdinalIgnoreCase))
            return "Microsoft 365 Apps";
        if (ids.Contains("ProPlus", StringComparison.OrdinalIgnoreCase))
            return "Microsoft Office Professional Plus";
        if (ids.Contains("HomeBusiness", StringComparison.OrdinalIgnoreCase))
            return "Microsoft Office Home and Business";
        if (ids.Contains("HomeStudent", StringComparison.OrdinalIgnoreCase))
            return "Microsoft Office Home and Student";
        if (ids.Contains("Standard", StringComparison.OrdinalIgnoreCase))
            return "Microsoft Office Standard";
        if (ids.Contains("Visio", StringComparison.OrdinalIgnoreCase))
            return "Microsoft Visio";
        if (ids.Contains("Project", StringComparison.OrdinalIgnoreCase))
            return "Microsoft Project";
        if (!string.IsNullOrWhiteSpace(ids))
            return "Microsoft Office";
        return "Microsoft Office (Click-to-Run)";
    }

    private static string InferChannelLabel(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
            return string.Empty;

        var c = channel.ToLowerInvariant();
        if (c.Contains("insiderfast") || c.Contains("dogfood"))
            return "Insider (Beta)";
        if (c.Contains("insiderslow") || c.Contains("insider"))
            return "Insider";
        if (c.Contains("monthlyenterprise"))
            return "Monthly Enterprise";
        if (c.Contains("current") || c.Contains("monthly"))
            return "Current Channel";
        if (c.Contains("deferred") || c.Contains("semi"))
            return "Semi-Annual";
        // CDN URL path fragments
        if (c.Contains("/frdc2r") || c.Contains("insiders_dev"))
            return "Insider";
        return channel.Length > 48 ? channel[..48] + "…" : channel;
    }

    private static string? FindOfficeC2RClient()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Common Files", "Microsoft Shared", "ClickToRun", "OfficeC2RClient.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Common Files", "Microsoft Shared", "ClickToRun", "OfficeC2RClient.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft Office", "root", "Office16", "OfficeC2RClient.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft Office", "root", "Office16", "OfficeC2RClient.exe")
        };

        foreach (var path in candidates)
        {
            try
            {
                if (File.Exists(path))
                    return path;
            }
            catch
            {
                // ignore
            }
        }

        // Registry ClientFolder
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
            var folder = key?.GetValue("ClientFolder") as string;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                var path = Path.Combine(folder, "OfficeC2RClient.exe");
                if (File.Exists(path))
                    return path;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static bool IsOfficeSuccess(ProcessResult proc)
    {
        // OfficeC2RClient often returns 0 even when it only scheduled work.
        if (proc.Success)
            return true;

        var text = proc.CombinedOutput ?? string.Empty;
        if (text.Contains("successfully", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("up to date", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no updates", StringComparison.OrdinalIgnoreCase))
            return true;

        // Exit codes: 0 success; some builds use 17002/etc. Treat known cancel as failure.
        return proc.ExitCode is 0;
    }

    private sealed record OfficeInfo(string DisplayName, string Version, string Channel, string? ClientPath);
}

using System.Text.RegularExpressions;
using ApplicationUpdater.Helpers;
using ApplicationUpdater.Models;

namespace ApplicationUpdater.Services;

public sealed class ChocolateyService
{
    private readonly LogService _log;
    private bool? _available;

    public ChocolateyService(LogService log)
    {
        _log = log;
    }

    private static string ExtractChocoError(ProcessResult proc)
    {
        if (proc.TimedOut)
            return "Update timed out.";

        var text = proc.CombinedOutput;
        if (string.IsNullOrWhiteSpace(text))
            return $"choco exit code {proc.ExitCode}";

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines.Reverse())
        {
            if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("not recognized", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Unable to", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Cannot", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }

        return lines.LastOrDefault() ?? $"choco exit code {proc.ExitCode}";
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_available.HasValue)
            return _available.Value;

        _available = await ProcessRunner.CommandExistsAsync("choco", ct).ConfigureAwait(false);
        if (_available == true)
            _log.Info("Chocolatey detected.");
        return _available.Value;
    }

    public async Task<IReadOnlyList<ProgramInfo>> ListInstalledAsync(CancellationToken ct = default)
    {
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
            return [];

        // --limit-output gives "id|version" lines
        var result = await ProcessRunner.RunAsync(
            "choco",
            ["list", "--local-only", "--limit-output", "--no-progress"],
            ct,
            timeoutSeconds: 120).ConfigureAwait(false);

        if (!result.Success && string.IsNullOrWhiteSpace(result.StdOut))
        {
            // Older choco uses different flags
            result = await ProcessRunner.RunAsync(
                "choco",
                ["list", "-l", "--limit-output"],
                ct,
                timeoutSeconds: 120).ConfigureAwait(false);
        }

        var programs = new List<ProgramInfo>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Chocolatey", StringComparison.OrdinalIgnoreCase))
                continue;

            string id, version;
            if (line.Contains('|'))
            {
                var parts = line.Split('|');
                id = parts[0].Trim();
                version = parts.Length > 1 ? parts[1].Trim() : "Unknown";
            }
            else
            {
                var parts = Regex.Split(line, @"\s+");
                if (parts.Length < 2) continue;
                id = parts[0];
                version = parts[1];
            }

            if (string.IsNullOrWhiteSpace(id))
                continue;

            programs.Add(new ProgramInfo
            {
                Name = HumanizeId(id),
                PackageId = id,
                Version = version,
                Source = PackageSource.Chocolatey
            });
        }

        _log.Info($"Chocolatey list returned {programs.Count} packages.");
        return programs;
    }

    public async Task<IReadOnlyList<ProgramInfo>> ListOutdatedAsync(CancellationToken ct = default)
    {
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
            return [];

        var result = await ProcessRunner.RunAsync(
            "choco",
            ["outdated", "--limit-output", "--no-progress"],
            ct,
            timeoutSeconds: 180).ConfigureAwait(false);

        if (!result.Success && string.IsNullOrWhiteSpace(result.StdOut))
            return [];

        var outdated = new List<ProgramInfo>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // id|current|available|pinned
            var parts = line.Split('|');
            if (parts.Length < 3) continue;

            outdated.Add(new ProgramInfo
            {
                Name = HumanizeId(parts[0]),
                PackageId = parts[0].Trim(),
                Version = parts[1].Trim(),
                AvailableVersion = parts[2].Trim(),
                UpdateAvailable = true,
                Source = PackageSource.Chocolatey
            });
        }

        _log.Info($"Chocolatey outdated: {outdated.Count} package(s).");
        return outdated;
    }

    public async Task<UpdateResult> UpgradeAsync(ProgramInfo program, CancellationToken ct = default)
    {
        var result = new UpdateResult
        {
            Program = program,
            StartTime = DateTime.Now
        };

        if (string.IsNullOrWhiteSpace(program.PackageId))
        {
            result.Success = false;
            result.ErrorMessage = "No Chocolatey package ID.";
            result.EndTime = DateTime.Now;
            return result;
        }

        _log.Info($"Upgrading {program.Name} ({program.PackageId}) via Chocolatey...");

        // Prefer elevated run — most Chocolatey upgrades need admin and fail silently without it.
        var attempts = new[]
        {
            new ProcessRunOptions { TimeoutSeconds = 900, Elevate = true, ShowWindow = false },
            new ProcessRunOptions { TimeoutSeconds = 900, Elevate = false, ShowWindow = false }
        };

        ProcessResult? proc = null;
        foreach (var options in attempts)
        {
            ct.ThrowIfCancellationRequested();
            proc = await ProcessRunner.RunAsync(
                "choco",
                ["upgrade", program.PackageId, "-y", "--no-progress", "--limit-output"],
                options,
                ct).ConfigureAwait(false);

            result.Output = string.IsNullOrWhiteSpace(result.Output)
                ? proc.CombinedOutput
                : result.Output + "\n\n" + proc.CombinedOutput;

            result.Success = proc.Success ||
                             proc.CombinedOutput.Contains("is the latest version", StringComparison.OrdinalIgnoreCase) ||
                             proc.CombinedOutput.Contains("upgraded", StringComparison.OrdinalIgnoreCase) ||
                             proc.CombinedOutput.Contains("already installed", StringComparison.OrdinalIgnoreCase);

            if (result.Success)
                break;

            // Don't retry non-elevated if user cancelled UAC
            if (proc.StdErr.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
                proc.CombinedOutput.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
                break;
        }

        result.EndTime = DateTime.Now;

        if (result.Success)
        {
            if (!string.IsNullOrWhiteSpace(program.AvailableVersion) &&
                !VersionText.IsUnknown(program.AvailableVersion))
            {
                program.Version = program.AvailableVersion.Trim();
            }

            program.UpdateAvailable = false;
            _log.Success($"Updated {program.Name} via Chocolatey → {program.Version}");
        }
        else
        {
            result.ErrorMessage = ExtractChocoError(proc ?? new ProcessResult { ExitCode = -1, StdErr = "No attempt ran." });
            _log.Error($"Failed to update {program.Name}: {result.ErrorMessage}");
            if (proc is not null && !string.IsNullOrWhiteSpace(proc.CombinedOutput))
                _log.Error(proc.CombinedOutput.Length > 800 ? proc.CombinedOutput[..800] + "…" : proc.CombinedOutput);
        }

        return result;
    }

    private static string HumanizeId(string id)
    {
        // googlechrome -> Googlechrome; keep readable-ish
        return id.Replace('-', ' ').Replace('.', ' ');
    }
}

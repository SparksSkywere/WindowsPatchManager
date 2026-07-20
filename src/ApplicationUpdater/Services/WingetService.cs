using ApplicationUpdater.Helpers;
using ApplicationUpdater.Models;

namespace ApplicationUpdater.Services;

/// <summary>
/// Reliable winget integration. Prefer bulk "upgrade available" listing over
/// per-package probes (the Python app's main failure mode).
/// </summary>
public sealed class WingetService
{
    // Known winget / AppInstaller HRESULT-style exit codes
    private const int WingetUpdateNotApplicable = unchecked((int)0x8A15002B); // -1978335212
    private const int WingetInstallFailed = unchecked((int)0x8A15000A);       // -1978335226
    private const int WingetNoApplicableInstall = unchecked((int)0x8A150010);

    private readonly ConfigService _config;
    private readonly LogService _log;
    private bool? _available;

    public WingetService(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_available.HasValue)
            return _available.Value;

        var path = ProcessRunner.FindWingetPath();
        _available = !string.IsNullOrWhiteSpace(path);
        if (_available == true)
        {
            var ver = await ProcessRunner.RunAsync("winget", ["--version"], ct, 15).ConfigureAwait(false);
            if (ver.Success)
                _log.Info($"winget detected: {ver.StdOut.Trim()} ({path})");
            else
            {
                _log.Warn($"winget found at {path} but --version failed: {ver.StdErr}");
                _available = false;
            }
        }
        else
        {
            _log.Warn("winget was not found on PATH.");
        }

        return _available.Value;
    }

    public async Task<IReadOnlyList<ProgramInfo>> ListInstalledAsync(CancellationToken ct = default)
    {
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
            return [];

        var args = new List<string>
        {
            "list",
            "--accept-source-agreements",
            "--disable-interactivity"
        };

        var result = await ProcessRunner.RunAsync("winget", args, ct, timeoutSeconds: 180)
            .ConfigureAwait(false);

        if (!result.Success && string.IsNullOrWhiteSpace(result.StdOut))
        {
            _log.Warn($"winget list failed: {result.StdErr.Trim()}");
            return [];
        }

        var rows = WingetTableParser.Parse(result.StdOut);
        var programs = new List<ProgramInfo>();

        foreach (var row in rows)
        {
            row.Columns.TryGetValue("Name", out var name);
            row.Columns.TryGetValue("Id", out var id);
            row.Columns.TryGetValue("Version", out var version);
            row.Columns.TryGetValue("Available", out var available);

            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (string.IsNullOrWhiteSpace(id) || id.Contains(' '))
                continue;

            var program = new ProgramInfo
            {
                Name = name.Trim(),
                PackageId = id.Trim(),
                Version = string.IsNullOrWhiteSpace(version) ? "Unknown" : version.Trim(),
                Source = PackageSource.Winget,
                AvailableVersion = string.IsNullOrWhiteSpace(available) ? string.Empty : available.Trim()
            };

            if (!string.IsNullOrWhiteSpace(program.AvailableVersion) &&
                program.AvailableVersion is not ("Unknown" or "-" or "—"))
            {
                var newer = VersionComparer.IsNewer(program.Version, program.AvailableVersion);
                program.UpdateAvailable = newer != false;
            }

            programs.Add(program);
        }

        _log.Info($"winget list returned {programs.Count} packages.");
        return programs;
    }

    public async Task<IReadOnlyList<ProgramInfo>> ListUpgradesAsync(CancellationToken ct = default)
    {
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
            return [];

        var behavior = _config.Config.UpdateBehavior;
        var args = new List<string>
        {
            "upgrade",
            "--accept-source-agreements",
            "--disable-interactivity"
        };

        if (behavior.IncludeUnknown)
            args.Add("--include-unknown");
        if (behavior.IncludePinned)
            args.Add("--include-pinned");

        var result = await ProcessRunner.RunAsync("winget", args, ct, timeoutSeconds: 180)
            .ConfigureAwait(false);

        var output = result.StdOut;
        if (string.IsNullOrWhiteSpace(output))
            output = result.StdErr;

        if (output.Contains("No installed package has an available upgrade", StringComparison.OrdinalIgnoreCase))
        {
            _log.Info("winget reports no upgrades available.");
            return [];
        }

        var rows = WingetTableParser.Parse(output);
        var upgrades = new List<ProgramInfo>();

        foreach (var row in rows)
        {
            row.Columns.TryGetValue("Name", out var name);
            row.Columns.TryGetValue("Id", out var id);
            row.Columns.TryGetValue("Version", out var version);
            row.Columns.TryGetValue("Available", out var available);
            row.Columns.TryGetValue("Source", out var source);

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id))
                continue;

            if (id.Contains(' ') && !id.Contains('.'))
                continue;

            upgrades.Add(new ProgramInfo
            {
                Name = name.Trim(),
                PackageId = id.Trim(),
                Version = string.IsNullOrWhiteSpace(version) ? "Unknown" : version.Trim(),
                AvailableVersion = string.IsNullOrWhiteSpace(available) ? "Unknown" : available.Trim(),
                UpdateAvailable = true,
                Source = PackageSource.Winget,
                Notes = string.IsNullOrWhiteSpace(source) ? null : source.Trim()
            });
        }

        _log.Info($"winget upgrade listed {upgrades.Count} package(s).");
        return upgrades;
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
            result.ErrorMessage = "No winget package ID.";
            result.EndTime = DateTime.Now;
            return result;
        }

        var isRemoveOnly = program.Name.Contains("remove only", StringComparison.OrdinalIgnoreCase);
        if (isRemoveOnly)
            _log.Warn($"{program.Name} is marked '(remove only)' by winget — will try upgrade then force install.");

        _log.Info($"Upgrading {program.Name} ({program.PackageId}) via winget...");

        // Attempt ladder: silent → non-silent quiet → interactive UI → elevated interactive → force install
        var attempts = BuildAttempts(program, isRemoveOnly);
        ProcessResult? last = null;
        var attemptIndex = 0;

        foreach (var attempt in attempts)
        {
            attemptIndex++;
            ct.ThrowIfCancellationRequested();

            _log.Info($"  Attempt {attemptIndex}/{attempts.Count}: {attempt.Description}");

            last = await ProcessRunner.RunAsync(
                "winget",
                attempt.Args,
                new ProcessRunOptions
                {
                    TimeoutSeconds = attempt.TimeoutSeconds,
                    ShowWindow = attempt.ShowWindow,
                    Elevate = attempt.Elevate
                },
                ct).ConfigureAwait(false);

            // Keep cumulative log for diagnostics
            result.Output = AppendOutput(result.Output, $"--- {attempt.Description} (exit {last.ExitCode}) ---", last.CombinedOutput);

            if (IsSuccessful(last))
            {
                result.Success = true;
                result.EndTime = DateTime.Now;
                _log.Success($"Updated {program.Name} ({attempt.Description})");
                return result;
            }

            var reason = ExtractError(last) ?? $"exit code {last.ExitCode}";
            _log.Warn($"  Attempt failed: {reason}");

            // If user cancelled UAC, stop retrying elevated/interactive paths that need admin
            if (reason.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
                break;
        }

        result.Success = false;
        result.ErrorMessage = ExtractError(last) ?? "winget upgrade failed after all attempts.";
        result.EndTime = DateTime.Now;
        _log.Error($"Failed to update {program.Name}: {result.ErrorMessage}");
        if (!string.IsNullOrWhiteSpace(last?.CombinedOutput))
            _log.Error(Truncate(last!.CombinedOutput, 800));

        return result;
    }

    private List<WingetAttempt> BuildAttempts(ProgramInfo program, bool isRemoveOnly)
    {
        var behavior = _config.Config.UpdateBehavior;
        var attempts = new List<WingetAttempt>();

        // 1) Preferred silent (if configured)
        if (behavior.Silent)
        {
            attempts.Add(new WingetAttempt(
                "silent upgrade",
                BuildUpgradeArgs(program, silent: true, interactive: false, force: false),
                TimeoutSeconds: 900,
                ShowWindow: false,
                Elevate: false));
        }

        // 2) Non-silent, still non-interactive (many EXEs need this; Bitvise silent → 131)
        attempts.Add(new WingetAttempt(
            "quiet upgrade (no --silent)",
            BuildUpgradeArgs(program, silent: false, interactive: false, force: false),
            TimeoutSeconds: 900,
            ShowWindow: false,
            Elevate: false));

        // 3) Interactive UI (user can click through installer) — fixes Bitvise etc.
        attempts.Add(new WingetAttempt(
            "interactive upgrade",
            BuildUpgradeArgs(program, silent: false, interactive: true, force: false),
            TimeoutSeconds: 1800,
            ShowWindow: true,
            Elevate: false));

        // 4) Elevated interactive (machine-scope packages / Program Files)
        attempts.Add(new WingetAttempt(
            "elevated interactive upgrade",
            BuildUpgradeArgs(program, silent: false, interactive: true, force: false),
            TimeoutSeconds: 1800,
            ShowWindow: true,
            Elevate: true));

        // 5) Force reinstall (helps "(remove only)" and broken ARP states)
        attempts.Add(new WingetAttempt(
            isRemoveOnly ? "force install (remove-only package)" : "force install (repair)",
            BuildInstallArgs(program, force: true, interactive: true),
            TimeoutSeconds: 1800,
            ShowWindow: true,
            Elevate: true));


        return attempts;
    }

    private List<string> BuildUpgradeArgs(ProgramInfo program, bool silent, bool interactive, bool force)
    {
        var behavior = _config.Config.UpdateBehavior;
        var args = new List<string>
        {
            "upgrade",
            "--id", program.PackageId,
            "--exact",
            "--accept-source-agreements",
            "--accept-package-agreements"
        };

        if (interactive)
            args.Add("--interactive");
        else
            args.Add("--disable-interactivity");

        if (silent && !interactive)
            args.Add("--silent");

        if (behavior.IncludeUnknown)
            args.Add("--include-unknown");
        if (behavior.IncludePinned)
            args.Add("--include-pinned");
        if (behavior.RestartIfRequired)
            args.Add("--allow-reboot");
        if (force)
            args.Add("--force");

        return args;
    }

    private static List<string> BuildInstallArgs(ProgramInfo program, bool force, bool interactive)
    {
        var args = new List<string>
        {
            "install",
            "--id", program.PackageId,
            "--exact",
            "--accept-source-agreements",
            "--accept-package-agreements"
        };

        if (interactive)
            args.Add("--interactive");
        else
        {
            args.Add("--disable-interactivity");
            args.Add("--silent");
        }

        if (force)
            args.Add("--force");

        return args;
    }

    private static bool IsSuccessful(ProcessResult proc)
    {
        if (proc.Success)
            return true;

        // Already up to date / not applicable
        if (proc.ExitCode is WingetUpdateNotApplicable or WingetNoApplicableInstall)
            return true;

        return OutputIndicatesSuccess(proc.CombinedOutput);
    }

    private static bool OutputIndicatesSuccess(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        var lower = output.ToLowerInvariant();
        return lower.Contains("successfully installed") ||
               lower.Contains("successfully upgraded") ||
               lower.Contains("no applicable update") ||
               lower.Contains("is already installed") ||
               lower.Contains("no available upgrade");
    }

    private static string? ExtractError(ProcessResult? proc)
    {
        if (proc is null) return null;
        if (proc.TimedOut) return "Update timed out.";
        var text = proc.CombinedOutput;
        if (string.IsNullOrWhiteSpace(text))
            return string.IsNullOrWhiteSpace(proc.StdErr) ? $"winget exit code {proc.ExitCode}" : proc.StdErr.Trim();

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines.Reverse())
        {
            if (line.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Installer failed", StringComparison.OrdinalIgnoreCase))
                return line;
        }

        // Map common winget codes
        if (proc.ExitCode == WingetInstallFailed)
            return lines.LastOrDefault(l => l.Contains("Installer failed", StringComparison.OrdinalIgnoreCase))
                   ?? $"Installer failed (winget {proc.ExitCode}).";

        return lines.LastOrDefault() ?? $"winget exit code {proc.ExitCode}";
    }

    private static string AppendOutput(string existing, string header, string body)
    {
        var chunk = string.IsNullOrWhiteSpace(body) ? header : $"{header}\n{body}";
        return string.IsNullOrWhiteSpace(existing) ? chunk : existing + "\n\n" + chunk;
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    private sealed record WingetAttempt(
        string Description,
        List<string> Args,
        int TimeoutSeconds,
        bool ShowWindow,
        bool Elevate);
}

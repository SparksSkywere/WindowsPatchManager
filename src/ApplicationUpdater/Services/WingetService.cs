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

            // Accept normal IDs and ARP / Steam App IDs (which contain spaces).
            // Skip only garbage header fragments with spaces and no path separators.
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (id.Contains(' ') &&
                !id.Contains('\\') &&
                !id.Contains("Steam App", StringComparison.OrdinalIgnoreCase) &&
                !id.Contains("ARP", StringComparison.OrdinalIgnoreCase))
                continue;

            var program = new ProgramInfo
            {
                Name = name.Trim(),
                PackageId = id.Trim(),
                Version = string.IsNullOrWhiteSpace(version) ? "Unknown" : version.Trim(),
                Source = PackageSource.Winget,
                AvailableVersion = string.IsNullOrWhiteSpace(available) ? string.Empty : available.Trim()
            };

            // Steam games via winget ARP bridge — mark origin early so UI isn't blank
            // before registry merge / AppOriginEnricher.
            if (id.Contains("Steam App", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("steamapps", StringComparison.OrdinalIgnoreCase))
            {
                program.Origin = "Steam";
            }

            // Do NOT mark UpdateAvailable from "winget list" Available column.
            // That column is often stale (e.g. WindowsAppRuntime) and disagrees with
            // "winget upgrade". Official update status comes only from ListUpgradesAsync.
            if (!string.IsNullOrWhiteSpace(program.AvailableVersion) &&
                program.AvailableVersion is ("Unknown" or "-" or "—"))
            {
                program.AvailableVersion = string.Empty;
            }

            program.UpdateAvailable = false;
            programs.Add(program);
        }

        _log.Info($"winget list returned {programs.Count} packages (update flags deferred until Check updates).");
        return programs;
    }

    /// <summary>Search the winget catalog for packages matching a query.</summary>
    public async Task<IReadOnlyList<ProgramInfo>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || !await IsAvailableAsync(ct).ConfigureAwait(false))
            return [];

        var result = await ProcessRunner.RunAsync(
            "winget",
            [
                "search",
                query.Trim(),
                "--accept-source-agreements",
                "--disable-interactivity"
            ],
            ct,
            timeoutSeconds: 120).ConfigureAwait(false);

        var text = string.IsNullOrWhiteSpace(result.StdOut) ? result.StdErr : result.StdOut;
        if (string.IsNullOrWhiteSpace(text))
        {
            _log.Warn($"winget search failed: {result.StdErr}");
            return [];
        }

        var rows = WingetTableParser.Parse(text);
        var programs = new List<ProgramInfo>();
        foreach (var row in rows)
        {
            row.Columns.TryGetValue("Name", out var name);
            row.Columns.TryGetValue("Id", out var id);
            row.Columns.TryGetValue("Version", out var version);
            row.Columns.TryGetValue("Source", out var source);
            row.Columns.TryGetValue("Match", out var match);

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id) || id.Contains(' '))
                continue;

            programs.Add(new ProgramInfo
            {
                Name = name.Trim(),
                PackageId = id.Trim(),
                Version = string.IsNullOrWhiteSpace(version) ? "—" : version.Trim(),
                AvailableVersion = string.IsNullOrWhiteSpace(version) ? "—" : version.Trim(),
                Source = PackageSource.Winget,
                UpdateAvailable = false,
                Notes = string.IsNullOrWhiteSpace(match) ? source : match,
                Publisher = string.IsNullOrWhiteSpace(source) ? "winget" : source.Trim()
            });
        }

        _log.Info($"winget search \"{query}\": {programs.Count} result(s).");
        return programs;
    }

    /// <summary>Install a package by id (latest unless <paramref name="version"/> is set).</summary>
    public async Task<UpdateResult> InstallPackageAsync(
        ProgramInfo program,
        string? version = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(version))
            return await InstallVersionAsync(program, version, ct).ConfigureAwait(false);

        var result = new UpdateResult
        {
            Program = program,
            StartTime = DateTime.Now
        };

        if (string.IsNullOrWhiteSpace(program.PackageId))
        {
            result.Success = false;
            result.ErrorMessage = "No package ID.";
            result.EndTime = DateTime.Now;
            return result;
        }

        _log.Info($"Installing {program.Name} ({program.PackageId}) via winget...");
        var args = BuildInstallArgs(program, force: false, interactive: !_config.Config.UpdateBehavior.Silent);
        var proc = await ProcessRunner.RunAsync(
            "winget",
            args,
            new ProcessRunOptions
            {
                TimeoutSeconds = 1800,
                ShowWindow = !_config.Config.UpdateBehavior.Silent,
                Elevate = false
            },
            ct).ConfigureAwait(false);

        result.Output = proc.CombinedOutput;
        result.EndTime = DateTime.Now;
        result.Success = IsSuccessful(proc);
        if (result.Success)
        {
            program.LastUpdated = DateTime.Now;
            _log.Success($"Installed {program.Name}");
        }
        else
        {
            // Retry elevated once
            var elev = await ProcessRunner.RunAsync(
                "winget",
                args,
                new ProcessRunOptions { TimeoutSeconds = 1800, ShowWindow = true, Elevate = true },
                ct).ConfigureAwait(false);
            result.Output += "\n\n" + elev.CombinedOutput;
            result.Success = IsSuccessful(elev);
            result.EndTime = DateTime.Now;
            if (result.Success)
            {
                program.LastUpdated = DateTime.Now;
                _log.Success($"Installed {program.Name} (elevated)");
            }
            else
            {
                result.ErrorMessage = ExtractError(elev) ?? ExtractError(proc) ?? "Install failed.";
                _log.Error($"Install failed {program.Name}: {result.ErrorMessage}");
            }
        }

        return result;
    }

    /// <summary>Uninstall a package by id via winget.</summary>
    public async Task<UpdateResult> UninstallPackageAsync(ProgramInfo program, CancellationToken ct = default)
    {
        var result = new UpdateResult
        {
            Program = program,
            StartTime = DateTime.Now
        };

        if (string.IsNullOrWhiteSpace(program.PackageId))
        {
            result.Success = false;
            result.ErrorMessage = "No package ID.";
            result.EndTime = DateTime.Now;
            return result;
        }

        _log.Info($"Uninstalling {program.Name} ({program.PackageId}) via winget...");
        var args = new List<string>
        {
            "uninstall",
            "--id", program.PackageId,
            "--exact",
            "--accept-source-agreements",
            "--disable-interactivity"
        };
        if (_config.Config.UpdateBehavior.Silent)
            args.Add("--silent");

        var proc = await ProcessRunner.RunAsync(
            "winget",
            args,
            new ProcessRunOptions { TimeoutSeconds = 1200, ShowWindow = false, Elevate = false },
            ct).ConfigureAwait(false);

        result.Output = proc.CombinedOutput;
        result.EndTime = DateTime.Now;
        result.Success = proc.Success ||
                         proc.CombinedOutput.Contains("successfully uninstalled", StringComparison.OrdinalIgnoreCase) ||
                         proc.CombinedOutput.Contains("No installed package found", StringComparison.OrdinalIgnoreCase);

        if (!result.Success)
        {
            var elev = await ProcessRunner.RunAsync(
                "winget",
                args,
                new ProcessRunOptions { TimeoutSeconds = 1200, ShowWindow = true, Elevate = true },
                ct).ConfigureAwait(false);
            result.Output += "\n\n" + elev.CombinedOutput;
            result.EndTime = DateTime.Now;
            result.Success = elev.Success ||
                             elev.CombinedOutput.Contains("successfully uninstalled", StringComparison.OrdinalIgnoreCase);
            if (!result.Success)
                result.ErrorMessage = ExtractError(elev) ?? ExtractError(proc) ?? "Uninstall failed.";
        }

        if (result.Success)
            _log.Success($"Uninstalled {program.Name}");
        else
            _log.Error($"Uninstall failed {program.Name}: {result.ErrorMessage}");

        return result;
    }

    /// <summary>
    /// Lists published versions for a package (winget show --versions), newest first.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAvailableVersionsAsync(
        string packageId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageId) || !await IsAvailableAsync(ct).ConfigureAwait(false))
            return [];

        var result = await ProcessRunner.RunAsync(
            "winget",
            [
                "show",
                "--id", packageId.Trim(),
                "--exact",
                "--versions",
                "--accept-source-agreements",
                "--disable-interactivity"
            ],
            ct,
            timeoutSeconds: 90).ConfigureAwait(false);

        var text = string.IsNullOrWhiteSpace(result.StdOut) ? result.StdErr : result.StdOut;
        if (string.IsNullOrWhiteSpace(text))
        {
            _log.Warn($"winget show --versions failed for {packageId}: {result.StdErr}");
            return [];
        }

        var versions = new List<string>();
        var pastHeader = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            if (!pastHeader)
            {
                // Header row is typically just "Version" followed by a dashed separator.
                if (line.Equals("Version", StringComparison.OrdinalIgnoreCase))
                {
                    pastHeader = true;
                    continue;
                }

                if (line.Length > 2 && line.All(ch => ch is '-' or '=' or '─' or '━' or ' '))
                {
                    pastHeader = true;
                    continue;
                }

                continue;
            }

            if (line.Length > 2 && line.All(ch => ch is '-' or '=' or '─' or '━' or ' '))
                continue;
            if (line.StartsWith("Found ", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("https://", StringComparison.OrdinalIgnoreCase))
                continue;

            // First token is the version
            var ver = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
            if (string.IsNullOrWhiteSpace(ver) || ver.Equals("Version", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!versions.Contains(ver, StringComparer.OrdinalIgnoreCase))
                versions.Add(ver);
        }

        _log.Info($"winget listed {versions.Count} version(s) for {packageId}.");
        return versions;
    }

    /// <summary>
    /// Installs a specific package version (supports downgrade via --force).
    /// </summary>
    public async Task<UpdateResult> InstallVersionAsync(
        ProgramInfo program,
        string version,
        CancellationToken ct = default)
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

        if (string.IsNullOrWhiteSpace(version))
        {
            result.Success = false;
            result.ErrorMessage = "No version selected.";
            result.EndTime = DateTime.Now;
            return result;
        }

        _log.Info($"Installing {program.Name} ({program.PackageId}) version {version} via winget...");

        var args = new List<string>
        {
            "install",
            "--id", program.PackageId,
            "--exact",
            "--version", version.Trim(),
            "--force", // required for side-by-side / downgrade when a different version is present
            "--accept-source-agreements",
            "--accept-package-agreements"
        };

        if (_config.Config.UpdateBehavior.Silent)
        {
            args.Add("--disable-interactivity");
            args.Add("--silent");
        }
        else
        {
            args.Add("--interactive");
        }

        var attempts = new[]
        {
            new ProcessRunOptions { TimeoutSeconds = 1800, ShowWindow = !_config.Config.UpdateBehavior.Silent, Elevate = false },
            new ProcessRunOptions { TimeoutSeconds = 1800, ShowWindow = true, Elevate = true }
        };

        ProcessResult? last = null;
        foreach (var options in attempts)
        {
            ct.ThrowIfCancellationRequested();
            last = await ProcessRunner.RunAsync("winget", args, options, ct).ConfigureAwait(false);
            result.Output = string.IsNullOrWhiteSpace(result.Output)
                ? last.CombinedOutput
                : result.Output + "\n\n" + last.CombinedOutput;

            if (IsSuccessful(last))
            {
                result.Success = true;
                program.Version = version.Trim();
                program.AvailableVersion = string.Empty;
                program.UpdateAvailable = false;
                program.LastUpdated = DateTime.Now;
                result.EndTime = DateTime.Now;
                _log.Success($"Installed {program.Name} version {version}");
                return result;
            }

            if (last.StdErr.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
                last.CombinedOutput.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
                break;
        }

        result.Success = false;
        result.ErrorMessage = ExtractError(last) ?? "winget install failed.";
        result.EndTime = DateTime.Now;
        _log.Error($"Failed to install {program.Name} v{version}: {result.ErrorMessage}");
        return result;
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

            // Skip garbage / multi-word non-ids; allow Vendor.Product and ARP\… paths
            if (id.Contains(' ') && !id.Contains('.') && !id.Contains('\\'))
                continue;

            var installed = string.IsNullOrWhiteSpace(version) ? "Unknown" : version.Trim();
            var availableVer = string.IsNullOrWhiteSpace(available) ? "Unknown" : available.Trim();

            // Drop rows that are not real upgrades (equal / not newer / empty available)
            if (VersionText.IsUnknown(availableVer))
                continue;
            if (string.Equals(installed, availableVer, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!VersionText.IsUnknown(installed) &&
                VersionComparer.IsNewer(installed, availableVer) != true)
                continue;

            upgrades.Add(new ProgramInfo
            {
                Name = name.Trim(),
                PackageId = id.Trim(),
                Version = installed,
                AvailableVersion = availableVer,
                UpdateAvailable = true,
                Source = PackageSource.Winget,
                Notes = string.IsNullOrWhiteSpace(source) ? null : source.Trim()
            });
        }

        _log.Info($"winget upgrade listed {upgrades.Count} package(s) after version filter.");
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
        var isUnknownVersion = VersionText.IsUnknown(program.Version);
        if (isRemoveOnly)
            _log.Warn($"{program.Name} is marked '(remove only)' by winget — will try upgrade then force install.");
        if (isUnknownVersion)
            _log.Info($"{program.Name} has unknown current version — will force reinstall to {program.AvailableVersion} to repair.");

        _log.Info($"Upgrading {program.Name} ({program.PackageId}) via winget...");

        // Attempt ladder: silent → quiet → interactive → elevated → force install
        // Unknown versions start with force reinstall so broken ARP states get replaced.
        var attempts = BuildAttempts(program, isRemoveOnly, isUnknownVersion);
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
                ApplyInstalledVersion(program);
                _log.Success($"Updated {program.Name} → {program.Version} ({attempt.Description})");
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

    private List<WingetAttempt> BuildAttempts(ProgramInfo program, bool isRemoveOnly, bool isUnknownVersion)
    {
        var behavior = _config.Config.UpdateBehavior;
        var attempts = new List<WingetAttempt>();

        // Unknown / broken ARP: reinstall available version first (force) so "Unknown" is replaced.
        if (isUnknownVersion)
        {
            if (behavior.Silent)
            {
                attempts.Add(new WingetAttempt(
                    "silent force install (unknown version repair)",
                    BuildInstallArgs(program, force: true, interactive: false),
                    TimeoutSeconds: 900,
                    ShowWindow: false,
                    Elevate: false));

                attempts.Add(new WingetAttempt(
                    "silent force upgrade (unknown version repair)",
                    BuildUpgradeArgs(program, silent: true, interactive: false, force: true),
                    TimeoutSeconds: 900,
                    ShowWindow: false,
                    Elevate: false));
            }

            attempts.Add(new WingetAttempt(
                "force install (unknown version repair)",
                BuildInstallArgs(program, force: true, interactive: true),
                TimeoutSeconds: 1800,
                ShowWindow: true,
                Elevate: true));
        }

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
        if (!isUnknownVersion)
        {
            attempts.Add(new WingetAttempt(
                isRemoveOnly ? "force install (remove-only package)" : "force install (repair)",
                BuildInstallArgs(program, force: true, interactive: true),
                TimeoutSeconds: 1800,
                ShowWindow: true,
                Elevate: true));
        }

        return attempts;
    }

    /// <summary>
    /// After a successful install/upgrade, replace Unknown (or stale) current version with the target version.
    /// </summary>
    private static void ApplyInstalledVersion(ProgramInfo program)
    {
        if (!string.IsNullOrWhiteSpace(program.AvailableVersion) &&
            !VersionText.IsUnknown(program.AvailableVersion))
        {
            program.Version = program.AvailableVersion.Trim();
        }

        program.UpdateAvailable = false;
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

    private static List<string> BuildInstallArgs(ProgramInfo program, bool force, bool interactive, string? version = null)
    {
        var args = new List<string>
        {
            "install",
            "--id", program.PackageId,
            "--exact",
            "--accept-source-agreements",
            "--accept-package-agreements"
        };

        if (!string.IsNullOrWhiteSpace(version))
        {
            args.Add("--version");
            args.Add(version.Trim());
        }

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

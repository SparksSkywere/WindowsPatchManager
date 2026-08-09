using System.Text.RegularExpressions;
using ApplicationUpdater.Helpers;
using ApplicationUpdater.Models;

namespace ApplicationUpdater.Services;

/// <summary>
/// Windows Subsystem for Linux platform updates (<c>wsl --update</c> / Microsoft.WSL)
/// and optional package-manager upgrades inside installed distros.
/// </summary>
public sealed class WslUpdateService
{
    public const string PlatformPackageId = "wsl:platform";
    public const string DistroPackageIdPrefix = "wsl:distro:";

    private static readonly Regex VersionLineRegex = new(
        @"^\s*WSL\s+version\s*:\s*(?<v>[\d\.]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex KernelVersionRegex = new(
        @"^\s*Kernel\s+version\s*:\s*(?<v>[\d\.\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly ConfigService _config;
    private readonly LogService _log;
    private bool? _available;

    public WslUpdateService(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
    }

    public bool IsSourceEnabled =>
        _config.Config.UpdateSources.Wsl.Enabled && _config.Config.Wsl.Enabled;

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_available.HasValue)
            return _available.Value;

        // wsl.exe is a system binary on modern Windows even when no distro is installed.
        var result = await ProcessRunner.RunAsync(
            "where.exe",
            ["wsl.exe"],
            ct,
            timeoutSeconds: 15).ConfigureAwait(false);

        _available = result.Success && !string.IsNullOrWhiteSpace(result.StdOut);
        if (_available == true)
            _log.Info("WSL (wsl.exe) detected.");
        else
            _log.Info("WSL is not available on this system.");

        return _available.Value;
    }

    /// <summary>
    /// Returns WSL platform row (and optionally distro rows) for the Programs list.
    /// Platform is always listed when WSL is present so users can run <c>wsl --update</c>.
    /// </summary>
    public async Task<IReadOnlyList<ProgramInfo>> ScanAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsSourceEnabled)
            return [];

        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
            return [];

        progress?.Report(new ScanProgress { Message = "Scanning Windows Subsystem for Linux…", Percent = 10 });

        var list = new List<ProgramInfo>();
        var (wslVersion, kernelVersion) = await GetVersionInfoAsync(ct).ConfigureAwait(false);
        var versionDisplay = !string.IsNullOrWhiteSpace(wslVersion)
            ? wslVersion!
            : (!string.IsNullOrWhiteSpace(kernelVersion) ? $"kernel {kernelVersion}" : "Installed");

        list.Add(new ProgramInfo
        {
            Name = "Windows Subsystem for Linux",
            PackageId = PlatformPackageId,
            Version = versionDisplay,
            Publisher = "Microsoft Corporation",
            Source = PackageSource.Wsl,
            Origin = "WSL",
            UpdateAvailable = false,
            Notes = string.IsNullOrWhiteSpace(kernelVersion)
                ? "Platform / kernel (wsl --update)"
                : $"Kernel {kernelVersion} · wsl --update"
        });

        if (_config.Config.Wsl.IncludeDistroPackages)
        {
            progress?.Report(new ScanProgress { Message = "Listing WSL distributions…", Percent = 40 });
            foreach (var distro in await ListDistrosAsync(ct).ConfigureAwait(false))
            {
                list.Add(new ProgramInfo
                {
                    Name = $"WSL · {distro.Name}",
                    PackageId = DistroPackageIdPrefix + distro.Name,
                    Version = distro.VersionLabel,
                    Publisher = "WSL distribution",
                    Source = PackageSource.Wsl,
                    Origin = "WSL",
                    UpdateAvailable = false,
                    Notes = distro.State
                });
            }
        }

        progress?.Report(new ScanProgress
        {
            Message = $"WSL: {list.Count} item(s)",
            Percent = 100
        });
        _log.Info($"WSL scan: {list.Count} item(s).");
        return list;
    }

    /// <summary>
    /// Marks WSL platform as updatable when a newer build is available (best-effort via winget),
    /// and probes distros for pending package upgrades when enabled.
    /// </summary>
    public async Task<IReadOnlyList<ProgramInfo>> CheckUpdatesAsync(
        IReadOnlyList<ProgramInfo>? existing = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsSourceEnabled)
            return [];

        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
            return [];

        progress?.Report(new ScanProgress { Message = "Checking WSL updates…", Percent = 15 });

        var scanned = (existing is { Count: > 0 }
            ? existing.Where(p => p.Source == PackageSource.Wsl).Select(p => p.Clone()).ToList()
            : (await ScanAsync(progress, ct).ConfigureAwait(false)).Select(p => p.Clone()).ToList());

        // Ensure platform row exists
        var platform = scanned.FirstOrDefault(p =>
            string.Equals(p.PackageId, PlatformPackageId, StringComparison.OrdinalIgnoreCase));
        if (platform is null)
        {
            var fresh = await ScanAsync(progress, ct).ConfigureAwait(false);
            scanned = fresh.Select(p => p.Clone()).ToList();
            platform = scanned.FirstOrDefault(p =>
                string.Equals(p.PackageId, PlatformPackageId, StringComparison.OrdinalIgnoreCase));
        }

        if (platform is not null)
        {
            // Prefer winget metadata for Microsoft.WSL when an upgrade is published.
            // When winget has no row, still offer a platform refresh via wsl --update only if
            // the client reports it is not already on the latest build (parsed from --update dry attempt).
            var wingetInfo = await TryGetWingetWslUpgradeAsync(ct).ConfigureAwait(false);
            if (wingetInfo is not null && wingetInfo.UpdateAvailable)
            {
                platform.Version = wingetInfo.Installed;
                platform.AvailableVersion = wingetInfo.Available;
                platform.UpdateAvailable = true;
                platform.Notes = "Microsoft.WSL via winget / wsl --update";
            }
            else
            {
                if (wingetInfo is not null && !VersionText.IsUnknown(wingetInfo.Installed))
                    platform.Version = wingetInfo.Installed;

                var needsUpdate = await ProbePlatformNeedsUpdateAsync(ct).ConfigureAwait(false);
                if (needsUpdate)
                {
                    platform.AvailableVersion = "Latest";
                    platform.UpdateAvailable = true;
                    platform.Notes = "WSL platform update available (wsl --update)";
                }
                else
                {
                    platform.UpdateAvailable = false;
                    platform.AvailableVersion = string.Empty;
                    platform.Notes = "Platform up to date (wsl --update / Microsoft.WSL)";
                }
            }
        }

        if (_config.Config.Wsl.IncludeDistroPackages)
        {
            progress?.Report(new ScanProgress { Message = "Checking WSL distro packages…", Percent = 50 });
            var distroRows = scanned
                .Where(p => p.PackageId.StartsWith(DistroPackageIdPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Discover distros that may be missing from the prior scan
            if (distroRows.Count == 0)
            {
                foreach (var d in await ListDistrosAsync(ct).ConfigureAwait(false))
                {
                    var row = new ProgramInfo
                    {
                        Name = $"WSL · {d.Name}",
                        PackageId = DistroPackageIdPrefix + d.Name,
                        Version = d.VersionLabel,
                        Publisher = "WSL distribution",
                        Source = PackageSource.Wsl,
                        Origin = "WSL",
                        Notes = d.State
                    };
                    scanned.Add(row);
                    distroRows.Add(row);
                }
            }

            foreach (var row in distroRows)
            {
                ct.ThrowIfCancellationRequested();
                var name = row.PackageId[DistroPackageIdPrefix.Length..];
                var pending = await CountPendingPackagesAsync(name, ct).ConfigureAwait(false);
                if (pending > 0)
                {
                    row.UpdateAvailable = true;
                    row.AvailableVersion = $"{pending} package(s)";
                    row.Notes = "Package manager upgrade inside the distro";
                }
                else
                {
                    row.UpdateAvailable = false;
                    row.AvailableVersion = string.Empty;
                }
            }
        }

        progress?.Report(new ScanProgress
        {
            Message = $"WSL: {scanned.Count(p => p.UpdateAvailable)} update(s)",
            Percent = 100
        });
        return scanned;
    }

    public async Task<UpdateResult> UpgradeAsync(
        ProgramInfo program,
        IProgress<UpdateProgress>? progress = null,
        int completed = 0,
        int total = 1,
        CancellationToken ct = default)
    {
        var result = new UpdateResult
        {
            Program = program,
            StartTime = DateTime.Now
        };

        try
        {
            if (string.Equals(program.PackageId, PlatformPackageId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(program.Name, "Windows Subsystem for Linux", StringComparison.OrdinalIgnoreCase))
            {
                result = await UpgradePlatformAsync(program, progress, completed, total, ct).ConfigureAwait(false);
            }
            else if (program.PackageId.StartsWith(DistroPackageIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var distro = program.PackageId[DistroPackageIdPrefix.Length..];
                result = await UpgradeDistroPackagesAsync(program, distro, progress, completed, total, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                result.Success = false;
                result.ErrorMessage = "Unknown WSL package id.";
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            _log.Error($"WSL update failed for {program.Name}: {ex.Message}");
        }

        result.EndTime = DateTime.Now;
        return result;
    }

    private async Task<UpdateResult> UpgradePlatformAsync(
        ProgramInfo program,
        IProgress<UpdateProgress>? progress,
        int completed,
        int total,
        CancellationToken ct)
    {
        var result = new UpdateResult { Program = program, StartTime = DateTime.Now };

        progress?.Report(new UpdateProgress
        {
            ProgramName = program.Name,
            ProgramKey = program.DisplayKey,
            Completed = completed,
            Total = total,
            ItemPercent = 15,
            IsStarting = true,
            Message = "Updating WSL platform…"
        });

        _log.Info("Running wsl --update…");
        var proc = await ProcessRunner.RunAsync(
            "wsl.exe",
            ["--update"],
            new ProcessRunOptions
            {
                TimeoutSeconds = 900,
                ShowWindow = false,
                Elevate = false
            },
            ct).ConfigureAwait(false);

        // Some hosts need elevation for component install
        if (!IsWslUpdateSuccess(proc))
        {
            _log.Info("Retrying wsl --update elevated…");
            proc = await ProcessRunner.RunAsync(
                "wsl.exe",
                ["--update"],
                new ProcessRunOptions
                {
                    TimeoutSeconds = 900,
                    ShowWindow = true,
                    Elevate = true
                },
                ct).ConfigureAwait(false);
        }

        result.Output = proc.CombinedOutput;
        result.Success = IsWslUpdateSuccess(proc);

        if (!result.Success)
        {
            // Fallback: winget upgrade Microsoft.WSL when available
            _log.Info("Falling back to winget upgrade Microsoft.WSL…");
            var winget = await ProcessRunner.RunAsync(
                "winget",
                [
                    "upgrade",
                    "--id", "Microsoft.WSL",
                    "--exact",
                    "--accept-source-agreements",
                    "--accept-package-agreements",
                    "--disable-interactivity"
                ],
                new ProcessRunOptions { TimeoutSeconds = 900, ShowWindow = false },
                ct).ConfigureAwait(false);

            result.Output += "\n\n" + winget.CombinedOutput;
            result.Success = winget.Success ||
                             winget.CombinedOutput.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase) ||
                             winget.CombinedOutput.Contains("already installed", StringComparison.OrdinalIgnoreCase) ||
                             winget.CombinedOutput.Contains("No applicable update", StringComparison.OrdinalIgnoreCase);
        }

        if (result.Success)
        {
            var (ver, _) = await GetVersionInfoAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ver))
                program.Version = ver!;
            else if (!string.IsNullOrWhiteSpace(program.AvailableVersion) &&
                     !program.AvailableVersion.Equals("Latest", StringComparison.OrdinalIgnoreCase))
                program.Version = program.AvailableVersion;

            program.UpdateAvailable = false;
            program.AvailableVersion = string.Empty;
            program.LastUpdated = DateTime.Now;
            _log.Success("WSL platform update completed.");
        }
        else
        {
            result.ErrorMessage = ExtractFirstUsefulLine(proc.CombinedOutput)
                                  ?? $"wsl --update failed (exit {proc.ExitCode}).";
            _log.Error(result.ErrorMessage);
        }

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

        result.EndTime = DateTime.Now;
        return result;
    }

    private async Task<UpdateResult> UpgradeDistroPackagesAsync(
        ProgramInfo program,
        string distro,
        IProgress<UpdateProgress>? progress,
        int completed,
        int total,
        CancellationToken ct)
    {
        var result = new UpdateResult { Program = program, StartTime = DateTime.Now };

        progress?.Report(new UpdateProgress
        {
            ProgramName = program.Name,
            ProgramKey = program.DisplayKey,
            Completed = completed,
            Total = total,
            ItemPercent = 20,
            IsStarting = true,
            Message = $"Updating packages in {distro}…"
        });

        _log.Info($"Upgrading packages inside WSL distro '{distro}'…");

        // Run as root so package managers do not prompt for a sudo password.
        var script =
            "set -e; " +
            "if command -v apt-get >/dev/null 2>&1; then " +
            "  export DEBIAN_FRONTEND=noninteractive; " +
            "  apt-get update -y; " +
            "  apt-get upgrade -y; " +
            "elif command -v dnf >/dev/null 2>&1; then " +
            "  dnf upgrade -y; " +
            "elif command -v yum >/dev/null 2>&1; then " +
            "  yum upgrade -y; " +
            "elif command -v zypper >/dev/null 2>&1; then " +
            "  zypper --non-interactive update; " +
            "elif command -v pacman >/dev/null 2>&1; then " +
            "  pacman -Syu --noconfirm; " +
            "elif command -v apk >/dev/null 2>&1; then " +
            "  apk update && apk upgrade; " +
            "else " +
            "  echo 'No supported package manager found' >&2; exit 2; " +
            "fi";

        var proc = await ProcessRunner.RunAsync(
            "wsl.exe",
            ["-d", distro, "-u", "root", "--", "sh", "-lc", script],
            new ProcessRunOptions
            {
                TimeoutSeconds = 3600,
                ShowWindow = false
            },
            ct).ConfigureAwait(false);

        result.Output = proc.CombinedOutput;
        result.Success = proc.Success ||
                         proc.CombinedOutput.Contains("0 upgraded", StringComparison.OrdinalIgnoreCase) ||
                         proc.CombinedOutput.Contains("Nothing to do", StringComparison.OrdinalIgnoreCase) ||
                         proc.CombinedOutput.Contains("No packages marked for update", StringComparison.OrdinalIgnoreCase);

        if (result.Success)
        {
            program.UpdateAvailable = false;
            program.AvailableVersion = string.Empty;
            program.Version = "Up to date";
            program.LastUpdated = DateTime.Now;
            _log.Success($"WSL distro '{distro}' packages updated.");
        }
        else
        {
            result.ErrorMessage = ExtractFirstUsefulLine(proc.CombinedOutput)
                                  ?? $"Distro package upgrade failed (exit {proc.ExitCode}).";
            _log.Error($"WSL distro '{distro}': {result.ErrorMessage}");
        }

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

        result.EndTime = DateTime.Now;
        return result;
    }

    private async Task<(string? WslVersion, string? KernelVersion)> GetVersionInfoAsync(CancellationToken ct)
    {
        // wsl --version prints multi-line version info on recent builds.
        var ver = await ProcessRunner.RunAsync(
            "wsl.exe",
            ["--version"],
            ct,
            timeoutSeconds: 30).ConfigureAwait(false);

        var text = ver.StdOut + "\n" + ver.StdErr;
        string? wslVersion = null;
        string? kernel = null;

        var m = VersionLineRegex.Match(text);
        if (m.Success)
            wslVersion = m.Groups["v"].Value.Trim();

        var k = KernelVersionRegex.Match(text);
        if (k.Success)
            kernel = k.Groups["v"].Value.Trim();

        // Older builds: first non-empty line may be the version
        if (string.IsNullOrWhiteSpace(wslVersion))
        {
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Regex.IsMatch(line, @"^\d+(\.\d+)+"))
                {
                    wslVersion = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(wslVersion))
        {
            // Fallback: status output
            var status = await ProcessRunner.RunAsync(
                "wsl.exe",
                ["--status"],
                ct,
                timeoutSeconds: 30).ConfigureAwait(false);
            var st = status.StdOut + "\n" + status.StdErr;
            var km = KernelVersionRegex.Match(st);
            if (km.Success)
                kernel ??= km.Groups["v"].Value.Trim();
        }

        return (wslVersion, kernel);
    }

    private async Task<IReadOnlyList<WslDistro>> ListDistrosAsync(CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            "wsl.exe",
            ["--list", "--verbose"],
            ct,
            timeoutSeconds: 30).ConfigureAwait(false);

        var text = result.StdOut;
        if (string.IsNullOrWhiteSpace(text))
            text = result.StdErr;

        // wsl often emits UTF-16; ProcessRunner uses UTF-8 — also strip NULs if present.
        text = text.Replace("\0", string.Empty);

        if (text.Contains("has no installed distributions", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no installed distributions", StringComparison.OrdinalIgnoreCase))
            return [];

        var list = new List<WslDistro>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line))
                continue;
            if (line.StartsWith("NAME", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("---", StringComparison.Ordinal))
                continue;
            if (line.Contains("no installed distributions", StringComparison.OrdinalIgnoreCase))
                continue;

            // Format: * Ubuntu    Running    2
            var star = line.StartsWith('*');
            if (star)
                line = line.TrimStart('*').Trim();

            var parts = Regex.Split(line, @"\s{2,}").Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            if (parts.Length == 0)
            {
                parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            if (parts.Length < 1)
                continue;

            var name = parts[0].Trim();
            if (string.IsNullOrWhiteSpace(name) ||
                name.Equals("docker-desktop", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("docker-desktop-data", StringComparison.OrdinalIgnoreCase))
                continue;

            var state = parts.Length > 1 ? parts[1].Trim() : "";
            var ver = parts.Length > 2 ? parts[^1].Trim() : "";
            list.Add(new WslDistro(
                name,
                string.IsNullOrWhiteSpace(ver) ? (star ? "default" : "—") : $"WSL{ver}",
                state));
        }

        return list;
    }

    private async Task<int> CountPendingPackagesAsync(string distro, CancellationToken ct)
    {
        // Quiet probe — returns a single integer count of upgradable packages.
        var script =
            "if command -v apt-get >/dev/null 2>&1; then " +
            "  export DEBIAN_FRONTEND=noninteractive; " +
            "  apt-get -qq update >/dev/null 2>&1 || true; " +
            "  apt-get -s upgrade 2>/dev/null | grep -c '^Inst ' || true; " +
            "elif command -v dnf >/dev/null 2>&1; then " +
            "  dnf -q check-update 2>/dev/null | grep -E '^[a-zA-Z0-9]' | wc -l || true; " +
            "elif command -v zypper >/dev/null 2>&1; then " +
            "  zypper --non-interactive lu 2>/dev/null | grep -c 'v |' || true; " +
            "elif command -v pacman >/dev/null 2>&1; then " +
            "  pacman -Qu 2>/dev/null | wc -l || true; " +
            "elif command -v apk >/dev/null 2>&1; then " +
            "  apk update >/dev/null 2>&1; apk upgrade -s 2>/dev/null | grep -c '^(' || true; " +
            "else echo 0; fi";

        try
        {
            var proc = await ProcessRunner.RunAsync(
                "wsl.exe",
                ["-d", distro, "-u", "root", "--", "sh", "-lc", script],
                new ProcessRunOptions { TimeoutSeconds = 300, ShowWindow = false },
                ct).ConfigureAwait(false);

            var text = (proc.StdOut + "\n" + proc.StdErr).Replace("\0", string.Empty).Trim();
            // Last integer line wins
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
            {
                if (int.TryParse(line.Trim(), out var n) && n >= 0)
                    return n;
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"WSL distro '{distro}' package probe failed: {ex.Message}");
        }

        return 0;
    }

    /// <summary>
    /// Best-effort probe: runs <c>wsl --update</c> is too heavy to always apply;
    /// instead inspect recent status / version. When virtualization is off the platform
    /// package may still update. Returns true only when evidence suggests a newer build.
    /// </summary>
    private async Task<bool> ProbePlatformNeedsUpdateAsync(CancellationToken ct)
    {
        // Avoid false positives: only mark available when winget listed an upgrade
        // (handled by caller) or when Microsoft Update / store package is older.
        // Default: do not spam "update available" when already current.
        try
        {
            // Compare local WSL version to winget show Microsoft.WSL version (catalog latest).
            var (local, _) = await GetVersionInfoAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(local))
                return false;

            var show = await ProcessRunner.RunAsync(
                "winget",
                [
                    "show",
                    "--id", "Microsoft.WSL",
                    "--exact",
                    "--accept-source-agreements",
                    "--disable-interactivity"
                ],
                ct,
                timeoutSeconds: 60).ConfigureAwait(false);

            var text = show.StdOut;
            if (string.IsNullOrWhiteSpace(text))
                text = show.StdErr;

            string? catalog = null;
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
                {
                    catalog = t["Version:".Length..].Trim();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(catalog) || VersionText.IsUnknown(catalog))
                return false;

            return VersionComparer.IsNewer(local!, catalog) == true;
        }
        catch (Exception ex)
        {
            _log.Warn($"WSL platform probe failed: {ex.Message}");
            return false;
        }
    }

    private async Task<WingetWslInfo?> TryGetWingetWslUpgradeAsync(CancellationToken ct)
    {
        try
        {
            // Installed version from winget list
            var list = await ProcessRunner.RunAsync(
                "winget",
                [
                    "list",
                    "--id", "Microsoft.WSL",
                    "--exact",
                    "--accept-source-agreements",
                    "--disable-interactivity"
                ],
                ct,
                timeoutSeconds: 90).ConfigureAwait(false);

            var listText = list.StdOut;
            if (string.IsNullOrWhiteSpace(listText))
                listText = list.StdErr;

            string installed = "Unknown";
            string available = string.Empty;
            if (!string.IsNullOrWhiteSpace(listText) &&
                !listText.Contains("No installed package found", StringComparison.OrdinalIgnoreCase))
            {
                var rows = WingetTableParser.Parse(listText);
                foreach (var row in rows)
                {
                    row.Columns.TryGetValue("Id", out var id);
                    if (!string.Equals(id, "Microsoft.WSL", StringComparison.OrdinalIgnoreCase))
                        continue;
                    row.Columns.TryGetValue("Version", out var ver);
                    row.Columns.TryGetValue("Available", out var avail);
                    if (!string.IsNullOrWhiteSpace(ver))
                        installed = ver.Trim();
                    if (!string.IsNullOrWhiteSpace(avail) &&
                        !avail.Trim().Equals("-", StringComparison.Ordinal) &&
                        !avail.Trim().Equals("—", StringComparison.Ordinal))
                        available = avail.Trim();
                    break;
                }
            }

            // Explicit upgrade listing
            var up = await ProcessRunner.RunAsync(
                "winget",
                [
                    "upgrade",
                    "--id", "Microsoft.WSL",
                    "--exact",
                    "--accept-source-agreements",
                    "--disable-interactivity"
                ],
                ct,
                timeoutSeconds: 90).ConfigureAwait(false);

            var upText = up.StdOut;
            if (string.IsNullOrWhiteSpace(upText))
                upText = up.StdErr;

            var hasUpgrade = !string.IsNullOrWhiteSpace(upText) &&
                             !upText.Contains("No installed package has an available upgrade", StringComparison.OrdinalIgnoreCase) &&
                             !upText.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase) &&
                             !upText.Contains("No installed package found", StringComparison.OrdinalIgnoreCase);

            if (hasUpgrade)
            {
                var rows = WingetTableParser.Parse(upText);
                foreach (var row in rows)
                {
                    row.Columns.TryGetValue("Id", out var id);
                    if (!string.Equals(id, "Microsoft.WSL", StringComparison.OrdinalIgnoreCase) &&
                        (id is null || !id.Contains("WSL", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    row.Columns.TryGetValue("Version", out var ver);
                    row.Columns.TryGetValue("Available", out var avail);
                    if (!string.IsNullOrWhiteSpace(ver))
                        installed = ver.Trim();
                    if (!string.IsNullOrWhiteSpace(avail))
                        available = avail.Trim();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(available) || VersionText.IsUnknown(available))
            {
                // No winget upgrade row — still allow wsl --update path
                return null;
            }

            var newer = VersionText.IsUnknown(installed) ||
                        VersionComparer.IsNewer(installed, available) == true;

            return new WingetWslInfo(installed, available, newer);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not query Microsoft.WSL via winget: {ex.Message}");
            return null;
        }
    }

    private static bool IsWslUpdateSuccess(ProcessResult proc)
    {
        if (proc.Success)
            return true;

        var text = proc.CombinedOutput;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("already installed", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("most recent version", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("successfully", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Installation successful", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractFirstUsefulLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (line.Length < 3)
                continue;
            if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Error code", StringComparison.OrdinalIgnoreCase))
                return line;
        }

        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
    }

    private sealed record WslDistro(string Name, string VersionLabel, string State);
    private sealed record WingetWslInfo(string Installed, string Available, bool UpdateAvailable);
}

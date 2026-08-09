using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ApplicationUpdater.Helpers;
using ApplicationUpdater.Models;

namespace ApplicationUpdater.Services;

/// <summary>
/// Checks GitHub Releases for tracked repositories and optional self-update.
/// </summary>
public sealed class GitHubUpdateService
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    private static readonly HttpClient Http = CreateClient();

    public GitHubUpdateService(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"WindowsPatchManager/{AppInfo.Version} (+https://github.com/{AppInfo.GitHubOwner}/{AppInfo.GitHubRepo})");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    /// <summary>
    /// Checks only this application's GitHub release for a newer version.
    /// Independent of the general "GitHub tracked projects" package list (uses SelfUpdate settings).
    /// </summary>
    public async Task<ProgramInfo?> CheckSelfUpdateAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var su = _config.Config.GitHub.SelfUpdate;
        if (!su.Enabled)
        {
            _log.Info("Self-update is disabled in Options.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(su.Owner) || string.IsNullOrWhiteSpace(su.Repo))
        {
            _log.Warn("Self-update repo is not configured.");
            return null;
        }

        ApplyToken(_config.Config.GitHub.Token);
        progress?.Report(new ScanProgress
        {
            Message = $"Checking for {AppInfo.ProductName} updates…",
            Percent = 10
        });

        try
        {
            var repo = new GitHubTrackedRepo
            {
                Owner = su.Owner.Trim(),
                Repo = su.Repo.Trim(),
                DisplayName = AppInfo.ProductName,
                InstalledVersion = AppInfo.Version,
                AssetPattern = string.IsNullOrWhiteSpace(su.AssetPattern) ? "Setup" : su.AssetPattern
            };

            var item = await CheckOneAsync(repo, preferSelfUpdateAssets: true, ct).ConfigureAwait(false);
            if (item is null)
            {
                progress?.Report(new ScanProgress { Message = "Self-update check finished", Percent = 100 });
                return null;
            }

            // Always stamp self package id so install path is unambiguous
            item.PackageId = $"github-self:{repo.Owner}/{repo.Repo}";
            item.Name = AppInfo.ProductName;
            item.Version = AppInfo.Version;

            if (item.UpdateAvailable)
                _log.Info(
                    $"Self-update available: {AppInfo.Version} → {item.AvailableVersion} " +
                    $"({item.Notes ?? item.DownloadUrl})");
            else
                _log.Info($"Self-update: already on latest ({AppInfo.Version}; remote {item.AvailableVersion}).");

            progress?.Report(new ScanProgress
            {
                Message = item.UpdateAvailable
                    ? $"Update available: {item.AvailableVersion}"
                    : "Application is up to date",
                Percent = 100
            });
            return item;
        }
        catch (Exception ex)
        {
            _log.Warn($"Self-update check failed: {ex.Message}");
            progress?.Report(new ScanProgress { Message = "Self-update check failed", Percent = 100 });
            return null;
        }
    }

    /// <summary>
    /// Downloads the self-update installer and launches it elevated, then signals success
    /// so the host can shut down and unlock files.
    /// </summary>
    public async Task<UpdateResult> InstallSelfUpdateAsync(
        ProgramInfo program,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        var start = DateTime.Now;
        var result = new UpdateResult { Program = program, StartTime = start };

        try
        {
            if (string.IsNullOrWhiteSpace(program.DownloadUrl))
                throw new InvalidOperationException("No download URL for this release asset.");

            var tempDir = Path.Combine(Path.GetTempPath(), "WindowsPatchManager", "self-update");
            Directory.CreateDirectory(tempDir);

            var fileName = Path.GetFileName(new Uri(program.DownloadUrl).LocalPath);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "WindowsPatchManager-Setup.exe";
            // Unique name so repeated updates don't hit a locked previous file
            var dest = Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(fileName)}_{program.AvailableVersion}{Path.GetExtension(fileName)}");

            progress?.Report(new UpdateProgress
            {
                ProgramName = program.Name,
                ProgramKey = program.DisplayKey,
                Completed = 0,
                Total = 1,
                ItemPercent = 5,
                IsStarting = true,
                Message = "Downloading update…"
            });

            _log.Info($"Downloading self-update to {dest}");
            await DownloadAsync(program.DownloadUrl, dest, p =>
            {
                progress?.Report(new UpdateProgress
                {
                    ProgramName = program.Name,
                    ProgramKey = program.DisplayKey,
                    Completed = 0,
                    Total = 1,
                    ItemPercent = Math.Clamp(5 + (int)(p * 75), 5, 80),
                    Message = $"Downloading… {(int)(p * 100)}%"
                });
            }, ct).ConfigureAwait(false);

            progress?.Report(new UpdateProgress
            {
                ProgramName = program.Name,
                ProgramKey = program.DisplayKey,
                Completed = 0,
                Total = 1,
                ItemPercent = 85,
                Message = "Starting installer…"
            });

            LaunchInstallerAndExitHost(dest);

            result.Success = true;
            result.Output = dest;
            if (!string.IsNullOrWhiteSpace(program.AvailableVersion))
                program.Version = program.AvailableVersion;
            program.UpdateAvailable = false;
            program.LastUpdated = DateTime.Now;
            _log.Success($"Self-update installer launched: {dest}");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            _log.Error($"Self-update install failed: {ex.Message}");
        }

        result.EndTime = DateTime.Now;
        progress?.Report(new UpdateProgress
        {
            ProgramName = program.Name,
            ProgramKey = program.DisplayKey,
            Success = result.Success,
            Completed = 1,
            Total = 1,
            ItemPercent = 100,
            OverallPercent = 100,
            Message = result.Success ? "Installer started — closing app…" : result.ErrorMessage
        });
        return result;
    }

    public async Task<IReadOnlyList<ProgramInfo>> CheckTrackedAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var gh = _config.Config.GitHub;
        if (!gh.Enabled || !_config.Config.UpdateSources.GitHub.Enabled)
            return [];

        ApplyToken(gh.Token);

        var list = new List<ProgramInfo>();
        var repos = gh.Repositories.Where(r =>
            !string.IsNullOrWhiteSpace(r.Owner) && !string.IsNullOrWhiteSpace(r.Repo)).ToList();

        // Include self as a list row too (install path uses InstallAsync)
        if (gh.SelfUpdate.Enabled &&
            !string.IsNullOrWhiteSpace(gh.SelfUpdate.Owner) &&
            !string.IsNullOrWhiteSpace(gh.SelfUpdate.Repo))
        {
            repos.Insert(0, new GitHubTrackedRepo
            {
                Owner = gh.SelfUpdate.Owner,
                Repo = gh.SelfUpdate.Repo,
                DisplayName = AppInfo.ProductName,
                InstalledVersion = AppInfo.Version,
                AssetPattern = gh.SelfUpdate.AssetPattern
            });
        }

        for (var i = 0; i < repos.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = repos[i];
            var pct = repos.Count == 0 ? 0 : (int)((i + 1) * 100.0 / repos.Count);
            progress?.Report(new ScanProgress
            {
                Message = $"GitHub: {r.Owner}/{r.Repo}",
                Percent = pct
            });

            try
            {
                var isSelf = string.Equals(r.Owner, gh.SelfUpdate.Owner, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(r.Repo, gh.SelfUpdate.Repo, StringComparison.OrdinalIgnoreCase);
                var item = await CheckOneAsync(r, preferSelfUpdateAssets: isSelf, ct).ConfigureAwait(false);
                if (item is not null)
                    list.Add(item);
            }
            catch (Exception ex)
            {
                _log.Warn($"GitHub {r.Owner}/{r.Repo}: {ex.Message}");
            }
        }

        return list;
    }

    public async Task<UpdateResult> InstallAsync(
        ProgramInfo program,
        IProgress<UpdateProgress>? progress,
        int completed,
        int total,
        CancellationToken ct)
    {
        // Self-update package: use dedicated path that elevates and expects app exit
        if (program.PackageId.StartsWith("github-self:", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(program.Name, AppInfo.ProductName, StringComparison.OrdinalIgnoreCase) &&
             program.Source == PackageSource.GitHub))
        {
            return await InstallSelfUpdateAsync(program, progress, ct).ConfigureAwait(false);
        }

        var start = DateTime.Now;
        var result = new UpdateResult { Program = program, StartTime = start };

        try
        {
            if (string.IsNullOrWhiteSpace(program.DownloadUrl))
                throw new InvalidOperationException("No download URL for this GitHub release.");

            var tempDir = Path.Combine(Path.GetTempPath(), "WindowsPatchManager", "github");
            Directory.CreateDirectory(tempDir);
            var fileName = Path.GetFileName(new Uri(program.DownloadUrl).LocalPath);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "update.bin";
            var dest = Path.Combine(tempDir, fileName);

            progress?.Report(new UpdateProgress
            {
                ProgramName = program.Name,
                ProgramKey = program.DisplayKey,
                Completed = completed,
                Total = total,
                ItemPercent = 5,
                IsStarting = true,
                Message = "Downloading…"
            });

            await DownloadAsync(program.DownloadUrl, dest, p =>
            {
                progress?.Report(new UpdateProgress
                {
                    ProgramName = program.Name,
                    ProgramKey = program.DisplayKey,
                    Completed = completed,
                    Total = total,
                    ItemPercent = Math.Clamp(5 + (int)(p * 0.7), 5, 75),
                    OverallPercent = total == 0 ? -1 : (int)((completed + p) * 100.0 / total),
                    Message = $"Downloading… {(int)(p * 100)}%"
                });
            }, ct).ConfigureAwait(false);

            progress?.Report(new UpdateProgress
            {
                ProgramName = program.Name,
                ProgramKey = program.DisplayKey,
                Completed = completed,
                Total = total,
                ItemPercent = 80,
                Message = "Installing…"
            });

            var ext = Path.GetExtension(dest).ToLowerInvariant();
            ProcessResult run;
            if (ext is ".msi")
            {
                run = await ProcessRunner.RunAsync(
                    "msiexec.exe",
                    ["/i", dest, "/qb", "/norestart"],
                    new ProcessRunOptions { TimeoutSeconds = 1800, Elevate = true, ShowWindow = true },
                    ct).ConfigureAwait(false);
            }
            else
            {
                run = await ProcessRunner.RunAsync(
                    dest,
                    [],
                    new ProcessRunOptions { TimeoutSeconds = 1800, Elevate = true, ShowWindow = true },
                    ct).ConfigureAwait(false);
            }

            result.Success = run.ExitCode is 0 or 3010;
            result.Output = run.CombinedOutput;
            result.ErrorMessage = result.Success ? string.Empty : $"Installer exit code {run.ExitCode}";
            if (result.Success)
            {
                if (!string.IsNullOrWhiteSpace(program.AvailableVersion))
                    program.Version = program.AvailableVersion;
                program.UpdateAvailable = false;
                program.LastUpdated = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        result.EndTime = DateTime.Now;
        progress?.Report(new UpdateProgress
        {
            ProgramName = program.Name,
            ProgramKey = program.DisplayKey,
            Success = result.Success,
            Completed = completed + 1,
            Total = total,
            ItemPercent = 100,
            OverallPercent = total == 0 ? 100 : (int)((completed + 1) * 100.0 / total),
            Message = result.Success ? "Done" : result.ErrorMessage
        });

        return result;
    }

    private async Task<ProgramInfo?> CheckOneAsync(
        GitHubTrackedRepo r,
        bool preferSelfUpdateAssets,
        CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{r.Owner}/{r.Repo}/releases/latest";
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = "";
            try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { /* ignore */ }
            _log.Warn($"GitHub {r.Owner}/{r.Repo}: HTTP {(int)resp.StatusCode} {Truncate(body, 200)}");
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString()?.Trim() ?? "";
        var version = tag.TrimStart('v', 'V');
        var published = root.TryGetProperty("published_at", out var pub)
            && DateTime.TryParse(pub.GetString(), out var dt)
            ? dt.ToLocalTime()
            : (DateTime?)null;

        string? assetUrl = null;
        string? assetName = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var candidates = new List<(string Name, string Url, int Rank)>();
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var dl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(dl))
                    continue;
                candidates.Add((name, dl!, RankAsset(name, r.AssetPattern, preferSelfUpdateAssets)));
            }

            var best = candidates
                .Where(c => c.Rank < 1000)
                .OrderBy(c => c.Rank)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(best.Url))
            {
                assetName = best.Name;
                assetUrl = best.Url;
            }
        }

        // Fallback: some releases only attach a source zip — still report version if newer
        var installed = string.IsNullOrWhiteSpace(r.InstalledVersion) ? AppInfo.Version : r.InstalledVersion.Trim();
        var newer = VersionComparer.IsNewer(installed, version);
        var update = newer == true;

        if (update && string.IsNullOrWhiteSpace(assetUrl))
        {
            _log.Warn(
                $"GitHub {r.Owner}/{r.Repo} {version} is newer than {installed} but no matching " +
                $"installer asset was found (pattern '{r.AssetPattern}').");
        }

        return new ProgramInfo
        {
            Name = string.IsNullOrWhiteSpace(r.DisplayName) ? $"{r.Owner}/{r.Repo}" : r.DisplayName,
            Version = installed,
            AvailableVersion = version,
            UpdateAvailable = update && !string.IsNullOrWhiteSpace(assetUrl),
            Publisher = r.Owner,
            PackageId = $"github:{r.Owner}/{r.Repo}",
            Source = PackageSource.GitHub,
            Category = UpdateCategory.Programs,
            GitHubOwner = r.Owner,
            GitHubRepo = r.Repo,
            DownloadUrl = assetUrl,
            Notes = assetName,
            LastUpdated = published,
            Origin = "GitHub"
        };
    }

    /// <summary>
    /// Lower rank = better. Prefers Burn Setup.exe, then MSI, then pattern match.
    /// </summary>
    private static int RankAsset(string name, string pattern, bool preferSelfUpdateAssets)
    {
        if (string.IsNullOrWhiteSpace(name))
            return 1000;

        var n = name.ToLowerInvariant();
        // Skip symbols / docs
        if (n.EndsWith(".pdb") || n.EndsWith(".zip") && n.Contains("source") ||
            n.EndsWith(".txt") || n.EndsWith(".md") || n.EndsWith(".blockmap"))
            return 1000;

        if (preferSelfUpdateAssets)
        {
            if (n.Contains("setup") && n.EndsWith(".exe"))
                return 0;
            if (n.Contains("windows") && n.Contains("patch") && n.EndsWith(".exe"))
                return 1;
            if (n.EndsWith(".msi") && n.Contains("patch"))
                return 2;
            if (n.EndsWith(".msi"))
                return 3;
            if (n.EndsWith(".exe") && MatchesPattern(name, pattern))
                return 4;
            if (MatchesPattern(name, pattern))
                return 5;
            if (n.EndsWith(".exe"))
                return 20;
            if (n.EndsWith(".msi"))
                return 21;
            return 1000;
        }

        if (MatchesPattern(name, pattern))
            return 0;
        return 1000;
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return true;
        return name.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyToken(string? token)
    {
        Http.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Starts the installer elevated and does not wait for completion (app must exit to unlock files).
    /// </summary>
    private static void LaunchInstallerAndExitHost(string installerPath)
    {
        if (!File.Exists(installerPath))
            throw new FileNotFoundException("Downloaded installer not found.", installerPath);

        var ext = Path.GetExtension(installerPath).ToLowerInvariant();
        ProcessStartInfo psi;
        if (ext is ".msi")
        {
            // Passive UI so the user sees progress; norestart keeps control with the user.
            psi = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = $"/i \"{installerPath}\" /passive /norestart",
                UseShellExecute = true,
                Verb = "runas"
            };
        }
        else
        {
            // Burn / Inno / NSIS setup — show UI (self-update should not be fully silent)
            psi = new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
                Verb = "runas"
            };
        }

        try
        {
            var proc = Process.Start(psi);
            if (proc is null)
                throw new InvalidOperationException("Could not start the installer process.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Administrator elevation was cancelled. Update was not installed.");
        }
    }

    private static async Task DownloadAsync(
        string url,
        string dest,
        Action<double> progress01,
        CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(dest);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total > 0)
                progress01(read / (double)total);
            else
                progress01(0.5);
        }
        progress01(1);
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max] + "…";
}

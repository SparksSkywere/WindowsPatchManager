using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
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
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsPatchManager/1.0 (+SkywereIndustries)");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
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

        if (gh.SelfUpdate.Enabled &&
            !string.IsNullOrWhiteSpace(gh.SelfUpdate.Owner) &&
            !string.IsNullOrWhiteSpace(gh.SelfUpdate.Repo))
        {
            repos.Insert(0, new GitHubTrackedRepo
            {
                Owner = gh.SelfUpdate.Owner,
                Repo = gh.SelfUpdate.Repo,
                DisplayName = AppInfo.ProductName,
                InstalledVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3),
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
                var item = await CheckOneAsync(r, ct).ConfigureAwait(false);
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
                    new[] { "/i", dest, "/qn", "/norestart" },
                    ct,
                    timeoutSeconds: 1800).ConfigureAwait(false);
            }
            else
            {
                // Typical Inno/NSIS silent flags
                run = await ProcessRunner.RunAsync(
                    dest,
                    new[] { "/VERYSILENT", "/NORESTART", "/SUPPRESSMSGBOXES" },
                    ct,
                    timeoutSeconds: 1800).ConfigureAwait(false);
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

    private async Task<ProgramInfo?> CheckOneAsync(GitHubTrackedRepo r, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{r.Owner}/{r.Repo}/releases/latest";
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _log.Warn($"GitHub {r.Owner}/{r.Repo}: HTTP {(int)resp.StatusCode}");
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
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (!MatchesPattern(name, r.AssetPattern))
                    continue;
                assetName = name;
                assetUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                break;
            }
        }

        var installed = r.InstalledVersion?.Trim() ?? "Unknown";
        var newer = VersionComparer.IsNewer(installed, version);
        var update = newer == true ||
                     (VersionText.IsUnknown(installed) && !string.IsNullOrWhiteSpace(version));

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
            LastUpdated = published
        };
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
}

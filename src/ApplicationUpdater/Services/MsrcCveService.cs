using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationUpdater.Models;
using Microsoft.Win32;

namespace ApplicationUpdater.Services;

/// <summary>
/// Microsoft Security Response Center (MSRC) CVRF client — maps recent Critical/Important
/// CVEs to KB articles for the local Windows edition and reports missing security KBs.
/// </summary>
public sealed class MsrcCveService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex KbNumberRegex = new(@"^(?:KB)?(?<n>\d{6,7})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KbInTextRegex = new(@"KB(?<n>\d{6,7})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MonthIdRegex = new(@"^\d{4}-[A-Za-z]{3}$", RegexOptions.Compiled);

    private readonly ConfigService _config;
    private readonly LogService _log;

    public MsrcCveService(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>
    /// Scans recent MSRC monthly releases for Critical/Important Windows CVEs whose
    /// remediation KBs are not installed and not already pending in Windows Update.
    /// </summary>
    public async Task<IReadOnlyList<ProgramInfo>> FindMissingSecurityKbsAsync(
        IReadOnlySet<string> installedKbs,
        IReadOnlyList<ProgramInfo> pendingUpdates,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var wu = _config.Config.WindowsUpdate;
        if (!wu.CveScanEnabled || !wu.QueryMsrcOnline)
            return [];

        progress?.Report(new ScanProgress { Message = "Querying MSRC for security CVEs…", Percent = 5 });

        try
        {
            var months = await ListRecentSecurityUpdateIdsAsync(wu.MsrcMonthsToScan, ct).ConfigureAwait(false);
            if (months.Count == 0)
            {
                _log.Warn("MSRC returned no recent security update releases.");
                return [];
            }

            var pendingKb = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in pendingUpdates)
            {
                foreach (var kb in SplitKbs(p.KbId))
                    pendingKb.Add(NormalizeKb(kb));
            }

            var osHints = GetLocalOsProductHints();
            _log.Info($"MSRC CVE scan: {months.Count} release(s); OS hints: {string.Join(", ", osHints)}");

            var byKb = new Dictionary<string, MsrcKbHit>(StringComparer.OrdinalIgnoreCase);
            var monthIndex = 0;
            foreach (var monthId in months)
            {
                ct.ThrowIfCancellationRequested();
                monthIndex++;
                var pct = 10 + (int)(monthIndex * 70.0 / months.Count);
                progress?.Report(new ScanProgress
                {
                    Message = $"MSRC {monthId}: loading CVE/KB map…",
                    Percent = Math.Clamp(pct, 10, 85)
                });

                await MergeCvrfMonthAsync(monthId, osHints, wu.MsrcCriticalAndImportantOnly, byKb, ct)
                    .ConfigureAwait(false);
            }

            var missing = new List<ProgramInfo>();
            foreach (var (kbNorm, hit) in byKb
                         .OrderBy(kv => SeverityRank(kv.Value.MaxSeverity))
                         .ThenByDescending(kv => kv.Value.Cves.Count))
            {
                if (installedKbs.Contains(kbNorm) || installedKbs.Contains("KB" + kbNorm))
                    continue;
                if (pendingKb.Contains(kbNorm) || pendingKb.Contains("KB" + kbNorm))
                    continue;

                var cveList = string.Join(", ", hit.Cves.OrderByDescending(c => c, StringComparer.OrdinalIgnoreCase).Take(8));
                if (hit.Cves.Count > 8)
                    cveList += $" (+{hit.Cves.Count - 8} more)";

                var severity = hit.MaxSeverity;
                var kbDisplay = "KB" + kbNorm;
                var title = string.IsNullOrWhiteSpace(hit.TitleHint)
                    ? $"{kbDisplay} security update"
                    : hit.TitleHint;

                missing.Add(new ProgramInfo
                {
                    Name = $"{kbDisplay} — {title}",
                    Version = "Not installed",
                    AvailableVersion = $"{kbDisplay} · {severity}",
                    UpdateAvailable = true,
                    Publisher = "Microsoft · MSRC",
                    PackageId = $"msrc:{kbNorm}",
                    KbId = kbDisplay,
                    Source = PackageSource.WindowsUpdate,
                    Category = UpdateCategory.WindowsUpdates,
                    Origin = "MSRC CVE",
                    IsSecurityUpdate = true,
                    Severity = severity,
                    SeverityRank = SeverityRank(severity),
                    CveIds = cveList,
                    Classification = "Security Updates",
                    Notes = $"Required security KB from MSRC ({string.Join(", ", hit.MonthIds)}). " +
                            $"Install via Windows Update when offered. CVEs: {cveList}",
                    DownloadUrl = $"https://support.microsoft.com/help/{kbNorm}"
                });
            }

            progress?.Report(new ScanProgress
            {
                Message = missing.Count == 0
                    ? "MSRC: no missing Critical/Important KBs detected"
                    : $"MSRC: {missing.Count} missing security KB(s)",
                Percent = 100
            });
            _log.Info($"MSRC CVE gap scan: {byKb.Count} relevant KB(s), {missing.Count} not installed / not pending.");
            return missing;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"MSRC CVE scan failed: {ex.Message}");
            progress?.Report(new ScanProgress { Message = "MSRC CVE scan failed (offline?)", Percent = 100 });
            return [];
        }
    }

    /// <summary>
    /// Fills empty CVE/severity fields on pending WU rows using the same MSRC feeds.
    /// </summary>
    public async Task EnrichPendingWithMsrcAsync(
        IList<ProgramInfo> pending,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var wu = _config.Config.WindowsUpdate;
        if (!wu.CveScanEnabled || !wu.QueryMsrcOnline || pending.Count == 0)
            return;

        var needEnrich = pending.Where(p =>
            !string.IsNullOrWhiteSpace(p.KbId) &&
            (string.IsNullOrWhiteSpace(p.CveIds) || string.IsNullOrWhiteSpace(p.Severity))).ToList();
        if (needEnrich.Count == 0)
            return;

        try
        {
            progress?.Report(new ScanProgress { Message = "Enriching KBs with MSRC CVE data…", Percent = 88 });
            var months = await ListRecentSecurityUpdateIdsAsync(wu.MsrcMonthsToScan, ct).ConfigureAwait(false);
            var osHints = GetLocalOsProductHints();
            var byKb = new Dictionary<string, MsrcKbHit>(StringComparer.OrdinalIgnoreCase);
            foreach (var monthId in months)
            {
                ct.ThrowIfCancellationRequested();
                await MergeCvrfMonthAsync(monthId, osHints, criticalImportantOnly: false, byKb, ct)
                    .ConfigureAwait(false);
            }

            foreach (var p in needEnrich)
            {
                foreach (var kb in SplitKbs(p.KbId))
                {
                    var norm = NormalizeKb(kb);
                    if (!byKb.TryGetValue(norm, out var hit))
                        continue;

                    if (string.IsNullOrWhiteSpace(p.CveIds) && hit.Cves.Count > 0)
                        p.CveIds = string.Join(", ", hit.Cves.Take(6));
                    if (string.IsNullOrWhiteSpace(p.Severity) && !string.IsNullOrWhiteSpace(hit.MaxSeverity))
                    {
                        p.Severity = hit.MaxSeverity;
                        p.SeverityRank = SeverityRank(hit.MaxSeverity);
                    }

                    p.IsSecurityUpdate = true;
                    if (string.IsNullOrWhiteSpace(p.Classification))
                        p.Classification = "Security Updates";
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"MSRC enrichment skipped: {ex.Message}");
        }
    }

    private async Task<List<string>> ListRecentSecurityUpdateIdsAsync(int months, CancellationToken ct)
    {
        months = Math.Clamp(months, 1, 6);
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.msrc.microsoft.com/cvrf/v3.0/updates");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var list = new List<(string Id, DateTime Date)>();
        if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        foreach (var item in value.EnumerateArray())
        {
            var id = item.TryGetProperty("ID", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || !MonthIdRegex.IsMatch(id))
                continue;

            var title = item.TryGetProperty("DocumentTitle", out var t) ? t.GetString() ?? "" : "";
            if (title.Contains("Mariner", StringComparison.OrdinalIgnoreCase) &&
                !title.Contains("Security Updates", StringComparison.OrdinalIgnoreCase))
                continue;

            var date = DateTime.MinValue;
            if (item.TryGetProperty("CurrentReleaseDate", out var d) && d.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(d.GetString(), out var parsed))
                date = parsed;
            else if (item.TryGetProperty("InitialReleaseDate", out var d2) && d2.ValueKind == JsonValueKind.String &&
                     DateTime.TryParse(d2.GetString(), out var parsed2))
                date = parsed2;

            list.Add((id, date));
        }

        return list
            .OrderByDescending(x => x.Date)
            .Select(x => x.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(months)
            .ToList();
    }

    private async Task MergeCvrfMonthAsync(
        string monthId,
        IReadOnlyList<string> osHints,
        bool criticalImportantOnly,
        Dictionary<string, MsrcKbHit> byKb,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.msrc.microsoft.com/cvrf/v3.0/cvrf/{Uri.EscapeDataString(monthId)}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _log.Warn($"MSRC CVRF {monthId}: HTTP {(int)resp.StatusCode}");
            return;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var products = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("ProductTree", out var tree) &&
            tree.TryGetProperty("FullProductName", out var full) &&
            full.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in full.EnumerateArray())
            {
                var pid = p.TryGetProperty("ProductID", out var idEl) ? idEl.GetString() : null;
                var name = p.TryGetProperty("Value", out var vEl) ? vEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(pid) && !string.IsNullOrWhiteSpace(name))
                    products[pid] = name;
            }
        }

        var relevantProductIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, name) in products)
        {
            if (ProductMatchesLocalOs(name, osHints))
                relevantProductIds.Add(pid);
        }

        if (relevantProductIds.Count == 0)
        {
            foreach (var (pid, name) in products)
            {
                if (name.Contains("Windows 11", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
                    relevantProductIds.Add(pid);
            }
        }

        if (!doc.RootElement.TryGetProperty("Vulnerability", out var vulns) ||
            vulns.ValueKind != JsonValueKind.Array)
            return;

        var matched = 0;
        foreach (var vuln in vulns.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var cve = vuln.TryGetProperty("CVE", out var cveEl) ? cveEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(cve) || !cve.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase))
                continue;

            var severity = ExtractMaxSeverity(vuln, relevantProductIds);
            if (criticalImportantOnly &&
                !severity.Equals("Critical", StringComparison.OrdinalIgnoreCase) &&
                !severity.Equals("Important", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrWhiteSpace(severity))
                severity = "Important";

            var title = "";
            if (vuln.TryGetProperty("Title", out var titleEl))
            {
                if (titleEl.ValueKind == JsonValueKind.Object && titleEl.TryGetProperty("Value", out var tv))
                    title = tv.GetString() ?? "";
                else if (titleEl.ValueKind == JsonValueKind.String)
                    title = titleEl.GetString() ?? "";
            }

            if (!vuln.TryGetProperty("Remediations", out var rems) || rems.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var rem in rems.EnumerateArray())
            {
                if (!RemediationTargetsProduct(rem, relevantProductIds))
                    continue;

                foreach (var kb in ExtractKbsFromRemediation(rem))
                {
                    var norm = NormalizeKb(kb);
                    if (string.IsNullOrWhiteSpace(norm))
                        continue;

                    if (!byKb.TryGetValue(norm, out var hit))
                    {
                        hit = new MsrcKbHit
                        {
                            MaxSeverity = severity,
                            TitleHint = string.IsNullOrWhiteSpace(title) ? cve! : title
                        };
                        byKb[norm] = hit;
                    }

                    hit.Cves.Add(cve!);
                    hit.MonthIds.Add(monthId);
                    if (SeverityRank(severity) < SeverityRank(hit.MaxSeverity))
                        hit.MaxSeverity = severity;
                    matched++;
                }
            }
        }

        _log.Info($"MSRC {monthId}: {matched} KB remediation link(s) for local OS products.");
    }

    private static string ExtractMaxSeverity(JsonElement vuln, HashSet<string> relevantProductIds)
    {
        if (!vuln.TryGetProperty("Threats", out var threats) || threats.ValueKind != JsonValueKind.Array)
            return "";

        var best = "";
        var bestRank = 99;
        foreach (var t in threats.EnumerateArray())
        {
            var type = t.TryGetProperty("Type", out var typeEl) && typeEl.TryGetInt32(out var ti) ? ti : -1;
            if (type != 3)
                continue;

            if (t.TryGetProperty("ProductID", out var pids) && pids.ValueKind == JsonValueKind.Array)
            {
                var any = false;
                foreach (var p in pids.EnumerateArray())
                {
                    var id = p.GetString();
                    if (!string.IsNullOrWhiteSpace(id) && relevantProductIds.Contains(id))
                    {
                        any = true;
                        break;
                    }
                }

                if (pids.GetArrayLength() > 0 && !any)
                    continue;
            }

            var sev = "";
            if (t.TryGetProperty("Description", out var desc))
            {
                if (desc.ValueKind == JsonValueKind.Object && desc.TryGetProperty("Value", out var v))
                    sev = v.GetString() ?? "";
                else if (desc.ValueKind == JsonValueKind.String)
                    sev = desc.GetString() ?? "";
            }

            var rank = SeverityRank(sev);
            if (rank < bestRank)
            {
                bestRank = rank;
                best = sev;
            }
        }

        return best;
    }

    private static bool RemediationTargetsProduct(JsonElement rem, HashSet<string> relevantProductIds)
    {
        if (!rem.TryGetProperty("ProductID", out var pids) || pids.ValueKind != JsonValueKind.Array)
            return true;
        if (pids.GetArrayLength() == 0)
            return true;

        foreach (var p in pids.EnumerateArray())
        {
            var id = p.GetString();
            if (!string.IsNullOrWhiteSpace(id) && relevantProductIds.Contains(id))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> ExtractKbsFromRemediation(JsonElement rem)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            text = text.Trim();
            var m = KbNumberRegex.Match(text);
            if (m.Success)
            {
                found.Add("KB" + m.Groups["n"].Value);
                return;
            }

            foreach (Match km in KbInTextRegex.Matches(text))
                found.Add("KB" + km.Groups["n"].Value);
        }

        if (rem.TryGetProperty("Description", out var desc))
        {
            if (desc.ValueKind == JsonValueKind.Object && desc.TryGetProperty("Value", out var v))
                Consider(v.GetString());
            else if (desc.ValueKind == JsonValueKind.String)
                Consider(desc.GetString());
        }

        if (rem.TryGetProperty("URL", out var url) && url.ValueKind == JsonValueKind.String)
            Consider(url.GetString());

        return found;
    }

    private static List<string> GetLocalOsProductHints()
    {
        var hints = new List<string>();
        try
        {
            var caption = "";
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Caption FROM Win32_OperatingSystem");
                foreach (var o in searcher.Get())
                {
                    caption = o["Caption"]?.ToString() ?? "";
                    break;
                }
            }
            catch { /* ignore */ }

            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var display = key?.GetValue("DisplayVersion") as string
                          ?? key?.GetValue("ReleaseId") as string
                          ?? "";
            var build = key?.GetValue("CurrentBuild") as string ?? "";
            var productName = key?.GetValue("ProductName") as string ?? caption;

            if (productName.Contains("Windows 11", StringComparison.OrdinalIgnoreCase) ||
                caption.Contains("Windows 11", StringComparison.OrdinalIgnoreCase))
                hints.Add("Windows 11");
            else if (productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) ||
                     caption.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
                hints.Add("Windows 10");
            else
            {
                hints.Add("Windows 11");
                hints.Add("Windows 10");
            }

            if (!string.IsNullOrWhiteSpace(display))
                hints.Add(display.Trim());

            hints.Add(Environment.Is64BitOperatingSystem ? "x64" : "32-bit");
            if (!string.IsNullOrWhiteSpace(build))
                hints.Add("build " + build);
        }
        catch
        {
            hints.Add("Windows 11");
            hints.Add("x64");
        }

        return hints;
    }

    private static bool ProductMatchesLocalOs(string productName, IReadOnlyList<string> hints)
    {
        if (string.IsNullOrWhiteSpace(productName))
            return false;

        if (productName.Contains("Azure Linux", StringComparison.OrdinalIgnoreCase) ||
            productName.Contains("CBL-Mariner", StringComparison.OrdinalIgnoreCase) ||
            productName.Contains("Mariner", StringComparison.OrdinalIgnoreCase))
            return false;

        var isWin11 = hints.Any(h => h.Equals("Windows 11", StringComparison.OrdinalIgnoreCase));
        var isWin10 = hints.Any(h => h.Equals("Windows 10", StringComparison.OrdinalIgnoreCase));

        if (isWin11 && productName.Contains("Windows 11", StringComparison.OrdinalIgnoreCase))
            return MatchesArch(productName, hints);

        if (isWin10 && productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
            return MatchesArch(productName, hints);

        return false;
    }

    private static bool MatchesArch(string productName, IReadOnlyList<string> hints)
    {
        var want64 = hints.Any(h => h.Contains("x64", StringComparison.OrdinalIgnoreCase));
        if (!want64)
            return true;

        if (productName.Contains("ARM64", StringComparison.OrdinalIgnoreCase))
            return false;
        if (productName.Contains("32-bit", StringComparison.OrdinalIgnoreCase) &&
            !productName.Contains("x64", StringComparison.OrdinalIgnoreCase))
            return false;

        return productName.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
               productName.Contains("64-based", StringComparison.OrdinalIgnoreCase) ||
               !productName.Contains("32-bit", StringComparison.OrdinalIgnoreCase);
    }

    internal static int SeverityRank(string? severity)
    {
        if (string.IsNullOrWhiteSpace(severity))
            return 40;
        return severity.Trim().ToLowerInvariant() switch
        {
            "critical" => 0,
            "important" => 10,
            "moderate" => 20,
            "low" => 30,
            _ => 40
        };
    }

    internal static string NormalizeKb(string kb)
    {
        if (string.IsNullOrWhiteSpace(kb))
            return string.Empty;
        var m = Regex.Match(kb, @"(\d{6,7})");
        return m.Success ? m.Groups[1].Value : kb.Trim();
    }

    private static IEnumerable<string> SplitKbs(string? kbField)
    {
        if (string.IsNullOrWhiteSpace(kbField))
            yield break;
        foreach (var part in kbField.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var n = NormalizeKb(part);
            if (!string.IsNullOrWhiteSpace(n))
                yield return "KB" + n;
        }
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "WindowsPatchManager/2.3 (+https://github.com/SparksSkywere/WindowsPatchManager)");
        return c;
    }

    private sealed class MsrcKbHit
    {
        public HashSet<string> Cves { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> MonthIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string MaxSeverity { get; set; } = "Important";
        public string TitleHint { get; set; } = string.Empty;
    }
}

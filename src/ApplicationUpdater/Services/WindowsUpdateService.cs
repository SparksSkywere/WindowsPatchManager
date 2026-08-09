using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ApplicationUpdater.Models;

namespace ApplicationUpdater.Services;

/// <summary>
/// Windows Update Agent COM API — pending software/driver updates, install history,
/// CVE/severity enrichment, installed-KB inventory, and optional MSRC gap analysis.
/// </summary>
public sealed class WindowsUpdateService
{
    // WU category GUIDs (IUpdateService / category catalog)
    private const string CategorySecurityUpdates = "0FA1201D-4330-4FA8-8AE9-B877473B6441";
    private const string CategoryCriticalUpdates = "E0789628-CE08-4437-BE74-2495B842F43B";
    private const string CategoryDrivers = "EBFC1FC5-71A4-4F7B-9ACA-3B9A503104A0";

    private static readonly Regex CveRegex = new(
        @"CVE-\d{4}-\d{4,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex KbRegex = new(
        @"KB\d{6,7}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly MsrcCveService _msrc;

    public WindowsUpdateService(ConfigService config, LogService log, MsrcCveService msrc)
    {
        _config = config;
        _log = log;
        _msrc = msrc;
    }

    public async Task<IReadOnlyList<ProgramInfo>> SearchSoftwareUpdatesAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        // COM work off the UI thread; MSRC is async HTTP.
        var pending = await Task.Run(() => SearchPending(softwareOnly: true, progress, ct), ct)
            .ConfigureAwait(false);

        if (!_config.Config.WindowsUpdate.CveScanEnabled)
            return pending;

        return await RunCveSecurityScanAsync(pending.ToList(), progress, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ProgramInfo>> SearchDriverUpdatesAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
        => Task.Run(() => (IReadOnlyList<ProgramInfo>)SearchPending(softwareOnly: false, progress, ct), ct);

    public Task<IReadOnlyList<ProgramInfo>> GetInstallHistoryAsync(
        bool driversOnly,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
        => Task.Run(() => (IReadOnlyList<ProgramInfo>)QueryHistory(driversOnly, progress, ct), ct);

    public Task<UpdateResult> InstallAsync(
        ProgramInfo program,
        IProgress<UpdateProgress>? progress,
        int completed,
        int total,
        CancellationToken ct)
        => Task.Run(() => InstallInternal(program, progress, completed, total, ct), ct);

    /// <summary>
    /// Full CVE/KB security pass: enrich pending WU rows, inventory installed KBs,
    /// optionally cross-reference MSRC for missing Critical/Important security KBs.
    /// </summary>
    private async Task<IReadOnlyList<ProgramInfo>> RunCveSecurityScanAsync(
        List<ProgramInfo> pending,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var settings = _config.Config.WindowsUpdate;

        progress?.Report(new ScanProgress { Message = "Inventorying installed security KBs…", Percent = 55 });
        var installedKbs = await Task.Run(() => GetInstalledKbSet(ct), ct).ConfigureAwait(false);
        _log.Info($"Installed KB inventory: {installedKbs.Count} hotfix(es).");

        // Mark pending updates that install KBs already present (rare race) as lower priority notes
        foreach (var p in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(p.KbId))
                continue;
            var nums = ExtractKbNumbers(p.KbId);
            if (nums.Count > 0 && nums.All(n => installedKbs.Contains(n) || installedKbs.Contains("KB" + n)))
            {
                p.Notes = string.IsNullOrWhiteSpace(p.Notes)
                    ? "KB appears already installed (inventory)"
                    : p.Notes + " · KB appears already installed";
            }
        }

        if (settings.QueryMsrcOnline)
        {
            progress?.Report(new ScanProgress { Message = "Cross-referencing MSRC CVE database…", Percent = 65 });
            await _msrc.EnrichPendingWithMsrcAsync(pending, progress, ct).ConfigureAwait(false);

            var missing = await _msrc.FindMissingSecurityKbsAsync(installedKbs, pending, progress, ct)
                .ConfigureAwait(false);

            foreach (var m in missing)
            {
                // Avoid duplicates by KB
                var norm = MsrcCveService.NormalizeKb(m.KbId ?? "");
                if (pending.Any(p => ExtractKbNumbers(p.KbId).Contains(norm)))
                    continue;
                pending.Add(m);
            }
        }

        if (settings.SecurityUpdatesOnly)
        {
            pending = pending
                .Where(p => p.IsSecurityUpdate ||
                            (!string.IsNullOrWhiteSpace(p.Severity) &&
                             p.Severity is "Critical" or "Important") ||
                            string.Equals(p.Origin, "MSRC CVE", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (settings.PrioritizeSecurity)
        {
            pending = pending
                .OrderBy(p => p.SeverityRank)
                .ThenByDescending(p => p.IsSecurityUpdate)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var critical = pending.Count(p =>
            string.Equals(p.Severity, "Critical", StringComparison.OrdinalIgnoreCase));
        var important = pending.Count(p =>
            string.Equals(p.Severity, "Important", StringComparison.OrdinalIgnoreCase));
        var withCve = pending.Count(p => !string.IsNullOrWhiteSpace(p.CveIds));

        progress?.Report(new ScanProgress
        {
            Message = pending.Count == 0
                ? "No Windows / CVE security updates required"
                : $"Found {pending.Count} update(s) · {critical} Critical · {important} Important · {withCve} with CVE",
            Percent = 100
        });
        _log.Info(
            $"Windows Update / CVE scan: {pending.Count} item(s) " +
            $"({critical} Critical, {important} Important, {withCve} CVE-linked).");

        return pending;
    }

    private List<ProgramInfo> SearchPending(
        bool softwareOnly,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        if (!_config.Config.WindowsUpdate.Enabled)
        {
            _log.Info("Windows Update source is disabled in Options.");
            return [];
        }

        var label = softwareOnly ? "Windows updates" : "driver updates";
        progress?.Report(new ScanProgress { Message = $"Searching {label}…", Percent = 5 });

        try
        {
            dynamic session = CreateSession();
            dynamic searcher = session.CreateUpdateSearcher();
            TrySetOnline(searcher);

            // Prefer security / critical categories first for software, then full software set.
            var criteriaList = softwareOnly
                ? BuildSoftwareCriteria()
                : new List<string>
                {
                    "IsInstalled=0 and IsHidden=0 and Type='Driver'",
                    "IsInstalled=0 and IsHidden=0"
                };

            if (!_config.Config.WindowsUpdate.IncludeOptional)
            {
                criteriaList = criteriaList
                    .Select(c => c.Contains("BrowseOnly=", StringComparison.Ordinal)
                        ? c
                        : c + " and BrowseOnly=0")
                    .ToList();
            }

            List<ProgramInfo> list = [];
            Exception? lastError = null;
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var criteria in criteriaList)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    _log.Info($"WU search criteria: {criteria}");
                    progress?.Report(new ScanProgress
                    {
                        Message = $"Querying Windows Update ({label})…",
                        Percent = 15
                    });
                    dynamic result = searcher.Search(criteria);
                    // Pass Updates as object so MapUpdates is bound statically (not dynamic).
                    object updatesObj = result.Updates;
                    List<ProgramInfo> mapped = MapUpdates(updatesObj, softwareOnly, !softwareOnly, progress, ct);

                    // For broad search without Type=, filter by type
                    if (!criteria.Contains("Type=", StringComparison.Ordinal) &&
                        !criteria.Contains("CategoryIDs", StringComparison.Ordinal))
                    {
                        mapped = mapped.Where(p => softwareOnly
                            ? p.Source == PackageSource.WindowsUpdate
                            : p.Source == PackageSource.Driver).ToList();
                    }

                    foreach (var item in mapped)
                    {
                        if (seenIds.Add(item.PackageId))
                            list.Add(item);
                    }

                    // If we already have security hits, still continue to collect remaining software
                    // unless SecurityUpdatesOnly and we only wanted category hits — still collect all
                    // software then filter later in CVE pass.
                    if (list.Count > 0 && criteria.Contains("CategoryIDs", StringComparison.Ordinal))
                        continue;

                    if (list.Count > 0 && criteria.Contains("Type='Software'", StringComparison.Ordinal))
                        break;

                    if (list.Count > 0 && !softwareOnly)
                        break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _log.Warn($"WU search failed for '{criteria}': {ex.Message}");
                }
            }

            if (list.Count == 0 && lastError is not null)
                _log.Warn($"Windows Update returned no results. Last error: {lastError.Message}");

            if (_config.Config.WindowsUpdate.PrioritizeSecurity && softwareOnly)
            {
                list = list
                    .OrderBy(p => p.SeverityRank)
                    .ThenByDescending(p => p.IsSecurityUpdate)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            progress?.Report(new ScanProgress
            {
                Message = list.Count == 0
                    ? $"No {label} available"
                    : $"Found {list.Count} {label}",
                Percent = softwareOnly && _config.Config.WindowsUpdate.CveScanEnabled ? 50 : 100
            });
            _log.Info($"{label}: {list.Count} pending update(s).");
            return list;
        }
        catch (COMException ex)
        {
            _log.Warn($"Windows Update API: {ex.Message} (0x{ex.ErrorCode:X8}). Try running as administrator.");
            progress?.Report(new ScanProgress { Message = "Windows Update search failed", Percent = 100 });
            return [];
        }
        catch (Exception ex)
        {
            _log.Warn($"Windows Update search failed: {ex.Message}");
            progress?.Report(new ScanProgress { Message = "Windows Update search failed", Percent = 100 });
            return [];
        }
    }

    private List<string> BuildSoftwareCriteria()
    {
        // Security Updates category, Critical Updates, then all software.
        return
        [
            $"IsInstalled=0 and IsHidden=0 and CategoryIDs contains '{CategorySecurityUpdates}'",
            $"IsInstalled=0 and IsHidden=0 and CategoryIDs contains '{CategoryCriticalUpdates}'",
            "IsInstalled=0 and IsHidden=0 and Type='Software'",
            "IsInstalled=0 and IsHidden=0"
        ];
    }

    private IReadOnlyList<ProgramInfo> QueryHistory(
        bool driversOnly,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new ScanProgress { Message = "Loading update history…", Percent = 10 });

        try
        {
            dynamic session = CreateSession();
            dynamic searcher = session.CreateUpdateSearcher();
            TrySetOnline(searcher);

            int total;
            try
            {
                total = (int)searcher.GetTotalHistoryCount();
            }
            catch
            {
                total = 0;
            }

            if (total <= 0)
            {
                _log.Info("Windows Update history is empty.");
                progress?.Report(new ScanProgress { Message = "No update history found", Percent = 100 });
                return [];
            }

            var take = Math.Min(total, 300);
            var start = Math.Max(0, total - take);
            dynamic history = searcher.QueryHistory(start, take);
            var count = (int)history.Count;
            var list = new List<ProgramInfo>();

            for (var i = count - 1; i >= 0; i--)
            {
                ct.ThrowIfCancellationRequested();
                dynamic h = history.Item(i);

                int operation = 0;
                try { operation = (int)h.Operation; } catch { /* ignore */ }
                if (operation != 1)
                    continue;

                int resultCode = 0;
                try { resultCode = (int)h.ResultCode; } catch { /* ignore */ }
                if (resultCode is not (2 or 3))
                    continue;

                string title = Safe(() => (string)h.Title) ?? "Windows Update";
                bool looksDriver = title.Contains("driver", StringComparison.OrdinalIgnoreCase) ||
                                   title.Contains("Device ", StringComparison.OrdinalIgnoreCase);
                try
                {
                    if (h.Categories != null)
                    {
                        for (var c = 0; c < (int)h.Categories.Count; c++)
                        {
                            string cat = Safe(() => (string)h.Categories.Item(c).Name) ?? "";
                            if (cat.Contains("Driver", StringComparison.OrdinalIgnoreCase))
                            {
                                looksDriver = true;
                                break;
                            }
                        }
                    }
                }
                catch { /* ignore */ }

                if (driversOnly != looksDriver)
                    continue;

                string kb = "";
                try
                {
                    var m = KbRegex.Match(title);
                    if (m.Success)
                        kb = m.Value.ToUpperInvariant();
                }
                catch { /* ignore */ }

                var cves = ExtractCves(title);
                DateTime? when = null;
                try { when = (DateTime)h.Date; } catch { /* ignore */ }

                string id = "";
                try { id = (string)h.UpdateIdentity.UpdateID; } catch { /* ignore */ }

                var isSecurity = title.Contains("Security", StringComparison.OrdinalIgnoreCase) ||
                                 cves.Count > 0;

                list.Add(new ProgramInfo
                {
                    Name = title,
                    Version = when?.ToString("yyyy-MM-dd") ?? "—",
                    AvailableVersion = string.IsNullOrWhiteSpace(kb) ? "Installed" : kb,
                    UpdateAvailable = false,
                    Publisher = "Microsoft",
                    PackageId = string.IsNullOrWhiteSpace(id) ? title : id,
                    KbId = kb,
                    CveIds = cves.Count > 0 ? string.Join(", ", cves) : null,
                    IsSecurityUpdate = isSecurity,
                    Source = driversOnly ? PackageSource.Driver : PackageSource.WindowsUpdate,
                    Category = driversOnly ? UpdateCategory.Drivers : UpdateCategory.WindowsUpdates,
                    LastUpdated = when,
                    Notes = isSecurity ? "Installed (history · security)" : "Installed (history)"
                });

                if (list.Count >= 150)
                    break;
            }

            progress?.Report(new ScanProgress
            {
                Message = $"Loaded {list.Count} installed update(s) from history",
                Percent = 100
            });
            _log.Info($"Update history ({(driversOnly ? "drivers" : "software")}): {list.Count} entr(y/ies).");
            return list;
        }
        catch (COMException ex)
        {
            _log.Warn($"Windows Update history: {ex.Message} (0x{ex.ErrorCode:X8})");
            return [];
        }
        catch (Exception ex)
        {
            _log.Warn($"Windows Update history failed: {ex.Message}");
            return [];
        }
    }

    private List<ProgramInfo> MapUpdates(
        object updatesObj,
        bool preferSoftware,
        bool driversOnly,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        dynamic updates = updatesObj;
        var list = new List<ProgramInfo>();
        var count = (int)updates.Count;

        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            object updateItem = updates.Item(i);
            dynamic u = updateItem;

            bool isDriver = IsDriverUpdate(updateItem);
            if (driversOnly && !isDriver)
                continue;
            if (!driversOnly && preferSoftware && isDriver)
                continue;

            string title = Safe(() => (string)u.Title) ?? "Windows Update";
            string description = Safe(() => (string)u.Description) ?? "";

            var kbs = ExtractKbIdsFromUpdate(updateItem, title, description);
            var kbPrimary = kbs.Count > 0 ? kbs[0] : "";
            var kbField = kbs.Count > 0 ? string.Join(", ", kbs) : "";

            string identity = Safe(() => (string)u.Identity.UpdateID) ?? title;
            DateTime? last = null;
            try { last = (DateTime)u.LastDeploymentChangeTime; } catch { /* ignore */ }

            string severity = "";
            try { severity = ((object)u.MsrcSeverity)?.ToString() ?? ""; } catch { /* ignore */ }

            var classified = ClassifyUpdate(updateItem, title, description, severity);
            bool isSecurity = classified.IsSecurity;
            string classification = classified.Classification;
            var cves = ExtractCves(title + "\n" + description);

            // Security bulletins sometimes embed CVE-like info in more fields
            try
            {
                dynamic du = updateItem;
                if (du.SecurityBulletinIDs != null)
                {
                    for (var b = 0; b < (int)du.SecurityBulletinIDs.Count; b++)
                    {
                        string? bulletin = null;
                        try { bulletin = (string)du.SecurityBulletinIDs.Item(b); } catch { /* ignore */ }
                        if (!string.IsNullOrWhiteSpace(bulletin))
                            cves.AddRange(ExtractCves(bulletin));
                    }
                }
            }
            catch { /* ignore */ }

            cves = cves
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (string.IsNullOrWhiteSpace(severity) && isSecurity)
                severity = "Important";

            var rank = MsrcCveService.SeverityRank(severity);
            if (!isSecurity && rank >= 40)
                rank = 50;

            var available = !string.IsNullOrWhiteSpace(kbPrimary)
                ? kbPrimary
                : (!string.IsNullOrWhiteSpace(severity) ? severity : "Pending");
            if (!string.IsNullOrWhiteSpace(severity) && !string.IsNullOrWhiteSpace(kbPrimary))
                available = $"{kbPrimary} · {severity}";

            var notesParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(classification))
                notesParts.Add(classification);
            if (cves.Count > 0)
                notesParts.Add(string.Join(", ", cves.Take(6)) + (cves.Count > 6 ? $" (+{cves.Count - 6})" : ""));
            if (!string.IsNullOrWhiteSpace(description))
            {
                var shortDesc = description.Length > 240 ? description[..240] + "…" : description;
                notesParts.Add(shortDesc);
            }

            list.Add(new ProgramInfo
            {
                Name = title,
                Version = isSecurity ? (severity is { Length: > 0 } ? severity : "Security") : "—",
                AvailableVersion = available,
                UpdateAvailable = true,
                Publisher = "Microsoft",
                PackageId = identity,
                KbId = kbField,
                Source = isDriver ? PackageSource.Driver : PackageSource.WindowsUpdate,
                Category = isDriver ? UpdateCategory.Drivers : UpdateCategory.WindowsUpdates,
                LastUpdated = last,
                Notes = string.Join(" · ", notesParts),
                Severity = string.IsNullOrWhiteSpace(severity) ? null : severity,
                SeverityRank = rank,
                CveIds = cves.Count > 0 ? string.Join(", ", cves) : null,
                IsSecurityUpdate = isSecurity,
                Classification = classification,
                Origin = isSecurity ? "Security Update" : (isDriver ? "Driver" : "Windows Update")
            });

            var pct = count == 0 ? 45 : (int)((i + 1) * 40.0 / count) + 10;
            progress?.Report(new ScanProgress
            {
                Message = title,
                Percent = Math.Clamp(pct, 10, 50)
            });
        }

        return list;
    }

    private readonly record struct UpdateClassification(bool IsSecurity, string Classification);

    private static UpdateClassification ClassifyUpdate(
        object updateObj,
        string title,
        string description,
        string severity)
    {
        dynamic u = updateObj;
        var names = new List<string>();
        try
        {
            if (u.Categories != null)
            {
                for (var c = 0; c < (int)u.Categories.Count; c++)
                {
                    string name = "";
                    string id = "";
                    try { name = (string)u.Categories.Item(c).Name ?? ""; } catch { /* ignore */ }
                    try { id = (string)u.Categories.Item(c).CategoryID ?? ""; } catch { /* ignore */ }
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);

                    if (id.Equals(CategorySecurityUpdates, StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Security Update", StringComparison.OrdinalIgnoreCase))
                        return new UpdateClassification(true, "Security Updates");

                    if (id.Equals(CategoryCriticalUpdates, StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Critical Updates", StringComparison.OrdinalIgnoreCase))
                        return new UpdateClassification(true, "Critical Updates");
                }
            }
        }
        catch { /* ignore */ }

        if (!string.IsNullOrWhiteSpace(severity))
            return new UpdateClassification(true, names.FirstOrDefault() ?? "Security Updates");

        var blob = title + "\n" + description;
        if (blob.Contains("Security Update", StringComparison.OrdinalIgnoreCase) ||
            blob.Contains("Security Only", StringComparison.OrdinalIgnoreCase) ||
            CveRegex.IsMatch(blob))
            return new UpdateClassification(
                true,
                names.FirstOrDefault(n => n.Contains("Security", StringComparison.OrdinalIgnoreCase))
                ?? "Security Updates");

        if (title.Contains("Cumulative Update", StringComparison.OrdinalIgnoreCase))
            return new UpdateClassification(true, "Cumulative Update"); // monthly LCU includes security

        return new UpdateClassification(false, names.FirstOrDefault() ?? "");
    }

    private static List<string> ExtractKbIdsFromUpdate(object updateObj, string title, string description)
    {
        dynamic u = updateObj;
        var kbs = new List<string>();
        try
        {
            if (u.KBArticleIDs != null)
            {
                for (var k = 0; k < (int)u.KBArticleIDs.Count; k++)
                {
                    string raw = "";
                    try
                    {
                        object? item = u.KBArticleIDs.Item(k);
                        raw = item?.ToString() ?? "";
                    }
                    catch { /* ignore */ }

                    if (string.IsNullOrWhiteSpace(raw))
                        continue;
                    var kb = raw.StartsWith("KB", StringComparison.OrdinalIgnoreCase)
                        ? raw.ToUpperInvariant()
                        : "KB" + raw.Trim();
                    if (!kbs.Contains(kb, StringComparer.OrdinalIgnoreCase))
                        kbs.Add(kb);
                }
            }
        }
        catch { /* ignore */ }

        foreach (Match m in KbRegex.Matches(title + " " + description))
        {
            var kb = m.Value.ToUpperInvariant();
            if (!kbs.Contains(kb, StringComparer.OrdinalIgnoreCase))
                kbs.Add(kb);
        }

        return kbs;
    }

    private static List<string> ExtractCves(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        return CveRegex.Matches(text)
            .Select(m => m.Value.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static HashSet<string> ExtractKbNumbers(string? kbField)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(kbField))
            return set;
        foreach (Match m in Regex.Matches(kbField, @"\d{6,7}"))
            set.Add(m.Value);
        return set;
    }

    /// <summary>
    /// Collects installed KBs from WMI QuickFixEngineering and Windows Update history.
    /// Keys are stored both as bare numbers and KB-prefixed forms.
    /// </summary>
    private HashSet<string> GetInstalledKbSet(CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT HotFixID FROM Win32_QuickFixEngineering");
            foreach (var obj in searcher.Get())
            {
                ct.ThrowIfCancellationRequested();
                var id = obj["HotFixID"]?.ToString();
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                set.Add(id);
                var n = MsrcCveService.NormalizeKb(id);
                if (!string.IsNullOrWhiteSpace(n))
                {
                    set.Add(n);
                    set.Add("KB" + n);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"WMI QuickFixEngineering scan failed: {ex.Message}");
        }

        try
        {
            dynamic session = CreateSession();
            dynamic searcher = session.CreateUpdateSearcher();
            int total = 0;
            try { total = (int)searcher.GetTotalHistoryCount(); } catch { /* ignore */ }
            if (total > 0)
            {
                var take = Math.Min(total, 500);
                var start = Math.Max(0, total - take);
                dynamic history = searcher.QueryHistory(start, take);
                var count = (int)history.Count;
                for (var i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    dynamic h = history.Item(i);
                    int resultCode = 0;
                    try { resultCode = (int)h.ResultCode; } catch { /* ignore */ }
                    if (resultCode is not (2 or 3))
                        continue;

                    string title = Safe(() => (string)h.Title) ?? "";
                    foreach (Match m in KbRegex.Matches(title))
                    {
                        var kb = m.Value.ToUpperInvariant();
                        set.Add(kb);
                        set.Add(MsrcCveService.NormalizeKb(kb));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"WU history KB inventory failed: {ex.Message}");
        }

        return set;
    }

    private static bool IsDriverUpdate(object updateObj)
    {
        dynamic u = updateObj;
        try
        {
            var t = u.Type;
            if (t is int ti)
                return ti == 2;
            if (t is not null)
            {
                var s = t.ToString() ?? "";
                if (s.Contains("Driver", StringComparison.OrdinalIgnoreCase) || s == "2")
                    return true;
                if (s.Contains("Software", StringComparison.OrdinalIgnoreCase) || s == "1")
                    return false;
            }
        }
        catch { /* ignore */ }

        try
        {
            if (u.Categories != null)
            {
                for (var c = 0; c < (int)u.Categories.Count; c++)
                {
                    string name = Safe(() => (string)u.Categories.Item(c).Name) ?? "";
                    string id = Safe(() => (string)u.Categories.Item(c).CategoryID) ?? "";
                    if (name.Contains("Driver", StringComparison.OrdinalIgnoreCase) ||
                        id.Equals(CategoryDrivers, StringComparison.OrdinalIgnoreCase) ||
                        id.Equals("5C9376AB-8CE6-464A-B136-22113DD69801", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { /* ignore */ }

        try
        {
            string title = (string)u.Title;
            if (title.Contains("driver", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { /* ignore */ }

        return false;
    }

    private UpdateResult InstallInternal(
        ProgramInfo program,
        IProgress<UpdateProgress>? progress,
        int completed,
        int total,
        CancellationToken ct)
    {
        var start = DateTime.Now;
        var result = new UpdateResult { Program = program, StartTime = start };

        // MSRC gap rows are not always in the current WU catalog — try to match by KB.
        try
        {
            progress?.Report(new UpdateProgress
            {
                ProgramName = program.Name,
                ProgramKey = program.DisplayKey,
                Completed = completed,
                Total = total,
                ItemPercent = 10,
                IsStarting = true,
                Message = "Preparing Windows Update…"
            });

            dynamic session = CreateSession();
            dynamic searcher = session.CreateUpdateSearcher();
            TrySetOnline(searcher);

            dynamic searchResult = searcher.Search("IsInstalled=0 and IsHidden=0");
            dynamic found = null!;
            var matched = false;
            var targetKbs = ExtractKbNumbers(program.KbId);

            for (var i = 0; i < (int)searchResult.Updates.Count; i++)
            {
                dynamic u = searchResult.Updates.Item(i);
                string id = Safe(() => (string)u.Identity.UpdateID) ?? "";
                string title = Safe(() => (string)u.Title) ?? "";

                if (string.Equals(id, program.PackageId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(title, program.Name, StringComparison.OrdinalIgnoreCase))
                {
                    found = u;
                    matched = true;
                    break;
                }

                if (targetKbs.Count > 0)
                {
                    string desc = "";
                    try { desc = (string)u.Description ?? ""; } catch { /* ignore */ }
                    object updateObj = u;
                    var updateKbs = ExtractKbIdsFromUpdate(updateObj, title, desc);
                    var updateNums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kb in updateKbs)
                    {
                        foreach (var n in ExtractKbNumbers(kb))
                            updateNums.Add(n);
                    }

                    if (targetKbs.Any(n => updateNums.Contains(n)))
                    {
                        found = u;
                        matched = true;
                        break;
                    }
                }
            }

            if (!matched)
            {
                if (program.PackageId.StartsWith("msrc:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "This security KB is not currently offered by Windows Update on this PC " +
                        "(may be superseded, not applicable, or not yet published to WU). " +
                        "Open the support link from Notes/DownloadUrl or run Windows Update later.");
                throw new InvalidOperationException("Update no longer available from Windows Update.");
            }

            dynamic toDownload = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
            toDownload.Add(found);

            progress?.Report(new UpdateProgress
            {
                ProgramName = program.Name,
                ProgramKey = program.DisplayKey,
                Completed = completed,
                Total = total,
                ItemPercent = 35,
                Message = "Downloading…"
            });

            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = toDownload;
            dynamic downloadResult = downloader.Download();
            if ((int)downloadResult.ResultCode != 2)
                throw new InvalidOperationException($"Download failed (code {(int)downloadResult.ResultCode}).");

            progress?.Report(new UpdateProgress
            {
                ProgramName = program.Name,
                ProgramKey = program.DisplayKey,
                Completed = completed,
                Total = total,
                ItemPercent = 70,
                Message = "Installing…"
            });

            dynamic toInstall = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
            if (!(bool)found.IsDownloaded)
                throw new InvalidOperationException("Update was not downloaded.");
            toInstall.Add(found);

            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = toInstall;
            dynamic installResult = installer.Install();
            var code = (int)installResult.ResultCode;
            result.Success = code is 2 or 3;
            result.ErrorMessage = result.Success ? string.Empty : $"Install result code {code}";
            if (result.Success)
            {
                program.UpdateAvailable = false;
                program.LastUpdated = DateTime.Now;
                program.Version = program.AvailableVersion;
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            _log.Error($"WU install {program.Name}: {ex.Message}");
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
            Message = result.Success ? "Installed" : result.ErrorMessage
        });
        return result;
    }

    private static dynamic CreateSession()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session")
                   ?? throw new InvalidOperationException("Microsoft.Update.Session COM is not available on this system.");
        return Activator.CreateInstance(type)
               ?? throw new InvalidOperationException("Could not create Windows Update session.");
    }

    private void TrySetOnline(dynamic searcher)
    {
        try
        {
            // ssWindowsUpdate = 2 → Microsoft Update / Windows Update online
            searcher.ServerSelection = 2;
        }
        catch
        {
            try { searcher.Online = true; } catch { /* ignore */ }
        }

        try { searcher.IncludePotentiallySupersededUpdates = false; } catch { /* ignore */ }
    }

    private static string? Safe(Func<string> f)
    {
        try { return f(); }
        catch { return null; }
    }
}

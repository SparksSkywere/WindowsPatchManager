using System.Runtime.InteropServices;
using ApplicationUpdater.Models;

namespace ApplicationUpdater.Services;

/// <summary>
/// Windows Update Agent COM API — pending software/driver updates and install history.
/// </summary>
public sealed class WindowsUpdateService
{
    private readonly ConfigService _config;
    private readonly LogService _log;

    public WindowsUpdateService(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
    }

    public Task<IReadOnlyList<ProgramInfo>> SearchSoftwareUpdatesAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
        => Task.Run(() => SearchPending(softwareOnly: true, progress, ct), ct);

    public Task<IReadOnlyList<ProgramInfo>> SearchDriverUpdatesAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
        => Task.Run(() => SearchPending(softwareOnly: false, progress, ct), ct);

    public Task<IReadOnlyList<ProgramInfo>> GetInstallHistoryAsync(
        bool driversOnly,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
        => Task.Run(() => QueryHistory(driversOnly, progress, ct), ct);

    public Task<UpdateResult> InstallAsync(
        ProgramInfo program,
        IProgress<UpdateProgress>? progress,
        int completed,
        int total,
        CancellationToken ct)
        => Task.Run(() => InstallInternal(program, progress, completed, total, ct), ct);

    private IReadOnlyList<ProgramInfo> SearchPending(
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

            // WU criteria: Type is 'Software' or 'Driver' (string), not integer.
            // Also try a broad search and filter if typed search returns nothing.
            var criteriaList = softwareOnly
                ? new[]
                {
                    "IsInstalled=0 and IsHidden=0 and Type='Software'",
                    "IsInstalled=0 and IsHidden=0"
                }
                : new[]
                {
                    "IsInstalled=0 and IsHidden=0 and Type='Driver'",
                    "IsInstalled=0 and IsHidden=0"
                };

            if (!_config.Config.WindowsUpdate.IncludeOptional)
            {
                criteriaList = criteriaList
                    .Select(c => c + " and BrowseOnly=0")
                    .ToArray();
            }

            List<ProgramInfo> list = [];
            Exception? lastError = null;

            foreach (var criteria in criteriaList)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    _log.Info($"WU search criteria: {criteria}");
                    progress?.Report(new ScanProgress { Message = $"Querying Windows Update ({label})…", Percent = 20 });
                    dynamic result = searcher.Search(criteria);
                    list = MapUpdates(result.Updates, softwareOnly, !softwareOnly, progress, ct);
                    // For broad search (second criteria), filter by type
                    if (criteria.Contains("Type=", StringComparison.Ordinal))
                        break;
                    // Broad search: keep only matching type
                    list = list.Where(p => softwareOnly
                        ? p.Source == PackageSource.WindowsUpdate
                        : p.Source == PackageSource.Driver).ToList();
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

            progress?.Report(new ScanProgress
            {
                Message = list.Count == 0
                    ? $"No {label} available"
                    : $"Found {list.Count} {label}",
                Percent = 100
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

            // Most recent first — QueryHistory(startIndex, count)
            var take = Math.Min(total, 300);
            var start = Math.Max(0, total - take);
            dynamic history = searcher.QueryHistory(start, take);
            var count = (int)history.Count;
            var list = new List<ProgramInfo>();

            // Iterate newest → oldest
            for (var i = count - 1; i >= 0; i--)
            {
                ct.ThrowIfCancellationRequested();
                dynamic h = history.Item(i);

                // Operation: 1 = Installation, 2 = Uninstallation
                int operation = 0;
                try { operation = (int)h.Operation; } catch { /* ignore */ }
                if (operation != 1)
                    continue;

                // ResultCode: 2 = Succeeded, 3 = SucceededWithErrors
                int resultCode = 0;
                try { resultCode = (int)h.ResultCode; } catch { /* ignore */ }
                if (resultCode is not (2 or 3))
                    continue;

                string title = Safe(() => (string)h.Title) ?? "Windows Update";
                bool looksDriver = title.Contains("driver", StringComparison.OrdinalIgnoreCase) ||
                                   title.Contains("Device ", StringComparison.OrdinalIgnoreCase);
                try
                {
                    // Some history entries expose Categories
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
                    // Title often contains (KBnnnnnn)
                    var m = System.Text.RegularExpressions.Regex.Match(title, @"KB\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success) kb = m.Value.ToUpperInvariant();
                }
                catch { /* ignore */ }

                DateTime? when = null;
                try { when = (DateTime)h.Date; } catch { /* ignore */ }

                string id = "";
                try { id = (string)h.UpdateIdentity.UpdateID; } catch { /* ignore */ }

                list.Add(new ProgramInfo
                {
                    Name = title,
                    Version = when?.ToString("yyyy-MM-dd") ?? "—",
                    AvailableVersion = string.IsNullOrWhiteSpace(kb) ? "Installed" : kb,
                    UpdateAvailable = false,
                    Publisher = "Microsoft",
                    PackageId = string.IsNullOrWhiteSpace(id) ? title : id,
                    KbId = kb,
                    Source = driversOnly ? PackageSource.Driver : PackageSource.WindowsUpdate,
                    Category = driversOnly ? UpdateCategory.Drivers : UpdateCategory.WindowsUpdates,
                    LastUpdated = when,
                    Notes = "Installed (history)"
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
        dynamic updates,
        bool preferSoftware,
        bool driversOnly,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var list = new List<ProgramInfo>();
        var count = (int)updates.Count;

        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            dynamic u = updates.Item(i);

            bool isDriver = IsDriverUpdate(u);
            if (driversOnly && !isDriver)
                continue;
            if (!driversOnly && preferSoftware && isDriver)
                continue;

            string title = Safe(() => (string)u.Title) ?? "Windows Update";
            string kb = "";
            try
            {
                if (u.KBArticleIDs != null && (int)u.KBArticleIDs.Count > 0)
                    kb = "KB" + u.KBArticleIDs.Item(0);
            }
            catch { /* ignore */ }

            string identity = Safe(() => (string)u.Identity.UpdateID) ?? title;
            DateTime? last = null;
            try { last = (DateTime)u.LastDeploymentChangeTime; } catch { /* ignore */ }

            string severity = "";
            try { severity = ((object)u.MsrcSeverity)?.ToString() ?? ""; } catch { /* ignore */ }

            list.Add(new ProgramInfo
            {
                Name = title,
                Version = "—",
                AvailableVersion = !string.IsNullOrWhiteSpace(kb) ? kb
                    : (!string.IsNullOrWhiteSpace(severity) ? severity : "Pending"),
                UpdateAvailable = true,
                Publisher = "Microsoft",
                PackageId = identity,
                KbId = kb,
                Source = isDriver ? PackageSource.Driver : PackageSource.WindowsUpdate,
                Category = isDriver ? UpdateCategory.Drivers : UpdateCategory.WindowsUpdates,
                LastUpdated = last,
                Notes = Safe(() => (string)u.Description)
            });

            var pct = count == 0 ? 100 : (int)((i + 1) * 90.0 / count) + 5;
            progress?.Report(new ScanProgress
            {
                Message = title,
                Percent = Math.Clamp(pct, 5, 95)
            });
        }

        return list;
    }

    private static bool IsDriverUpdate(dynamic u)
    {
        try
        {
            // IUpdate.Type: utSoftware = 1, utDriver = 2
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
                    // Drivers category GUID
                    if (name.Contains("Driver", StringComparison.OrdinalIgnoreCase) ||
                        id.Equals("780016B9-ABC3-4B5E-8A62-5A1B0B0E0E0E", StringComparison.OrdinalIgnoreCase) ||
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
            for (var i = 0; i < (int)searchResult.Updates.Count; i++)
            {
                dynamic u = searchResult.Updates.Item(i);
                string id = Safe(() => (string)u.Identity.UpdateID) ?? "";
                if (string.Equals(id, program.PackageId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals((string)u.Title, program.Name, StringComparison.OrdinalIgnoreCase))
                {
                    found = u;
                    matched = true;
                    break;
                }
            }

            if (!matched)
                throw new InvalidOperationException("Update no longer available from Windows Update.");

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

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using ApplicationUpdater.Helpers;
using ApplicationUpdater.Models;
using ApplicationUpdater.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace ApplicationUpdater.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly PatchManagerService _patchManager;
    private readonly SchedulerService _scheduler;
    private readonly LogService _log;
    private CancellationTokenSource? _cts;

    public ObservableCollection<ProgramItemViewModel> Programs { get; } = [];
    public ObservableCollection<ProgramItemViewModel> Drivers { get; } = [];
    public ObservableCollection<ProgramItemViewModel> WindowsUpdates { get; } = [];
    public ObservableCollection<ProgramItemViewModel> InstallResults { get; } = [];
    public ObservableCollection<ProgramItemViewModel> UninstallList { get; } = [];
    public ObservableCollection<string> LogEntries { get; } = [];

    public ICollectionView ProgramsView { get; }
    public ICollectionView DriversView { get; }
    public ICollectionView WindowsUpdatesView { get; }
    public ICollectionView InstallResultsView { get; }
    public ICollectionView UninstallListView { get; }

    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _catalogQuery = string.Empty;
    [ObservableProperty] private bool _showUpdatesOnly;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private string _progressPercentText = string.Empty;
    [ObservableProperty] private string _busyOverlayText = "Working…";
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _updateCount;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _summaryText = "No programs loaded";
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _isShowingHistory;
    [ObservableProperty] private bool _showEmptyState;
    [ObservableProperty] private string _emptyStateTitle = "No updates available";
    [ObservableProperty] private string _emptyStateDetail = "Run Scan or Check updates.";
    [ObservableProperty] private bool _showHistoryButton;
    [ObservableProperty] private bool _isUpdateTab = true;
    [ObservableProperty] private bool _isInstallTab;
    [ObservableProperty] private bool _isUninstallTab;

    public MainViewModel(PatchManagerService patchManager, SchedulerService scheduler, LogService log)
    {
        _patchManager = patchManager;
        _scheduler = scheduler;
        _log = log;

        ProgramsView = CollectionViewSource.GetDefaultView(Programs);
        ProgramsView.Filter = FilterProgram;
        ProgramsView.SortDescriptions.Add(new SortDescription(nameof(ProgramItemViewModel.UpdateAvailable), ListSortDirection.Descending));
        ProgramsView.SortDescriptions.Add(new SortDescription(nameof(ProgramItemViewModel.Name), ListSortDirection.Ascending));

        DriversView = CollectionViewSource.GetDefaultView(Drivers);
        DriversView.Filter = FilterProgram;
        DriversView.SortDescriptions.Add(new SortDescription(nameof(ProgramItemViewModel.UpdateAvailable), ListSortDirection.Descending));
        DriversView.SortDescriptions.Add(new SortDescription(nameof(ProgramItemViewModel.Name), ListSortDirection.Ascending));

        WindowsUpdatesView = CollectionViewSource.GetDefaultView(WindowsUpdates);
        WindowsUpdatesView.Filter = FilterProgram;
        WindowsUpdatesView.SortDescriptions.Add(new SortDescription(nameof(ProgramItemViewModel.Name), ListSortDirection.Ascending));

        InstallResultsView = CollectionViewSource.GetDefaultView(InstallResults);
        InstallResultsView.Filter = FilterProgram;
        InstallResultsView.SortDescriptions.Add(new SortDescription(nameof(ProgramItemViewModel.Name), ListSortDirection.Ascending));

        UninstallListView = CollectionViewSource.GetDefaultView(UninstallList);
        UninstallListView.Filter = FilterProgram;
        UninstallListView.SortDescriptions.Add(new SortDescription(nameof(ProgramItemViewModel.Name), ListSortDirection.Ascending));

        _log.MessageLogged += (_, line) => UiThread.Post(() =>
        {
            LogEntries.Add(line);
            while (LogEntries.Count > 1000)
                LogEntries.RemoveAt(0);
        });
    }

    private ObservableCollection<ProgramItemViewModel> ActiveList => SelectedTabIndex switch
    {
        1 => Drivers,
        2 => WindowsUpdates,
        3 => InstallResults,
        4 => UninstallList,
        _ => Programs
    };

    /// <summary>Bound list for the selected tab.</summary>
    public ICollectionView ActiveView => SelectedTabIndex switch
    {
        1 => DriversView,
        2 => WindowsUpdatesView,
        3 => InstallResultsView,
        4 => UninstallListView,
        _ => ProgramsView
    };

    partial void OnSearchTextChanged(string value) =>
        UiThread.Post(() =>
        {
            ProgramsView.Refresh();
            DriversView.Refresh();
            WindowsUpdatesView.Refresh();
            InstallResultsView.Refresh();
            UninstallListView.Refresh();
        });

    partial void OnShowUpdatesOnlyChanged(bool value)
    {
        _patchManager.Config.Config.General.ShowOnlyUpdatable = value;
        _patchManager.Config.Save();
        UiThread.Post(() =>
        {
            ProgramsView.Refresh();
            DriversView.Refresh();
            WindowsUpdatesView.Refresh();
            RefreshSummary();
        });
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        // Tab switch only updates the bound list — no automatic re-scan (initial scan on load covers apps/drivers/WU).
        IsShowingHistory = false;
        IsUpdateTab = value is 0 or 1 or 2;
        IsInstallTab = value == 3;
        IsUninstallTab = value == 4;
        OnPropertyChanged(nameof(ActiveView));
        RefreshSummary();
        UpdateEmptyState();
    }

    private bool FilterProgram(object obj)
    {
        if (obj is not ProgramItemViewModel item)
            return false;

        // "Show updates only" only applies to update tabs
        if (IsUpdateTab && ShowUpdatesOnly && !item.UpdateAvailable)
            return false;

        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var q = SearchText.Trim();
        return item.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               item.PackageId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               item.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               item.Source.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               item.AvailableVersion.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private async Task LoadedAsync()
    {
        ShowUpdatesOnly = _patchManager.Config.Config.General.ShowOnlyUpdatable;
        _log.Info("Windows Patch Manager started.");
        // Initial scan populates Applications, Drivers, and Windows Update together.
        await ScanAllUpdateTabsAsync().ConfigureAwait(true);
        if (_patchManager.Config.Config.General.AutoCheckUpdates)
            await CheckUpdatesAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Scans applications + drivers + Windows Update (used on launch and from Applications Scan).
    /// </summary>
    private async Task ScanAllUpdateTabsAsync()
    {
        IsShowingHistory = false;
        await RunBusyAsync("Scanning applications, drivers & Windows Update…", async (ct, progress) =>
        {
            progress.Report(new ScanProgress { Message = "Scanning applications…", Percent = 5 });
            var apps = await _patchManager.ScanAsync(progress, ct).ConfigureAwait(false);
            UiThread.Send(() => ReplaceList(Programs, apps));

            progress.Report(new ScanProgress { Message = "Scanning drivers…", Percent = 45 });
            var drivers = await _patchManager.ScanDriversAsync(progress, ct).ConfigureAwait(false);
            UiThread.Send(() => ReplaceList(Drivers, drivers));

            progress.Report(new ScanProgress { Message = "Scanning Windows Update…", Percent = 75 });
            var wu = await _patchManager.ScanWindowsUpdatesAsync(progress, ct).ConfigureAwait(false);
            UiThread.Send(() =>
            {
                ReplaceList(WindowsUpdates, wu);
                StatusText =
                    $"Scan complete · {Programs.Count} apps · {Drivers.Count} drivers · {WindowsUpdates.Count} Windows updates";
            });
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ScanAsync()
    {
        IsShowingHistory = false;

        // Applications tab (and initial intent): refresh all three update surfaces.
        if (SelectedTabIndex is 0)
        {
            await ScanAllUpdateTabsAsync().ConfigureAwait(true);
            return;
        }

        await RunBusyAsync("Scanning…", async (ct, progress) =>
        {
            if (SelectedTabIndex == 1)
            {
                var list = await _patchManager.ScanDriversAsync(progress, ct).ConfigureAwait(false);
                UiThread.Send(() => ReplaceList(Drivers, list));
            }
            else if (SelectedTabIndex == 2)
            {
                var list = await _patchManager.ScanWindowsUpdatesAsync(progress, ct).ConfigureAwait(false);
                UiThread.Send(() => ReplaceList(WindowsUpdates, list));
            }
            else if (SelectedTabIndex == 4)
            {
                var list = await _patchManager.ListWingetInstalledAsync(ct).ConfigureAwait(false);
                UiThread.Send(() => ReplaceList(UninstallList, list));
            }
            else if (SelectedTabIndex == 3)
            {
                // Install tab: scan is catalog search via CatalogQuery
                await SearchCatalogInternalAsync(ct, progress).ConfigureAwait(false);
            }
            else
            {
                var list = await _patchManager.ScanAsync(progress, ct).ConfigureAwait(false);
                UiThread.Send(() => ReplaceList(Programs, list));
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SearchCatalogAsync()
    {
        if (SelectedTabIndex != 3)
            SelectedTabIndex = 3;

        await RunBusyAsync("Searching winget…", async (ct, progress) =>
        {
            await SearchCatalogInternalAsync(ct, progress).ConfigureAwait(false);
        }).ConfigureAwait(true);
    }

    private async Task SearchCatalogInternalAsync(CancellationToken ct, IProgress<ScanProgress> progress)
    {
        var q = !string.IsNullOrWhiteSpace(CatalogQuery) ? CatalogQuery : SearchText;
        if (string.IsNullOrWhiteSpace(q))
        {
            UiThread.Send(() =>
            {
                StatusText = "Enter a package name or ID to search winget";
                UpdateEmptyState();
            });
            return;
        }

        progress.Report(new ScanProgress { Message = $"Searching winget for \"{q.Trim()}\"…", Percent = 20 });
        var list = await _patchManager.SearchWingetAsync(q.Trim(), ct).ConfigureAwait(false);
        UiThread.Send(() =>
        {
            ReplaceList(InstallResults, list);
            StatusText = list.Count == 0
                ? $"No winget packages matched \"{q.Trim()}\""
                : $"Found {list.Count} package(s)";
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallSelectedAsync()
    {
        if (SelectedTabIndex != 3)
            SelectedTabIndex = 3;

        var selected = InstallResults.Where(p => p.IsSelected).Select(p => p.Model).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(
                "Select one or more packages from the Install tab search results.",
                "Install",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await RunBusyAsync($"Installing {selected.Count} package(s)…", async (ct, _) =>
        {
            var done = 0;
            foreach (var program in selected)
            {
                ct.ThrowIfCancellationRequested();
                UiThread.Post(() =>
                {
                    SetDeterminateProgress(done * 100.0 / selected.Count, $"Installing {program.Name}…");
                    BusyOverlayText = $"Installing {program.Name}\n{done + 1} of {selected.Count}";
                });

                var result = await _patchManager.InstallPackageAsync(program, ct).ConfigureAwait(false);
                if (result.Success)
                    _log.Success($"Installed {program.Name}");
                else
                    _log.Error($"Install failed {program.Name}: {result.ErrorMessage}");
                done++;
            }

            UiThread.Send(() => StatusText = $"Install finished ({done} package(s))");
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task LoadUninstallListAsync()
    {
        if (SelectedTabIndex != 4)
            SelectedTabIndex = 4;

        await RunBusyAsync("Loading installed packages…", async (ct, progress) =>
        {
            progress.Report(new ScanProgress { Message = "Listing winget packages…", Percent = 30 });
            var list = await _patchManager.ListWingetInstalledAsync(ct).ConfigureAwait(false);
            UiThread.Send(() =>
            {
                ReplaceList(UninstallList, list);
                StatusText = $"Loaded {list.Count} installed package(s)";
            });
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UninstallSelectedAsync()
    {
        if (SelectedTabIndex != 4)
            SelectedTabIndex = 4;

        var selected = UninstallList.Where(p => p.IsSelected).Select(p => p.Model).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(
                "Select one or more installed packages to uninstall.",
                "Uninstall",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Uninstall {selected.Count} package(s)?\n\n{string.Join("\n", selected.Take(8).Select(p => "• " + p.Name))}" +
            (selected.Count > 8 ? $"\n… and {selected.Count - 8} more" : string.Empty),
            "Confirm uninstall",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return;

        await RunBusyAsync($"Uninstalling {selected.Count} package(s)…", async (ct, _) =>
        {
            var done = 0;
            var removed = new List<string>();
            foreach (var program in selected)
            {
                ct.ThrowIfCancellationRequested();
                UiThread.Post(() =>
                {
                    SetDeterminateProgress(done * 100.0 / selected.Count, $"Uninstalling {program.Name}…");
                    BusyOverlayText = $"Uninstalling {program.Name}\n{done + 1} of {selected.Count}";
                });

                var result = await _patchManager.UninstallPackageAsync(program, ct).ConfigureAwait(false);
                if (result.Success)
                {
                    removed.Add(program.PackageId);
                    _log.Success($"Uninstalled {program.Name}");
                }
                else
                    _log.Error($"Uninstall failed {program.Name}: {result.ErrorMessage}");
                done++;
            }

            UiThread.Send(() =>
            {
                foreach (var id in removed)
                {
                    var item = UninstallList.FirstOrDefault(p =>
                        string.Equals(p.PackageId, id, StringComparison.OrdinalIgnoreCase));
                    if (item is not null)
                        UninstallList.Remove(item);
                }

                UninstallListView.Refresh();
                StatusText = $"Uninstall finished ({removed.Count} removed)";
                RefreshSummary();
                UpdateEmptyState();
            });
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CheckUpdatesAsync()
    {
        IsShowingHistory = false;
        await RunBusyAsync("Checking for updates…", async (ct, progress) =>
        {
            if (SelectedTabIndex == 1)
            {
                var list = await _patchManager.ScanDriversAsync(progress, ct).ConfigureAwait(false);
                UiThread.Send(() => ReplaceList(Drivers, list));
            }
            else if (SelectedTabIndex == 2)
            {
                var list = await _patchManager.ScanWindowsUpdatesAsync(progress, ct).ConfigureAwait(false);
                UiThread.Send(() => ReplaceList(WindowsUpdates, list));
            }
            else
            {
                if (_patchManager.Programs.Count == 0)
                {
                    var scanned = await _patchManager.ScanAsync(progress, ct).ConfigureAwait(false);
                    UiThread.Send(() => ReplaceList(Programs, scanned));
                }

                await _patchManager.CheckUpdatesAsync(progress, ct).ConfigureAwait(false);
                UiThread.Send(() =>
                {
                    ReplaceList(Programs, _patchManager.Programs);
                    if (_patchManager.Config.Config.Notifications.ShowUpdateAvailable && UpdateCount > 0)
                        StatusText = $"{UpdateCount} update(s) available";
                });
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ShowUpdateHistoryAsync()
    {
        // Always load history — no confirmation dialogs.
        // If on Programs tab, open Windows Updates history by default.
        if (SelectedTabIndex is not (1 or 2))
            SelectedTabIndex = 2;

        var driversOnly = SelectedTabIndex == 1;
        await RunBusyAsync("Loading update history…", async (ct, progress) =>
        {
            var list = await _patchManager.GetUpdateHistoryAsync(driversOnly, progress, ct).ConfigureAwait(false);
            UiThread.Send(() =>
            {
                IsShowingHistory = true;
                if (driversOnly)
                    ReplaceList(Drivers, list);
                else
                    ReplaceList(WindowsUpdates, list);

                StatusText = list.Count == 0
                    ? "No installed updates found in Windows Update history"
                    : $"Showing {list.Count} installed update(s) from history";
            });
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task BackToAvailableUpdatesAsync()
    {
        IsShowingHistory = false;
        if (SelectedTabIndex == 1 || SelectedTabIndex == 2)
            await CheckUpdatesAsync().ConfigureAwait(true);
        else
            UpdateEmptyState();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UpdateSelectedAsync()
    {
        var selected = ActiveList.Where(p => p.IsSelected && p.UpdateAvailable).Select(p => p.Model).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(
                "Select one or more items that have updates available.",
                "Windows Patch Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await RunUpdatesAsync(selected).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UpdateAllAsync()
    {
        var all = ActiveList.Where(p => p.UpdateAvailable).Select(p => p.Model).ToList();
        if (all.Count == 0)
        {
            MessageBox.Show(
                "No updates are available on this tab. Run Check for Updates first.",
                "Windows Patch Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await RunUpdatesAsync(all).ConfigureAwait(true);
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var p in ActiveList)
            p.IsSelected = true;
        RefreshSelectionCount();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var p in ActiveList)
            p.IsSelected = false;
        RefreshSelectionCount();
    }

    [RelayCommand]
    private void SelectUpdatesOnly()
    {
        foreach (var p in ActiveList)
            p.IsSelected = p.UpdateAvailable;
        RefreshSelectionCount();
    }

    [RelayCommand]
    private void Export()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export list",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = $"updates_{DateTime.Now:yyyyMMdd_HHmmss}.json",
            DefaultExt = ".json",
            AddExtension = true
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            _patchManager.Export(dlg.FileName);
            MessageBox.Show($"Exported to:\n{dlg.FileName}", "Export complete",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenConfigFolder()
    {
        var dir = _patchManager.Config.AppDataDirectory;
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = dir,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void CreateSchedule()
    {
        if (_scheduler.CreateDailyTask())
        {
            MessageBox.Show(
                $"Daily scheduled task \"{SchedulerService.TaskName}\" was created.\n\nYou can change the schedule in Task Scheduler (taskschd.msc).",
                "Scheduled task",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(
                "Could not create the scheduled task. Try running Windows Patch Manager as administrator.",
                "Scheduled task",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void RemoveSchedule()
    {
        if (_scheduler.RemoveTask())
        {
            MessageBox.Show("Scheduled task removed.", "Scheduled task",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("Scheduled task was not found or could not be removed.", "Scheduled task",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Cancelling...";
    }

    [RelayCommand]
    private void ClearLog() => LogEntries.Clear();

    [RelayCommand]
    private void RefreshSelection() => RefreshSelectionCount();

    public void NotifyListChanged()
    {
        ActiveView.Refresh();
        RefreshSummary();
    }

    private bool CanRun() => !IsBusy;

    private async Task RunUpdatesAsync(List<ProgramInfo> programs)
    {
        await RunBusyAsync($"Updating {programs.Count} item(s)…", async (ct, _) =>
        {
            var progress = new Progress<UpdateProgress>(p =>
            {
                UiThread.Post(() =>
                {
                    if (p.OverallPercent >= 0)
                        SetDeterminateProgress(p.OverallPercent, p.Message ?? p.ProgramName);
                    else if (p.Total > 0)
                    {
                        var pct = p.Completed * 100.0 / p.Total;
                        if (p.ItemPercent >= 0)
                            pct = (p.Completed + p.ItemPercent / 100.0) * 100.0 / p.Total;
                        SetDeterminateProgress(pct, p.Message ?? $"Updating {p.ProgramName} ({p.Completed}/{p.Total})");
                    }

                    // % is shown under the swirl; keep overlay text to the current step only
                    BusyOverlayText = string.IsNullOrWhiteSpace(p.Message)
                        ? $"Updating {p.ProgramName}\n{p.Completed} of {p.Total}"
                        : $"{p.ProgramName}\n{p.Message}";

                    // Per-row progress cell
                    if (!string.IsNullOrWhiteSpace(p.ProgramKey))
                    {
                        var row = FindRow(p.ProgramKey) ?? FindRowByName(p.ProgramName);
                        if (row is not null)
                        {
                            if (p.ItemPercent >= 0)
                                row.SetProgress(p.ItemPercent, p.Message);
                            if (p.Completed > 0 && p.ItemPercent >= 100)
                            {
                                if (p.Success)
                                    row.SetProgress(100, "Done");
                                else if (!string.IsNullOrWhiteSpace(p.Message))
                                    row.SetProgress(p.ItemPercent < 0 ? 0 : p.ItemPercent, p.Message);
                            }
                        }
                    }

                    if (!p.IsStarting && p.Completed > 0 && p.ItemPercent >= 100)
                    {
                        if (p.Success)
                            _log.Success($"Updated {p.ProgramName}");
                        else if (!string.IsNullOrWhiteSpace(p.Message))
                            _log.Error($"Failed {p.ProgramName}: {p.Message}");
                    }
                });
            });

            var results = await _patchManager.UpdateAsync(programs, progress, ct).ConfigureAwait(false);
            var ok = results.Values.Count(r => r.Success);
            var fail = results.Count - ok;

            UiThread.Send(() =>
            {
                foreach (var vm in ActiveList)
                    vm.ClearProgress();

                StatusText = $"Updates finished: {ok} succeeded, {fail} failed";

                // Refresh active tab data
                if (SelectedTabIndex == 0)
                    ReplaceList(Programs, _patchManager.Programs);
                else if (SelectedTabIndex == 1)
                    ReplaceList(Drivers, _patchManager.Drivers);
                else
                    ReplaceList(WindowsUpdates, _patchManager.WindowsUpdates);

                if (_patchManager.Config.Config.Notifications.ShowUpdateComplete)
                {
                    MessageBox.Show(
                        $"Finished updating.\n\nSucceeded: {ok}\nFailed: {fail}",
                        "Updates complete",
                        MessageBoxButton.OK,
                        fail > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
                }
            });
        }).ConfigureAwait(true);
    }

    private ProgramItemViewModel? FindRow(string key) =>
        Programs.Concat(Drivers).Concat(WindowsUpdates)
            .FirstOrDefault(p => string.Equals(p.Model.DisplayKey, key, StringComparison.OrdinalIgnoreCase));

    private ProgramItemViewModel? FindRowByName(string name) =>
        ActiveList.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private async Task RunBusyAsync(string status, Func<CancellationToken, IProgress<ScanProgress>, Task> work)
    {
        if (IsBusy) return;

        _cts = new CancellationTokenSource();
        IsBusy = true;
        SetIndeterminateProgress(status);
        BusyOverlayText = status;
        NotifyBusyCommands();

        var progress = new Progress<ScanProgress>(p =>
        {
            UiThread.Post(() =>
            {
                if (p.Percent >= 0)
                {
                    SetDeterminateProgress(p.Percent, p.Message);
                    BusyOverlayText = p.Message;
                }
                else
                {
                    SetIndeterminateProgress(p.Message);
                    BusyOverlayText = p.Message;
                }
            });
        });

        try
        {
            await work(_cts.Token, progress).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Operation cancelled";
            _log.Warn("Operation cancelled by user.");
        }
        catch (Exception ex)
        {
            StatusText = "Error";
            _log.Error(ex.Message);
            MessageBox.Show(ex.Message, "Windows Patch Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            if (ProgressValue < 100 &&
                !StatusText.Contains("fail", StringComparison.OrdinalIgnoreCase) &&
                !StatusText.Contains("cancel", StringComparison.OrdinalIgnoreCase) &&
                !StatusText.Contains("Error", StringComparison.OrdinalIgnoreCase))
            {
                ProgressValue = 100;
            }

            ProgressPercentText = ProgressValue >= 100 ? "100%" : $"{(int)Math.Round(ProgressValue)}%";
            BusyOverlayText = "Working…";
            NotifyBusyCommands();
            _cts?.Dispose();
            _cts = null;
            RefreshSummary();
            UpdateEmptyState();
        }
    }

    private void SetIndeterminateProgress(string status)
    {
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressPercentText = "…";
        StatusText = status;
    }

    private void SetDeterminateProgress(double percent, string status)
    {
        IsProgressIndeterminate = false;
        ProgressValue = Math.Clamp(percent, 0, 100);
        ProgressPercentText = $"{(int)Math.Round(ProgressValue)}%";
        StatusText = status;
    }

    private void NotifyBusyCommands()
    {
        ScanCommand.NotifyCanExecuteChanged();
        CheckUpdatesCommand.NotifyCanExecuteChanged();
        UpdateSelectedCommand.NotifyCanExecuteChanged();
        UpdateAllCommand.NotifyCanExecuteChanged();
        ShowUpdateHistoryCommand.NotifyCanExecuteChanged();
        BackToAvailableUpdatesCommand.NotifyCanExecuteChanged();
        SearchCatalogCommand.NotifyCanExecuteChanged();
        InstallSelectedCommand.NotifyCanExecuteChanged();
        LoadUninstallListCommand.NotifyCanExecuteChanged();
        UninstallSelectedCommand.NotifyCanExecuteChanged();
    }

    private void ReplaceList(ObservableCollection<ProgramItemViewModel> target, IReadOnlyList<ProgramInfo> list)
    {
        target.Clear();
        foreach (var p in list)
        {
            var vm = new ProgramItemViewModel(p);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ProgramItemViewModel.IsSelected))
                    RefreshSelectionCount();
            };
            target.Add(vm);
        }

        if (ReferenceEquals(target, Programs))
            ProgramsView.Refresh();
        else if (ReferenceEquals(target, Drivers))
            DriversView.Refresh();
        else if (ReferenceEquals(target, WindowsUpdates))
            WindowsUpdatesView.Refresh();
        else if (ReferenceEquals(target, InstallResults))
            InstallResultsView.Refresh();
        else if (ReferenceEquals(target, UninstallList))
            UninstallListView.Refresh();

        RefreshSummary();
        UpdateEmptyState();
    }

    private void RefreshSelectionCount()
    {
        SelectedCount = ActiveList.Count(p => p.IsSelected);
        RefreshSummary();
    }

    private int CountVisible()
    {
        var n = 0;
        foreach (var _ in ActiveView)
            n++;
        return n;
    }

    private void RefreshSummary()
    {
        TotalCount = ActiveList.Count;
        UpdateCount = ActiveList.Count(p => p.UpdateAvailable);
        SelectedCount = ActiveList.Count(p => p.IsSelected);
        var visible = CountVisible();

        SummaryText = SelectedTabIndex switch
        {
            1 => IsShowingHistory
                ? $"{visible} driver history · {SelectedCount} selected"
                : $"{visible} drivers · {UpdateCount} updates · {SelectedCount} selected",
            2 => IsShowingHistory
                ? $"{visible} update history · {SelectedCount} selected"
                : $"{visible} Windows updates · {UpdateCount} pending · {SelectedCount} selected",
            3 => $"{visible} search results · {SelectedCount} selected",
            4 => $"{visible} installed · {SelectedCount} selected",
            _ => ShowUpdatesOnly && TotalCount > 0
                ? $"{visible} shown · {TotalCount} total · {UpdateCount} updates · {SelectedCount} selected"
                : $"{visible} programs · {UpdateCount} updates · {SelectedCount} selected"
        };
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        if (IsBusy)
        {
            ShowEmptyState = false;
            return;
        }

        var visible = CountVisible();
        // Empty list OR filter hides everything (e.g. Show updates only with 0 updates)
        var empty = visible == 0;
        ShowEmptyState = empty;

        if (!empty)
        {
            ShowHistoryButton = false;
            return;
        }

        switch (SelectedTabIndex)
        {
            case 1:
                EmptyStateTitle = IsShowingHistory ? "No driver history" : "No driver updates available";
                EmptyStateDetail = IsShowingHistory
                    ? "No successful driver installs found in Windows Update history."
                    : "Nothing pending. You can open update history below.";
                ShowHistoryButton = !IsShowingHistory;
                break;
            case 2:
                EmptyStateTitle = IsShowingHistory ? "No update history" : "No Windows updates available";
                EmptyStateDetail = IsShowingHistory
                    ? "No successful installs found in Windows Update history."
                    : "Nothing pending. You can open update history below.";
                ShowHistoryButton = !IsShowingHistory;
                break;
            case 3:
                EmptyStateTitle = "Search packages to install";
                EmptyStateDetail = "Enter a name or package ID in the catalog box, then Search.";
                ShowHistoryButton = false;
                break;
            case 4:
                EmptyStateTitle = "No packages loaded";
                EmptyStateDetail = "Click Load installed to list winget packages you can uninstall.";
                ShowHistoryButton = false;
                break;
            default:
                if (ActiveList.Count > 0 && ShowUpdatesOnly && UpdateCount == 0)
                {
                    EmptyStateTitle = "No updates available";
                    EmptyStateDetail =
                        $"{ActiveList.Count} programs are loaded, but none have updates.\n" +
                        "Turn off “Show updates only” above to see the full list.";
                }
                else
                {
                    EmptyStateTitle = "No programs loaded";
                    EmptyStateDetail = "Click Scan, then Check updates.";
                }

                ShowHistoryButton = false;
                break;
        }
    }
}

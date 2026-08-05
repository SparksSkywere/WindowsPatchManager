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
    public ObservableCollection<string> LogEntries { get; } = [];

    public ICollectionView ProgramsView { get; }
    public ICollectionView DriversView { get; }
    public ICollectionView WindowsUpdatesView { get; }

    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _searchText = string.Empty;
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
    [ObservableProperty] private string _emptyStateDetail = "Run Scan or Check updates to search again.";
    [ObservableProperty] private bool _showHistoryButton;

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
        _ => Programs
    };

    /// <summary>Bound list for the selected tab (Programs / Drivers / Windows Updates).</summary>
    public ICollectionView ActiveView => SelectedTabIndex switch
    {
        1 => DriversView,
        2 => WindowsUpdatesView,
        _ => ProgramsView
    };

    partial void OnSearchTextChanged(string value) =>
        UiThread.Post(() =>
        {
            ProgramsView.Refresh();
            DriversView.Refresh();
            WindowsUpdatesView.Refresh();
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

    partial void OnSelectedTabIndexChanged(int value) =>
        UiThread.Post(() =>
        {
            IsShowingHistory = false;
            OnPropertyChanged(nameof(ActiveView));
            RefreshSummary();
            UpdateEmptyState();
        });

    private bool FilterProgram(object obj)
    {
        if (obj is not ProgramItemViewModel item)
            return false;

        if (ShowUpdatesOnly && !item.UpdateAvailable)
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
        await ScanAsync().ConfigureAwait(true);
        if (_patchManager.Config.Config.General.AutoCheckUpdates)
            await CheckUpdatesAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ScanAsync()
    {
        IsShowingHistory = false;
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
            else
            {
                var list = await _patchManager.ScanAsync(progress, ct).ConfigureAwait(false);
                UiThread.Send(() => ReplaceList(Programs, list));
            }
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

                    BusyOverlayText = string.IsNullOrWhiteSpace(p.Message)
                        ? $"Updating {p.ProgramName}\n{p.Completed} of {p.Total} · {ProgressPercentText}"
                        : $"{p.ProgramName}\n{p.Message}\n{ProgressPercentText}";

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
                    BusyOverlayText = $"{p.Message}\n{ProgressPercentText}";
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
        else
            WindowsUpdatesView.Refresh();

        RefreshSummary();
        UpdateEmptyState();
    }

    private void RefreshSelectionCount()
    {
        SelectedCount = ActiveList.Count(p => p.IsSelected);
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        TotalCount = ActiveList.Count;
        UpdateCount = ActiveList.Count(p => p.UpdateAvailable);
        SelectedCount = ActiveList.Count(p => p.IsSelected);
        var tab = SelectedTabIndex switch
        {
            1 => IsShowingHistory ? "driver history" : "drivers",
            2 => IsShowingHistory ? "Windows update history" : "Windows updates",
            _ => "programs"
        };
        SummaryText = IsShowingHistory
            ? $"{TotalCount} installed {tab} · history view"
            : $"{TotalCount} {tab} · {UpdateCount} update(s) available · {SelectedCount} selected";
        if (string.IsNullOrWhiteSpace(StatusText) || StatusText is "Ready" or "Error")
            StatusText = SummaryText;
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        var empty = !IsBusy && ActiveList.Count == 0;
        ShowEmptyState = empty;

        if (!empty)
        {
            ShowHistoryButton = false;
            return;
        }

        switch (SelectedTabIndex)
        {
            case 1:
                EmptyStateTitle = IsShowingHistory ? "No driver history found" : "No driver updates available";
                EmptyStateDetail = IsShowingHistory
                    ? "Windows Update has no successful driver installations in history on this PC."
                    : "Your drivers look up to date, or Windows Update returned no driver packages.\nYou can still browse installed driver updates from history.";
                ShowHistoryButton = !IsShowingHistory;
                break;
            case 2:
                EmptyStateTitle = IsShowingHistory ? "No Windows Update history found" : "No Windows updates available";
                EmptyStateDetail = IsShowingHistory
                    ? "Windows Update has no successful software installations in history on this PC."
                    : "This PC has no pending Windows / CVE updates right now.\nYou can browse previously installed updates from history.";
                ShowHistoryButton = !IsShowingHistory;
                break;
            default:
                EmptyStateTitle = "No programs loaded";
                EmptyStateDetail = "Click Scan to detect installed applications, then Check updates.";
                ShowHistoryButton = false;
                break;
        }
    }
}

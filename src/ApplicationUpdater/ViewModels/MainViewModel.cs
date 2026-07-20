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
    public ObservableCollection<string> LogEntries { get; } = [];
    public ICollectionView ProgramsView { get; }

    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _showUpdatesOnly;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _updateCount;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _summaryText = "No programs loaded";

    public MainViewModel(PatchManagerService patchManager, SchedulerService scheduler, LogService log)
    {
        _patchManager = patchManager;
        _scheduler = scheduler;
        _log = log;

        ProgramsView = CollectionViewSource.GetDefaultView(Programs);
        ProgramsView.Filter = FilterProgram;
        ProgramsView.SortDescriptions.Add(new SortDescription(nameof(ProgramItemViewModel.UpdateAvailable), ListSortDirection.Descending));
        ProgramsView.SortDescriptions.Add(new SortDescription(nameof(ProgramItemViewModel.Name), ListSortDirection.Ascending));

        // UI-owned log list — always append on the dispatcher
        _log.MessageLogged += (_, line) => UiThread.Post(() =>
        {
            LogEntries.Add(line);
            while (LogEntries.Count > 1000)
                LogEntries.RemoveAt(0);
        });
    }

    partial void OnSearchTextChanged(string value) =>
        UiThread.Post(() => ProgramsView.Refresh());

    partial void OnShowUpdatesOnlyChanged(bool value)
    {
        _patchManager.Config.Config.General.ShowOnlyUpdatable = value;
        _patchManager.Config.Save();
        UiThread.Post(() =>
        {
            ProgramsView.Refresh();
            RefreshSummary();
        });
    }

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
               item.Source.Contains(q, StringComparison.OrdinalIgnoreCase);
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
        await RunBusyAsync("Scanning installed programs...", async (ct, progress) =>
        {
            var list = await _patchManager.ScanAsync(progress, ct).ConfigureAwait(false);
            UiThread.Send(() => ReplacePrograms(list));
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CheckUpdatesAsync()
    {
        await RunBusyAsync("Checking for updates...", async (ct, progress) =>
        {
            if (_patchManager.Programs.Count == 0)
            {
                var scanned = await _patchManager.ScanAsync(progress, ct).ConfigureAwait(false);
                UiThread.Send(() => ReplacePrograms(scanned));
            }

            await _patchManager.CheckUpdatesAsync(progress, ct).ConfigureAwait(false);
            UiThread.Send(() =>
            {
                ReplacePrograms(_patchManager.Programs);
                if (_patchManager.Config.Config.Notifications.ShowUpdateAvailable && UpdateCount > 0)
                    StatusText = $"{UpdateCount} update(s) available";
            });
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UpdateSelectedAsync()
    {
        var selected = Programs.Where(p => p.IsSelected && p.UpdateAvailable).Select(p => p.Model).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(
                "Select one or more programs that have updates available.",
                "Windows Patch Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!ConfirmUpdates(selected.Count))
            return;

        await RunUpdatesAsync(selected).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UpdateAllAsync()
    {
        var all = Programs.Where(p => p.UpdateAvailable).Select(p => p.Model).ToList();
        if (all.Count == 0)
        {
            MessageBox.Show(
                "No updates are available. Run Check for Updates first.",
                "Windows Patch Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!ConfirmUpdates(all.Count))
            return;

        await RunUpdatesAsync(all).ConfigureAwait(true);
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var p in Programs)
            p.IsSelected = true;
        RefreshSelectionCount();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var p in Programs)
            p.IsSelected = false;
        RefreshSelectionCount();
    }

    [RelayCommand]
    private void SelectUpdatesOnly()
    {
        foreach (var p in Programs)
            p.IsSelected = p.UpdateAvailable;
        RefreshSelectionCount();
    }

    [RelayCommand]
    private void Export()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export program list",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = $"programs_{DateTime.Now:yyyyMMdd_HHmmss}.json",
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

    private bool CanRun() => !IsBusy;

    private bool ConfirmUpdates(int count)
    {
        if (!_patchManager.Config.Config.UpdateBehavior.RequireConfirmation)
            return true;

        var result = MessageBox.Show(
            $"Install updates for {count} program(s)?\n\nSome installers may require administrator approval (UAC).",
            "Confirm updates",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }

    private async Task RunUpdatesAsync(List<ProgramInfo> programs)
    {
        await RunBusyAsync($"Updating {programs.Count} program(s)...", async (ct, _) =>
        {
            // Capture UI sync context for progress callbacks
            var progress = new Progress<UpdateProgress>(p =>
            {
                UiThread.Post(() =>
                {
                    ProgressValue = p.Total == 0 ? 0 : (p.Completed * 100.0 / p.Total);
                    IsProgressIndeterminate = false;
                    StatusText = $"Updating {p.ProgramName} ({p.Completed}/{p.Total})";
                });

                if (p.Success)
                    _log.Success($"Updated {p.ProgramName}");
                else
                    _log.Error($"Failed {p.ProgramName}: {p.Message}");
            });

            var results = await _patchManager.UpdateAsync(programs, progress, ct).ConfigureAwait(false);
            var ok = results.Values.Count(r => r.Success);
            var fail = results.Count - ok;

            await _patchManager.CheckUpdatesAsync(null, ct).ConfigureAwait(false);

            UiThread.Send(() =>
            {
                StatusText = $"Updates finished: {ok} succeeded, {fail} failed";
                ReplacePrograms(_patchManager.Programs);

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

    private async Task RunBusyAsync(string status, Func<CancellationToken, IProgress<ScanProgress>, Task> work)
    {
        if (IsBusy) return;

        _cts = new CancellationTokenSource();
        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        StatusText = status;
        NotifyBusyCommands();

        // Progress<T> captures the current (UI) SynchronizationContext when constructed here
        var progress = new Progress<ScanProgress>(p =>
        {
            UiThread.Post(() =>
            {
                StatusText = p.Message;
                if (p.Percent >= 0)
                {
                    IsProgressIndeterminate = false;
                    ProgressValue = p.Percent;
                }
                else
                {
                    IsProgressIndeterminate = true;
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

            NotifyBusyCommands();
            _cts?.Dispose();
            _cts = null;
            RefreshSummary();
        }
    }

    private void NotifyBusyCommands()
    {
        ScanCommand.NotifyCanExecuteChanged();
        CheckUpdatesCommand.NotifyCanExecuteChanged();
        UpdateSelectedCommand.NotifyCanExecuteChanged();
        UpdateAllCommand.NotifyCanExecuteChanged();
    }

    private void ReplacePrograms(IReadOnlyList<ProgramInfo> list)
    {
        // Must run on UI thread (caller ensures via UiThread.Send)
        Programs.Clear();
        foreach (var p in list)
        {
            var vm = new ProgramItemViewModel(p);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ProgramItemViewModel.IsSelected))
                    RefreshSelectionCount();
            };
            Programs.Add(vm);
        }

        ProgramsView.Refresh();
        RefreshSummary();
    }

    private void RefreshSelectionCount()
    {
        SelectedCount = Programs.Count(p => p.IsSelected);
    }

    private void RefreshSummary()
    {
        TotalCount = Programs.Count;
        UpdateCount = Programs.Count(p => p.UpdateAvailable);
        SelectedCount = Programs.Count(p => p.IsSelected);
        SummaryText = $"{TotalCount} programs · {UpdateCount} update(s) available · {SelectedCount} selected";
        if (string.IsNullOrWhiteSpace(StatusText) || StatusText is "Ready" or "Error")
            StatusText = SummaryText;
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ApplicationUpdater.Helpers;
using ApplicationUpdater.Models;
using ApplicationUpdater.Services;
using ApplicationUpdater.ViewModels;
using ApplicationUpdater.Views;

namespace ApplicationUpdater;

public partial class MainWindow : Window
{
    private readonly ConfigService _config;
    private readonly WingetService _winget;
    private readonly LogService _log;
    private readonly MainViewModel _viewModel;
    private string? _lastSortProperty;
    private ListSortDirection _lastSortDirection = ListSortDirection.Ascending;
    private string _lastCellProperty = nameof(ProgramItemViewModel.PackageId);
    private string _lastCellHeader = "Package ID";

    private static readonly Dictionary<string, string> HeaderToProperty = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Name"] = nameof(ProgramItemViewModel.Name),
        ["Program"] = nameof(ProgramItemViewModel.Name),
        ["Current"] = nameof(ProgramItemViewModel.Version),
        ["Status"] = nameof(ProgramItemViewModel.UpdateStatus),
        ["Available"] = nameof(ProgramItemViewModel.AvailableVersion),
        ["Progress"] = nameof(ProgramItemViewModel.ProgressPercent),
        ["Last updated"] = nameof(ProgramItemViewModel.LastUpdated),
        ["Source"] = nameof(ProgramItemViewModel.Source),
        ["Package ID"] = nameof(ProgramItemViewModel.PackageId),
        ["Package / ID"] = nameof(ProgramItemViewModel.PackageId),
        ["Publisher"] = nameof(ProgramItemViewModel.Publisher)
    };

    public MainWindow(MainViewModel viewModel, ConfigService config, WingetService winget, LogService log)
    {
        InitializeComponent();
        Title = AppInfo.ProductName;
        AppIcon.ApplyTo(this);
        DataContext = viewModel;
        _viewModel = viewModel;
        _config = config;
        _winget = winget;
        _log = log;
        SourceInitialized += (_, _) =>
            WindowChromeHelper.ApplyTheme(this, ThemeManager.IsDarkEffective);

        Loaded += async (_, _) =>
        {
            ThemeManager.RefreshWindow(this);
            if (viewModel.LoadedCommand.CanExecute(null))
                await viewModel.LoadedCommand.ExecuteAsync(null);
        };
    }

    private void Options_Click(object sender, RoutedEventArgs e)
    {
        var vm = new SettingsViewModel(_config);
        var window = new SettingsWindow(vm) { Owner = this };
        window.ShowDialog();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    private void Feedback_Click(object sender, RoutedEventArgs e) =>
        OpenUrl(AppInfo.GitHubIssuesUrl);

    private void GitHub_Click(object sender, RoutedEventArgs e) =>
        OpenUrl(AppInfo.GitHubUrl);

    private void Releases_Click(object sender, RoutedEventArgs e) =>
        OpenUrl(AppInfo.GitHubReleasesUrl);

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            MessageBox.Show(url, "Open in browser", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void CopyCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = SelectedProgram is not null && ProgramList.IsKeyboardFocusWithin;
        e.Handled = true;
    }

    private void CopyCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        CopyText(GetPropertyValue(SelectedProgram, _lastCellProperty));
        e.Handled = true;
    }

    private void ProgramList_ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header)
            return;

        // Ignore select-all checkbox column and non-text headers
        if (header.Column?.Header is CheckBox || e.OriginalSource is CheckBox)
            return;

        var rawHeader = header.Column?.Header?.ToString()
                        ?? (header.Content as TextBlock)?.Text
                        ?? header.Content?.ToString()
                        ?? string.Empty;
        var headerText = rawHeader.Replace(" ▲", string.Empty).Replace(" ▼", string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(headerText) ||
            headerText.Contains("System.Windows.Controls.CheckBox", StringComparison.Ordinal) ||
            !HeaderToProperty.TryGetValue(headerText, out var property))
            return;

        if (DataContext is not MainViewModel vm)
            return;

        var direction = ListSortDirection.Ascending;
        if (string.Equals(_lastSortProperty, property, StringComparison.Ordinal) &&
            _lastSortDirection == ListSortDirection.Ascending)
        {
            direction = ListSortDirection.Descending;
        }

        _lastSortProperty = property;
        _lastSortDirection = direction;

        var view = vm.ActiveView;
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(property, direction));
            if (!string.Equals(property, nameof(ProgramItemViewModel.Name), StringComparison.Ordinal))
                view.SortDescriptions.Add(new SortDescription(nameof(ProgramItemViewModel.Name), ListSortDirection.Ascending));
        }

        // Visual cue on headers
        if (ProgramList.View is GridView grid)
        {
            foreach (var col in grid.Columns)
            {
                if (col.Header is not string s)
                    continue;
                var baseHeader = s.Replace(" ▲", string.Empty).Replace(" ▼", string.Empty).Trim();
                if (ReferenceEquals(col, header.Column))
                    col.Header = direction == ListSortDirection.Ascending ? $"{baseHeader} ▲" : $"{baseHeader} ▼";
                else if (!string.IsNullOrEmpty(baseHeader))
                    col.Header = baseHeader;
            }
        }
    }

    private void ProgramList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Select the row under the cursor and remember which column was clicked for "Copy cell".
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null && dep is not ListViewItem && dep is not GridViewColumnHeader)
            dep = VisualTreeHelper.GetParent(dep);

        if (dep is ListViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
            ResolveClickedColumn(e.GetPosition(ProgramList));
        }
    }

    private void ProgramList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ProgramList.SelectedItem is null)
            e.Handled = true;
        else
            UpdateCopyCellMenuHeader();
    }

    private void ResolveClickedColumn(Point positionInList)
    {
        if (ProgramList.View is not GridView grid || grid.Columns.Count == 0)
            return;

        // Account for horizontal scroll offset inside the ListView
        var scrollViewer = FindVisualChild<ScrollViewer>(ProgramList);
        var x = positionInList.X + (scrollViewer?.HorizontalOffset ?? 0);

        double offset = 0;
        foreach (var col in grid.Columns)
        {
            var width = col.ActualWidth > 0 ? col.ActualWidth : col.Width;
            if (double.IsNaN(width) || width <= 0)
                width = 100;

            if (x >= offset && x < offset + width)
            {
                var headerText = (col.Header?.ToString() ?? string.Empty)
                    .Replace(" ▲", string.Empty).Replace(" ▼", string.Empty).Trim();

                if (HeaderToProperty.TryGetValue(headerText, out var prop))
                {
                    _lastCellProperty = prop;
                    _lastCellHeader = headerText;
                }
                else if (string.IsNullOrEmpty(headerText))
                {
                    // Checkbox column — default to package id for copy
                    _lastCellProperty = nameof(ProgramItemViewModel.PackageId);
                    _lastCellHeader = "Package ID";
                }

                break;
            }

            offset += width;
        }

        UpdateCopyCellMenuHeader();
    }

    private void UpdateCopyCellMenuHeader()
    {
        if (CopyCellMenuItem is not null)
            CopyCellMenuItem.Header = $"Copy {_lastCellHeader}";
    }

    private ProgramItemViewModel? SelectedProgram =>
        ProgramList.SelectedItem as ProgramItemViewModel;

    private void ExcludeProgram_Click(object sender, RoutedEventArgs e)
    {
        // Prefer all selected rows; fall back to the right-clicked row.
        var targets = ProgramList.SelectedItems
            .OfType<ProgramItemViewModel>()
            .ToList();
        if (targets.Count == 0 && SelectedProgram is not null)
            targets.Add(SelectedProgram);

        if (targets.Count == 0)
            return;

        var added = 0;
        foreach (var item in targets)
        {
            if (_config.ExcludeProgram(item.Model))
                added++;

            // Reflect exclusion immediately in the list
            item.Model.UpdateAvailable = false;
            item.Model.AvailableVersion = string.Empty;
            item.IsSelected = false;
            item.NotifyModelChanged();
        }

        _viewModel.NotifyListChanged();

        var names = string.Join(", ", targets.Select(t => t.Name).Take(3));
        if (targets.Count > 3)
            names += $" (+{targets.Count - 3} more)";

        if (added > 0)
            MessageBox.Show(
                $"Excluded {targets.Count} program(s) from updates:\n{names}\n\nYou can edit the full list under Options → Exclusions.",
                "Excluded",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        else
            MessageBox.Show(
                "That program is already in the exclusion list.",
                "Excluded",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
    }

    private void InstallSpecificVersion_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedProgram;
        if (item is null)
            return;

        if (string.IsNullOrWhiteSpace(item.Model.PackageId) ||
            item.Model.Source is PackageSource.WindowsUpdate or PackageSource.Driver or PackageSource.Registry)
        {
            MessageBox.Show(
                "Install specific version is only available for winget packages with a package ID.",
                "Install specific version",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_viewModel.IsBusy)
        {
            MessageBox.Show("Wait for the current operation to finish.", "Install specific version",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new InstallVersionWindow(item.Model, _winget, _log) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.InstallResult?.Success == true)
        {
            item.NotifyModelChanged();
            _viewModel.NotifyListChanged();
            MessageBox.Show(
                $"Installed {item.Name} version {item.Version}.",
                "Install complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void CopyCell_Click(object sender, RoutedEventArgs e) =>
        CopyText(GetPropertyValue(SelectedProgram, _lastCellProperty));

    private void CopyPackageId_Click(object sender, RoutedEventArgs e) =>
        CopyText(SelectedProgram?.PackageId);

    private void CopyName_Click(object sender, RoutedEventArgs e) =>
        CopyText(SelectedProgram?.Name);

    private void CopyVersion_Click(object sender, RoutedEventArgs e) =>
        CopyText(SelectedProgram?.Version);

    private void CopyAvailable_Click(object sender, RoutedEventArgs e) =>
        CopyText(SelectedProgram?.AvailableVersion);

    private void CopySource_Click(object sender, RoutedEventArgs e) =>
        CopyText(SelectedProgram?.Source);

    private void CopyPublisher_Click(object sender, RoutedEventArgs e) =>
        CopyText(SelectedProgram?.Publisher);

    private void CopyRow_Click(object sender, RoutedEventArgs e)
    {
        var p = SelectedProgram;
        if (p is null) return;
        CopyText(string.Join('\t',
            p.Name, p.Version, p.UpdateStatus, p.AvailableVersion, p.Source, p.PackageId, p.Publisher));
    }

    private static string GetPropertyValue(ProgramItemViewModel? item, string propertyName)
    {
        if (item is null) return string.Empty;
        return propertyName switch
        {
            nameof(ProgramItemViewModel.Name) => item.Name,
            nameof(ProgramItemViewModel.Version) => item.Version,
            nameof(ProgramItemViewModel.UpdateStatus) => item.UpdateStatus,
            nameof(ProgramItemViewModel.AvailableVersion) => item.AvailableVersion,
            nameof(ProgramItemViewModel.Source) => item.Source,
            nameof(ProgramItemViewModel.PackageId) => item.PackageId,
            nameof(ProgramItemViewModel.Publisher) => item.Publisher,
            _ => item.PackageId
        };
    }

    private static void CopyText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text is "—")
            return;

        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard can be locked by other apps; ignore
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }
}

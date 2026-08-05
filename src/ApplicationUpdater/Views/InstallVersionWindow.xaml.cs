using System.Windows;
using System.Windows.Controls;
using ApplicationUpdater.Helpers;
using ApplicationUpdater.Models;
using ApplicationUpdater.Services;

namespace ApplicationUpdater.Views;

public partial class InstallVersionWindow : Window
{
    private readonly ProgramInfo _program;
    private readonly WingetService _winget;
    private readonly LogService _log;
    private CancellationTokenSource? _cts;

    public string? SelectedVersion { get; private set; }
    public UpdateResult? InstallResult { get; private set; }

    public InstallVersionWindow(ProgramInfo program, WingetService winget, LogService log)
    {
        InitializeComponent();
        AppIcon.ApplyTo(this);
        SourceInitialized += (_, _) =>
            WindowChromeHelper.ApplyTheme(this, ThemeManager.IsDarkEffective);
        ThemeManager.RefreshWindow(this);
        _program = program;
        _winget = winget;
        _log = log;

        TitleText.Text = $"Install specific version — {program.Name}";
        SubtitleText.Text =
            $"Package: {program.PackageId}\nCurrent: {program.Version}\n\n" +
            "Select a published version. Downgrades use a forced reinstall when required.";

        Loaded += async (_, _) => await LoadVersionsAsync().ConfigureAwait(true);
        Closed += (_, _) => _cts?.Cancel();
    }

    private async Task LoadVersionsAsync()
    {
        _cts = new CancellationTokenSource();
        LoadingText.Visibility = Visibility.Visible;
        VersionList.IsEnabled = false;
        InstallButton.IsEnabled = false;
        StatusText.Text = string.Empty;

        try
        {
            var versions = await _winget.ListAvailableVersionsAsync(_program.PackageId, _cts.Token)
                .ConfigureAwait(true);

            VersionList.Items.Clear();
            foreach (var v in versions)
            {
                var label = v;
                if (string.Equals(v, _program.Version, StringComparison.OrdinalIgnoreCase))
                    label = $"{v}  (current)";
                VersionList.Items.Add(new VersionItem(v, label));
            }

            LoadingText.Visibility = Visibility.Collapsed;
            VersionList.IsEnabled = true;

            if (versions.Count == 0)
            {
                StatusText.Text = "No versions returned by winget for this package (or the package has no version history).";
            }
            else
            {
                StatusText.Text = $"{versions.Count} version(s) available. Select one to install.";
                // Prefer currently installed if present, else first (newest)
                var current = versions.FirstOrDefault(v =>
                    string.Equals(v, _program.Version, StringComparison.OrdinalIgnoreCase));
                if (current is not null)
                    VersionList.SelectedItem = VersionList.Items.OfType<VersionItem>()
                        .FirstOrDefault(i => i.Version == current);
                else
                    VersionList.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            LoadingText.Visibility = Visibility.Collapsed;
            StatusText.Text = "Failed to load versions: " + ex.Message;
            _log.Error(ex.Message);
        }
    }

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InstallButton.IsEnabled = VersionList.SelectedItem is VersionItem;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (VersionList.SelectedItem is not VersionItem item)
            return;

        SelectedVersion = item.Version;
        InstallButton.IsEnabled = false;
        VersionList.IsEnabled = false;
        StatusText.Text = $"Installing {item.Version}…";

        try
        {
            _cts ??= new CancellationTokenSource();
            InstallResult = await _winget.InstallVersionAsync(_program, item.Version, _cts.Token)
                .ConfigureAwait(true);

            if (InstallResult.Success)
            {
                DialogResult = true;
                Close();
            }
            else
            {
                StatusText.Text = "Install failed: " + InstallResult.ErrorMessage;
                InstallButton.IsEnabled = true;
                VersionList.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Install failed: " + ex.Message;
            InstallButton.IsEnabled = true;
            VersionList.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
        Close();
    }

    private sealed record VersionItem(string Version, string Display)
    {
        public override string ToString() => Display;
    }
}

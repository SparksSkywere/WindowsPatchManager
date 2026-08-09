using System.Collections.ObjectModel;
using System.Windows;
using ApplicationUpdater.Helpers;
using ApplicationUpdater.Models;
using ApplicationUpdater.Services;
using ApplicationUpdater.Themes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApplicationUpdater.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigService _config;

    [ObservableProperty] private bool _autoCheckUpdates;
    [ObservableProperty] private int _checkIntervalHours;
    [ObservableProperty] private bool _createBackups;
    [ObservableProperty] private bool _allowInstallerDesktopShortcuts;
    [ObservableProperty] private bool _wingetEnabled;
    [ObservableProperty] private bool _chocolateyEnabled;
    [ObservableProperty] private bool _gitHubEnabled;
    [ObservableProperty] private bool _windowsUpdateEnabled;
    [ObservableProperty] private bool _cveScanEnabled;
    [ObservableProperty] private bool _prioritizeSecurity;
    [ObservableProperty] private bool _securityUpdatesOnly;
    [ObservableProperty] private bool _queryMsrcOnline;
    [ObservableProperty] private int _msrcMonthsToScan = 3;
    [ObservableProperty] private bool _msrcCriticalAndImportantOnly = true;
    [ObservableProperty] private bool _showUninstallableMsrcGaps;
    [ObservableProperty] private bool _microsoftStoreEnabled;
    [ObservableProperty] private bool _wslEnabled;
    [ObservableProperty] private bool _wslIncludeDistroPackages;
    [ObservableProperty] private bool _microsoftOfficeEnabled;
    [ObservableProperty] private string _gitHubReposText = string.Empty;
    [ObservableProperty] private string _gitHubToken = string.Empty;
    [ObservableProperty] private bool _selfUpdateEnabled;
    [ObservableProperty] private string _selfUpdateRepo = string.Empty;
    [ObservableProperty] private int _maxConcurrentUpdates;
    [ObservableProperty] private bool _includeUnknown;
    [ObservableProperty] private bool _includePinned;
    [ObservableProperty] private bool _silent;
    [ObservableProperty] private bool _showUpdateAvailable;
    [ObservableProperty] private bool _showUpdateComplete;
    [ObservableProperty] private bool _showErrors;
    [ObservableProperty] private string _excludedProgramsText = string.Empty;
    [ObservableProperty] private string _excludedPublishersText = string.Empty;
    [ObservableProperty] private string _excludedKeywordsText = string.Empty;
    [ObservableProperty] private string _excludedPackageIdsText = string.Empty;
    [ObservableProperty] private string _configPath = string.Empty;
    [ObservableProperty] private string _shortcutStatus = string.Empty;
    [ObservableProperty] private ThemeOption? _selectedTheme;

    public ObservableCollection<ThemeOption> ThemeOptions { get; } = new(ThemeCatalog.PickerOptions);

    public SettingsViewModel(ConfigService config)
    {
        _config = config;
        LoadFromConfig();
    }

    private bool _loading;

    /// <summary>Live-preview theme when the combo box changes (also saved on OK).</summary>
    partial void OnSelectedThemeChanged(ThemeOption? value)
    {
        if (_loading || value is null) return;
        ThemeManager.Apply(value.Id);
    }

    public void RevertThemePreview() =>
        ThemeManager.Apply(_config.Config.General.Theme);

    public void LoadFromConfig()
    {
        _loading = true;
        var c = _config.Config;
        AutoCheckUpdates = c.General.AutoCheckUpdates;
        CheckIntervalHours = c.General.CheckIntervalHours;
        CreateBackups = c.General.CreateBackups;
        AllowInstallerDesktopShortcuts = c.General.AllowInstallerDesktopShortcuts;
        var themeId = ThemeCatalog.NormalizeId(c.General.Theme);
        SelectedTheme = ThemeOptions.FirstOrDefault(t => t.Id == themeId)
                        ?? ThemeOptions.First();
        WingetEnabled = c.UpdateSources.Winget.Enabled;
        ChocolateyEnabled = c.UpdateSources.Chocolatey.Enabled;
        GitHubEnabled = c.UpdateSources.GitHub.Enabled && c.GitHub.Enabled;
        WindowsUpdateEnabled = c.WindowsUpdate.Enabled;
        CveScanEnabled = c.WindowsUpdate.CveScanEnabled;
        PrioritizeSecurity = c.WindowsUpdate.PrioritizeSecurity;
        SecurityUpdatesOnly = c.WindowsUpdate.SecurityUpdatesOnly;
        QueryMsrcOnline = c.WindowsUpdate.QueryMsrcOnline;
        MsrcMonthsToScan = Math.Clamp(c.WindowsUpdate.MsrcMonthsToScan, 1, 6);
        MsrcCriticalAndImportantOnly = c.WindowsUpdate.MsrcCriticalAndImportantOnly;
        ShowUninstallableMsrcGaps = c.WindowsUpdate.ShowUninstallableMsrcGaps;
        MicrosoftStoreEnabled = c.UpdateSources.MicrosoftStore.Enabled;
        WslEnabled = c.UpdateSources.Wsl.Enabled && c.Wsl.Enabled;
        WslIncludeDistroPackages = c.Wsl.IncludeDistroPackages;
        MicrosoftOfficeEnabled = c.UpdateSources.MicrosoftOffice.Enabled;
        GitHubToken = c.GitHub.Token ?? string.Empty;
        SelfUpdateEnabled = c.GitHub.SelfUpdate.Enabled;
        SelfUpdateRepo = string.IsNullOrWhiteSpace(c.GitHub.SelfUpdate.Owner)
            ? $"{AppInfo.GitHubOwner}/{AppInfo.GitHubRepo}"
            : $"{c.GitHub.SelfUpdate.Owner}/{c.GitHub.SelfUpdate.Repo}";
        GitHubReposText = string.Join(Environment.NewLine,
            c.GitHub.Repositories
                .Where(r => !string.IsNullOrWhiteSpace(r.Owner) && !string.IsNullOrWhiteSpace(r.Repo))
                .Select(r =>
                {
                    var line = $"{r.Owner}/{r.Repo}";
                    if (!string.IsNullOrWhiteSpace(r.DisplayName))
                        line += "|" + r.DisplayName;
                    if (!string.IsNullOrWhiteSpace(r.AssetPattern) && r.AssetPattern != ".exe")
                        line += "|" + r.AssetPattern;
                    return line;
                }));
        MaxConcurrentUpdates = c.UpdateBehavior.MaxConcurrentUpdates;
        IncludeUnknown = c.UpdateBehavior.IncludeUnknown;
        IncludePinned = c.UpdateBehavior.IncludePinned;
        Silent = c.UpdateBehavior.Silent;
        ShowUpdateAvailable = c.Notifications.ShowUpdateAvailable;
        ShowUpdateComplete = c.Notifications.ShowUpdateComplete;
        ShowErrors = c.Notifications.ShowErrors;
        ExcludedProgramsText = string.Join(Environment.NewLine, c.Exclusions.Programs);
        ExcludedPublishersText = string.Join(Environment.NewLine, c.Exclusions.Publishers);
        ExcludedKeywordsText = string.Join(Environment.NewLine, c.Exclusions.Keywords);
        ExcludedPackageIdsText = string.Join(Environment.NewLine, c.Exclusions.PackageIds);
        ConfigPath = _config.ConfigPath;
        RefreshShortcutStatus();
        _loading = false;
    }

    [RelayCommand]
    private void Save()
    {
        var c = _config.Config;
        c.General.AutoCheckUpdates = AutoCheckUpdates;
        c.General.CheckIntervalHours = Math.Clamp(CheckIntervalHours, 1, 168);
        c.General.CreateBackups = CreateBackups;
        c.General.AllowInstallerDesktopShortcuts = AllowInstallerDesktopShortcuts;
        var themeId = SelectedTheme?.Id ?? ThemeCatalog.SystemId;
        c.General.Theme = ThemeManager.ToConfigValue(themeId);
        ThemeManager.Apply(themeId);
        c.UpdateSources.Winget.Enabled = WingetEnabled;
        c.UpdateSources.Chocolatey.Enabled = ChocolateyEnabled;
        c.UpdateSources.GitHub.Enabled = GitHubEnabled;
        c.GitHub.Enabled = GitHubEnabled;
        c.GitHub.Token = string.IsNullOrWhiteSpace(GitHubToken) ? null : GitHubToken.Trim();
        c.WindowsUpdate.Enabled = WindowsUpdateEnabled;
        c.WindowsUpdate.IncludeDrivers = WindowsUpdateEnabled;
        c.WindowsUpdate.CveScanEnabled = CveScanEnabled;
        c.WindowsUpdate.PrioritizeSecurity = PrioritizeSecurity;
        c.WindowsUpdate.SecurityUpdatesOnly = SecurityUpdatesOnly;
        c.WindowsUpdate.QueryMsrcOnline = QueryMsrcOnline;
        c.WindowsUpdate.MsrcMonthsToScan = Math.Clamp(MsrcMonthsToScan, 1, 6);
        c.WindowsUpdate.MsrcCriticalAndImportantOnly = MsrcCriticalAndImportantOnly;
        c.WindowsUpdate.ShowUninstallableMsrcGaps = ShowUninstallableMsrcGaps;
        c.UpdateSources.MicrosoftStore.Enabled = MicrosoftStoreEnabled;
        c.UpdateSources.Wsl.Enabled = WslEnabled;
        c.Wsl.Enabled = WslEnabled;
        c.Wsl.IncludeDistroPackages = WslIncludeDistroPackages;
        c.UpdateSources.MicrosoftOffice.Enabled = MicrosoftOfficeEnabled;
        c.GitHub.SelfUpdate.Enabled = SelfUpdateEnabled;
        if (!string.IsNullOrWhiteSpace(SelfUpdateRepo) && SelfUpdateRepo.Contains('/'))
        {
            var parts = SelfUpdateRepo.Trim().Split('/', 2);
            c.GitHub.SelfUpdate.Owner = parts[0].Trim();
            c.GitHub.SelfUpdate.Repo = parts[1].Trim();
        }
        c.GitHub.Repositories = ParseGitHubRepos(GitHubReposText);
        c.UpdateBehavior.RequireConfirmation = false;
        c.UpdateBehavior.MaxConcurrentUpdates = Math.Clamp(MaxConcurrentUpdates, 1, 4);
        c.UpdateBehavior.IncludeUnknown = IncludeUnknown;
        c.UpdateBehavior.IncludePinned = IncludePinned;
        c.UpdateBehavior.Silent = Silent;
        c.Notifications.ShowUpdateAvailable = ShowUpdateAvailable;
        c.Notifications.ShowUpdateComplete = ShowUpdateComplete;
        c.Notifications.ShowErrors = ShowErrors;
        c.Exclusions.Programs = SplitLines(ExcludedProgramsText);
        c.Exclusions.Publishers = SplitLines(ExcludedPublishersText);
        c.Exclusions.Keywords = SplitLines(ExcludedKeywordsText);
        c.Exclusions.PackageIds = SplitLines(ExcludedPackageIdsText);
        _config.Save();
    }

    [RelayCommand]
    private void OpenFeedback()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppInfo.GitHubIssuesUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Feedback", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void CreateDesktopShortcut()
    {
        try
        {
            DesktopShortcutHelper.CreatePatchManagerShortcut();
            RefreshShortcutStatus();
            MessageBox.Show(
                $"Desktop shortcut created:\n{DesktopShortcutHelper.PatchManagerShortcutPath}",
                "Desktop shortcut",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not create desktop shortcut:\n{ex.Message}",
                "Desktop shortcut",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void RemoveDesktopShortcut()
    {
        try
        {
            if (DesktopShortcutHelper.RemovePatchManagerShortcut())
            {
                RefreshShortcutStatus();
                MessageBox.Show(
                    "Desktop shortcut removed.",
                    "Desktop shortcut",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                RefreshShortcutStatus();
                MessageBox.Show(
                    "No Windows Patch Manager desktop shortcut was found.",
                    "Desktop shortcut",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not remove desktop shortcut:\n{ex.Message}",
                "Desktop shortcut",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void RefreshShortcutStatus()
    {
        ShortcutStatus = DesktopShortcutHelper.PatchManagerShortcutExists()
            ? "Desktop shortcut: present"
            : "Desktop shortcut: not present";
    }

    private static List<string> SplitLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Lines: owner/repo or owner/repo|Display Name or owner/repo|Display Name|assetPattern
    /// </summary>
    private static List<GitHubTrackedRepo> ParseGitHubRepos(string text)
    {
        var list = new List<GitHubTrackedRepo>();
        foreach (var line in SplitLines(text))
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            var repoPart = parts[0];
            if (!repoPart.Contains('/'))
                continue;
            var slash = repoPart.IndexOf('/');
            var owner = repoPart[..slash].Trim();
            var repo = repoPart[(slash + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
                continue;

            list.Add(new GitHubTrackedRepo
            {
                Owner = owner,
                Repo = repo,
                DisplayName = parts.Length > 1 ? parts[1] : $"{owner}/{repo}",
                AssetPattern = parts.Length > 2 ? parts[2] : ".exe"
            });
        }

        return list;
    }
}

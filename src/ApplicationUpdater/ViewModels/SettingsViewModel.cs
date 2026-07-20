using ApplicationUpdater.Models;
using ApplicationUpdater.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApplicationUpdater.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigService _config;

    [ObservableProperty] private bool _autoCheckUpdates;
    [ObservableProperty] private int _checkIntervalHours;
    [ObservableProperty] private bool _createBackups;
    [ObservableProperty] private bool _wingetEnabled;
    [ObservableProperty] private bool _chocolateyEnabled;
    [ObservableProperty] private bool _requireConfirmation;
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

    public SettingsViewModel(ConfigService config)
    {
        _config = config;
        LoadFromConfig();
    }

    public void LoadFromConfig()
    {
        var c = _config.Config;
        AutoCheckUpdates = c.General.AutoCheckUpdates;
        CheckIntervalHours = c.General.CheckIntervalHours;
        CreateBackups = c.General.CreateBackups;
        WingetEnabled = c.UpdateSources.Winget.Enabled;
        ChocolateyEnabled = c.UpdateSources.Chocolatey.Enabled;
        RequireConfirmation = c.UpdateBehavior.RequireConfirmation;
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
    }

    [RelayCommand]
    private void Save()
    {
        var c = _config.Config;
        c.General.AutoCheckUpdates = AutoCheckUpdates;
        c.General.CheckIntervalHours = Math.Clamp(CheckIntervalHours, 1, 168);
        c.General.CreateBackups = CreateBackups;
        c.UpdateSources.Winget.Enabled = WingetEnabled;
        c.UpdateSources.Chocolatey.Enabled = ChocolateyEnabled;
        c.UpdateBehavior.RequireConfirmation = RequireConfirmation;
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

    private static List<string> SplitLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

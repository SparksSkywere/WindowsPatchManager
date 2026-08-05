using System.Text.Json.Serialization;

namespace ApplicationUpdater.Models;

public sealed class AppConfig
{
    [JsonPropertyName("general")]
    public GeneralSettings General { get; set; } = new();

    [JsonPropertyName("updateSources")]
    public UpdateSourcesSettings UpdateSources { get; set; } = new();

    [JsonPropertyName("github")]
    public GitHubSettings GitHub { get; set; } = new();

    [JsonPropertyName("windowsUpdate")]
    public WindowsUpdateSettings WindowsUpdate { get; set; } = new();

    [JsonPropertyName("exclusions")]
    public ExclusionSettings Exclusions { get; set; } = new();

    [JsonPropertyName("updateBehavior")]
    public UpdateBehaviorSettings UpdateBehavior { get; set; } = new();

    [JsonPropertyName("notifications")]
    public NotificationSettings Notifications { get; set; } = new();
}

public sealed class GeneralSettings
{
    public bool AutoCheckUpdates { get; set; } = true;
    public int CheckIntervalHours { get; set; } = 24;
    public bool CreateBackups { get; set; } = true;
    public string BackupDirectory { get; set; } = "backups";
    public bool ShowOnlyUpdatable { get; set; }
    public bool StartMinimized { get; set; }

    /// <summary>
    /// When true, package installers may leave new desktop shortcuts.
    /// When false (default), shortcuts created during an update run are removed afterward.
    /// </summary>
    public bool AllowInstallerDesktopShortcuts { get; set; }

    /// <summary>
    /// UI theme id: "system" (default, follow Windows light/dark),
    /// or Chronolog era themes: win95, win98, win2000, winxp, winvista, win7, win8, win10, win11, win11-dark.
    /// </summary>
    public string Theme { get; set; } = "system";
}

public sealed class UpdateSourcesSettings
{
    public SourceToggle Winget { get; set; } = new() { Enabled = true, Priority = 1 };
    public SourceToggle Chocolatey { get; set; } = new() { Enabled = true, Priority = 2 };
    public SourceToggle GitHub { get; set; } = new() { Enabled = true, Priority = 3 };
}

/// <summary>Track GitHub release-based apps (including this app if self-update is set).</summary>
public sealed class GitHubSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Optional PAT for higher rate limits / private repos.</summary>
    public string? Token { get; set; }
    public List<GitHubTrackedRepo> Repositories { get; set; } = [];
    public GitHubSelfUpdateSettings SelfUpdate { get; set; } = new();
}

public sealed class GitHubTrackedRepo
{
    public string Owner { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Optional installed version override; otherwise from registry/file if found.</summary>
    public string? InstalledVersion { get; set; }
    /// <summary>Glob-like fragment to pick a release asset (e.g. Setup, .msi, win-x64).</summary>
    public string AssetPattern { get; set; } = ".exe";
}

public sealed class GitHubSelfUpdateSettings
{
    public bool Enabled { get; set; } = true;
    public string Owner { get; set; } = "SparksSkywere";
    public string Repo { get; set; } = "WindowsPatchManager";
    public string AssetPattern { get; set; } = "Setup";
}

public sealed class WindowsUpdateSettings
{
    public bool Enabled { get; set; } = true;
    public bool IncludeDrivers { get; set; } = true;
    public bool IncludeOptional { get; set; }
}

public sealed class SourceToggle
{
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 1;
}

public sealed class ExclusionSettings
{
    public List<string> Programs { get; set; } = [];

    public List<string> Publishers { get; set; } = [];

    public List<string> Keywords { get; set; } = [];

    public List<string> PackageIds { get; set; } = [];
}

public sealed class UpdateBehaviorSettings
{
    /// <summary>Kept for config compatibility; update actions no longer prompt (selection is the confirmation).</summary>
    public bool RequireConfirmation { get; set; }
    public int MaxConcurrentUpdates { get; set; } = 2;
    public bool RestartIfRequired { get; set; }
    public bool IncludeUnknown { get; set; } = true;
    public bool IncludePinned { get; set; }
    public bool AcceptAgreements { get; set; } = true;
    public bool Silent { get; set; } = true;
}

public sealed class NotificationSettings
{
    public bool ShowUpdateAvailable { get; set; } = true;
    public bool ShowUpdateComplete { get; set; } = true;
    public bool ShowErrors { get; set; } = true;
}

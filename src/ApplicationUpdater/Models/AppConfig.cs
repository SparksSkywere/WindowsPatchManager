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

    [JsonPropertyName("wsl")]
    public WslSettings Wsl { get; set; } = new();

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

    /// <summary>
    /// Microsoft Store apps via winget source "msstore" (and Store package IDs).
    /// </summary>
    public SourceToggle MicrosoftStore { get; set; } = new() { Enabled = true, Priority = 4 };

    /// <summary>
    /// Windows Subsystem for Linux platform (<c>wsl --update</c> / Microsoft.WSL) and distro package upgrades.
    /// </summary>
    public SourceToggle Wsl { get; set; } = new() { Enabled = true, Priority = 5 };

    /// <summary>
    /// Microsoft 365 / Office Click-to-Run updates via OfficeC2RClient.
    /// </summary>
    public SourceToggle MicrosoftOffice { get; set; } = new() { Enabled = true, Priority = 6 };
}

/// <summary>WSL-specific scan/update options.</summary>
public sealed class WslSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true, also probe installed distros for package manager upgrades (apt/dnf/zypper/pacman).
    /// </summary>
    public bool IncludeDistroPackages { get; set; } = true;
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

    /// <summary>
    /// When true, the Windows Update tab runs a CVE/KB security scan:
    /// extracts CVEs from pending updates, ranks Critical/Important, inventories
    /// installed KBs, and optionally cross-references MSRC for missing security KBs.
    /// </summary>
    public bool CveScanEnabled { get; set; } = true;

    /// <summary>Sort and highlight security updates ahead of quality/feature patches.</summary>
    public bool PrioritizeSecurity { get; set; } = true;

    /// <summary>When true, only list security / critical updates (hide non-security software updates).</summary>
    public bool SecurityUpdatesOnly { get; set; }

    /// <summary>
    /// When true, query Microsoft Security Response Center (MSRC) online to attach
    /// CVE IDs / severity to pending Windows Update packages, and to discover KBs
    /// that still need a live WU match before they can be installed.
    /// </summary>
    public bool QueryMsrcOnline { get; set; } = true;

    /// <summary>How many recent monthly MSRC releases to scan for CVE→KB data (1–6).</summary>
    public int MsrcMonthsToScan { get; set; } = 3;

    /// <summary>
    /// When true (default), MSRC analysis keeps Critical and Important only.
    /// When false, also includes Moderate severity CVEs for the local OS.
    /// </summary>
    public bool MsrcCriticalAndImportantOnly { get; set; } = true;

    /// <summary>
    /// When true, list MSRC KBs that are not offered by Windows Update as informational
    /// rows (not installable). Default false — only show updates WU can actually install.
    /// </summary>
    public bool ShowUninstallableMsrcGaps { get; set; }
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

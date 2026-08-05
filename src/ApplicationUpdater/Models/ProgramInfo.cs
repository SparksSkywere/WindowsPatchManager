namespace ApplicationUpdater.Models;

public enum PackageSource
{
    Unknown,
    Winget,
    Chocolatey,
    Registry,
    GitHub,
    WindowsUpdate,
    Driver
}

public enum UpdateCategory
{
    Programs,
    Drivers,
    WindowsUpdates
}

public sealed class ProgramInfo
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "Unknown";
    public string Publisher { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public PackageSource Source { get; set; } = PackageSource.Unknown;
    public UpdateCategory Category { get; set; } = UpdateCategory.Programs;
    public bool UpdateAvailable { get; set; }
    public string AvailableVersion { get; set; } = string.Empty;
    public string? Notes { get; set; }

    /// <summary>
    /// Human-facing origin for sorting/display: Steam, Epic Games, winget, Windows, …
    /// Prefer this over raw PackageSource for the Source column.
    /// </summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>When this item was last successfully updated (or last known install/check time).</summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>0-100 per-item progress while an update is running; -1 = idle.</summary>
    public int ProgressPercent { get; set; } = -1;

    public string ProgressStatus { get; set; } = string.Empty;

    /// <summary>GitHub owner/repo when Source is GitHub.</summary>
    public string? GitHubOwner { get; set; }
    public string? GitHubRepo { get; set; }
    public string? DownloadUrl { get; set; }
    public string? KbId { get; set; }

    /// <summary>Shown in the Source column — store/origin first, then package manager.</summary>
    public string SourceDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Origin) &&
                !Origin.Equals("registry", StringComparison.OrdinalIgnoreCase) &&
                !Origin.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                return Origin;

            return Source switch
            {
                PackageSource.Winget => "winget",
                PackageSource.Chocolatey => "chocolatey",
                PackageSource.Registry => "Windows",
                PackageSource.GitHub => "GitHub",
                PackageSource.WindowsUpdate => "Windows Update",
                PackageSource.Driver => "Driver",
                _ => "Windows"
            };
        }
    }

    public string DisplayKey =>
        !string.IsNullOrWhiteSpace(PackageId)
            ? $"{Category}|{PackageId}"
            : $"{Category}|{Name}|{Version}".ToLowerInvariant();

    public string LastUpdatedDisplay =>
        LastUpdated is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm") : "—";

    public string ProgressDisplay =>
        ProgressPercent < 0 ? "—" : $"{ProgressPercent}%";

    public ProgramInfo Clone() => new()
    {
        Name = Name,
        Version = Version,
        Publisher = Publisher,
        InstallLocation = InstallLocation,
        PackageId = PackageId,
        Source = Source,
        Category = Category,
        UpdateAvailable = UpdateAvailable,
        AvailableVersion = AvailableVersion,
        Notes = Notes,
        Origin = Origin,
        LastUpdated = LastUpdated,
        ProgressPercent = ProgressPercent,
        ProgressStatus = ProgressStatus,
        GitHubOwner = GitHubOwner,
        GitHubRepo = GitHubRepo,
        DownloadUrl = DownloadUrl,
        KbId = KbId
    };
}

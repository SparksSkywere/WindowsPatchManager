namespace ApplicationUpdater.Models;

public enum PackageSource
{
    Unknown,
    Winget,
    Chocolatey,
    Registry,
    GitHub,
    WindowsUpdate,
    Driver,
    /// <summary>Microsoft Store package (typically winget source "msstore").</summary>
    MicrosoftStore,
    /// <summary>Windows Subsystem for Linux platform or distro packages.</summary>
    Wsl,
    /// <summary>Microsoft Office Click-to-Run client updates.</summary>
    Office
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

    /// <summary>MSRC severity: Critical, Important, Moderate, Low (Windows Update / CVE scan).</summary>
    public string? Severity { get; set; }

    /// <summary>Comma-separated CVE IDs associated with this update (e.g. CVE-2024-1234).</summary>
    public string? CveIds { get; set; }

    /// <summary>True when classification is a security update / critical security patch.</summary>
    public bool IsSecurityUpdate { get; set; }

    /// <summary>WU category / classification label (Security Updates, Critical Updates, …).</summary>
    public string? Classification { get; set; }

    /// <summary>
    /// Rank for sorting (lower = more urgent). Critical=0 … unknown/non-security=50.
    /// </summary>
    public int SeverityRank { get; set; } = 50;

    /// <summary>Shown in the Source column — store/origin first, then package manager.</summary>
    public string SourceDisplay
    {
        get
        {
            if (IsSecurityUpdate && Source is PackageSource.WindowsUpdate)
            {
                if (!string.IsNullOrWhiteSpace(Severity) &&
                    Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase))
                    return "Security · Critical";
                if (!string.IsNullOrWhiteSpace(Severity))
                    return $"Security · {Severity}";
                return "Security Update";
            }

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
                PackageSource.MicrosoftStore => "Microsoft Store",
                PackageSource.Wsl => "WSL",
                PackageSource.Office => "Microsoft Office",
                _ => "Windows"
            };
        }
    }

    public string SeverityDisplay =>
        string.IsNullOrWhiteSpace(Severity) ? (IsSecurityUpdate ? "Security" : "—") : Severity;

    public string CveDisplay =>
        string.IsNullOrWhiteSpace(CveIds) ? "—" : CveIds;

    public string KbDisplay =>
        string.IsNullOrWhiteSpace(KbId) ? "—" : KbId;

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
        KbId = KbId,
        Severity = Severity,
        CveIds = CveIds,
        IsSecurityUpdate = IsSecurityUpdate,
        Classification = Classification,
        SeverityRank = SeverityRank
    };
}

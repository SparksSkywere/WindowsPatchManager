namespace ApplicationUpdater.Models;

public enum PackageSource
{
    Unknown,
    Winget,
    Chocolatey,
    Registry
}

public sealed class ProgramInfo
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "Unknown";
    public string Publisher { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public PackageSource Source { get; set; } = PackageSource.Unknown;
    public bool UpdateAvailable { get; set; }
    public string AvailableVersion { get; set; } = string.Empty;
    public string? Notes { get; set; }

    public string SourceDisplay => Source switch
    {
        PackageSource.Winget => "winget",
        PackageSource.Chocolatey => "chocolatey",
        PackageSource.Registry => "registry",
        _ => "unknown"
    };

    public string DisplayKey =>
        !string.IsNullOrWhiteSpace(PackageId)
            ? PackageId
            : $"{Name}|{Version}".ToLowerInvariant();

    public ProgramInfo Clone() => new()
    {
        Name = Name,
        Version = Version,
        Publisher = Publisher,
        InstallLocation = InstallLocation,
        PackageId = PackageId,
        Source = Source,
        UpdateAvailable = UpdateAvailable,
        AvailableVersion = AvailableVersion,
        Notes = Notes
    };
}

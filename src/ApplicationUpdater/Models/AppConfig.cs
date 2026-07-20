using System.Text.Json.Serialization;

namespace ApplicationUpdater.Models;

public sealed class AppConfig
{
    [JsonPropertyName("general")]
    public GeneralSettings General { get; set; } = new();

    [JsonPropertyName("updateSources")]
    public UpdateSourcesSettings UpdateSources { get; set; } = new();

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
}

public sealed class UpdateSourcesSettings
{
    public SourceToggle Winget { get; set; } = new() { Enabled = true, Priority = 1 };
    public SourceToggle Chocolatey { get; set; } = new() { Enabled = true, Priority = 2 };
}

public sealed class SourceToggle
{
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 1;
}

public sealed class ExclusionSettings
{
    public List<string> Programs { get; set; } =
    [
        "Windows Security",
        "Microsoft Edge WebView2 Runtime",
        "Microsoft Visual C++ 2015-2022 Redistributable"
    ];

    public List<string> Publishers { get; set; } = [];

    public List<string> Keywords { get; set; } =
    [
        "driver",
        "codec",
        "redistributable",
        "runtime"
    ];

    public List<string> PackageIds { get; set; } = [];
}

public sealed class UpdateBehaviorSettings
{
    public bool RequireConfirmation { get; set; } = true;
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

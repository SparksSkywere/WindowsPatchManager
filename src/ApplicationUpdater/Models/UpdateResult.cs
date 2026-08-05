namespace ApplicationUpdater.Models;

public sealed class UpdateResult
{
    public required ProgramInfo Program { get; init; }
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }

    public TimeSpan Duration =>
        EndTime > StartTime ? EndTime - StartTime : TimeSpan.Zero;
}

public sealed class ScanProgress
{
    public string Message { get; init; } = string.Empty;
    public int Percent { get; init; } = -1;
    public bool IsIndeterminate => Percent < 0;
}

public sealed class UpdateProgress
{
    public string ProgramName { get; init; } = string.Empty;
    public string? ProgramKey { get; init; }
    public bool Success { get; init; }
    public int Completed { get; init; }
    public int Total { get; init; }
    /// <summary>Overall batch percent 0-100, or -1 unknown.</summary>
    public int OverallPercent { get; init; } = -1;
    /// <summary>Per-item percent 0-100 while that item is running.</summary>
    public int ItemPercent { get; init; } = -1;
    public string? Message { get; init; }
    public bool IsStarting { get; init; }
}

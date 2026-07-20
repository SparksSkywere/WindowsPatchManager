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
    public bool Success { get; init; }
    public int Completed { get; init; }
    public int Total { get; init; }
    public string? Message { get; init; }
}

using System.IO;

namespace ApplicationUpdater.Services;

/// <summary>
/// Thread-safe logging. Does not touch UI collections — subscribers marshal as needed.
/// </summary>
public sealed class LogService
{
    private readonly object _sync = new();
    private readonly string _logDirectory;

    public LogService(string? logDirectory = null)
    {
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppInfo.CompanyFolderName,
            AppInfo.AppDataFolderName,
            "logs");

        Directory.CreateDirectory(_logDirectory);
    }

    public string LogDirectory => _logDirectory;

    /// <summary>Raised on the calling thread (often a background worker). UI must marshal.</summary>
    public event EventHandler<string>? MessageLogged;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);
    public void Success(string message) => Write("OK", message);

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";

        lock (_sync)
        {
            try
            {
                var file = Path.Combine(_logDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line + Environment.NewLine);
            }
            catch
            {
                // ignore disk errors
            }
        }

        try
        {
            MessageLogged?.Invoke(this, line);
        }
        catch
        {
            // never let logging crash the app
        }
    }
}

using System.IO;
using System.Text.Json;
using ApplicationUpdater.Models;

namespace ApplicationUpdater.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _configPath;
    private AppConfig _config = new();

    public ConfigService(string? configPath = null)
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppInfo.CompanyFolderName,
            AppInfo.AppDataFolderName);

        Directory.CreateDirectory(appData);
        _configPath = configPath ?? Path.Combine(appData, "config.json");
        Load();
    }

    public AppConfig Config => _config;
    public string ConfigPath => _configPath;

    public string AppDataDirectory =>
        Path.GetDirectoryName(_configPath) ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppInfo.CompanyFolderName, AppInfo.AppDataFolderName);

    public void Load()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                _config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                return;
            }

            // Seed from default if present next to exe
            var defaultPath = Path.Combine(AppContext.BaseDirectory, "Assets", "config.default.json");
            if (File.Exists(defaultPath))
            {
                var json = File.ReadAllText(defaultPath);
                _config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
            else
            {
                _config = new AppConfig();
            }

            Save();
        }
        catch
        {
            _config = new AppConfig();
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_config, JsonOptions);
            File.WriteAllText(_configPath, json);
        }
        catch
        {
            // non-fatal
        }
    }

    public bool IsExcluded(ProgramInfo program)
    {
        var excl = _config.Exclusions;
        var name = program.Name ?? string.Empty;
        var publisher = program.Publisher ?? string.Empty;
        var id = program.PackageId ?? string.Empty;

        if (excl.Programs.Any(p => name.Equals(p, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (!string.IsNullOrWhiteSpace(id) &&
            excl.PackageIds.Any(p => id.Equals(p, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (!string.IsNullOrWhiteSpace(publisher) &&
            excl.Publishers.Any(p => publisher.Equals(p, StringComparison.OrdinalIgnoreCase)))
            return true;

        var nameLower = name.ToLowerInvariant();
        foreach (var keyword in excl.Keywords)
        {
            if (!string.IsNullOrWhiteSpace(keyword) &&
                nameLower.Contains(keyword.ToLowerInvariant(), StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

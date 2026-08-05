using System.IO;
using System.Text.Json;
using ApplicationUpdater.Helpers;

namespace ApplicationUpdater.Services;

/// <summary>
/// Remembers packages that reported "Unknown" current version and were successfully
/// reinstalled to a known available version, so they do not keep reappearing as updates
/// until a newer version is published.
/// </summary>
public sealed class UnknownVersionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, FixedPackage> _items = new(StringComparer.OrdinalIgnoreCase);

    public UnknownVersionStore(string appDataDirectory)
    {
        Directory.CreateDirectory(appDataDirectory);
        _path = Path.Combine(appDataDirectory, "unknown_version_fixes.json");
        Load();
    }

    public void Remember(string packageIdOrName, string installedVersion)
    {
        if (string.IsNullOrWhiteSpace(packageIdOrName) || string.IsNullOrWhiteSpace(installedVersion))
            return;
        if (VersionText.IsUnknown(installedVersion))
            return;

        lock (_gate)
        {
            _items[packageIdOrName.Trim()] = new FixedPackage
            {
                InstalledVersion = installedVersion.Trim(),
                FixedAt = DateTime.UtcNow
            };
            Save();
        }
    }

    public bool TryGetInstalledVersion(string packageIdOrName, out string installedVersion)
    {
        installedVersion = string.Empty;
        if (string.IsNullOrWhiteSpace(packageIdOrName))
            return false;

        lock (_gate)
        {
            if (_items.TryGetValue(packageIdOrName.Trim(), out var fixedPkg) &&
                !string.IsNullOrWhiteSpace(fixedPkg.InstalledVersion))
            {
                installedVersion = fixedPkg.InstalledVersion;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when we already forced this package to the same (or newer) available version.
    /// </summary>
    public bool IsSatisfied(string packageIdOrName, string? availableVersion)
    {
        if (!TryGetInstalledVersion(packageIdOrName, out var installed))
            return false;

        if (string.IsNullOrWhiteSpace(availableVersion) || VersionText.IsUnknown(availableVersion))
            return true;

        // Still offer an update only if catalog available is strictly newer than what we fixed to.
        var newer = VersionComparer.IsNewer(installed, availableVersion);
        return newer != true;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize<Dictionary<string, FixedPackage>>(json, JsonOptions);
            if (data is not null)
                _items = new Dictionary<string, FixedPackage>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _items = new Dictionary<string, FixedPackage>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_items, JsonOptions);
            File.WriteAllText(_path, json);
        }
        catch
        {
            // non-fatal
        }
    }

    private sealed class FixedPackage
    {
        public string InstalledVersion { get; set; } = string.Empty;
        public DateTime FixedAt { get; set; }
    }
}

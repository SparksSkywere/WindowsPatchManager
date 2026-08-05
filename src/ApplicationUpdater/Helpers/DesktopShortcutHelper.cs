using System.IO;
using System.Runtime.InteropServices;

namespace ApplicationUpdater.Helpers;

/// <summary>
/// Creates/removes the Windows Patch Manager desktop shortcut, and optionally
/// suppresses new desktop .lnk files created by package installers during an update run.
/// </summary>
public static class DesktopShortcutHelper
{
    public static string DesktopDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static string PatchManagerShortcutPath =>
        Path.Combine(DesktopDirectory, $"{AppInfo.ProductName}.lnk");

    public static bool PatchManagerShortcutExists() => File.Exists(PatchManagerShortcutPath);

    public static void CreatePatchManagerShortcut()
    {
        var target = Environment.ProcessPath
                     ?? Path.Combine(AppContext.BaseDirectory, AppInfo.ExeFileName);
        CreateShortcut(
            PatchManagerShortcutPath,
            target,
            AppContext.BaseDirectory,
            AppInfo.Description,
            target);
    }

    public static bool RemovePatchManagerShortcut()
    {
        var path = PatchManagerShortcutPath;
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    public static HashSet<string> SnapshotDesktopShortcuts()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(DesktopDirectory))
                return set;

            foreach (var file in Directory.EnumerateFiles(DesktopDirectory, "*.lnk"))
                set.Add(Path.GetFileName(file));
        }
        catch
        {
            // ignore
        }

        return set;
    }

    /// <summary>
    /// Removes desktop .lnk files that appeared after <paramref name="before"/> was taken.
    /// Never removes the Patch Manager shortcut itself.
    /// </summary>
    public static IReadOnlyList<string> RemoveNewDesktopShortcuts(HashSet<string> before)
    {
        var removed = new List<string>();
        try
        {
            if (!Directory.Exists(DesktopDirectory))
                return removed;

            var keepName = Path.GetFileName(PatchManagerShortcutPath);
            foreach (var file in Directory.EnumerateFiles(DesktopDirectory, "*.lnk"))
            {
                var name = Path.GetFileName(file);
                if (before.Contains(name))
                    continue;
                if (name.Equals(keepName, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    File.Delete(file);
                    removed.Add(name);
                }
                catch
                {
                    // locked / in use
                }
            }
        }
        catch
        {
            // ignore
        }

        return removed;
    }

    public static void CreateShortcut(
        string shortcutPath,
        string targetPath,
        string workingDirectory,
        string description,
        string? iconPath = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

        // IWshRuntimeLibrary via late-bound COM (no extra package reference)
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("WScript.Shell COM is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)
                        ?? throw new InvalidOperationException("Could not create WScript.Shell.");
        var shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.Description = description;
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
            shortcut.IconLocation = iconPath;
        shortcut.Save();

        Marshal.FinalReleaseComObject(shortcut);
        Marshal.FinalReleaseComObject(shell);
    }
}

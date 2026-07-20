using System.Reflection;

namespace ApplicationUpdater;

/// <summary>Product branding and version metadata for Windows Patch Manager.</summary>
public static class AppInfo
{
    public const string ProductName = "Windows Patch Manager";
    public const string ProductNameShort = "Patch Manager";
    public const string Company = "Skywere Industries";
    public const string Copyright = "Copyright © Skywere Industries";
    public const string Description =
        "Manage software updates on Windows. Detects installed programs and installs " +
        "available updates using Windows Package Manager (winget) and Chocolatey.";

    public const string ExeFileName = "WindowsPatchManager.exe";
    public const string InstallFolderName = "WindowsPatchManager";
    public const string UninstallRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WindowsPatchManager";
    public const string AppDataFolderName = "WindowsPatchManager";
    public const string CompanyFolderName = "Skywere Industries";
    public const string ScheduledTaskName = "Windows Patch Manager";

    public static string Version
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }

            return asm.GetName().Version?.ToString(3) ?? "2.1.0";
        }
    }

    public static string AboutText =>
        $"{ProductName}\n" +
        $"Version {Version}\n\n" +
        $"{Description}\n\n" +
        $"{Copyright}\n" +
        $"Created by {Company}";
}

using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace ApplicationUpdater.Helpers;

public static class ElevationHelper
{
    public const string SkipElevateArg = "--no-elevate";
    public const string ElevatingArg = "--elevating";

    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tries to relaunch the current process elevated. Returns true if a new elevated
    /// process was started (caller should exit). Returns false if already elevated,
    /// elevation was declined, or elevation is not possible — continue as-is.
    /// </summary>
    public static bool TryRelaunchElevated(string[] args)
    {
        if (IsElevated())
            return false;

        // Avoid loops if parent already tried, or user/CLI asked to skip.
        if (args.Any(a => a.Equals(SkipElevateArg, StringComparison.OrdinalIgnoreCase) ||
                          a.Equals(ElevatingArg, StringComparison.OrdinalIgnoreCase)))
            return false;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return false;

        // Preserve original args and mark that we already attempted elevation.
        var forwarded = args
            .Where(a => !a.Equals(ElevatingArg, StringComparison.OrdinalIgnoreCase))
            .Append(ElevatingArg)
            .ToArray();

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory
        };

        foreach (var arg in forwarded)
            psi.ArgumentList.Add(arg);

        try
        {
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User clicked No on UAC — continue without admin.
            return false;
        }
        catch
        {
            return false;
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Xml.Linq;

namespace ApplicationUpdater.Services;

public sealed class SchedulerService
{
    public static string TaskName => AppInfo.ScheduledTaskName;
    private readonly LogService _log;

    public SchedulerService(LogService log)
    {
        _log = log;
    }

    public bool CreateDailyTask(TimeSpan? runAt = null)
    {
        try
        {
            var exe = Environment.ProcessPath ??
                       Path.Combine(AppContext.BaseDirectory, AppInfo.ExeFileName);
            var start = DateTime.Today.Add(runAt ?? new TimeSpan(10, 0, 0));
            if (start < DateTime.Now)
                start = start.AddDays(1);

            var xml = BuildTaskXml(exe, start);
            var temp = Path.Combine(Path.GetTempPath(), $"wpm_task_{Guid.NewGuid():N}.xml");
            File.WriteAllText(temp, xml, System.Text.Encoding.Unicode);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Create /TN \"{TaskName}\" /XML \"{temp}\" /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc is null)
                    return false;

                proc.WaitForExit(30_000);
                var ok = proc.ExitCode == 0;
                if (ok)
                    _log.Success($"Scheduled task '{TaskName}' created.");
                else
                    _log.Error($"Failed to create scheduled task: {proc.StandardError.ReadToEnd()}");
                return ok;
            }
            finally
            {
                try { File.Delete(temp); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Scheduler error: {ex.Message}");
            return false;
        }
    }

    public bool RemoveTask()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /TN \"{TaskName}\" /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(15_000);
            var ok = proc.ExitCode == 0;
            if (ok) _log.Success("Scheduled task removed.");
            return ok;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to remove task: {ex.Message}");
            return false;
        }
    }

    public bool TaskExists()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(15_000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildTaskXml(string exePath, DateTime start)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(ns + "Task",
                new XAttribute("version", "1.2"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Date", DateTime.Now.ToString("s")),
                    new XElement(ns + "Author", AppInfo.Company),
                    new XElement(ns + "Description", $"Check for application updates via {AppInfo.ProductName}")
                ),
                new XElement(ns + "Triggers",
                    new XElement(ns + "CalendarTrigger",
                        new XElement(ns + "StartBoundary", start.ToString("s")),
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "ScheduleByDay",
                            new XElement(ns + "DaysInterval", "1")
                        )
                    )
                ),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "HighestAvailable")
                    )
                ),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "StartWhenAvailable", "true"),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", "true"),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "Hidden", "false"),
                    new XElement(ns + "ExecutionTimeLimit", "PT2H")
                ),
                new XElement(ns + "Actions",
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", exePath),
                        new XElement(ns + "Arguments", "--check-updates --no-ui"),
                        new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(exePath) ?? "")
                    )
                )
            )
        );

        using var sw = new StringWriter();
        doc.Save(sw);
        return sw.ToString();
    }
}

using ApplicationUpdater.Services;

namespace ApplicationUpdater.Cli;

public static class CliHost
{
    public static bool ShouldRunCli(string[] args)
    {
        if (args.Length == 0) return false;

        var set = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
        return set.Contains("--no-ui") ||
               set.Contains("--scan") ||
               set.Contains("--check-updates") ||
               set.Contains("--list") ||
               set.Contains("--list-updates") ||
               set.Contains("--update-all") ||
               set.Contains("--help") ||
               set.Contains("-h") ||
               set.Contains("/?");
    }

    public static async Task<int> RunAsync(
        string[] args,
        PatchManagerService patchManager,
        SchedulerService scheduler,
        LogService log)
    {
        var set = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
        var noConfirm = set.Contains("--no-confirm");

        if (set.Contains("--help") || set.Contains("-h") || set.Contains("/?"))
        {
            PrintHelp();
            return 0;
        }

        try
        {
            if (set.Contains("--scan") || set.Contains("--list") || set.Contains("--list-updates") ||
                set.Contains("--check-updates") || set.Contains("--update-all"))
            {
                Console.WriteLine("Scanning installed programs...");
                await patchManager.ScanAsync().ConfigureAwait(false);
                Console.WriteLine($"Found {patchManager.Programs.Count} programs.");
            }

            if (set.Contains("--list") && !set.Contains("--list-updates"))
            {
                foreach (var p in patchManager.Programs)
                    Console.WriteLine($"{p.Name}\t{p.Version}\t{p.SourceDisplay}\t{p.PackageId}");
            }

            if (set.Contains("--check-updates") || set.Contains("--list-updates") || set.Contains("--update-all"))
            {
                Console.WriteLine("Checking for updates...");
                var updates = await patchManager.CheckUpdatesAsync().ConfigureAwait(false);
                Console.WriteLine($"{updates.Count} update(s) available.");

                if (set.Contains("--list-updates") || set.Contains("--check-updates"))
                {
                    foreach (var p in updates)
                        Console.WriteLine($"* {p.Name}: {p.Version} -> {p.AvailableVersion} [{p.SourceDisplay}] ({p.PackageId})");
                }

                if (set.Contains("--update-all"))
                {
                    if (updates.Count == 0)
                    {
                        Console.WriteLine("Nothing to update.");
                        return 0;
                    }

                    if (!noConfirm && patchManager.Config.Config.UpdateBehavior.RequireConfirmation)
                    {
                        Console.Write("Proceed with updates? (y/N): ");
                        var answer = Console.ReadLine();
                        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine("Cancelled.");
                            return 1;
                        }
                    }

                    Console.WriteLine("Installing updates...");
                    var results = await patchManager.UpdateAsync(updates.ToList()).ConfigureAwait(false);
                    var ok = results.Values.Count(r => r.Success);
                    Console.WriteLine($"Done: {ok}/{results.Count} succeeded.");
                    foreach (var r in results.Values.Where(r => !r.Success))
                        Console.WriteLine($"FAIL {r.Program.Name}: {r.ErrorMessage}");
                }
            }

            if (set.Contains("--schedule-create"))
                scheduler.CreateDailyTask();
            if (set.Contains("--schedule-remove"))
                scheduler.RemoveTask();

            var exportArg = args.Select((a, i) => (a, i))
                .FirstOrDefault(t => t.a.Equals("--export", StringComparison.OrdinalIgnoreCase));
            if (exportArg.a is not null)
            {
                if (patchManager.Programs.Count == 0)
                    await patchManager.ScanAsync().ConfigureAwait(false);

                var path = exportArg.i + 1 < args.Length && !args[exportArg.i + 1].StartsWith('-')
                    ? args[exportArg.i + 1]
                    : $"programs_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                patchManager.Export(path);
                Console.WriteLine($"Exported to {path}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            log.Error(ex.ToString());
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Windows Patch Manager — CLI

            Usage:
              WindowsPatchManager.exe [options]

            Options:
              --scan              Scan installed programs
              --check-updates     Check for available updates
              --list              List installed programs
              --list-updates      List programs with updates
              --update-all        Install all available updates
              --no-confirm        Skip confirmation prompts
              --no-ui             Force console mode
              --export [file]     Export program list to JSON
              --schedule-create   Create daily scheduled task
              --schedule-remove   Remove scheduled task
              --help              Show this help

            Examples:
              WindowsPatchManager.exe --check-updates --no-ui
              WindowsPatchManager.exe --update-all --no-confirm --no-ui
            """);
    }
}

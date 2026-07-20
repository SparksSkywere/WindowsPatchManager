using System.Diagnostics;
using System.IO;
using System.Text;

namespace ApplicationUpdater.Helpers;

public sealed class ProcessResult
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public bool TimedOut { get; init; }
    public bool Success => ExitCode == 0 && !TimedOut;
    public string CombinedOutput =>
        string.IsNullOrWhiteSpace(StdErr) ? StdOut : $"{StdOut}\n{StdErr}".Trim();
}

public sealed class ProcessRunOptions
{
    public int TimeoutSeconds { get; init; } = 300;
    public string? WorkingDirectory { get; init; }
    public bool ShowWindow { get; init; }
    /// <summary>Request UAC elevation (UseShellExecute + runas). Output capture is limited.</summary>
    public bool Elevate { get; init; }
}

public static class ProcessRunner
{
    private static string? _wingetPath;

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default,
        int timeoutSeconds = 300,
        string? workingDirectory = null)
        => await RunAsync(
            fileName,
            arguments,
            new ProcessRunOptions
            {
                TimeoutSeconds = timeoutSeconds,
                WorkingDirectory = workingDirectory
            },
            cancellationToken).ConfigureAwait(false);

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        ProcessRunOptions options,
        CancellationToken cancellationToken = default)
    {
        var argList = arguments.ToList();
        var resolved = ResolveExecutable(fileName);

        if (options.Elevate)
            return await RunElevatedAsync(resolved, argList, options, cancellationToken).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = resolved,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = !options.ShowWindow,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
            psi.WorkingDirectory = options.WorkingDirectory;

        psi.Environment["WINGET_DISABLE_INTERACTIVITY"] =
            options.ShowWindow ? "0" : "1";
        psi.Environment["NO_COLOR"] = "1";

        foreach (var arg in argList)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                lock (stdout) stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                lock (stderr) stderr.AppendLine(e.Data);
        };

        try
        {
            if (!process.Start())
            {
                return new ProcessResult
                {
                    ExitCode = -1,
                    StdErr = $"Failed to start process: {resolved}"
                };
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return new ProcessResult
                {
                    ExitCode = -1,
                    TimedOut = true,
                    StdOut = stdout.ToString(),
                    StdErr = "Process timed out."
                };
            }

            // Drain async readers
            await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);

            return new ProcessResult
            {
                ExitCode = process.ExitCode,
                StdOut = stdout.ToString(),
                StdErr = stderr.ToString()
            };
        }
        catch (Exception ex)
        {
            TryKill(process);
            return new ProcessResult
            {
                ExitCode = -1,
                StdErr = ex.Message
            };
        }
    }

    public static async Task<bool> CommandExistsAsync(string command, CancellationToken cancellationToken = default)
    {
        if (command.Equals("winget", StringComparison.OrdinalIgnoreCase))
        {
            var path = FindWingetPath();
            return !string.IsNullOrWhiteSpace(path);
        }

        var result = await RunAsync("where.exe", [command], cancellationToken, timeoutSeconds: 15)
            .ConfigureAwait(false);
        return result.Success && !string.IsNullOrWhiteSpace(result.StdOut);
    }

    public static string ResolveExecutable(string fileName)
    {
        if (fileName.Equals("winget", StringComparison.OrdinalIgnoreCase))
            return FindWingetPath() ?? "winget";

        return fileName;
    }

    /// <summary>
    /// Prefer a real winget.exe under WindowsApps / DesktopAppInstaller, not a broken alias.
    /// </summary>
    public static string? FindWingetPath()
    {
        if (!string.IsNullOrWhiteSpace(_wingetPath) && File.Exists(_wingetPath))
            return _wingetPath;

        var candidates = new List<string>();

        try
        {
            var localApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe");
            candidates.Add(localApps);
        }
        catch { /* ignore */ }

        try
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var packages = Path.Combine(pf, "WindowsApps");
            if (Directory.Exists(packages))
            {
                foreach (var dir in Directory.EnumerateDirectories(packages, "Microsoft.DesktopAppInstaller_*")
                             .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var winget = Path.Combine(dir, "winget.exe");
                    if (File.Exists(winget))
                        candidates.Add(winget);
                }
            }
        }
        catch { /* access denied on WindowsApps is common */ }

        // where.exe
        try
        {
            var where = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "winget",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(where);
            if (p is not null)
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (File.Exists(line))
                        candidates.Add(line);
                }
            }
        }
        catch { /* ignore */ }

        _wingetPath = candidates.FirstOrDefault(File.Exists);
        return _wingetPath;
    }

    private static async Task<ProcessResult> RunElevatedAsync(
        string fileName,
        List<string> arguments,
        ProcessRunOptions options,
        CancellationToken cancellationToken)
    {
        // Elevated processes cannot easily redirect stdout. Capture via a temp log wrapper.
        var logFile = Path.Combine(Path.GetTempPath(), $"appupdater_elev_{Guid.NewGuid():N}.log");
        var argString = string.Join(" ", arguments.Select(QuoteArg));
        // cmd /c ""exe" args > log 2>&1"
        var cmdArgs = $"/c \"\"{fileName}\" {argString} > \"{logFile}\" 2>&1\"";

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = cmdArgs,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = options.ShowWindow ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
            WorkingDirectory = options.WorkingDirectory ?? Environment.SystemDirectory
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return new ProcessResult
                {
                    ExitCode = -1,
                    StdErr = "Failed to start elevated process (UAC cancelled?)."
                };
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return new ProcessResult
                {
                    ExitCode = -1,
                    TimedOut = true,
                    StdErr = "Elevated process timed out."
                };
            }

            var output = File.Exists(logFile) ? await File.ReadAllTextAsync(logFile, cancellationToken).ConfigureAwait(false) : string.Empty;
            try { File.Delete(logFile); } catch { /* ignore */ }

            return new ProcessResult
            {
                ExitCode = process.ExitCode,
                StdOut = output
            };
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — user declined UAC
            return new ProcessResult
            {
                ExitCode = -1,
                StdErr = "Administrator elevation was cancelled."
            };
        }
        catch (Exception ex)
        {
            return new ProcessResult
            {
                ExitCode = -1,
                StdErr = ex.Message
            };
        }
    }

    private static string QuoteArg(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.Contains(' ') || arg.Contains('"'))
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        return arg;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignored
        }
    }
}

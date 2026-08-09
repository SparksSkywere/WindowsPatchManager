using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ApplicationUpdater.Cli;
using ApplicationUpdater.Helpers;
using ApplicationUpdater.Services;
using ApplicationUpdater.ViewModels;

namespace ApplicationUpdater;

public partial class App : Application
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    private const int AttachParentProcess = -1;
    private static int _uiErrorCount;
    private LogService? _log;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                _log?.Error("Fatal: " + ex.Message);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log?.Error("Background task: " + args.Exception.GetBaseException().Message);
            args.SetObserved();
        };

        // GUI: offer elevation once. If the user declines UAC, continue without admin
        // (individual installers can still prompt later). CLI is left as-is.
        if (!CliHost.ShouldRunCli(e.Args) && ElevationHelper.TryRelaunchElevated(e.Args))
        {
            Shutdown(0);
            return;
        }

        var config = new ConfigService();
        ThemeManager.Apply(config.Config.General.Theme);

        _log = new LogService();
        if (ElevationHelper.IsElevated())
            _log.Info("Running with administrator privileges.");
        else
            _log.Info("Running without administrator privileges (UAC declined or unavailable). Installers may still request elevation.");

        var unknownVersions = new UnknownVersionStore(config.AppDataDirectory);
        var winget = new WingetService(config, _log);
        var chocolatey = new ChocolateyService(_log);
        var wsl = new WslUpdateService(config, _log);
        var office = new OfficeUpdateService(config, _log);
        var detector = new ProgramDetectorService(config, winget, chocolatey, wsl, office, _log, unknownVersions);
        var installer = new UpdateInstallerService(config, winget, chocolatey, wsl, office, _log, unknownVersions);
        var github = new GitHubUpdateService(config, _log);
        var msrc = new MsrcCveService(config, _log);
        var windowsUpdate = new WindowsUpdateService(config, _log, msrc);
        var patchManager = new PatchManagerService(config, detector, installer, winget, github, windowsUpdate, wsl, office, _log);
        var scheduler = new SchedulerService(_log);

        if (CliHost.ShouldRunCli(e.Args))
        {
            EnsureConsole();
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var code = await CliHost.RunAsync(e.Args, patchManager, scheduler, _log).ConfigureAwait(true);
            Shutdown(code);
            return;
        }

        // Ensure self-update defaults point at the public repo when empty
        if (string.IsNullOrWhiteSpace(config.Config.GitHub.SelfUpdate.Owner))
        {
            config.Config.GitHub.SelfUpdate.Owner = AppInfo.GitHubOwner;
            config.Config.GitHub.SelfUpdate.Repo = AppInfo.GitHubRepo;
        }

        var mainVm = new MainViewModel(patchManager, scheduler, _log);
        var window = new MainWindow(mainVm, config, winget, _log);
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        var message = args.Exception.GetBaseException().Message;
        _log?.Error("UI: " + message);

        // Avoid flooding the user with dozens of dialogs (e.g. repeated binding/thread issues)
        if (Interlocked.Increment(ref _uiErrorCount) <= 1)
        {
            MessageBox.Show(
                message + "\n\nFurther errors will be written to the activity log only.",
                "Windows Patch Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        args.Handled = true;
    }

    private static void EnsureConsole()
    {
        if (!AttachConsole(AttachParentProcess))
            AllocConsole();

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
    }
}

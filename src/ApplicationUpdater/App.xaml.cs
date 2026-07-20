using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ApplicationUpdater.Cli;
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

        var config = new ConfigService();
        _log = new LogService();
        var winget = new WingetService(config, _log);
        var chocolatey = new ChocolateyService(_log);
        var detector = new ProgramDetectorService(config, winget, chocolatey, _log);
        var installer = new UpdateInstallerService(config, winget, chocolatey, _log);
        var patchManager = new PatchManagerService(config, detector, installer, _log);
        var scheduler = new SchedulerService(_log);

        if (CliHost.ShouldRunCli(e.Args))
        {
            EnsureConsole();
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var code = await CliHost.RunAsync(e.Args, patchManager, scheduler, _log).ConfigureAwait(true);
            Shutdown(code);
            return;
        }

        var mainVm = new MainViewModel(patchManager, scheduler, _log);
        var window = new MainWindow(mainVm, config);
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

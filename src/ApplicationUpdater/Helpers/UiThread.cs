using System.Windows;
using System.Windows.Threading;

namespace ApplicationUpdater.Helpers;

/// <summary>Marshal work onto the WPF UI thread safely.</summary>
internal static class UiThread
{
    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.InvokeAsync(action, DispatcherPriority.DataBind);
    }

    public static void Send(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action, DispatcherPriority.DataBind);
    }

    public static async Task RunAsync(Func<Task> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            await action().ConfigureAwait(true);
            return;
        }

        await dispatcher.InvokeAsync(action).Task.Unwrap().ConfigureAwait(true);
    }
}

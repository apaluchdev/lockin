using System.Windows;

namespace LockIn.App;

/// <summary>Helpers for getting back onto the UI thread from background services.</summary>
internal static class Ui
{
    /// <summary>Runs <paramref name="action"/> on the UI thread without blocking the caller.</summary>
    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}

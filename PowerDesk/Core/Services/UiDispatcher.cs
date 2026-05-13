using System;
using System.Windows;
using System.Windows.Threading;

namespace PowerDesk.Core.Services;

/// <summary>
/// Marshals work to the WPF UI thread. Services that mutate ObservableCollections or fire
/// PropertyChanged events from background threads (e.g. continuations of Task.Run-backed
/// awaits) should route through here so view models stay testable while the shell stays
/// thread-safe.
/// </summary>
public static class UiDispatcher
{
    private static Dispatcher Dispatcher =>
        System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    public static bool IsOnUiThread => Dispatcher.CheckAccess();

    public static void Invoke(Action action)
    {
        if (action is null) return;
        var d = Dispatcher;
        if (d.CheckAccess()) action();
        else d.Invoke(action);
    }

    public static void Post(Action action)
    {
        if (action is null) return;
        var d = Dispatcher;
        if (d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }
}

using System.Windows.Threading;
using Microsoft.Win32;

namespace Wisp.App;

// A monitor that is unplugged, turned off or changes resolution can leave a HUD window outside every
// display. Windows raises DisplaySettingsChanged for each step of such a change, so the windows are
// placed again once the sequence has settled.
internal sealed class DisplayChangeWatcher : IDisposable
{
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(750);
    private readonly Dispatcher _dispatcher;
    private readonly Action _replace;
    private readonly DispatcherTimer _settle;
    private bool _disposed;

    internal DisplayChangeWatcher(Dispatcher dispatcher, Action replace)
    {
        _dispatcher = dispatcher;
        _replace = replace;
        _settle = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = SettleDelay };
        _settle.Tick += OnSettled;
    }

    internal static DisplayChangeWatcher Start(Dispatcher dispatcher, Action replace)
    {
        var watcher = new DisplayChangeWatcher(dispatcher, replace);
        SystemEvents.DisplaySettingsChanged += watcher.OnDisplaySettingsChanged;
        return watcher;
    }

    internal bool Pending => _settle.IsEnabled;

    // Raised on the thread that owns the system events window; the dashboard thread owns the windows and the timer.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(DispatcherPriority.Normal, Notice);

    // Dashboard thread: each step of a display change restarts the wait.
    internal void Notice()
    {
        if (_disposed) return;
        _settle.Stop();
        _settle.Start();
    }

    private void OnSettled(object? sender, EventArgs e)
    {
        _settle.Stop();
        if (_disposed) return;
        try { _replace(); }
        catch (Exception error) { App.RecoveredExceptionSink?.Invoke(error, "display-change"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _settle.Stop();
    }
}

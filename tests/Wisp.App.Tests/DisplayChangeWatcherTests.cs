using System.Windows.Threading;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DisplayChangeWatcherTests
{
    [Fact]
    public void ABurstOfDisplayChangesPlacesTheWindowsOnceAfterItSettles()
    {
        var replaced = 0;
        var replacedBeforeTheLateStep = -1;
        bool pendingAfterNotice = false, pendingAfterRun = true;
        var failure = RunOnDispatcherThread(dispatcher =>
        {
            using var watcher = new DisplayChangeWatcher(dispatcher, () => replaced++);
            watcher.Notice();
            watcher.Notice();
            pendingAfterNotice = watcher.Pending;
            // One more step of the change arrives after the first placement; it is placed again, once.
            DispatcherTimer? lateStep = null;
            lateStep = new DispatcherTimer(DisplayChangeWatcher.SettleDelay * 1.5, DispatcherPriority.Normal, (_, _) =>
            {
                lateStep!.Stop();
                replacedBeforeTheLateStep = replaced;
                watcher.Notice();
            }, dispatcher);
            var stop = new DispatcherTimer(DisplayChangeWatcher.SettleDelay * 4, DispatcherPriority.Normal,
                (_, _) => dispatcher.InvokeShutdown(), dispatcher);
            Dispatcher.Run();
            stop.Stop();
            pendingAfterRun = watcher.Pending;
        });
        Assert.Null(failure);
        Assert.True(pendingAfterNotice);
        Assert.Equal(1, replacedBeforeTheLateStep);
        Assert.Equal(2, replaced);
        Assert.False(pendingAfterRun);
    }

    [Fact]
    public void ADisposedWatcherPlacesNothing()
    {
        var replaced = 0;
        var pendingAfterDispose = true;
        var failure = RunOnDispatcherThread(dispatcher =>
        {
            var watcher = new DisplayChangeWatcher(dispatcher, () => replaced++);
            watcher.Notice();
            watcher.Dispose();
            watcher.Notice();
            pendingAfterDispose = watcher.Pending;
            var stop = new DispatcherTimer(DisplayChangeWatcher.SettleDelay * 2, DispatcherPriority.Normal,
                (_, _) => dispatcher.InvokeShutdown(), dispatcher);
            Dispatcher.Run();
            stop.Stop();
        });
        Assert.Null(failure);
        Assert.False(pendingAfterDispose);
        Assert.Equal(0, replaced);
    }

    private static Exception? RunOnDispatcherThread(Action<Dispatcher> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(Dispatcher.CurrentDispatcher); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) return new TimeoutException("The dispatcher thread did not finish.");
        return failure;
    }
}

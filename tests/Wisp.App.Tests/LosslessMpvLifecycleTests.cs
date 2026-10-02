using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Threading;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class LosslessMpvLifecycleTests
{
    [Fact]
    public async Task TimedOutOwnerBlocksReuseUntilReleaseAndCannotPoisonNextOwner()
    {
        var first = new object(); var second = new object();
        var lease = await LosslessMpvRuntime.AcquireAsync(first, TestContext.Current.CancellationToken);
        try
        {
            LosslessMpvRuntime.SetStage(first, "stopping");
            LosslessMpvRuntime.CleanupDelayed(first);
            Assert.True(LosslessMpvRuntime.CleanupPending);
            await Assert.ThrowsAsync<InvalidOperationException>(() => LosslessMpvRuntime.AcquireAsync(second, TestContext.Current.CancellationToken));
        }
        finally { lease.Dispose(); }
        using var replacement = await LosslessMpvRuntime.AcquireAsync(second, TestContext.Current.CancellationToken);
        LosslessMpvRuntime.SetStage(second, "player-ready");
        LosslessMpvRuntime.CleanupDelayed(first);
        lease.Dispose();
        Assert.False(LosslessMpvRuntime.CleanupPending);
        Assert.Equal("player-ready", LosslessMpvRuntime.RuntimeStage);
    }

    [Fact]
    public void ClosingBeforeSurfaceExistsCancelsOpenWithoutLoadingDecoderOrPublishingReady()
    {
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var host = new LosslessVideoHost(new Border());
                    var player = new LosslessClipPlayer(host, 1);
                    var changes = 0;
                    player.Changed += (_, _) => changes++;
                    player.Open(new(Guid.NewGuid(), DateTimeOffset.UtcNow,
                        new(60, 1080, 60, 100, LosslessVideo: true),
                        new(1, 1920, 1080, 60, 0, 10_000_000, false, LosslessVideo: true)), "never-opened.mp4");
                    await Task.Delay(20, TestContext.Current.CancellationToken);
                    var close = player.CloseAsync();
                    Assert.Same(close, player.CloseAsync());
                    Assert.True(await close.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
                    player.SetPaused(false); player.Seek(1); player.RequestPoll();
                    Assert.False(host.Ready.IsCompleted);
                    Assert.False(player.Snapshot.Ready);
                    Assert.Null(player.Snapshot.Failure);
                    Assert.Equal(0, changes);
                    host.Dispose();
                }
                catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); finished.Set(); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

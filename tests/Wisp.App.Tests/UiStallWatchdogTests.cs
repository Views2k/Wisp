using System.Diagnostics;
using Wisp.App.DebugLogging;
using Xunit;

namespace Wisp.App.Tests;

public sealed class UiStallWatchdogTests
{
    private sealed class Clock
    {
        private long _ticks = Stopwatch.Frequency * 1000;
        internal long Now() => _ticks;
        internal void Advance(TimeSpan span) => _ticks += (long)(span.TotalSeconds * Stopwatch.Frequency);
    }

    [Fact]
    public void AStallIsRecordedWhenNoticedAndAgainWithItsLengthWhenTheDispatcherRunsAgain()
    {
        var clock = new Clock();
        var posted = new Queue<Action>();
        var recorded = new List<(HealthEventCode Code, int Milliseconds)>();
        using var watchdog = new UiStallWatchdog(posted.Enqueue, clock.Now, (code, ms) => recorded.Add((code, ms)));

        clock.Advance(TimeSpan.FromSeconds(1));
        watchdog.Check();
        Assert.Single(posted);
        posted.Dequeue()();
        Assert.Empty(recorded);
        Assert.False(watchdog.Stalled);

        // The dispatcher is blocked: the posted heartbeat does not run while the timer keeps checking.
        clock.Advance(TimeSpan.FromSeconds(1));
        watchdog.Check();
        Assert.Single(posted);
        clock.Advance(TimeSpan.FromSeconds(6));
        watchdog.Check();
        watchdog.Check();
        Assert.True(watchdog.Stalled);
        Assert.Equal(HealthEventCode.UiStalled, Assert.Single(recorded).Code);
        Assert.InRange(recorded[0].Milliseconds, 7000, 7100);
        Assert.Single(posted); // No second heartbeat is queued behind the pending one.

        clock.Advance(TimeSpan.FromSeconds(3));
        posted.Dequeue()();
        Assert.False(watchdog.Stalled);
        Assert.Equal(2, recorded.Count);
        Assert.Equal(HealthEventCode.UiStallEnded, recorded[1].Code);
        Assert.InRange(recorded[1].Milliseconds, 10000, 10100);

        clock.Advance(TimeSpan.FromSeconds(1));
        watchdog.Check();
        posted.Dequeue()();
        Assert.Equal(2, recorded.Count);
    }

    [Fact]
    public void AFailedPostReleasesTheHeartbeatSlot()
    {
        var clock = new Clock();
        var posts = 0;
        using var watchdog = new UiStallWatchdog(_ => { posts++; throw new InvalidOperationException(); }, clock.Now, (_, _) => { });
        watchdog.Check();
        watchdog.Check();
        Assert.Equal(2, posts);
    }
}

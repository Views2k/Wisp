using System.Collections.Concurrent;
using System.IO;
using Wisp.App.CrashDiagnostics;
using Xunit;

namespace Wisp.App.Tests;

public sealed class CrashExceptionMonitorTests
{
    [Fact]
    public void UiAndBackgroundFatalDuplicateIsSavedOnce()
    {
        var reports = new List<CrashReport>();
        var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var monitor = new CrashExceptionMonitor(new Sink(report => { reports.Add(report); return true; }), () => now);
        var failure = new IOException("PRIVATE_MESSAGE");
        monitor.UiException(failure);
        monitor.BackgroundException(failure, terminating: true);
        var report = Assert.Single(reports);
        Assert.True(report.IsTerminating); Assert.Equal(CrashOrigin.UiDispatcher, report.Origin); Assert.Equal(now, report.TimeUtc);
    }

    [Fact]
    public void BackgroundAndTaskEventsKeepTheirActualTerminationStatus()
    {
        var reports = new List<CrashReport>();
        var monitor = new CrashExceptionMonitor(new Sink(report => { reports.Add(report); return true; }));
        monitor.BackgroundException(new IOException(), terminating: true);
        monitor.UnobservedTaskException(new AggregateException(new InvalidOperationException("PRIVATE_MESSAGE")));
        Assert.Equal(2, reports.Count);
        Assert.Equal(CrashOrigin.BackgroundThread, reports[0].Origin); Assert.True(reports[0].IsTerminating);
        Assert.Equal(CrashOrigin.UnobservedTask, reports[1].Origin); Assert.False(reports[1].IsTerminating);
        Assert.Equal(2, reports[1].Exceptions.Length);
        Assert.DoesNotContain("PRIVATE_MESSAGE", reports[1].Format(), StringComparison.Ordinal);
    }

    [Fact]
    public void LoggerFailureCannotEscapeOrSuppressTheNextRecord()
    {
        var calls = 0;
        var monitor = new CrashExceptionMonitor(new Sink(_ => { calls++; throw new IOException("PRIVATE_STORAGE"); }));
        var error = new InvalidOperationException();
        monitor.UiException(error);
        monitor.BackgroundException(error, terminating: true);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ReentrantLoggerDoesNotRecurse()
    {
        var calls = 0;
        CrashExceptionMonitor? monitor = null;
        monitor = new CrashExceptionMonitor(new Sink(_ =>
        {
            calls++;
            monitor!.UiException(new IOException());
            return true;
        }));
        monitor.UiException(new InvalidOperationException());
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndependentFatalThreadIsNotSuppressedByABlockedNonfatalSave(bool uiFatal)
    {
        var reports = new ConcurrentQueue<CrashReport>();
        var nonfatalEntered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseNonfatal = new ManualResetEventSlim();
        var fatalThread = 0;
        var monitor = new CrashExceptionMonitor(new Sink(report =>
        {
            if (!report.IsTerminating)
            {
                nonfatalEntered.TrySetResult(Environment.CurrentManagedThreadId);
                if (!releaseNonfatal.Wait(TimeSpan.FromSeconds(15))) return false;
            }
            else fatalThread = Environment.CurrentManagedThreadId;
            reports.Enqueue(report);
            return true;
        }));
        var nonfatal = Task.Factory.StartNew(() => monitor.UnobservedTaskException(new AggregateException(new IOException())),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            var blockedThread = await nonfatalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var fatal = Task.Factory.StartNew(() =>
            {
                if (uiFatal) monitor.UiException(new InvalidOperationException());
                else monitor.BackgroundException(new InvalidOperationException(), terminating: true);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await fatal.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var saved = Assert.Single(reports);
            Assert.True(saved.IsTerminating);
            Assert.Equal(uiFatal ? CrashOrigin.UiDispatcher : CrashOrigin.BackgroundThread, saved.Origin);
            Assert.NotEqual(blockedThread, fatalThread);
            Assert.False(nonfatal.IsCompleted);
        }
        finally
        {
            releaseNonfatal.Set();
#pragma warning disable xUnit1051 // Join the released worker even if the test's token is already cancelled.
            await nonfatal.WaitAsync(TimeSpan.FromSeconds(5));
#pragma warning restore xUnit1051
        }
        Assert.Equal(2, reports.Count);
    }

    [Fact]
    public void NonExceptionBackgroundPayloadIsNeverCopied()
    {
        CrashReport? saved = null;
        var monitor = new CrashExceptionMonitor(new Sink(report => { saved = report; return true; }));
        monitor.BackgroundException("PRIVATE_PAYLOAD", terminating: true);
        Assert.NotNull(saved); Assert.Empty(saved.Exceptions);
        Assert.DoesNotContain("PRIVATE_PAYLOAD", saved.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnobservedTaskLoggingDoesNotChangeRuntimeObservationPolicy()
    {
        var monitor = new CrashExceptionMonitor(new Sink(_ => true));
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(new IOException()));
        Assert.False(args.Observed);
        monitor.UnobservedTaskException(args);
        Assert.False(args.Observed);
    }

    [Fact]
    public void ARecoveredFailureOfOneKindIsSavedOncePerMinute()
    {
        var reports = new List<CrashReport>();
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var monitor = new CrashExceptionMonitor(new Sink(report => { reports.Add(report); return true; }), () => now);
        monitor.RecoveredUiException(new IOException("first"), "File held by: a");
        monitor.RecoveredUiException(new IOException("second"), "File held by: a");
        now += TimeSpan.FromSeconds(59);
        monitor.RecoveredUiException(new IOException("third"), "File held by: a");
        Assert.Equal("File held by: a", Assert.Single(reports).Note);

        // Another operation recovering from the same exception type is a different kind of failure.
        monitor.RecoveredUiException(new IOException("other"), "display-change");
        Assert.Equal(2, reports.Count);
        Assert.Equal("display-change", reports[1].Note);

        now += TimeSpan.FromSeconds(1);
        monitor.RecoveredUiException(new IOException("fourth"), "File held by: a");
        Assert.Equal(3, reports.Count);
        Assert.Equal("File held by: a", reports[2].Note);
    }

    [Fact]
    public void TheRepeatWindowLeavesDistinctAndTerminatingFailuresAlone()
    {
        var reports = new List<CrashReport>();
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var monitor = new CrashExceptionMonitor(new Sink(report => { reports.Add(report); return true; }), () => now);
        monitor.RecoveredUiException(new IOException());
        monitor.RecoveredUiException(new InvalidOperationException());
        monitor.RecoveredUiException(new AggregateException(new IOException()));
        monitor.UnobservedTaskException(new IOException());
        monitor.UiException(new IOException());
        monitor.BackgroundException(new IOException(), terminating: true);
        Assert.Equal(6, reports.Count);
        Assert.Equal([false, false, false, false, true, true], reports.Select(report => report.IsTerminating));
    }

    [Fact]
    public void AReportTheStoreRefusedDoesNotStartTheRepeatWindow()
    {
        var saved = 0;
        var accept = false;
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var monitor = new CrashExceptionMonitor(new Sink(_ => accept && ++saved > 0), () => now);
        monitor.RecoveredUiException(new IOException());
        accept = true;
        monitor.RecoveredUiException(new IOException());
        monitor.RecoveredUiException(new IOException());
        Assert.Equal(1, saved);
    }

    [Fact]
    public void AClockThatRanBackwardsDoesNotSuppressReports()
    {
        var reports = 0;
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var monitor = new CrashExceptionMonitor(new Sink(_ => ++reports > 0), () => now);
        monitor.RecoveredUiException(new IOException());
        now -= TimeSpan.FromHours(1);
        monitor.RecoveredUiException(new IOException());
        Assert.Equal(2, reports);
    }

    [Fact]
    public void FailedOptionalContextStillSavesTheException()
    {
        CrashReport? saved = null;
        var monitor = new CrashExceptionMonitor(new Sink(report => { saved = report; return true; }),
            context: () => throw new InvalidOperationException());
        monitor.UiException(new IOException());
        Assert.NotNull(saved); Assert.Null(saved.Context); Assert.Single(saved.Exceptions);
    }

    private sealed class Sink(Func<CrashReport, bool> save) : ICrashReportSink
    {
        public bool TrySave(CrashReport report) => save(report);
    }
}

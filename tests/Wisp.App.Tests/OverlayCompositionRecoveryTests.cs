using System.Runtime.InteropServices;
using System.Windows.Threading;
using Wisp.App.DebugLogging;
using Wisp.App.Supplementary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class OverlayCompositionRecoveryTests
{
    private const int CompositionChangedMessage = 0x031E;
    private const int DestroyMessage = 0x0082;
    private const int MessageLost = unchecked((int)0xD0000701);
    private static readonly IntPtr Handle = new(0x1234);

    [Fact]
    public void ThrowingCompositionCallInTheMessagePathIsReportedOnceAndRetried()
    {
        var health = new HealthContextRecorder();
        var retries = new List<(TimeSpan Delay, Action Run)>();
        var calls = 0;
        var available = false;
        var recovery = new OverlayCompositionRecovery(_ =>
        {
            calls++;
            return available ? 0 : throw new COMException("DWM restarting", MessageLost);
        }, health, (delay, run) => retries.Add((delay, run)));

        Assert.False(Send(recovery, CompositionChangedMessage));
        Assert.False(Send(recovery, CompositionChangedMessage));
        Assert.Equal(2, calls);
        Assert.Equal(1, recovery.PendingCount);
        Assert.All(retries, retry => Assert.Equal(OverlayCompositionRecovery.DefaultFirstRetryDelay, retry.Delay));

        // The second message superseded the first retry.
        retries[0].Run();
        Assert.Equal(2, calls);

        available = true;
        retries[1].Run();
        Assert.Equal(3, calls);
        Assert.Equal(0, recovery.PendingCount);
        Assert.Equal(2, retries.Count);

        var snapshot = health.CaptureForReport();
        var failure = Assert.Single(snapshot.Breadcrumbs);
        Assert.Equal(HealthEventCode.RendererFailed, failure.Code);
        Assert.Equal(HealthFailureStage.RendererHost, failure.FailureStage);
        Assert.Equal(MessageLost, failure.ErrorCode);
        var incident = SupplementaryIncidentCapture.Breadcrumb("renderer", failure, snapshot);
        Assert.True(SupplementaryIncidentSchema.Valid(incident));
        Assert.Equal("native-failure", incident.Reason);
        Assert.Equal("renderer-host", incident.FailureStage);
        Assert.Equal(unchecked((uint)MessageLost), incident.Code);
    }

    [Fact]
    public void RetriesAreCappedAndTheNextMessageStartsAnotherRound()
    {
        var health = new HealthContextRecorder();
        var retries = new List<(TimeSpan Delay, Action Run)>();
        var calls = 0;
        var result = MessageLost;
        var recovery = new OverlayCompositionRecovery(_ =>
        {
            calls++;
            return result;
        }, health, (delay, run) => retries.Add((delay, run)));

        Send(recovery, CompositionChangedMessage);
        for (var index = 0; index < retries.Count; index++) retries[index].Run();
        Assert.Equal(OverlayCompositionRecovery.MaximumRetries, retries.Count);
        Assert.Equal(OverlayCompositionRecovery.MaximumRetries + 1, calls);
        Assert.Equal(
            Enumerable.Range(0, OverlayCompositionRecovery.MaximumRetries)
                .Select(index => OverlayCompositionRecovery.DefaultFirstRetryDelay * (1 << index)),
            retries.Select(retry => retry.Delay));

        Send(recovery, CompositionChangedMessage);
        Assert.Equal(OverlayCompositionRecovery.MaximumRetries + 1, retries.Count);
        Assert.Equal(OverlayCompositionRecovery.DefaultFirstRetryDelay, retries[^1].Delay);
        Assert.Single(health.CaptureForReport().Breadcrumbs);

        result = 0;
        retries[^1].Run();
        Assert.Equal(0, recovery.PendingCount);

        // A later failure is a new episode and is reported again.
        result = MessageLost;
        Send(recovery, CompositionChangedMessage);
        Assert.Equal(2, health.CaptureForReport().Breadcrumbs.Length);
    }

    [Fact]
    public void FailingOverlayWindowsShareOneReportAndAClosedWindowIsNotRetried()
    {
        var health = new HealthContextRecorder();
        var retries = new List<Action>();
        var other = new IntPtr(0x5678);
        var applied = new List<IntPtr>();
        var available = false;
        var recovery = new OverlayCompositionRecovery(handle =>
        {
            applied.Add(handle);
            return available ? 0 : MessageLost;
        }, health, (_, run) => retries.Add(run));

        Send(recovery, CompositionChangedMessage);
        OverlayPresentation.TryHandleWindowMessage(other, CompositionChangedMessage, IntPtr.Zero, IntPtr.Zero, recovery);
        Assert.Equal(2, recovery.PendingCount);
        Assert.Single(health.CaptureForReport().Breadcrumbs);

        Send(recovery, DestroyMessage);
        Assert.Equal(1, recovery.PendingCount);
        available = true;
        applied.Clear();
        foreach (var retry in retries) retry();
        Assert.Equal(new[] { other }, applied);
        Assert.Equal(0, recovery.PendingCount);
        Assert.Single(health.CaptureForReport().Breadcrumbs);
    }

    [Fact]
    public void RetryFromAnEarlierEpisodeDoesNotDriveALaterOne()
    {
        var retries = new List<(TimeSpan Delay, Action Run)>();
        var calls = 0;
        var result = MessageLost;
        var recovery = new OverlayCompositionRecovery(_ =>
        {
            calls++;
            return result;
        }, new HealthContextRecorder(), (delay, run) => retries.Add((delay, run)));

        Send(recovery, CompositionChangedMessage);
        result = 0;
        Send(recovery, CompositionChangedMessage);
        result = MessageLost;
        Send(recovery, CompositionChangedMessage);
        Assert.Equal(3, calls);
        Assert.Equal(2, retries.Count);

        retries[0].Run();
        Assert.Equal(3, calls);
        Assert.Equal(2, retries.Count);

        retries[1].Run();
        Assert.Equal(4, calls);
        Assert.Equal(OverlayCompositionRecovery.DefaultFirstRetryDelay * 2, retries[^1].Delay);
    }

    [Fact]
    public void RetryForADestroyedWindowDoesNotDriveAWindowReusingItsHandle()
    {
        var retries = new List<Action>();
        var calls = 0;
        var recovery = new OverlayCompositionRecovery(_ =>
        {
            calls++;
            return MessageLost;
        }, new HealthContextRecorder(), (_, run) => retries.Add(run));

        Send(recovery, CompositionChangedMessage);
        Send(recovery, DestroyMessage);
        Assert.Equal(0, recovery.PendingCount);
        Send(recovery, CompositionChangedMessage);
        Assert.Equal(2, calls);
        Assert.Equal(2, retries.Count);

        retries[0]();
        Assert.Equal(2, calls);
        Assert.Equal(2, retries.Count);

        retries[1]();
        Assert.Equal(3, calls);
        Assert.Equal(3, retries.Count);
    }

    [Fact]
    public void DispatcherTimerRetryRestoresCompositionWithoutAnotherMessage()
    {
        var health = new HealthContextRecorder();
        var calls = 0;
        Exception? escaped = null;
        OverlayCompositionRecovery? recovery = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.UnhandledException += (_, eventArgs) =>
            {
                escaped = eventArgs.Exception;
                eventArgs.Handled = true;
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            };
            var local = new OverlayCompositionRecovery(_ =>
            {
                if (++calls == 1) throw new COMException("DWM restarting", MessageLost);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                return 0;
            }, health, firstRetryDelay: TimeSpan.FromMilliseconds(10));
            recovery = local;
            dispatcher.BeginInvoke(new Action(() => Send(local, CompositionChangedMessage)));
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(escaped);
        Assert.Equal(2, calls);
        Assert.Equal(0, recovery!.PendingCount);
        Assert.Single(health.CaptureForReport().Breadcrumbs);
    }

    private static bool Send(OverlayCompositionRecovery recovery, int message) =>
        OverlayPresentation.TryHandleWindowMessage(Handle, message, IntPtr.Zero, IntPtr.Zero, recovery);
}

using System.Diagnostics;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeNeedleDeliveryTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(72)]
    public void AcceptedHistoryContinuesAcrossUiAndRendererStall(int stallMilliseconds)
    {
        var source = new HistorySource();
        var direct = Playback(source);
        var uiOnly = new AnalogHudPlayback();
        AnalogHudSample before = default;
        for (var ms = 0; ms <= 200; ms += 4)
        {
            source.Publish(ms);
            var frame = source.Frame(ms);
            direct.ObserveQueued(frame, Timestamp(ms), Timestamp(ms));
            uiOnly.ObserveQueued(frame, Timestamp(ms), Timestamp(ms));
            direct.RefreshNativeHistory(Timestamp(ms));
            before = direct.Sample(Timestamp(ms));
            uiOnly.Sample(Timestamp(ms));
        }

        var afterWait = 200 + stallMilliseconds;
        for (var ms = 204; ms <= afterWait; ms += 4)
            source.Publish(ms, gameTimestamp: 1_200); // UI telemetry itself has stopped.
        Assert.False(direct.RefreshNativeHistory(Timestamp(afterWait)));
        var actual = direct.Sample(Timestamp(afterWait));
        var previousPath = uiOnly.Sample(Timestamp(afterWait));
        // Preserve the existing gradual startup phase correction. The feed
        // changes available history, not the playback clock's correction law.
        var expectedDelay = 20 - (20 - before.PlaybackDelayMilliseconds) * Math.Exp(-stallMilliseconds / 100d);

        Assert.True(actual.Native);
        Assert.Equal(20, actual.PlaybackTargetDelayMilliseconds, 6);
        Assert.Equal(expectedDelay, actual.PlaybackDelayMilliseconds, 6);
        Assert.Equal(Angle(afterWait - expectedDelay), actual.Angle, 6);
        Assert.Equal(Blur(afterWait - expectedDelay), actual.Blur, 6);
        Assert.Equal(before.ReseedCount, actual.ReseedCount);
        Assert.Equal(before.StarvationReseedCount, actual.StarvationReseedCount);
        Assert.False(actual.PlaybackAtNewest);
        Assert.Equal(Timestamp(afterWait), actual.Frame.NativeGaugeObservedTimestamp);
        Assert.Equal(Timestamp(200), actual.Frame.ReceivedTimestamp);
        Assert.Equal(source.Frame(200).EngineRpm, actual.Frame.EngineRpm);
        Assert.True(previousPath.Native);
        Assert.True(previousPath.PlaybackAtNewest);
        Assert.Equal(stallMilliseconds, previousPath.PlaybackDelayMilliseconds, 6);
        Assert.Equal(Angle(200), previousPath.Angle, 6);
    }

    [Fact]
    public void StaleUiPairAndInvalidationCannotReplaceAcceptedHistoryOrItsGameClock()
    {
        var source = new HistorySource();
        var playback = Playback(source);
        playback.ObserveQueued(source.Frame(0), Timestamp(0), Timestamp(0));
        source.Publish(0, gameTimestamp: 1_000);
        playback.RefreshNativeHistory(Timestamp(0));
        var before = playback.Sample(Timestamp(0));

        playback.ObserveQueued(source.Frame(4) with
        {
            NativeNeedleAngleDegrees = 999,
            NativeNeedleBlurAmount = .6,
            NativeGaugeSourceInvalidated = true,
            GameTimestampMilliseconds = 9_000
        }, Timestamp(4), Timestamp(4));
        source.Publish(4, gameTimestamp: 1_000);
        playback.RefreshNativeHistory(Timestamp(4));
        source.Publish(8, gameTimestamp: 1_000);
        playback.RefreshNativeHistory(Timestamp(8));
        var actual = playback.Sample(Timestamp(8));

        Assert.True(actual.Native);
        Assert.Equal(before.ReseedCount, actual.ReseedCount);
        Assert.Equal(Timestamp(8), actual.Frame.NativeGaugeObservedTimestamp);
        Assert.Equal(Angle(8), actual.Frame.NativeNeedleAngleDegrees);
        Assert.False(actual.Frame.NativeGaugeSourceInvalidated);
        Assert.InRange(actual.Angle, Angle(0), Angle(8));
    }

    [Fact]
    public void NewSameCarIdentityCannotReuseOldUiIdentityOrOldNativePixels()
    {
        var source = new HistorySource();
        var playback = Playback(source);
        var oldFrame = source.Frame(0);
        playback.ObserveQueued(oldFrame, Timestamp(0), Timestamp(0));
        source.Publish(0);
        playback.RefreshNativeHistory(Timestamp(0));
        Assert.True(playback.Sample(Timestamp(0)).Native);

        source.History.Reset(3766);
        source.History.Reset(314);
        source.Publish(4);
        Assert.True(playback.RefreshNativeHistory(Timestamp(4)));
        Assert.False(playback.Sample(Timestamp(4)).Native);
        Assert.True(playback.CurrentFrame.NativeGaugeSourceInvalidated);
        Assert.Equal(0, playback.CurrentFrame.NativeGaugeObservedTimestamp);

        playback.ObserveQueued(source.Frame(4), Timestamp(4), Timestamp(5));
        Assert.True(playback.RefreshNativeHistory(Timestamp(5)));
        var recovered = playback.Sample(Timestamp(5));
        Assert.True(recovered.Native);
        Assert.Equal(Angle(4), recovered.Angle);
        Assert.NotEqual(oldFrame.NativeSourceIdentity, recovered.Frame.NativeSourceIdentity);
    }

    [Fact]
    public void TemporaryUnavailablePublicationRetainsOriginalExpiryThenFallsBack()
    {
        var source = new HistorySource();
        var playback = Playback(source);
        playback.ObserveQueued(source.Frame(0), Timestamp(0), Timestamp(0));
        source.Publish(0);
        playback.RefreshNativeHistory(Timestamp(0));
        source.PublishUnavailable(NativeAssistProviderStatus.TelemetryMismatch);
        Assert.False(playback.RefreshNativeHistory(Timestamp(20)));
        var held = playback.Sample(Timestamp(20));
        Assert.True(held.Native);
        Assert.Equal(Angle(0), held.Angle);
        Assert.Equal(0, held.Frame.NativeGaugeObservedTimestamp);
        Assert.False(held.Frame.NativeGaugeSourceInvalidated);
        Assert.True(playback.Sample(Timestamp(75)).Native);
        Assert.False(playback.Sample(Timestamp(76)).Native);
        Assert.Equal(20, playback.Sample(Timestamp(76)).PlaybackTargetDelayMilliseconds);
    }

    [Fact]
    public void InvalidationWithinBatchDiscardsPendingPixelsEvenIfNativeHasRecovered()
    {
        var source = new HistorySource();
        var playback = Playback(source);
        playback.ObserveQueued(source.Frame(0), Timestamp(0), Timestamp(0));
        source.Publish(0);
        playback.RefreshNativeHistory(Timestamp(0));
        var before = playback.Sample(Timestamp(0));
        source.PublishUnavailable(NativeAssistProviderStatus.Unavailable);
        source.Publish(4);

        Assert.True(playback.RefreshNativeHistory(Timestamp(4)));
        var recovered = playback.Sample(Timestamp(4));
        Assert.True(recovered.Native);
        Assert.Equal(before.ReseedCount + 1, recovered.ReseedCount);
        Assert.Equal(Angle(4), recovered.Angle);
        Assert.False(playback.RefreshNativeHistory(Timestamp(5)));
    }

    [Fact]
    public void MatchingEmptySourceDoesNotClearInitialInvalidation()
    {
        var source = new HistorySource();
        var playback = Playback(source);
        playback.ObserveQueued(source.Frame(0) with { NativeGaugeSourceInvalidated = true }, Timestamp(0), Timestamp(0));
        playback.RefreshNativeHistory(Timestamp(0));
        Assert.False(playback.Sample(Timestamp(0)).Native);
        Assert.True(playback.CurrentFrame.NativeGaugeSourceInvalidated);
    }

    [Fact]
    public void IndependentConsumersAndOverflowNeverConsumeOrBlendAnotherReadersHistory()
    {
        var source = new HistorySource();
        var first = Playback(source);
        var paused = Playback(source);
        foreach (var playback in new[] { first, paused })
            playback.ObserveQueued(source.Frame(0), Timestamp(0), Timestamp(0));
        source.Publish(0);
        first.RefreshNativeHistory(Timestamp(0));
        paused.RefreshNativeHistory(Timestamp(0));
        var pausedBefore = paused.Sample(Timestamp(0));
        for (var ms = 1; ms <= 100; ms++)
        {
            source.Publish(ms);
            first.RefreshNativeHistory(Timestamp(ms));
            first.Sample(Timestamp(ms));
        }

        Assert.True(paused.RefreshNativeHistory(Timestamp(100)));
        var resumed = paused.Sample(Timestamp(100));
        var continuous = first.Sample(Timestamp(100));
        Assert.True(resumed.Native);
        Assert.Equal(pausedBefore.ReseedCount + 1, resumed.ReseedCount);
        Assert.Equal(Angle(80), resumed.Angle, 6);
        Assert.Equal(Blur(80), resumed.Blur, 6);
        Assert.Equal(1, continuous.ReseedCount); // The first cursor was never overrun.
        Assert.Equal(20, continuous.PlaybackTargetDelayMilliseconds, 6);
        Assert.InRange(continuous.PlaybackDelayMilliseconds, 19, 20);
        Assert.Equal(20, resumed.PlaybackDelayMilliseconds, 6);
        Assert.False(first.RefreshNativeHistory(Timestamp(100)));
        paused.Reset();
        Assert.Equal(default, paused.Sample(Timestamp(100)));
        paused.ObserveQueued(source.Frame(100), Timestamp(100), Timestamp(100));
        Assert.True(paused.RefreshNativeHistory(Timestamp(100)));
        Assert.True(paused.Sample(Timestamp(100)).Native);
    }

    [Fact]
    public void PublicationDuringCopyIsNotDiscardedAsFuture()
    {
        var source = new HistorySource();
        var playback = Playback(source);
        playback.ObserveQueued(source.Frame(0), Timestamp(0), Timestamp(0));
        source.Publish(0);
        playback.RefreshNativeHistory(Timestamp(0));
        source.DuringCopy = () => source.Publish(12);
        source.CopiedTimestamp = Timestamp(13);

        playback.RefreshNativeHistory(Timestamp(10));
        var actual = playback.Sample(Timestamp(13));
        Assert.True(actual.Native);
        Assert.Equal(Timestamp(12), actual.Frame.NativeGaugeObservedTimestamp);
        Assert.Equal(2, actual.BufferedSamples);
        Assert.Equal(1, actual.ReseedCount);
        Assert.True(playback.HasNativeNeedle(Timestamp(87)));
        Assert.False(playback.HasNativeNeedle(Timestamp(88)));
    }

    [Fact]
    public void LegacyPendingFrameUsesAuthoritativeNativeStateAndChecksExpiryAfterDraw()
    {
        var source = new HistorySource();
        var playback = Playback(source);
        var staleUi = source.Frame(0) with { NativeGaugeSourceInvalidated = true };
        playback.ObserveQueued(staleUi, Timestamp(0), Timestamp(0));
        source.Publish(0);
        playback.RefreshNativeHistory(Timestamp(0));
        var sample = playback.Sample(Timestamp(0));
        var presentation = new AnalogHudPresentation(800, 600, 0, 0, 1, 0, 0, 1, 1,
            true, false, default, null);
        var pending = new AnalogHudPendingFrame(sample, presentation, Timestamp(0), 1);

        Assert.True(pending.CanReuse(presentation, playback.CurrentFrame, playback.HasNativeNeedle(Timestamp(1))));
        Assert.False(pending.CanReuse(presentation, staleUi, playback.HasNativeNeedle(Timestamp(1))));
        Assert.False(playback.RefreshNativeHistory(Timestamp(76))); // no new publication
        Assert.False(pending.CanReuse(presentation, playback.CurrentFrame, playback.HasNativeNeedle(Timestamp(76))));
    }

    [Fact]
    public void NativeTrialFloorAdaptsToRealGapsWhileRpmDefaultStaysForty()
    {
        var native = new NativeNeedlePlayback();
        for (var ms = 0; ms <= 40; ms += 4)
            native.ObserveQueued(314, (uint)(1_000 + ms), Angle(ms), Blur(ms), Timestamp(ms), Timestamp(ms), false);
        Assert.Equal(20, native.PlaybackTargetDelayMilliseconds);
        native.ObserveQueued(314, 1_080, Angle(80), Blur(80), Timestamp(80), Timestamp(80), false);
        Assert.Equal(50, native.PlaybackTargetDelayMilliseconds);
        native.ObserveQueued(314, 1_150, Angle(150), Blur(150), Timestamp(150), Timestamp(150), false);
        Assert.Equal(75, native.PlaybackTargetDelayMilliseconds);
        for (var ms = 154; ms <= 182; ms += 4)
            native.ObserveQueued(314, (uint)(1_000 + ms), Angle(ms), Blur(ms), Timestamp(ms), Timestamp(ms), false);
        Assert.Equal(20, native.PlaybackTargetDelayMilliseconds);
        Assert.Equal(40, new NativeTachometerInterpolator().PlaybackTargetDelayMilliseconds);
        Assert.Equal(75, NativeNeedlePlayback.NativeSampleFreshnessMilliseconds);
    }

    [Fact]
    public void AcceptedPublicationAndHistoryDeliveryDoNotAllocatePerRefresh()
    {
        var source = new HistorySource();
        var playback = Playback(source);
        playback.ObserveQueued(source.Frame(0), Timestamp(0), Timestamp(0));
        var snapshot = source.Snapshot(0);
        source.History.Publish(snapshot, 1_000);
        playback.RefreshNativeHistory(Timestamp(0));
        playback.Sample(Timestamp(0));
        var snapshots = new NativeHudSnapshot[1_024];
        for (var index = 0; index < snapshots.Length; index++)
            snapshots[index] = source.Snapshot(index + 1) with
            {
                NativeNeedleAngleDegrees = 100 + index % 200 * .5,
                NativeNeedleBlurAmount = -.2 + index % 200 * .001
            };
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < snapshots.Length; index++)
        {
            var now = Timestamp(index + 1);
            source.History.Publish(snapshots[index], (uint)(1_001 + index));
            playback.RefreshNativeHistory(now);
            playback.Sample(now);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(Timestamp(snapshots.Length), playback.CurrentFrame.NativeGaugeObservedTimestamp);
        Assert.Equal(1, playback.Sample(Timestamp(snapshots.Length)).ReseedCount);
    }

    private static AnalogHudPlayback Playback(HistorySource source)
    {
        var playback = new AnalogHudPlayback();
        playback.SetNativeSource(source);
        return playback;
    }

    private static double Angle(double milliseconds) => 100 + milliseconds * .5;
    private static double Blur(double milliseconds) => -.2 + milliseconds * .001;
    private static long Timestamp(int milliseconds) =>
        (long)Math.Round((1_000 + milliseconds) * Stopwatch.Frequency / 1_000d);

    private sealed class HistorySource : INativeNeedleHistorySource
    {
        internal NativeNeedleHistory History { get; } = new();
        internal Action? DuringCopy;
        internal long CopiedTimestamp;
        internal HistorySource() => History.Reset(314);
        internal NativeHudSnapshot Snapshot(int milliseconds) => NativeHudSnapshot.Unavailable(
            NativeAssistProviderStatus.Ready, carOrdinal: 314, nativeSourceIdentity: History.SourceIdentity) with
        {
            Available = true,
            NativeNeedleAngleDegrees = Angle(milliseconds),
            NativeNeedleBlurAmount = Blur(milliseconds),
            NativeGaugeObservedTimestamp = Timestamp(milliseconds)
        };
        internal void Publish(int milliseconds, uint? gameTimestamp = null) =>
            History.Publish(Snapshot(milliseconds), gameTimestamp ?? (uint)(1_000 + milliseconds));
        internal void PublishUnavailable(NativeAssistProviderStatus status) =>
            History.Publish(NativeHudSnapshot.Unavailable(status, carOrdinal: 314,
                nativeSourceIdentity: History.SourceIdentity), 1_000);
        internal NativeGaugeFrame Frame(int milliseconds) => new(true, 100, 4_000, 8_000,
            TransmissionGear.First, SpeedUnit.MilesPerHour, ExactRedlineResult.Exact(7_000 * 2 * Math.PI / 60),
            CarOrdinal: 314, GameTimestampMilliseconds: (uint)(1_000 + milliseconds),
            ReceivedTimestamp: Timestamp(milliseconds), NativeNeedleAngleDegrees: Angle(milliseconds),
            NativeNeedleBlurAmount: Blur(milliseconds), NativeGaugeObservedTimestamp: Timestamp(milliseconds),
            NativeSourceIdentity: History.SourceIdentity);
        public NativeNeedleHistoryRead CopySince(int carOrdinal, long sourceIdentity,
            ref NativeNeedleHistoryCursor cursor, Span<NativeNeedleObservation> destination)
        {
            var duringCopy = DuringCopy;
            DuringCopy = null;
            duringCopy?.Invoke();
            return History.CopySince(carOrdinal, sourceIdentity, ref cursor, destination) with
            {
                CopiedTimestamp = CopiedTimestamp
            };
        }
    }
}

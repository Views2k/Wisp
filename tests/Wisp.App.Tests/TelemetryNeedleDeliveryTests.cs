using System.Diagnostics;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class TelemetryNeedleDeliveryTests
{
    [Fact]
    public void RpmHistoryAdvancesWhileUiPublicationStopsAndAnOldUiFrameCannotRewindIt()
    {
        var source = new Source();
        var direct = Playback(source);
        var uiOnly = new AnalogHudPlayback();
        for (var ms = 0; ms <= 100; ms += 10)
        {
            source.Rpm.Publish(State(ms));
            direct.ObserveQueued(Frame(source, ms), Timestamp(ms), Timestamp(ms));
            uiOnly.ObserveQueued(Frame(source, ms), Timestamp(ms), Timestamp(ms));
            direct.RefreshNativeHistory(Timestamp(ms));
            direct.Sample(Timestamp(ms));
            uiOnly.Sample(Timestamp(ms));
        }
        // No further UI snapshots: the existing scale remains valid and only
        // accepted receiver samples continue for longer than needle freshness.
        for (var ms = 110; ms <= 220; ms += 10)
        {
            source.Rpm.Publish(State(ms));
            direct.RefreshNativeHistory(Timestamp(ms));
            direct.Sample(Timestamp(ms));
        }
        var actual = direct.Sample(Timestamp(220));
        var stopped = uiOnly.Sample(Timestamp(220));
        Assert.False(actual.Native);
        Assert.True(actual.NeedleVisible);
        Assert.True(actual.AppliedRpm > stopped.AppliedRpm);
        Assert.False(actual.PlaybackAtNewest);
        Assert.Equal(20, actual.PlaybackTargetDelayMilliseconds);
        Assert.Equal(State(220).EngineRpm, actual.Frame.EngineRpm);
        Assert.Equal(Timestamp(220), actual.Frame.ReceivedTimestamp);
        Assert.True(direct.TryCopyCompositorCurve(Timestamp(220), new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints], out _));

        direct.ObserveQueued(Frame(source, 100), Timestamp(221), Timestamp(221));
        direct.RefreshNativeHistory(Timestamp(221));
        var rebound = direct.Sample(Timestamp(221));
        Assert.Equal(actual.ReseedCount, rebound.ReseedCount);
        Assert.Equal(actual.Frame.EngineRpm, rebound.Frame.EngineRpm);
        Assert.Equal(actual.Frame.ReceivedTimestamp, rebound.Frame.ReceivedTimestamp);
        Assert.True(rebound.AppliedRpm >= actual.AppliedRpm);
    }

    [Fact]
    public void OptionalSourceWithoutPublicationsPreservesFixtureAndPreviewInput()
    {
        var source = new Source();
        var direct = Playback(source);
        var previous = new AnalogHudPlayback();
        for (var ms = 0; ms <= 100; ms += 10)
        {
            direct.ObserveQueued(Frame(source, ms), Timestamp(ms), Timestamp(ms));
            previous.ObserveQueued(Frame(source, ms), Timestamp(ms), Timestamp(ms));
            direct.RefreshNativeHistory(Timestamp(ms));
            Assert.Equal(previous.Sample(Timestamp(ms)).AppliedRpm, direct.Sample(Timestamp(ms)).AppliedRpm);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmedRaceOrSessionResetCannotResurrectAnOldSameCarUiSnapshot(bool explicitReset)
    {
        var source = new Source();
        var playback = Playback(source);
        source.Rpm.Publish(State(0));
        playback.ObserveQueued(Frame(source, 0), Timestamp(0), Timestamp(0));
        playback.RefreshNativeHistory(Timestamp(0));
        Assert.True(playback.Sample(Timestamp(0)).NeedleVisible);
        if (explicitReset) source.Rpm.Reset(Timestamp(10));
        else
        {
            source.Rpm.Publish(State(10) with { IsRaceOn = false });
            source.Rpm.Reset(Timestamp(10), onlyIfActive: true);
        }
        Assert.True(playback.RefreshNativeHistory(Timestamp(10)));
        Assert.False(playback.RefreshNativeHistory(Timestamp(11))); // Same invalid epoch is not another reset.
        playback.ObserveQueued(Frame(source, 0), Timestamp(12), Timestamp(12));
        playback.RefreshNativeHistory(Timestamp(12));
        Assert.False(playback.Sample(Timestamp(12)).NeedleVisible);

        source.Rpm.Publish(State(20));
        playback.RefreshNativeHistory(Timestamp(20));
        Assert.False(playback.Sample(Timestamp(20)).NeedleVisible);
        playback.ObserveQueued(Frame(source, 20), Timestamp(21), Timestamp(21));
        playback.RefreshNativeHistory(Timestamp(21));
        Assert.True(playback.Sample(Timestamp(21)).NeedleVisible);
        Assert.Equal(State(20).EngineRpm, playback.Sample(Timestamp(21)).AppliedRpm);
    }

    [Fact]
    public void BriefRaceOffPulsePreservesExistingNeedleAndDoesNotRefreshItsInput()
    {
        var source = new Source();
        var playback = Playback(source);
        source.Rpm.Publish(State(0));
        playback.ObserveQueued(Frame(source, 0), Timestamp(0), Timestamp(0));
        playback.RefreshNativeHistory(Timestamp(0));
        var before = playback.Sample(Timestamp(0));
        source.Rpm.Publish(State(10) with { IsRaceOn = false, EngineRpm = 0 });
        Assert.False(playback.RefreshNativeHistory(Timestamp(10)));
        var held = playback.Sample(Timestamp(10));
        Assert.True(held.NeedleVisible);
        Assert.Equal(before.Angle, held.Angle);
        Assert.Equal(before.Frame.ReceivedTimestamp, held.Frame.ReceivedTimestamp);
        Assert.False(playback.TryCopyCompositorCurve(Timestamp(76), new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints], out _));
        source.Rpm.Publish(State(20));
        Assert.False(playback.RefreshNativeHistory(Timestamp(20)));
        Assert.Equal(Timestamp(20), playback.CurrentFrame.ReceivedTimestamp);
    }

    [Fact]
    public void SameCarScaleChangeWaitsForUpdatedUiMetadata()
    {
        var source = new Source();
        var playback = Playback(source);
        source.Rpm.Publish(State(0));
        playback.ObserveQueued(Frame(source, 0), Timestamp(0), Timestamp(0));
        playback.RefreshNativeHistory(Timestamp(0));
        source.Rpm.Publish(State(10) with { EngineMaximumRpm = 9_000 });
        playback.RefreshNativeHistory(Timestamp(10));
        Assert.False(playback.Sample(Timestamp(10)).NeedleVisible);
        playback.ObserveQueued(Frame(source, 10) with { TachometerMaximumRpm = 9_000 }, Timestamp(11), Timestamp(11));
        playback.RefreshNativeHistory(Timestamp(11));
        var resumed = playback.Sample(Timestamp(11));
        Assert.True(resumed.NeedleVisible);
        Assert.Equal(9_000, resumed.Frame.TachometerMaximumRpm);
        Assert.Equal(State(10).EngineRpm, resumed.AppliedRpm);
    }

    [Fact]
    public void CarChangeWaitsForMatchingUiScaleAndStaleHistoryCannotAuthorMotion()
    {
        var source = new Source();
        var playback = Playback(source);
        source.Rpm.Publish(State(0));
        playback.ObserveQueued(Frame(source, 0), Timestamp(0), Timestamp(0));
        playback.RefreshNativeHistory(Timestamp(0));
        source.Rpm.Publish(State(10) with { CarOrdinal = 500 });
        playback.RefreshNativeHistory(Timestamp(10));
        Assert.False(playback.Sample(Timestamp(10)).NeedleVisible);
        playback.ObserveQueued(Frame(source, 10) with { CarOrdinal = 500 }, Timestamp(11), Timestamp(11));
        playback.RefreshNativeHistory(Timestamp(11));
        Assert.True(playback.Sample(Timestamp(11)).NeedleVisible);
        Assert.Equal(500, playback.Sample(Timestamp(11)).Frame.CarOrdinal);
        Assert.False(playback.TryCopyCompositorCurve(Timestamp(86), new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints], out _));
    }

    [Fact]
    public void DirectRpmDeliveryPreservesValidatedNativeNeedlePriority()
    {
        var source = new Source();
        var playback = Playback(source);
        source.Rpm.Publish(State(0));
        source.Native.Publish(NativeHudSnapshot.Unavailable(carOrdinal: 314) with
        {
            NativeNeedleAngleDegrees = 300,
            NativeNeedleBlurAmount = -.2,
            NativeGaugeObservedTimestamp = Timestamp(0)
        }, 1_000);
        playback.ObserveQueued(Frame(source, 0), Timestamp(0), Timestamp(0));
        playback.RefreshNativeHistory(Timestamp(0));
        var sample = playback.Sample(Timestamp(0));
        Assert.True(sample.Native);
        Assert.Equal(300, sample.Angle);
        Assert.Equal(-.2, sample.Blur);
    }

    [Fact]
    public void IndependentConsumersRecoverFromOverflowAndClosedMailboxRejectsLatePublication()
    {
        var source = new TelemetryNeedleHistory();
        var first = default(NativeNeedleHistoryCursor);
        var second = default(NativeNeedleHistoryCursor);
        var batch = new TelemetryNeedleObservation[TelemetryNeedleHistory.Capacity];
        source.Publish(State(0));
        source.CopySince(314, Timestamp(0), ref first, batch);
        for (var ms = 1; ms <= 80; ms++) source.Publish(State(ms));
        var catchup = source.CopySince(314, Timestamp(0), ref first, batch);
        Assert.True(catchup.Reset);
        Assert.Equal(TelemetryNeedleHistory.Capacity, catchup.Count);
        Assert.Equal(Timestamp(17), batch[0].ReceivedTimestamp);
        Assert.Equal(Timestamp(80), batch[63].ReceivedTimestamp);
        Assert.Equal(catchup.Count, source.CopySince(314, Timestamp(0), ref second, batch).Count);
        Assert.Equal(0, source.CopySince(314, Timestamp(0), ref first, batch).Count);
        source.Reset(Timestamp(90), close: true);
        source.Publish(State(100));
        Assert.False(source.CopySince(314, Timestamp(100), ref first, batch).MatchesFrame);
    }

    [Fact]
    public void ClockDiscontinuityRequiresNewUiIdentityAndOldPacketsCannotRestoreIt()
    {
        var source = new TelemetryNeedleHistory();
        var cursor = default(NativeNeedleHistoryCursor);
        var batch = new TelemetryNeedleObservation[TelemetryNeedleHistory.Capacity];
        source.Publish(State(0));
        source.CopySince(314, Timestamp(0), ref cursor, batch);
        source.Publish(State(10) with { GameTimestampMilliseconds = 1 });
        Assert.False(source.CopySince(314, Timestamp(0), ref cursor, batch).MatchesFrame);
        source.Publish(State(0));
        var current = source.CopySince(314, Timestamp(10), ref cursor, batch);
        Assert.True(current.MatchesFrame);
        Assert.Equal(1, current.Count);
        Assert.Equal(Timestamp(10), batch[0].ReceivedTimestamp);
    }

    private static AnalogHudPlayback Playback(Source source)
    {
        var playback = new AnalogHudPlayback();
        playback.SetNativeSource(source);
        return playback;
    }

    private static long Timestamp(int milliseconds) =>
        (long)Math.Round((1_000 + milliseconds) * Stopwatch.Frequency / 1_000d);

    private static NativeGaugeFrame Frame(Source source, int ms) => new(true, 100, State(ms).EngineRpm, 8_000,
        TransmissionGear.First, SpeedUnit.MilesPerHour, ExactRedlineResult.Exact(7_000 * Math.PI / 30),
        CarOrdinal: 314, GameTimestampMilliseconds: (uint)(1_000 + ms), ReceivedTimestamp: Timestamp(ms),
        NativeSourceIdentity: source.Native.SourceIdentity);

    private static VehicleState State(int ms) => new()
    {
        IsRaceOn = true,
        CarOrdinal = 314,
        EngineRpm = 1_000 + ms * 20,
        EngineMaximumRpm = 8_000,
        GameTimestampMilliseconds = (uint)(1_000 + ms),
        ReceivedTimestamp = Timestamp(ms),
        ReceivedAtUtc = DateTimeOffset.UnixEpoch,
        Drivetrain = DrivetrainType.RearWheelDrive,
        GroundSpeedMetersPerSecond = 0,
        WheelRotationRadiansPerSecond = default,
        TireSlipRatio = default,
        TireSlipAngle = default,
        NormalizedSuspensionTravel = default,
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 0,
        Gear = TransmissionGear.First,
        Steering = 0,
        Accelerator = 0,
        Brake = 0
    };

    private sealed class Source : INativeNeedleHistorySource, ITelemetryNeedleHistorySource
    {
        internal NativeNeedleHistory Native { get; } = new();
        internal TelemetryNeedleHistory Rpm { get; } = new();
        internal Source() => Native.Reset(314);
        public NativeNeedleHistoryRead CopySince(int carOrdinal, long sourceIdentity,
            ref NativeNeedleHistoryCursor cursor, Span<NativeNeedleObservation> destination) =>
            Native.CopySince(carOrdinal, sourceIdentity, ref cursor, destination);
        public TelemetryNeedleHistoryRead CopyTelemetrySince(int carOrdinal, long frameReceivedTimestamp,
            ref NativeNeedleHistoryCursor cursor, Span<TelemetryNeedleObservation> destination) =>
            Rpm.CopySince(carOrdinal, frameReceivedTimestamp, ref cursor, destination);
    }
}

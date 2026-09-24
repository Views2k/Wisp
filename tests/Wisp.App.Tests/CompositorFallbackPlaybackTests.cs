using System.Diagnostics;
using Wisp.App;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class CompositorFallbackPlaybackTests
{
    [Fact]
    public void FallbackCurveUsesExistingRpmTimelineAngleMappingAndCurrentSignedBlur()
    {
        var playback = CreateSteady();
        var expected = CreateSteady().Sample(Timestamp(120));
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];

        Assert.True(playback.TryCopyCompositorCurve(Timestamp(120), points, out var curve));

        Assert.False(curve.NativeSource);
        Assert.Equal(20, curve.PlaybackTargetDelayMilliseconds);
        Assert.Equal(Timestamp(120), curve.LatestObservationTimestamp);
        Assert.Equal(Timestamp(195), curve.FreshUntilTimestamp);
        Assert.Equal(expected.Angle, points[0].Angle);
        Assert.Equal(expected.Blur, points[0].Blur);
        Assert.True(points[0].Blur < 0);
        Assert.Equal(NativeGaugeGeometry.AnalogNeedleAngle(Rpm(120), 8_000), points[curve.Count - 1].Angle, 8);
        Assert.InRange(curve.EndTimestamp, Timestamp(120), Timestamp(195));
        Assert.InRange(curve.Count, 2, CompositorNeedleCurve.MaximumPoints);
        for (var index = 0; index < curve.Count; index++)
        {
            var independent = CreateSteady();
            independent.Sample(Timestamp(120));
            var timestamp = curve.StartTimestamp + (long)Math.Round(points[index].OffsetSeconds * Stopwatch.Frequency);
            Assert.Equal(independent.Sample(timestamp).Angle, points[index].Angle, 8);
            Assert.Equal(expected.Blur, points[index].Blur);
            if (index > 0) Assert.True(points[index].OffsetSeconds > points[index - 1].OffsetSeconds);
        }
    }

    [Fact]
    public void TenMinuteSteadyRpmSessionKeepsTwentyMillisecondCurveDelayWithoutDrift()
    {
        var playback = new AnalogHudPlayback();
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];
        // The 100 ms phase correction retains sub-microsecond startup error at
        // one second. Keep that bound, plus one QPC tick, for the whole session.
        var delayToleranceMilliseconds = .001 + 1_000d / Stopwatch.Frequency;
        var horizonToleranceTicks = (long)Math.Ceiling(delayToleranceMilliseconds * Stopwatch.Frequency / 1_000d);
        for (var time = 0; time <= 600_000; time += 4)
        {
            playback.ObserveQueued(Frame(time) with { EngineRpm = SessionRpm(time) }, Timestamp(time), Timestamp(time));
            var sample = playback.Sample(Timestamp(time));
            if (time < 1_000 || time % 1_000 != 0) continue;

            Assert.True(playback.TryCopyCompositorCurve(Timestamp(time), points, out var curve));
            Assert.False(curve.NativeSource);
            Assert.Equal(20, curve.PlaybackTargetDelayMilliseconds, 6);
            Assert.InRange(curve.PlaybackDelayMilliseconds, 20 - delayToleranceMilliseconds, 20 + delayToleranceMilliseconds);
            Assert.InRange(sample.PlaybackDelayMilliseconds, 20 - delayToleranceMilliseconds, 20 + delayToleranceMilliseconds);
            // This triangle changes by at most one RPM per millisecond.
            Assert.InRange(sample.AppliedRpm!.Value, SessionRpm(time - 20) - delayToleranceMilliseconds,
                SessionRpm(time - 20) + delayToleranceMilliseconds);
            Assert.Equal(sample.Angle, points[0].Angle, 8);
            Assert.InRange(curve.EndTimestamp, Timestamp(time + 20) - horizonToleranceTicks,
                Timestamp(time + 20) + horizonToleranceTicks);
            Assert.Equal(Timestamp(time + 75), curve.FreshUntilTimestamp);
            Assert.Equal(1, curve.ReseedCount);
            Assert.Equal(0, sample.StarvationReseedCount);
            Assert.False(sample.PlaybackAtNewest);
        }

        static double SessionRpm(int time)
        {
            var phase = time % 10_000;
            return 1_000 + (phase <= 5_000 ? phase : 10_000 - phase);
        }
    }

    [Fact]
    public void TwentyMillisecondMinimumRetainsAdaptiveDelayCeilingAndFreshness()
    {
        var playback = new AnalogHudPlayback();
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];
        playback.ObserveQueued(Frame(0), Timestamp(0), Timestamp(0));
        playback.ObserveQueued(Frame(60), Timestamp(60), Timestamp(60));

        Assert.True(playback.TryCopyCompositorCurve(Timestamp(60), points, out var curve));
        Assert.Equal(75, curve.PlaybackTargetDelayMilliseconds, 6);
        Assert.Equal(Timestamp(135), curve.FreshUntilTimestamp);
        Assert.False(playback.TryCopyCompositorCurve(Timestamp(135), points, out _));
    }

    [Fact]
    public void ExportAdvancesOnlyTheRealSampleAndLeavesFutureLivePlaybackUnchanged()
    {
        var actual = CreateSteady();
        var expected = CreateSteady();
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];
        expected.Sample(Timestamp(120));
        Assert.True(actual.TryCopyCompositorCurve(Timestamp(120), points, out _));

        foreach (var time in new[] { 124, 132, 146 })
            Assert.Equal(expected.Sample(Timestamp(time)), actual.Sample(Timestamp(time)));
    }

    [Fact]
    public void DuplicateAndFutureRpmFramesCannotExtendTheAcceptedFreshnessDeadline()
    {
        var playback = CreateSteady();
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];
        playback.ObserveQueued(Frame(120) with { EngineRpm = 7_900 }, Timestamp(160), Timestamp(160));
        playback.ObserveQueued(Frame(1_000), Timestamp(164), Timestamp(164));

        Assert.True(playback.TryCopyCompositorCurve(Timestamp(164), points, out var curve));
        Assert.Equal(Timestamp(120), curve.LatestObservationTimestamp);
        Assert.Equal(Timestamp(195), curve.FreshUntilTimestamp);
        Assert.Equal(NativeGaugeGeometry.AnalogNeedleAngle(Rpm(120), 8_000), points[curve.Count - 1].Angle, 8);
        Assert.False(playback.TryCopyCompositorCurve(Timestamp(195), points, out _));
        var held = playback.Sample(Timestamp(300));
        Assert.True(held.NeedleVisible);
        Assert.Equal(Rpm(120), held.AppliedRpm);
    }

    [Fact]
    public void NativeAcquisitionAndInvalidationSwitchCurveSourcesWithoutBlendingChannels()
    {
        var playback = CreateSteady();
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];
        Assert.True(playback.TryCopyCompositorCurve(Timestamp(120), points, out var fallback));
        Assert.False(fallback.NativeSource);

        playback.ObserveQueued(Frame(124) with { NativeNeedleAngleDegrees = 320, NativeNeedleBlurAmount = -.3 },
            Timestamp(124), Timestamp(124));
        Assert.True(playback.TryCopyCompositorCurve(Timestamp(124), points, out var native));
        Assert.True(native.NativeSource);
        Assert.Equal(320, points[0].Angle);
        Assert.Equal(-.3, points[0].Blur);

        playback.ObserveQueued(Frame(128) with { NativeGaugeSourceInvalidated = true }, Timestamp(128), Timestamp(128));
        Assert.True(playback.TryCopyCompositorCurve(Timestamp(128), points, out var returned));
        Assert.False(returned.NativeSource);
        Assert.Equal(0, points[0].Blur);
        Assert.Equal(playback.Sample(Timestamp(128)).Angle, points[0].Angle);
        Assert.NotEqual(320, points[0].Angle);
    }

    [Fact]
    public void NativeExpiryCanUseFreshRpmAndCarChangeCannotReuseThePreviousCurve()
    {
        var playback = CreateSteady();
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];
        playback.ObserveQueued(Frame(124) with { NativeNeedleAngleDegrees = 320, NativeNeedleBlurAmount = -.3 },
            Timestamp(124), Timestamp(124));
        Assert.True(playback.TryCopyCompositorCurve(Timestamp(124), points, out var native));
        playback.ObserveQueued(Frame(180), Timestamp(180), Timestamp(180));
        Assert.True(playback.TryCopyCompositorCurve(Timestamp(200), points, out var expiredNative));
        Assert.False(expiredNative.NativeSource);
        Assert.Equal(Timestamp(180), expiredNative.LatestObservationTimestamp);

        playback.ObserveQueued(Frame(204) with { CarOrdinal = 3766, EngineRpm = 900 }, Timestamp(204), Timestamp(204));
        Assert.True(playback.TryCopyCompositorCurve(Timestamp(204), points, out var other));
        Assert.Equal(3766, other.AcceptedCarOrdinal);
        Assert.False(other.NativeSource);
        Assert.All(points.Take(other.Count), point => Assert.Equal(NativeGaugeGeometry.AnalogNeedleAngle(900, 8_000), point.Angle));
        playback.Reset();
        Assert.False(playback.TryCopyCompositorCurve(Timestamp(208), points, out _));
    }

    [Fact]
    public void InvalidGeometryOrRpmCannotExportAVisibleFallbackCurve()
    {
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];
        var playback = CreateSteady();
        playback.ObserveQueued(Frame(124) with { TachometerMaximumRpm = double.NaN }, Timestamp(124), Timestamp(124));
        Assert.False(playback.TryCopyCompositorCurve(Timestamp(124), points, out _));
        playback.ObserveQueued(Frame(128) with { EngineRpm = double.NaN }, Timestamp(128), Timestamp(128));
        Assert.False(playback.TryCopyCompositorCurve(Timestamp(128), points, out _));
    }

    private static AnalogHudPlayback CreateSteady()
    {
        var playback = new AnalogHudPlayback();
        for (var time = 0; time <= 120; time += 4)
        {
            playback.ObserveQueued(Frame(time), Timestamp(time), Timestamp(time));
            if (time < 120) playback.Sample(Timestamp(time));
        }
        return playback;
    }

    private static NativeGaugeFrame Frame(int milliseconds) => new(
        true, 100, Rpm(milliseconds), 8_000, TransmissionGear.Neutral, SpeedUnit.MilesPerHour,
        ExactRedlineResult.Exact(7_000 * 2 * Math.PI / 60), CarOrdinal: 314,
        GameTimestampMilliseconds: (uint)(1_000 + milliseconds), ReceivedTimestamp: Timestamp(milliseconds),
        NativeGaugeObservedTimestamp: Timestamp(milliseconds));

    private static double Rpm(int milliseconds) => 1_000 + milliseconds * 25;
    private static long Timestamp(int milliseconds) => Stopwatch.Frequency +
        (long)Math.Round(Stopwatch.Frequency * milliseconds / 1_000d);
}

using System.Diagnostics;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class AnalogHudPendingFrameTests
{
    [Fact]
    public void FreshTelemetryKeepsPendingPixelsWhilePlaybackConsumesItsOriginalSourceTime()
    {
        var playback = new AnalogHudPlayback();
        for (int ms = 0; ms <= 40; ms += 10)
            playback.Observe(Frame(ms, 1_000 + ms * 100), Timestamp(ms));
        var presentation = Presentation();
        var pending = new AnalogHudPendingFrame(playback.Sample(Timestamp(50)), presentation, Timestamp(40), 7);
        var queued = Frame(49, 5_900) with { Speed = 125, Gear = TransmissionGear.Third };

        playback.ObserveQueued(queued, Timestamp(49), Timestamp(52));

        Assert.True(pending.CanReuse(presentation, queued, false));
        Assert.Equal(2_000, pending.Sample.AppliedRpm);
        Assert.Equal(Timestamp(50), pending.Sample.Timestamp);
        Assert.Equal(Timestamp(40), pending.QueuedTimestamp);
        Assert.Equal(7, pending.Sequence);
        var next = playback.Sample(Timestamp(85));
        Assert.Equal(5_500, next.AppliedRpm!.Value, 6);
        Assert.Equal(queued, next.Frame);
        Assert.Equal(Timestamp(49), next.Frame.ReceivedTimestamp);
        Assert.Equal(pending.Sample.ReseedCount, next.ReseedCount);
    }

    [Theory]
    [InlineData("car")]
    [InlineData("electric")]
    [InlineData("redline")]
    [InlineData("missing-scale")]
    [InlineData("maximum-rpm")]
    [InlineData("native-invalidated")]
    [InlineData("speed-unit")]
    [InlineData("gear-mode")]
    public void ChangedCarOrNeedleValidityDiscardsPendingPixels(string change)
    {
        var pending = Pending();
        var original = pending.Sample.Frame;
        var changed = change switch
        {
            "car" => original with { CarOrdinal = original.CarOrdinal + 1 },
            "electric" => original with { IsElectric = true },
            "redline" => original with { ExactRedline = ExactRedlineResult.Exact(6_000 * 2 * Math.PI / 60) },
            "missing-scale" => original with { ExactRedline = ExactRedlineResult.Unavailable() },
            "maximum-rpm" => original with { TachometerMaximumRpm = 9_000 },
            "speed-unit" => original with { Unit = SpeedUnit.KilometersPerHour },
            "gear-mode" => original with { GearDisplayMode = GearDisplayMode.Automatic },
            _ => original with { NativeGaugeSourceInvalidated = true }
        };

        Assert.False(pending.CanReuse(pending.Presentation, changed, false));
    }

    [Fact]
    public void RestoredNativeValidityAlsoDiscardsAnInvalidatedPendingFrame()
    {
        var pending = Pending();
        pending = pending with
        {
            Sample = pending.Sample with
            {
                Frame = pending.Sample.Frame with { NativeGaugeSourceInvalidated = true }
            }
        };

        Assert.False(pending.CanReuse(pending.Presentation,
            pending.Sample.Frame with { NativeGaugeSourceInvalidated = false }, false));
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("origin")]
    [InlineData("transform")]
    [InlineData("layout")]
    [InlineData("traction")]
    [InlineData("traction-color")]
    public void ChangedPresentationDiscardsPendingPixels(string change)
    {
        var pending = Pending();
        var original = pending.Presentation;
        var changed = change switch
        {
            "hidden" => original with { Active = false },
            "width" => original with { Width = 900 },
            "height" => original with { Height = 700 },
            "origin" => original with { OriginY = 12 },
            "transform" => original with { AxisXX = 1.25f },
            "layout" => original with { Layout = original.Layout! with { NeedlePivot = new(145, 143) } },
            "traction" => original with { TractionActive = true },
            _ => original with { TractionColor = new(255, 80, 80) }
        };

        Assert.False(pending.CanReuse(changed, pending.Sample.Frame, false));
    }

    [Fact]
    public void AnEquivalentPresentationSnapshotDoesNotForceAnotherDraw()
    {
        var pending = Pending();
        var equivalent = pending.Presentation with { Layout = pending.Presentation.Layout! with { } };

        Assert.NotSame(pending.Presentation, equivalent);
        Assert.NotSame(pending.Presentation.Layout, equivalent.Layout);
        Assert.True(pending.CanReuse(equivalent, pending.Sample.Frame, false));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.5f)]
    [InlineData(1f)]
    public void CompositionOpacityDoesNotInvalidateAlreadyDrawnPixels(float opacity)
    {
        var pending = Pending();

        Assert.True(pending.CanReuse(pending.Presentation with { Opacity = opacity }, pending.Sample.Frame, false));
    }

    [Fact]
    public void NewlyAvailableNativePairDiscardsPendingRpmPixelsWithoutAnInvalidationFlagChange()
    {
        var playback = new AnalogHudPlayback();
        playback.Observe(Frame(0, 4_000), Timestamp(0));
        var pending = new AnalogHudPendingFrame(playback.Sample(Timestamp(1)), Presentation(), Timestamp(0), 1);
        var native = Frame(10, 4_000) with
        {
            NativeNeedleAngleDegrees = 320,
            NativeNeedleBlurAmount = -.4,
            NativeGaugeObservedTimestamp = Timestamp(10)
        };

        playback.ObserveQueued(native, Timestamp(10), Timestamp(12));

        Assert.False(pending.Sample.Native);
        Assert.True(playback.HasNativeNeedle(Timestamp(12)));
        Assert.Equal(pending.Sample.Frame.NativeGaugeSourceInvalidated, native.NativeGaugeSourceInvalidated);
        Assert.False(pending.CanReuse(pending.Presentation, native, playback.HasNativeNeedle(Timestamp(12))));
    }

    [Fact]
    public void NativePixelsRemainReusableUntilTheExactPairExpiresWithoutNewTelemetry()
    {
        var playback = new AnalogHudPlayback();
        var native = Frame(0, 4_000) with
        {
            NativeNeedleAngleDegrees = 320,
            NativeNeedleBlurAmount = -.4,
            NativeGaugeObservedTimestamp = Timestamp(0)
        };
        playback.Observe(native, Timestamp(0));
        var pending = new AnalogHudPendingFrame(playback.Sample(Timestamp(1)), Presentation(), Timestamp(0), 1);
        var lastFresh = Timestamp(NativeNeedlePlayback.NativeSampleFreshnessMilliseconds);
        var expired = lastFresh + 1;

        Assert.True(pending.Sample.Native);
        Assert.True(playback.HasNativeNeedle(lastFresh));
        Assert.True(pending.CanReuse(pending.Presentation, native, playback.HasNativeNeedle(lastFresh)));
        Assert.False(playback.HasNativeNeedle(expired));
        Assert.False(pending.CanReuse(pending.Presentation, native, playback.HasNativeNeedle(expired)));
    }

    private static AnalogHudPendingFrame Pending()
    {
        var playback = new AnalogHudPlayback();
        playback.Observe(Frame(0, 4_000), Timestamp(0));
        return new(playback.Sample(Timestamp(1)), Presentation(), Timestamp(0), 1);
    }

    private static AnalogHudPresentation Presentation() => new(800, 600, 0, 0, 1, 0, 0, 1, 1,
        true, false, new(85, 230, 193), AnalogHudLayout.Authored);

    private static NativeGaugeFrame Frame(int milliseconds, double rpm) => new(
        true, 100, rpm, 8_000, TransmissionGear.Neutral, SpeedUnit.MilesPerHour,
        ExactRedlineResult.Exact(7_000 * 2 * Math.PI / 60), CarOrdinal: 314,
        GameTimestampMilliseconds: (uint)(1_000 + milliseconds), ReceivedTimestamp: Timestamp(milliseconds));

    private static long Timestamp(double milliseconds) =>
        (long)Math.Round((1_000 + milliseconds) * Stopwatch.Frequency / 1_000d);
}

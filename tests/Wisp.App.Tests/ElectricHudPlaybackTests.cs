using System.Diagnostics;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ElectricHudPlaybackTests
{
    [Fact]
    public void NativeAngleAndBlurStayPairedOnTheExistingTimeline()
    {
        var playback = new ElectricHudPlayback();
        playback.Observe(Frame(0, 20) with { NativeNeedleAngleDegrees = 180, NativeNeedleBlurAmount = -.2 }, Timestamp(0));
        playback.Observe(Frame(20, 60) with { NativeNeedleAngleDegrees = 220, NativeNeedleBlurAmount = -.4 }, Timestamp(20));
        var sample = playback.Sample(Timestamp(50));
        Assert.True(sample.Native);
        Assert.Equal(200, sample.Angle, 6);
        Assert.Equal(-.3, sample.Blur, 6);
        Assert.Null(sample.AppliedSpeed);
        Assert.Equal(60, sample.Frame.Speed);
        Assert.Equal(40, sample.PlaybackTargetDelayMilliseconds, 6);
    }

    [Fact]
    public void FallbackKeepsTheElectricSpeedScaleAndDoesNotReuseAnotherCar()
    {
        var playback = new ElectricHudPlayback();
        playback.Observe(Frame(0, 20), Timestamp(0));
        playback.Observe(Frame(20, 60), Timestamp(20));
        var sample = playback.Sample(Timestamp(50));
        Assert.False(sample.Native);
        Assert.Equal(40, sample.AppliedSpeed!.Value, 6);
        Assert.Equal(NativeGaugeGeometry.ElectricAnalogNeedleAngle(40, 240), sample.Angle, 6);
        playback.Observe(Frame(60, 100) with { CarOrdinal = 2 }, Timestamp(60));
        sample = playback.Sample(Timestamp(61));
        Assert.Equal(100, sample.AppliedSpeed!.Value, 6);
        Assert.Equal(0, sample.Blur);
        playback.Observe(Frame(70, 100) with { IsElectric = false }, Timestamp(70));
        Assert.Equal(default, playback.Sample(Timestamp(71)));
    }

    [Fact]
    public void ExpiredNativePairFallsBackAndPartialPowerStateDoesNotInventABar()
    {
        var playback = new ElectricHudPlayback();
        playback.Observe(Frame(0, 20) with
        {
            NativeNeedleAngleDegrees = 220,
            NativeNeedleBlurAmount = -.3,
            NativeRegenFillAmount = .1,
            NativePowerFillAmount = .6,
            NativeRegenPowerRatio = .3
        }, Timestamp(0));
        var sample = playback.Sample(Timestamp(1));
        Assert.True(sample.Native);
        Assert.True(sample.PowerBar.Available);
        Assert.Equal(.14, sample.PowerBar.RegenFill, 12);
        Assert.False(playback.Sample(Timestamp(100)).Native);
        playback.Observe(Frame(110, 20) with
        {
            NativeRegenFillAmount = .1,
            NativePowerFillAmount = double.NaN,
            NativeRegenPowerRatio = .3
        }, Timestamp(110));
        Assert.False(playback.Sample(Timestamp(111)).PowerBar.Available);
        playback.Reset();
        Assert.Equal(default, playback.Sample(Timestamp(112)));
    }

    private static NativeGaugeFrame Frame(int ms, int speed) => new(true, speed, 0, 0,
        TransmissionGear.First, SpeedUnit.MilesPerHour, ExactRedlineResult.Unavailable(),
        IsElectric: true, CarOrdinal: 1, GameTimestampMilliseconds: (uint)ms,
        ReceivedTimestamp: Timestamp(ms), NativeElectricMaximumSpeed: 240,
        NativeGaugeObservedTimestamp: Timestamp(ms));
    private static long Timestamp(double milliseconds) => (long)Math.Round((1000 + milliseconds) * Stopwatch.Frequency / 1000d);
}

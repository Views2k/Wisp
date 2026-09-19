using System.Diagnostics;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DigitalHudPlaybackTests
{
    [Fact]
    public void DigitalTachUsesExistingInterpolationAndReseedsAfterElectricTransition()
    {
        var playback = new DigitalHudPlayback();
        playback.Observe(Frame(0, 2000), Timestamp(0));
        playback.Observe(Frame(20, 6000), Timestamp(20));
        var sample = playback.Sample(Timestamp(50));
        Assert.Equal(4000, sample.AppliedRpm, 6);
        Assert.Equal(6000, sample.Frame.EngineRpm);
        playback.Observe(Frame(60, 0) with { IsElectric = true }, Timestamp(60));
        Assert.Equal(0, playback.Sample(Timestamp(61)).AppliedRpm);
        playback.Observe(Frame(70, 7500), Timestamp(70));
        Assert.Equal(7500, playback.Sample(Timestamp(71)).AppliedRpm);
        playback.Reset();
        Assert.False(playback.Sample(Timestamp(72)).Available);
    }

    [Fact]
    public void DigitalElectricBarRequiresTheCompleteNativeTripletAndKeepsIdleMarker()
    {
        var playback = new DigitalHudPlayback();
        var frame = Frame(0, 0) with { IsElectric = true, NativeRegenFillAmount = 0, NativePowerFillAmount = .6, NativeRegenPowerRatio = .3 };
        playback.Observe(frame, Timestamp(0));
        var sample = playback.Sample(Timestamp(1));
        Assert.True(sample.PowerBar.Available);
        Assert.Equal(.04, sample.PowerBar.RegenFill, 12);
        Assert.Equal(.6, sample.PowerBar.PowerFill);
        playback.Observe(frame with { NativePowerFillAmount = double.NaN }, Timestamp(20));
        Assert.False(playback.Sample(Timestamp(21)).PowerBar.Available);
    }

    private static NativeGaugeFrame Frame(uint milliseconds, double rpm) => new(true, 137, rpm, 9000,
        TransmissionGear.Third, SpeedUnit.MilesPerHour, ExactRedlineResult.Exact(8000 * 2 * Math.PI / 60),
        CarOrdinal: 1, GameTimestampMilliseconds: milliseconds, ReceivedTimestamp: Timestamp(milliseconds));
    private static long Timestamp(double milliseconds) => (long)Math.Round((1000 + milliseconds) * Stopwatch.Frequency / 1000d);
}

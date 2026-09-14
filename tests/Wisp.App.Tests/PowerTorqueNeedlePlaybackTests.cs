using System.Diagnostics;
using Xunit;

namespace Wisp.App.Tests;

public sealed class PowerTorqueNeedlePlaybackTests
{
    private static long Ticks(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1_000);

    private static PowerTorqueDisplay Display(double power, double torque) => new(true, power, torque, 900, 1_100)
    { ReadoutPowerBhp = 100, ReadoutTorqueNm = 200 };

    [Fact]
    public void BothNeedlesAdvanceBetweenPacketsWhileNumbersAndPeaksStayUntouched()
    {
        var playback = new PowerTorqueNeedlePlayback();
        playback.Observe(Display(0, 0), 1, 1_000, Ticks(1_000), Ticks(1_000));
        playback.Observe(Display(100, 200), 1, 1_000, Ticks(1_016), Ticks(1_016));
        playback.Observe(Display(200, 400), 1, 1_016, Ticks(1_032), Ticks(1_032));
        var before = playback.Sample(Ticks(1_044));
        var after = playback.Sample(Ticks(1_048));
        Assert.InRange(before.PowerBhp, 24.999, 25.001);
        Assert.InRange(after.PowerBhp, 49.999, 50.001);
        Assert.Equal(after.PowerBhp * 2, after.TorqueNm);
        Assert.Equal(before.ReadoutPowerBhp, after.ReadoutPowerBhp);
        Assert.Equal(before.ReadoutTorqueNm, after.ReadoutTorqueNm);
        Assert.Equal(900, after.PeakPowerBhp);
        Assert.Equal(1_100, after.PeakTorqueNm);
    }

    [Fact]
    public void PacketLossNeverExtrapolatesAndFreshReturnDoesNotReplayOldHistory()
    {
        var playback = new PowerTorqueNeedlePlayback();
        playback.Observe(Display(100, 200), 1, 1_000, Ticks(1_000), Ticks(1_000));
        playback.Observe(Display(200, 400), 1, 1_016, Ticks(1_016), Ticks(1_016));
        Assert.Equal(200, playback.Sample(Ticks(1_500)).PowerBhp);
        Assert.Equal(200, playback.Sample(Ticks(2_000)).PowerBhp);
        var resumed = playback.Observe(Display(300, 600), 1, 2_000, Ticks(2_000), Ticks(2_000));
        Assert.Equal(300, resumed.PowerBhp);
        Assert.Equal(600, resumed.TorqueNm);
    }

    [Fact]
    public void CarChangeAndInvalidDataClearHistoryAndSignedValuesStaySupported()
    {
        var playback = new PowerTorqueNeedlePlayback();
        playback.Observe(Display(500, 600), 1, 1_000, Ticks(1_000), Ticks(1_000));
        var changed = playback.Observe(Display(-69, -138), 2, 1_016, Ticks(1_016), Ticks(1_016));
        Assert.Equal(-69, changed.PowerBhp);
        Assert.Equal(-138, changed.TorqueNm);
        playback.Observe(default, 2, 1_032, Ticks(1_032), Ticks(1_032));
        Assert.False(playback.HasSamples);
        Assert.False(playback.Sample(Ticks(1_040)).Available);
        var recovered = playback.Observe(Display(20, 30), 2, 1_048, Ticks(1_048), Ticks(1_048));
        Assert.Equal(20, recovered.PowerBhp);
        Assert.Equal(30, recovered.TorqueNm);
        playback.Reset();
        Assert.False(playback.HasSamples);
    }

    [Fact]
    public void ResettingPeaksKeepsTheCurrentNeedleTimeline()
    {
        var playback = new PowerTorqueNeedlePlayback();
        playback.Observe(Display(0, 0), 1, 1_000, Ticks(1_000), Ticks(1_000));
        playback.Observe(Display(100, 200), 1, 1_016, Ticks(1_016), Ticks(1_016));
        var before = playback.Sample(Ticks(1_044));
        playback.UpdatePeaks(new(true, 0, 0, 100, 200));
        var after = playback.Sample(Ticks(1_048));
        Assert.True(after.PowerBhp > before.PowerBhp);
        Assert.Equal(100, after.PeakPowerBhp);
        Assert.Equal(200, after.PeakTorqueNm);
    }
}

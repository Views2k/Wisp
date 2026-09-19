using System.Diagnostics;
using Xunit;

namespace Wisp.App.Tests;

public sealed class PowerTorqueNeedlePlaybackTests
{
    private static long Ticks(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1_000);

    private static PowerTorqueDisplay Display(double power, double torque) => new(true, power, torque, 900, 1_100)
    { ReadoutPowerBhp = 100, ReadoutTorqueNm = 200 };

    [Theory]
    [InlineData(.5)]
    [InlineData(1.25)]
    [InlineData(3)]
    public void FlashUsesSelectedPeriodAndDefaultRetainsEightHundredMilliseconds(double frequency)
    {
        var playback = new PowerTorqueNeedlePlayback();
        var start = Ticks(1_000);
        if (frequency != 1.25) playback.SetDriftFlashFrequency(frequency, start);
        var display = Display(500, 600) with { IsDriftPowerCut = true, DriftPulseAllowed = true };
        playback.Observe(display, 1, 1_000, start, start);
        var period = (long)Math.Round(Stopwatch.Frequency / frequency);
        foreach (var (phase, expected) in new[] { (0d, 0d), (.25, .5), (.5, 1d), (.75, .5), (1d, 0d) })
        {
            var actual = playback.Sample(start + (long)Math.Round(period * phase));
            Assert.InRange(Math.Abs(actual.DriftCutPulse - expected), 0, .000001);
            Assert.Equal(display, actual with { DriftCutPulse = 0 });
        }
    }

    [Theory]
    [InlineData(.25, .5)]
    [InlineData(.25, 3)]
    [InlineData(.75, .5)]
    [InlineData(.75, 3)]
    public void FrequencyEditPreservesPulsePhaseDirectionAndGaugeState(double phase, double frequency)
    {
        var playback = new PowerTorqueNeedlePlayback();
        var start = Ticks(1_000);
        var display = Display(500, 600) with { IsDriftPowerCut = true, DriftPulseAllowed = true };
        playback.Observe(display, 1, 1_000, start, start);
        var changedAt = start + Ticks(800 * phase);
        var before = playback.Sample(changedAt);
        playback.SetDriftFlashFrequency(frequency, changedAt);
        var after = playback.Sample(changedAt);
        Assert.InRange(Math.Abs(before.DriftCutPulse - after.DriftCutPulse), 0, .000001);
        Assert.Equal(before with { DriftCutPulse = 0 }, after with { DriftCutPulse = 0 });
        playback.SetDriftFlashFrequency(frequency, changedAt);
        Assert.Equal(after, playback.Sample(changedAt));
        var later = playback.Sample(changedAt + Ticks(20));
        Assert.Equal(phase < .5, later.DriftCutPulse > after.DriftCutPulse);
        Assert.Equal(before with { DriftCutPulse = 0 }, later with { DriftCutPulse = 0 });
    }

    [Fact]
    public void FrequencyChangesCannotReviveExpiredPulsesAndResetKeepsTheSelection()
    {
        var playback = new PowerTorqueNeedlePlayback();
        var display = Display(500, 600) with { IsDriftPowerCut = true, DriftPulseAllowed = true };
        playback.Observe(display, 1, 1_000, Ticks(1_000), Ticks(1_000));
        Assert.Equal(0, playback.Sample(Ticks(1_900)).DriftCutPulse);
        playback.SetDriftFlashFrequency(.5, Ticks(1_900));
        Assert.Equal(0, playback.Sample(Ticks(2_400)).DriftCutPulse);
        playback.Reset();
        playback.Observe(display, 2, 3_000, Ticks(3_000), Ticks(3_000));
        Assert.Equal(1, playback.Sample(Ticks(4_000)).DriftCutPulse, 6);
    }

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

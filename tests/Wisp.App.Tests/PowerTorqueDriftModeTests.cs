using System.Diagnostics;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class PowerTorqueDriftModeTests
{
    private const double WattsPerBhp = 745.69987158227022;
    private const double CutPowerBhp = -310_778.21875 / WattsPerBhp;
    private const double CutTorqueNm = -289.520263671875;
    private static readonly PowerTorqueDriftInput Driving = new(true, 255, 30, 6_000, TransmissionGear.Second);
    private static long Ticks(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1_000);
    private static PowerTorqueDisplayModel Enabled(double smoothing = 0) => new()
    {
        DriftModeEnabled = true,
        SmoothingMilliseconds = smoothing
    };

    private static PowerTorqueDisplay Observe(PowerTorqueDisplayModel model, uint time, double power, double torque,
        PowerTorqueDriftInput? input = null, int car = 1, uint? gameTime = null) =>
        model.Observe(car, gameTime ?? time, power * WattsPerBhp, torque, Ticks(time), input ?? Driving);

    private static void AssertHeld(PowerTorqueDisplay expected, PowerTorqueDisplay actual)
    {
        Assert.True(actual.IsDriftPowerCut);
        Assert.True(actual.DriftPulseAllowed);
        Assert.Equal(expected.PowerBhp, actual.PowerBhp);
        Assert.Equal(expected.TorqueNm, actual.TorqueNm);
        Assert.Equal(expected.ReadoutPowerBhp, actual.ReadoutPowerBhp);
        Assert.Equal(expected.ReadoutTorqueNm, actual.ReadoutTorqueNm);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(250, false)]
    [InlineData(250, true)]
    [InlineData(1500, true)]
    public void DisabledModeKeepsExistingSmoothingReadoutsAndPeaksExactly(double smoothing, bool negative)
    {
        var existing = new PowerTorqueDisplayModel { SmoothingMilliseconds = smoothing, ShowNegative = negative };
        var withContext = new PowerTorqueDisplayModel { SmoothingMilliseconds = smoothing, ShowNegative = negative };
        Assert.False(withContext.DriftModeEnabled);
        var sequence = new[] { (1500d, 1600d), (0d, 0d), (1500d, 1600d), (CutPowerBhp, CutTorqueNm), (700d, 900d) };
        for (var index = 0; index < 25; index++)
        {
            var time = (uint)(1_000 + index * 20);
            var sample = sequence[index % sequence.Length];
            var baseline = existing.Observe(1, time, sample.Item1 * WattsPerBhp, sample.Item2, Ticks(time));
            Assert.Equal(baseline, Observe(withContext, time, sample.Item1, sample.Item2));
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1600)]
    [InlineData(1500, 0)]
    [InlineData(CutPowerBhp, CutTorqueNm)]
    [InlineData(CutPowerBhp, 1600)]
    [InlineData(1500, CutTorqueNm)]
    public void ABriefCutInEitherChannelHoldsBothNeedlesAndBothNumbers(double power, double torque)
    {
        var model = Enabled(250);
        Observe(model, 1_000, 1_000, 1_100);
        var before = Observe(model, 1_120, 1_500, 1_600);
        AssertHeld(before, Observe(model, 1_140, power, torque));
        AssertHeld(before, Observe(model, 1_260, power, torque));
        AssertHeld(before, Observe(model, 1_400, power, torque));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(CutPowerBhp, CutTorqueNm)]
    public void RepeatedBriefCutTrainsRemainStableButSustainedCutsExpireWithoutRearming(double cutPower, double cutTorque)
    {
        var model = Enabled();
        var expected = Observe(model, 1_000, 1_500, 1_600);
        for (uint time = 1_020; time < 3_020; time += 100)
        {
            AssertHeld(expected, Observe(model, time, cutPower, cutTorque));
            var recovered = Observe(model, time + 50, 1_500, 1_600);
            Assert.False(recovered.IsDriftPowerCut);
            Assert.Equal(expected.PowerBhp, recovered.PowerBhp);
            Assert.Equal(expected.TorqueNm, recovered.TorqueNm);
        }
        for (uint time = 3_020; time <= 4_020; time += 50)
        {
            var current = Observe(model, time, cutPower, cutTorque);
            if (time >= 3_520)
            {
                Assert.False(current.IsDriftPowerCut);
                Assert.Equal(0, current.PowerBhp);
                Assert.Equal(0, current.TorqueNm);
            }
        }
        var positive = Observe(model, 4_070, 900, 1_000);
        AssertHeld(positive, Observe(model, 4_090, cutPower, cutTorque));
    }

    private static IEnumerable<PowerTorqueDriftInput> IneligibleInputs()
    {
        yield return Driving with { IsRaceOn = false };
        yield return Driving with { Accelerator = 0 };
        yield return Driving with { GroundSpeedMetersPerSecond = 0 };
        yield return Driving with { EngineRpm = 700 };
        yield return Driving with { GroundSpeedMetersPerSecond = double.NaN };
        yield return Driving with { EngineRpm = double.PositiveInfinity };
        yield return Driving with { Gear = TransmissionGear.Reverse };
        yield return Driving with { Gear = TransmissionGear.Neutral };
        yield return Driving with { Gear = TransmissionGear.Unknown };
    }

    [Fact]
    public void LiftCoastIdleReverseAndInvalidContextImmediatelyReleaseTheHold()
    {
        foreach (var input in IneligibleInputs())
        {
            var model = Enabled();
            Observe(model, 1_000, 1_500, 1_600);
            Assert.True(Observe(model, 1_020, 0, 0).IsDriftPowerCut);
            var released = Observe(model, 1_040, CutPowerBhp, CutTorqueNm, input);
            Assert.False(released.IsDriftPowerCut);
            Assert.False(released.DriftPulseAllowed);
            Assert.Equal(0, released.PowerBhp);
            Assert.Equal(0, released.TorqueNm);
            Assert.False(Observe(model, 1_060, 0, 0).IsDriftPowerCut);
        }
    }

    [Fact]
    public void AnElectricCarCanHoldACutWithoutACombustionRpmReading()
    {
        var model = Enabled();
        var electric = Driving with { IsElectric = true, EngineRpm = 0 };
        var expected = Observe(model, 1_000, 1_500, 1_600, electric);
        AssertHeld(expected, Observe(model, 1_020, 0, 0, electric));
        Assert.False(Observe(model, 1_040, 0, 0, electric with { Gear = TransmissionGear.Reverse }).IsDriftPowerCut);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ElectricRegenerationNeverRetainsPositiveOutput(bool showNegative)
    {
        var model = Enabled();
        model.ShowNegative = showNegative;
        var electric = Driving with { IsElectric = true, EngineRpm = 0 };
        Observe(model, 1_000, 1_500, 1_600, electric);
        AssertHeld(model.Current, Observe(model, 1_020, 0, 0, electric));
        var regeneration = Observe(model, 1_140, CutPowerBhp, CutTorqueNm, electric);
        Assert.False(regeneration.IsDriftPowerCut);
        Assert.False(regeneration.DriftPulseAllowed);
        Assert.Equal(showNegative ? CutPowerBhp : 0, regeneration.PowerBhp, 9);
        Assert.Equal(showNegative ? CutTorqueNm : 0, regeneration.TorqueNm, 9);
        Assert.Equal(regeneration.PowerBhp, regeneration.ReadoutPowerBhp);
        Assert.Equal(regeneration.TorqueNm, regeneration.ReadoutTorqueNm);
        Assert.False(Observe(model, 1_160, 0, 0, electric).IsDriftPowerCut);
    }

    [Fact]
    public void SmallRecoveryNoiseDoesNotRestartTheMaximumCutWindow()
    {
        var model = Enabled();
        var expected = Observe(model, 1_000, 1_500, 1_600);
        AssertHeld(expected, Observe(model, 1_020, 0, 0));
        for (uint time = 1_040; time <= 1_400; time += 20)
            AssertHeld(expected, Observe(model, time, time % 40 == 0 ? 150 : 0, time % 40 == 0 ? 160 : 0));
        for (uint time = 1_420; time <= 1_600; time += 20)
            Observe(model, time, 0, 0);
        Assert.False(model.Current.IsDriftPowerCut);
        Assert.Equal(0, model.Current.PowerBhp);
        Assert.Equal(0, model.Current.TorqueNm);
    }

    [Fact]
    public void MissingDrivingContextAndVisibleNegativeOutputNeverStartAHold()
    {
        var model = Enabled();
        model.ShowNegative = true;
        Observe(model, 1_000, 1_500, 1_600);
        var missing = model.Observe(1, 1_020, 0, 0, Ticks(1_020));
        Assert.False(missing.IsDriftPowerCut);
        Assert.False(missing.DriftPulseAllowed);
        Observe(model, 1_040, 1_500, 1_600);
        var signed = Observe(model, 1_160, -30, -40);
        Assert.False(signed.IsDriftPowerCut);
        Assert.Equal(-30, signed.PowerBhp, 9);
        Assert.Equal(-40, signed.TorqueNm);
        Assert.Equal(-30, signed.ReadoutPowerBhp!.Value, 9);
        Assert.Equal(-40, signed.ReadoutTorqueNm);
    }

    [Theory]
    [InlineData(2, 1_060)]
    [InlineData(1, 900)]
    [InlineData(1, 1_400)]
    public void CarChangeClockRewindAndMissingPacketsCannotCarryAHeldReading(int car, uint gameTime)
    {
        var model = Enabled();
        Observe(model, 1_000, 1_500, 1_600);
        Assert.True(Observe(model, 1_020, 0, 0).IsDriftPowerCut);
        var current = Observe(model, Math.Max(1_060u, gameTime), 0, 0, car: car, gameTime: gameTime);
        Assert.False(current.IsDriftPowerCut);
        Assert.Equal(0, current.PowerBhp);
        Assert.Equal(0, current.TorqueNm);
    }

    [Fact]
    public void QuantizedGameTimestampsStillExpireTheHoldUsingReceiveTime()
    {
        var model = Enabled();
        var expected = Observe(model, 1_000, 1_500, 1_600, gameTime: 1_000);
        AssertHeld(expected, Observe(model, 1_020, 0, 0, gameTime: 1_000));
        for (uint time = 1_040; time <= 1_600; time += 20)
            Observe(model, time, 0, 0, gameTime: 1_000);
        Assert.False(model.Current.IsDriftPowerCut);
        Assert.Equal(0, model.Current.PowerBhp);
        Assert.Equal(0, model.Current.TorqueNm);
    }

    [Fact]
    public void MissingTelemetryAndManualDisableClearHeldStateBeforeTheNextReading()
    {
        var model = Enabled();
        Observe(model, 1_000, 1_500, 1_600);
        Observe(model, 1_020, 0, 0);
        model.ResetCurrent();
        Assert.False(model.Current.Available);
        Assert.False(model.Current.IsDriftPowerCut);
        Assert.False(model.Current.DriftPulseAllowed);
        Assert.False(Observe(model, 1_040, 0, 0).IsDriftPowerCut);
        Observe(model, 1_060, 1_500, 1_600);
        Observe(model, 1_080, 0, 0);
        model.DriftModeEnabled = false;
        Assert.False(model.Current.IsDriftPowerCut);
        Assert.False(model.Current.DriftPulseAllowed);
        var disabled = Observe(model, 1_100, 0, 0);
        Assert.Equal(0, disabled.PowerBhp);
        Assert.Equal(0, disabled.TorqueNm);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(CutPowerBhp, CutTorqueNm)]
    public void CutTrainPlaybackKeepsBothReadingsSteadyAndTheFlashSlowAndBounded(double cutPower, double cutTorque)
    {
        var model = Enabled();
        var playback = new PowerTorqueNeedlePlayback();
        var start = Observe(model, 1_000, 1_500, 1_600);
        playback.Observe(start, 1, 1_000, Ticks(1_000), Ticks(1_000));
        var pulses = new List<double>();
        for (uint time = 1_020; time <= 4_200; time += 20)
        {
            var cut = (time / 40) % 2 == 1;
            var display = Observe(model, time, cut ? cutPower : 1_500, cut ? cutTorque : 1_600);
            Assert.Equal(cut, display.IsDriftPowerCut);
            Assert.True(display.DriftPulseAllowed);
            playback.Observe(display, 1, time, Ticks(time), Ticks(time));
            var rendered = playback.Sample(Ticks(time));
            Assert.Equal(start.PowerBhp, rendered.PowerBhp);
            Assert.Equal(start.TorqueNm, rendered.TorqueNm);
            Assert.Equal(start.ReadoutPowerBhp, rendered.ReadoutPowerBhp);
            Assert.Equal(start.ReadoutTorqueNm, rendered.ReadoutTorqueNm);
            Assert.InRange(rendered.DriftCutPulse, 0, 1);
            if (time >= 1_200) pulses.Add(rendered.DriftCutPulse);
        }
        Assert.True(pulses.Max() - pulses.Min() > .25);
        Assert.All(pulses.Zip(pulses.Skip(1)), pair => Assert.InRange(Math.Abs(pair.Second - pair.First), 0, .2));
        var changesOfDirection = 0;
        var previousDirection = 0;
        foreach (var pair in pulses.Zip(pulses.Skip(1)))
        {
            var direction = Math.Sign(pair.Second - pair.First);
            if (direction == 0) continue;
            if (previousDirection != 0 && direction != previousDirection) changesOfDirection++;
            previousDirection = direction;
        }
        Assert.InRange(changesOfDirection, 2, 10);
        var lifted = Observe(model, 4_220, 0, 0, Driving with { Accelerator = 0 });
        var released = playback.Observe(lifted, 1, 4_220, Ticks(4_220), Ticks(4_220));
        Assert.Equal(0, released.DriftCutPulse);
        playback.Reset();
        Assert.False(playback.Sample(Ticks(4_240)).Available);
        Assert.Equal(0, playback.Sample(Ticks(4_240)).DriftCutPulse);
    }

    [Fact]
    public void HoldsNeverReplaceRawPeakSamplesOrPeakResetValues()
    {
        var model = Enabled();
        var before = Observe(model, 1_000, 1_500, 1_600);
        var held = Observe(model, 1_020, 0, 2_000);
        AssertHeld(before, held);
        Assert.Equal(1_500, held.PeakPowerBhp, 9);
        Assert.Equal(2_000, held.PeakTorqueNm);
        model.ResetPeaks();
        Assert.Equal(0, model.Current.PeakPowerBhp);
        Assert.Equal(2_000, model.Current.PeakTorqueNm);
        AssertHeld(before, model.Current);
    }
}

using Xunit;

namespace Wisp.App.Tests;

public sealed class PowerTorqueDisplayModelTests
{
    private const double WattsPerBhp = 745.69987158227022;

    [Theory]
    [InlineData(427, 507)]
    [InlineData(0, 0)]
    [InlineData(-87, -145)]
    public void FirstReadingPreservesSignedPowerAndTorque(double horsepower, double torque)
    {
        var model = new PowerTorqueDisplayModel { ShowNegative = true };
        var display = model.Observe(2974, 1_000, horsepower * WattsPerBhp, torque);
        Assert.True(display.Available);
        Assert.Equal(horsepower, display.PowerBhp, 9);
        Assert.Equal(torque, display.TorqueNm);
        Assert.Equal(horsepower, display.ReadoutPowerBhp!.Value, 9);
        Assert.Equal(torque, display.ReadoutTorqueNm);
        Assert.Equal(Math.Max(0, horsepower), display.PeakPowerBhp, 9);
        Assert.Equal(Math.Max(0, torque), display.PeakTorqueNm);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    [InlineData(500)]
    [InlineData(1500)]
    public void SmoothingDependsOnGameTimeRatherThanObserverFrequency(double smoothing)
    {
        var frequent = new PowerTorqueDisplayModel { SmoothingMilliseconds = smoothing };
        var sparse = new PowerTorqueDisplayModel { SmoothingMilliseconds = smoothing };
        frequent.Observe(1, 1_000, 0, 0);
        sparse.Observe(1, 1_000, 0, 0);
        for (uint timestamp = 1_010; timestamp <= 1_150; timestamp += 10)
            frequent.Observe(1, timestamp, 1_000 * WattsPerBhp, 1_200);
        var result = sparse.Observe(1, 1_150, 1_000 * WattsPerBhp, 1_200);
        Assert.Equal(result.PowerBhp, frequent.Current.PowerBhp, 9);
        Assert.Equal(result.TorqueNm, frequent.Current.TorqueNm, 9);
        Assert.Equal(result.PeakPowerBhp, frequent.Current.PeakPowerBhp);
        Assert.Equal(result.PeakTorqueNm, frequent.Current.PeakTorqueNm);
    }

    [Fact]
    public void RepeatedSnapshotDoesNotAdvanceSmoothingOrPeaks()
    {
        var model = new PowerTorqueDisplayModel();
        var first = model.Observe(1, 100, 400 * WattsPerBhp, 500);
        Assert.Equal(first, model.Observe(1, 100, 9_000 * WattsPerBhp, 9_000));
    }

    [Fact]
    public void BriefRawPeakIsRetainedEvenWhenTheReadoutSmoothingHasBarelyMoved()
    {
        var model = new PowerTorqueDisplayModel();
        model.Observe(1, 1_000, 0, 0);
        var peak = model.Observe(1, 1_001, 900 * WattsPerBhp, 1_100);
        Assert.InRange(peak.PowerBhp, 1, 2);
        Assert.InRange(peak.TorqueNm, 2, 3);
        Assert.Equal(0, peak.ReadoutPowerBhp);
        Assert.Equal(0, peak.ReadoutTorqueNm);
        Assert.Equal(900, peak.PeakPowerBhp, 9);
        Assert.Equal(1_100, peak.PeakTorqueNm);
        var afterPeak = model.Observe(1, 1_002, 0, 0);
        Assert.True(afterPeak.PowerBhp < peak.PowerBhp);
        Assert.True(afterPeak.TorqueNm < peak.TorqueNm);
        Assert.Equal(900, afterPeak.PeakPowerBhp, 9);
        Assert.Equal(1_100, afterPeak.PeakTorqueNm);
    }

    [Fact]
    public void ResetUsesTheLatestRawSampleAndCannotRecreateAnOldPeakFromFilterLag()
    {
        var model = new PowerTorqueDisplayModel();
        model.Observe(1, 1_000, 900 * WattsPerBhp, 1_100);
        var low = model.Observe(1, 1_001, 100 * WattsPerBhp, 120);
        Assert.True(low.PowerBhp > 800);
        Assert.True(low.TorqueNm > 1_000);
        model.ResetPeaks();
        Assert.Equal(100, model.Current.PeakPowerBhp, 9);
        Assert.Equal(120, model.Current.PeakTorqueNm);
        var afterReset = model.Observe(1, 1_002, 50 * WattsPerBhp, 60);
        Assert.True(afterReset.PowerBhp > 800);
        Assert.Equal(100, afterReset.PeakPowerBhp, 9);
        Assert.Equal(120, afterReset.PeakTorqueNm);
    }

    [Fact]
    public void UnsignedTimestampWrapRemainsContinuous()
    {
        var wrapped = new PowerTorqueDisplayModel();
        var continuous = new PowerTorqueDisplayModel();
        wrapped.Observe(1, uint.MaxValue - 49, 100 * WattsPerBhp, 100);
        continuous.Observe(1, 1_000, 100 * WattsPerBhp, 100);
        Assert.Equal(continuous.Observe(1, 1_100, 500 * WattsPerBhp, 500),
            wrapped.Observe(1, 50, 500 * WattsPerBhp, 500));
    }

    [Theory]
    [InlineData(9_000)]
    [InlineData(100)]
    public void DiscontinuityReseedsCurrentReadingWithoutErasingCarPeaks(uint timestamp)
    {
        var model = new PowerTorqueDisplayModel();
        model.Observe(1, 1_000, 700 * WattsPerBhp, 900);
        var display = model.Observe(1, timestamp, 100 * WattsPerBhp, 120);
        Assert.Equal(100, display.PowerBhp, 9);
        Assert.Equal(120, display.TorqueNm);
        Assert.Equal(100, display.ReadoutPowerBhp!.Value, 9);
        Assert.Equal(120, display.ReadoutTorqueNm);
        Assert.Equal(700, display.PeakPowerBhp, 9);
        Assert.Equal(900, display.PeakTorqueNm);
    }

    [Fact]
    public void CarChangeAndExplicitResetDoNotCarryThePreviousCarsPeaks()
    {
        var model = new PowerTorqueDisplayModel();
        model.Observe(1, 100, 800 * WattsPerBhp, 900);
        var secondCar = model.Observe(2, 110, 100 * WattsPerBhp, 120);
        Assert.Equal(100, secondCar.PeakPowerBhp, 9);
        Assert.Equal(120, secondCar.PeakTorqueNm);
        model.Reset();
        Assert.Equal(PowerTorqueDisplay.Unavailable, model.Current);
        Assert.Equal(0, model.Observe(2, 120, -20 * WattsPerBhp, -10).PeakPowerBhp);
    }

    [Fact]
    public void ResetPeaksStartsFromTheCurrentReadingAndEngineBrakingCannotRaisePeaks()
    {
        var model = new PowerTorqueDisplayModel();
        model.Observe(1, 100, 800 * WattsPerBhp, 900);
        model.Observe(1, 3_000, 100 * WattsPerBhp, 120);
        model.ResetPeaks();
        Assert.Equal(100, model.Current.PeakPowerBhp, 9);
        Assert.Equal(120, model.Current.PeakTorqueNm);
        var braking = model.Observe(1, 6_000, -500 * WattsPerBhp, -500);
        Assert.Equal(100, braking.PeakPowerBhp, 9);
        Assert.Equal(120, braking.PeakTorqueNm);
        model.ResetPeaks();
        Assert.Equal(0, model.Current.PeakPowerBhp);
        Assert.Equal(0, model.Current.PeakTorqueNm);
    }

    [Theory]
    [InlineData(double.NaN, 500)]
    [InlineData(500, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, 500)]
    public void InvalidTelemetryNeverEntersTheReadoutOrPeakState(double watts, double torque)
    {
        var model = new PowerTorqueDisplayModel();
        model.Observe(1, 100, 400 * WattsPerBhp, 500);
        var invalid = model.Observe(1, 150, watts, torque);
        Assert.False(invalid.Available);
        Assert.Equal(0, invalid.PowerBhp);
        Assert.Equal(0, invalid.TorqueNm);
        Assert.Null(invalid.ReadoutPowerBhp);
        Assert.Null(invalid.ReadoutTorqueNm);
        Assert.Equal(400, invalid.PeakPowerBhp, 9);
        Assert.Equal(500, invalid.PeakTorqueNm);
        var recovered = model.Observe(1, 200, 200 * WattsPerBhp, 250);
        Assert.True(recovered.Available);
        Assert.Equal(200, recovered.PowerBhp, 9);
        Assert.Equal(250, recovered.TorqueNm);
        Assert.Equal(200, recovered.ReadoutPowerBhp!.Value, 9);
        Assert.Equal(250, recovered.ReadoutTorqueNm);
    }

    [Fact]
    public void LossOfCarHidesReadingsAndDoesNotEraseSameCarsPeak()
    {
        var model = new PowerTorqueDisplayModel();
        model.Observe(1, 100, 400 * WattsPerBhp, 500);
        Assert.False(model.Observe(0, 150, 0, 0).Available);
        var restored = model.Observe(1, 200, 100 * WattsPerBhp, 120);
        Assert.Equal(100, restored.PowerBhp, 9);
        Assert.Equal(400, restored.PeakPowerBhp, 9);
        model.ResetCurrent();
        model.ResetPeaks();
        Assert.False(model.Current.Available);
        Assert.Equal(0, model.Current.PeakPowerBhp);
    }

    [Fact]
    public void UnitConversionDoesNotMutateTheCanonicalNewtonMeters()
    {
        var display = new PowerTorqueDisplayModel().Observe(1, 100, WattsPerBhp, 1_000);
        Assert.Equal(1_000, PowerTorqueDisplay.ConvertTorque(display.TorqueNm, TorqueUnit.NewtonMeters));
        Assert.Equal(737.5621492772656, PowerTorqueDisplay.ConvertTorque(display.TorqueNm, TorqueUnit.PoundFeet), 9);
        Assert.Equal(-73.75621492772656, PowerTorqueDisplay.ConvertTorque(-100, TorqueUnit.PoundFeet), 9);
        Assert.Equal(1_000, display.TorqueNm);
    }

    [Fact]
    public void DefaultSmoothingSlowsReadoutsWhileTheNeedleStillReceivesEverySample()
    {
        var model = new PowerTorqueDisplayModel();
        Assert.Equal(500, model.SmoothingMilliseconds);
        model.Observe(1, 1_000, 0, 0);
        double lastNeedle = 0;
        for (uint timestamp = 1_010; timestamp < 1_100; timestamp += 10)
        {
            var sample = model.Observe(1, timestamp, 1_000 * WattsPerBhp, 1_200);
            Assert.True(sample.PowerBhp > lastNeedle);
            Assert.Equal(0, sample.ReadoutPowerBhp);
            Assert.Equal(0, sample.ReadoutTorqueNm);
            lastNeedle = sample.PowerBhp;
        }
        var published = model.Observe(1, 1_100, 1_000 * WattsPerBhp, 1_200);
        Assert.InRange(published.PowerBhp, 181.26, 181.28);
        Assert.InRange(published.TorqueNm, 217.51, 217.53);
        Assert.Equal(published.PowerBhp, published.ReadoutPowerBhp);
        Assert.Equal(published.TorqueNm, published.ReadoutTorqueNm);
        var next = model.Observe(1, 1_110, 1_000 * WattsPerBhp, 1_200);
        Assert.True(next.PowerBhp > published.PowerBhp);
        Assert.Equal(published.ReadoutPowerBhp, next.ReadoutPowerBhp);
        Assert.Equal(published.ReadoutTorqueNm, next.ReadoutTorqueNm);
    }

    [Fact]
    public void RapidAlternatingSignedOutputKeepsNumbersOnTheirCadenceAndRetainsRawPeaks()
    {
        var signed = new PowerTorqueDisplayModel { ShowNegative = true };
        var positiveOnly = new PowerTorqueDisplayModel();
        signed.Observe(1, 1_000, 0, 0);
        positiveOnly.Observe(1, 1_000, 0, 0);
        double? previousReadout = 0;
        var changes = 0;
        for (uint index = 1; index <= 100; index++)
        {
            var sign = index % 2 == 0 ? -1 : 1;
            var sample = signed.Observe(1, 1_000 + index * 20, sign * 900 * WattsPerBhp, sign * 1_100);
            positiveOnly.Observe(1, 1_000 + index * 20, sign * 900 * WattsPerBhp, sign * 1_100);
            if (sample.ReadoutPowerBhp != previousReadout)
            {
                Assert.Equal(0u, index % 5);
                changes++;
            }
            previousReadout = sample.ReadoutPowerBhp;
        }
        Assert.Equal(20, changes);
        Assert.InRange(Math.Abs(signed.Current.PowerBhp), 0, 25);
        Assert.InRange(Math.Abs(signed.Current.TorqueNm), 0, 30);
        Assert.InRange(positiveOnly.Current.PowerBhp, 420, 470);
        Assert.InRange(positiveOnly.Current.TorqueNm, 510, 580);
        Assert.Equal(900, signed.Current.PeakPowerBhp, 9);
        Assert.Equal(1_100, signed.Current.PeakTorqueNm);
        Assert.Equal(signed.Current.PeakPowerBhp, positiveOnly.Current.PeakPowerBhp);
        Assert.Equal(signed.Current.PeakTorqueNm, positiveOnly.Current.PeakTorqueNm);
    }

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(true, -69, -138)]
    public void NegativeReadingsAreOptionalWithoutChangingRawPositivePeaks(bool showNegative,
        double expectedPower, double expectedTorque)
    {
        var model = new PowerTorqueDisplayModel { ShowNegative = showNegative };
        var display = model.Observe(1, 100, -69 * WattsPerBhp, -138);
        Assert.Equal(expectedPower, display.PowerBhp, 9);
        Assert.Equal(expectedTorque, display.TorqueNm);
        Assert.Equal(expectedPower, display.ReadoutPowerBhp!.Value, 9);
        Assert.Equal(expectedTorque, display.ReadoutTorqueNm);
        Assert.Equal(0, display.PeakPowerBhp);
        Assert.Equal(0, display.PeakTorqueNm);
    }

    [Fact]
    public void HiddenNegativeInputsAreClampedBeforeFilteringRatherThanDraggingPositiveOutputBelowZero()
    {
        var positiveOnly = new PowerTorqueDisplayModel();
        var signed = new PowerTorqueDisplayModel { ShowNegative = true };
        Assert.False(positiveOnly.ShowNegative);
        positiveOnly.Observe(1, 1_000, 400 * WattsPerBhp, 500);
        signed.Observe(1, 1_000, 400 * WattsPerBhp, 500);
        var clamped = positiveOnly.Observe(1, 1_100, -4_000 * WattsPerBhp, -5_000);
        var unclamped = signed.Observe(1, 1_100, -4_000 * WattsPerBhp, -5_000);
        Assert.InRange(clamped.PowerBhp, 325, 330);
        Assert.InRange(clamped.TorqueNm, 405, 415);
        Assert.True(unclamped.PowerBhp < 0);
        Assert.True(unclamped.TorqueNm < 0);
        Assert.Equal(400, clamped.PeakPowerBhp, 9);
        Assert.Equal(500, clamped.PeakTorqueNm);
        Assert.Equal(clamped.PeakPowerBhp, unclamped.PeakPowerBhp);
        Assert.Equal(clamped.PeakTorqueNm, unclamped.PeakTorqueNm);
    }

    [Fact]
    public void HidingNegativeReadingsClearsPreviouslyDisplayedNegativesImmediately()
    {
        var model = new PowerTorqueDisplayModel { ShowNegative = true };
        model.Observe(1, 100, 100 * WattsPerBhp, 200);
        model.Observe(1, 3_000, -69 * WattsPerBhp, -138);
        Assert.True(model.Current.PowerBhp < 0);
        Assert.True(model.Current.ReadoutPowerBhp < 0);
        model.ShowNegative = false;
        Assert.Equal(0, model.Current.PowerBhp);
        Assert.Equal(0, model.Current.TorqueNm);
        Assert.Equal(0, model.Current.ReadoutPowerBhp);
        Assert.Equal(0, model.Current.ReadoutTorqueNm);
        Assert.Equal(100, model.Current.PeakPowerBhp, 9);
        Assert.Equal(200, model.Current.PeakTorqueNm);
        Assert.Equal(0, model.Observe(1, 3_010, -500 * WattsPerBhp, -600).PowerBhp);
        model.SmoothingMilliseconds = 0;
        model.ShowNegative = true;
        var signed = model.Observe(1, 3_100, -69 * WattsPerBhp, -138);
        Assert.Equal(-69, signed.PowerBhp, 9);
        Assert.Equal(-69, signed.ReadoutPowerBhp!.Value, 9);
        Assert.Equal(-138, signed.ReadoutTorqueNm);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(750, 750)]
    [InlineData(2_000, 1_500)]
    [InlineData(double.NaN, 500)]
    [InlineData(double.PositiveInfinity, 500)]
    public void SmoothingIsBoundedAndSurvivesCarAndDisplayResets(double requested, double expected)
    {
        var model = new PowerTorqueDisplayModel { SmoothingMilliseconds = requested, ShowNegative = true };
        Assert.Equal(expected, model.SmoothingMilliseconds);
        model.Observe(1, 100, 200 * WattsPerBhp, 300);
        model.Observe(2, 110, -69 * WattsPerBhp, -138);
        Assert.Equal(expected, model.SmoothingMilliseconds);
        Assert.True(model.ShowNegative);
        Assert.Equal(-69, model.Current.ReadoutPowerBhp!.Value, 9);
        model.ResetCurrent();
        Assert.Null(model.Current.ReadoutPowerBhp);
        model.Reset();
        Assert.Equal(expected, model.SmoothingMilliseconds);
        Assert.True(model.ShowNegative);
    }
}

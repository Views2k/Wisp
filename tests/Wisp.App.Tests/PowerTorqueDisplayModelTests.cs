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
        var model = new PowerTorqueDisplayModel();
        var display = model.Observe(2974, 1_000, horsepower * WattsPerBhp, torque);
        Assert.True(display.Available);
        Assert.Equal(horsepower, display.PowerBhp, 9);
        Assert.Equal(torque, display.TorqueNm);
        Assert.Equal(Math.Max(0, horsepower), display.PeakPowerBhp, 9);
        Assert.Equal(Math.Max(0, torque), display.PeakTorqueNm);
    }

    [Fact]
    public void SmoothingDependsOnGameTimeRatherThanObserverFrequency()
    {
        var frequent = new PowerTorqueDisplayModel();
        var sparse = new PowerTorqueDisplayModel();
        frequent.Observe(1, 1_000, 0, 0);
        sparse.Observe(1, 1_000, 0, 0);
        for (uint timestamp = 1_010; timestamp <= 1_150; timestamp += 10)
            frequent.Observe(1, timestamp, 1_000 * WattsPerBhp, 1_200);
        var result = sparse.Observe(1, 1_150, 1_000 * WattsPerBhp, 1_200);
        Assert.Equal(result.PowerBhp, frequent.Current.PowerBhp, 9);
        Assert.Equal(result.TorqueNm, frequent.Current.TorqueNm, 9);
        Assert.InRange(result.PowerBhp, 632.12, 632.13);
        Assert.InRange(result.TorqueNm, 758.54, 758.55);
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
        Assert.InRange(peak.PowerBhp, 5, 7);
        Assert.InRange(peak.TorqueNm, 7, 8);
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
        Assert.Equal(400, invalid.PeakPowerBhp, 9);
        Assert.Equal(500, invalid.PeakTorqueNm);
        var recovered = model.Observe(1, 200, 200 * WattsPerBhp, 250);
        Assert.True(recovered.Available);
        Assert.Equal(200, recovered.PowerBhp, 9);
        Assert.Equal(250, recovered.TorqueNm);
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
}

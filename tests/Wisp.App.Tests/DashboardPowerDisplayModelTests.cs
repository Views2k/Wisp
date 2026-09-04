using Xunit;

namespace Wisp.App.Tests;

public sealed class DashboardPowerDisplayModelTests
{
    private const double WattsPerHorsepower = 745.69987158227022;

    [Theory]
    [InlineData(113, "+113 HP")]
    [InlineData(-113, "-113 HP")]
    [InlineData(0, "0 HP")]
    public void FirstSamplePublishesImmediatelyAsWholeHorsepower(
        double horsepower,
        string expected)
    {
        var model = new DashboardPowerDisplayModel();

        var display = model.Observe(1, 1_000, horsepower * WattsPerHorsepower, 250);

        Assert.Equal(expected, display.Power);
        Assert.Equal("+250 Nm", display.Torque);
    }

    [Fact]
    public void RapidSamplesAreSmoothedAndRateLimited()
    {
        var model = new DashboardPowerDisplayModel();

        Assert.Equal(
            "+100 HP",
            model.Observe(1, 1_000, 100 * WattsPerHorsepower, 100).Power);

        Assert.Equal(
            "+100 HP",
            model.Observe(1, 1_050, 1_000 * WattsPerHorsepower, 1_000).Power);

        var display = model.Observe(
            1,
            1_125,
            1_000 * WattsPerHorsepower,
            1_000);

        Assert.Equal("+454 HP", display.Power);
        Assert.Equal("+454 Nm", display.Torque);
    }

    [Fact]
    public void NewCarAndExplicitResetPublishTheNewValueImmediately()
    {
        var model = new DashboardPowerDisplayModel();

        _ = model.Observe(1, 1_000, 100 * WattsPerHorsepower, 100);
        _ = model.Observe(1, 1_125, 1_000 * WattsPerHorsepower, 1_000);

        var newCar = model.Observe(2, 1_130, 250 * WattsPerHorsepower, 300);
        Assert.Equal("+250 HP", newCar.Power);
        Assert.Equal("+300 Nm", newCar.Torque);

        model.Reset();

        var reset = model.Observe(2, 1_131, -100 * WattsPerHorsepower, -150);
        Assert.Equal("-100 HP", reset.Power);
        Assert.Equal("-150 Nm", reset.Torque);
    }

    [Fact]
    public void TorqueUnitChangesFormattingWithoutResettingSmoothing()
    {
        var model = new DashboardPowerDisplayModel();

        var metric = model.Observe(1, 1_000, 100 * WattsPerHorsepower, 300);
        var imperial = model.Current(TorqueUnit.PoundFeet);

        Assert.Equal("+300 Nm", metric.Torque);
        Assert.Equal("+221 lb-ft", imperial.Torque);
        Assert.Equal("221 lb-ft", imperial.PeakTorque);
    }

    [Fact]
    public void PeaksUseSmoothedPositiveOutputAndIgnoreRegeneration()
    {
        var model = new DashboardPowerDisplayModel();

        _ = model.Observe(1, 1_000, 100 * WattsPerHorsepower, 100);
        var peak = model.Observe(1, 1_125, 1_000 * WattsPerHorsepower, 1_000);
        var regenerative = model.Observe(1, 1_250, -1_000 * WattsPerHorsepower, -1_000);

        Assert.Equal("454 HP", peak.PeakPower);
        Assert.Equal("454 Nm", peak.PeakTorque);
        Assert.Equal("454 HP", regenerative.PeakPower);
        Assert.Equal("454 Nm", regenerative.PeakTorque);
    }

    [Fact]
    public void SameCarGapAndCurrentResetPreservePeaks()
    {
        var model = new DashboardPowerDisplayModel();

        _ = model.Observe(1, 1_000, 500 * WattsPerHorsepower, 500);
        var afterGap = model.Observe(1, 4_000, 100 * WattsPerHorsepower, 100);
        model.ResetCurrent();
        var unavailable = model.Current(TorqueUnit.NewtonMeters);

        Assert.Equal("500 HP", afterGap.PeakPower);
        Assert.Equal("500 Nm", afterGap.PeakTorque);
        Assert.Equal("—", unavailable.Power);
        Assert.Equal("500 HP", unavailable.PeakPower);
        Assert.Equal("500 Nm", unavailable.PeakTorque);
    }

    [Fact]
    public void CarChangeAndManualResetClearPreviousPeaks()
    {
        var model = new DashboardPowerDisplayModel();

        _ = model.Observe(1, 1_000, 500 * WattsPerHorsepower, 500);
        var newCar = model.Observe(2, 1_100, 200 * WattsPerHorsepower, 250);
        model.ResetPeaks();
        var reset = model.Current(TorqueUnit.NewtonMeters);

        Assert.Equal("200 HP", newCar.PeakPower);
        Assert.Equal("250 Nm", newCar.PeakTorque);
        Assert.Equal("—", reset.PeakPower);
        Assert.Equal("—", reset.PeakTorque);
    }

    [Fact]
    public void TopSpeedTracksEveryAvailableSampleAndReformatsFromMetersPerSecond()
    {
        var model = new DashboardPowerDisplayModel();

        _ = model.Observe(
            1,
            1_000,
            100 * WattsPerHorsepower,
            100,
            speedMetersPerSecond: 30);
        var peak = model.Observe(
            1,
            1_050,
            100 * WattsPerHorsepower,
            100,
            speedMetersPerSecond: 40);
        var metric = model.Current(
            TorqueUnit.NewtonMeters,
            Wisp.Core.SpeedUnit.KilometersPerHour);

        Assert.Equal("89 MPH", peak.TopSpeed);
        Assert.Equal("144 KM/H", metric.TopSpeed);
    }

    [Fact]
    public void TopSpeedUsesTheSameCarLossAndManualResetSemanticsAsOtherPeaks()
    {
        var model = new DashboardPowerDisplayModel();

        _ = model.Observe(
            1,
            1_000,
            100 * WattsPerHorsepower,
            100,
            speedMetersPerSecond: 40);
        model.ResetCurrent();
        var retained = model.Current(TorqueUnit.NewtonMeters);
        var newCar = model.Observe(
            2,
            1_100,
            100 * WattsPerHorsepower,
            100,
            speedMetersPerSecond: 20);
        model.ResetPeaks();
        var reset = model.Current(TorqueUnit.NewtonMeters);

        Assert.Equal("89 MPH", retained.TopSpeed);
        Assert.Equal("44 MPH", newCar.TopSpeed);
        Assert.Equal("—", reset.TopSpeed);
    }

    [Fact]
    public void AvailableSpeedStartsTheNewCarSessionWhenPowertrainDataIsInvalid()
    {
        var model = new DashboardPowerDisplayModel();

        _ = model.Observe(
            1,
            1_000,
            100 * WattsPerHorsepower,
            100,
            speedMetersPerSecond: 40);
        var newCar = model.Observe(
            2,
            1_100,
            double.NaN,
            100,
            speedMetersPerSecond: 50);

        Assert.Equal("—", newCar.Power);
        Assert.Equal("—", newCar.PeakPower);
        Assert.Equal("111 MPH", newCar.TopSpeed);
    }
}

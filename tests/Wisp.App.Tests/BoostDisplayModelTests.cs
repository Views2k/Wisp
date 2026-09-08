using Xunit;

namespace Wisp.App.Tests;

public sealed class BoostDisplayModelTests
{
    [Theory]
    [InlineData(-2.9465)]
    [InlineData(-10)]
    [InlineData(-20)]
    [InlineData(-35)]
    public void VacuumOptionPreservesTelemetryWithoutTreatingVacuumAsBoost(double pressure)
    {
        var model = new BoostDisplayModel();
        model.Calculate(3411, false, 24);

        var display = model.Calculate(3411, false, pressure, showVacuum: true);

        Assert.True(display.IsAvailable);
        Assert.Equal(pressure, display.PressurePsi);
        Assert.Equal(24, display.LearnedPeakPsi);
        Assert.Equal(0, display.Fraction);
        Assert.Equal(-20, display.ScaleMinimumPsi);
        Assert.Equal(70, display.ScaleMaximumPsi);
        var disabled = model.Calculate(3411, false, pressure);
        Assert.Equal(0, disabled.PressurePsi);
        Assert.Equal(0, disabled.ScaleMinimumPsi);
        Assert.Equal(24, disabled.LearnedPeakPsi);
    }

    [Fact]
    public void VacuumOptionDoesNotInventPressureOrBypassVehicleAvailability()
    {
        var model = new BoostDisplayModel();
        Assert.True(model.Calculate(3411, false, 0, showVacuum: true).IsAvailable);
        Assert.Equal(0, model.Calculate(3411, false, -10, showVacuum: true).PressurePsi);
        Assert.Equal(0, model.Calculate(3411, false, 0, showVacuum: true).PressurePsi);
        Assert.False(model.Calculate(3411, true, -10, showVacuum: true).IsAvailable);
        Assert.True(model.Calculate(3411, false, 0, showVacuum: true).IsAvailable);
        Assert.True(model.Calculate(3411, false, 10, showVacuum: true).IsAvailable);
        Assert.True(model.Calculate(1024, false, 0, showVacuum: true).IsAvailable);
        Assert.False(model.Calculate(1024, false, double.NaN, showVacuum: true).IsAvailable);
        Assert.False(model.Calculate(1024, false, double.NegativeInfinity, showVacuum: true).IsAvailable);
    }

    [Fact]
    public void ProvidesFifteenColorPalettesAndOneNeutralStockStyle()
    {
        Assert.Equal(16, BoostGaugeThemes.All.Count);
        Assert.Equal(
            AppColorThemes.All.Select(theme => theme.Name),
            BoostGaugeThemes.All.Take(15).Select(theme => theme.Name));
        var stock = Assert.Single(BoostGaugeThemes.All, theme => theme.Name == "Stock");
        Assert.Equal(stock.Low, stock.Mid);
        Assert.Equal(stock.Mid, stock.High);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NaturallyAspiratedVacuumAndThrottleCyclesKeepTheGaugeAvailableAtZero(bool showVacuum)
    {
        var model = new BoostDisplayModel();

        foreach (var pressure in new[] { -15d, -10, -4, 0, 0.1, -15, 0, -15 })
        {
            var display = model.Calculate(3411, false, pressure, showVacuum);
            Assert.True(display.IsAvailable);
            Assert.Equal(0, display.PressurePsi);
            Assert.Equal(0, display.LearnedPeakPsi);
            Assert.Equal(0, display.Fraction);
        }
    }

    [Fact]
    public void KeepsPositivePsiAndClampsVacuumToZero()
    {
        var model = new BoostDisplayModel();
        model.Calculate(3411, false, 12);

        var first = model.Calculate(3411, false, 29.3437);
        var released = model.Calculate(3411, false, -2.9465);

        Assert.Equal(29.3437, first.PressurePsi, 4);
        Assert.Equal(29.3437, released.LearnedPeakPsi, 4);
        Assert.Equal(0, released.PressurePsi);
        Assert.Equal(0, released.Fraction);
        Assert.Equal(70, released.ScaleMaximumPsi);
    }

    [Fact]
    public void ZeroPressureDoesNotIdentifyForcedInduction()
    {
        var model = new BoostDisplayModel();
        for (var i = 0; i < 20; i++)
        {
            var display = model.Calculate(22, false, 0);
            Assert.True(display.IsAvailable);
            Assert.Equal(0, display.PressurePsi);
            Assert.False(model.HasDetectedBoost);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ElectricVehiclesNeverExposeBoost(bool showVacuum)
    {
        var model = new BoostDisplayModel();

        Assert.False(model.Calculate(23, true, 15, showVacuum).IsAvailable);
        Assert.False(model.Calculate(23, true, -11, showVacuum).IsAvailable);
    }

    [Fact]
    public void ElectricSamplesClearLearnedBoostState()
    {
        var model = new BoostDisplayModel();
        Assert.True(model.Calculate(23, false, 15).IsAvailable);

        Assert.False(model.Calculate(23, true, 15).IsAvailable);
        Assert.False(model.Calculate(23, true, 0).IsAvailable);
        var combustion = model.Calculate(23, false, -15, showVacuum: true);
        Assert.True(combustion.IsAvailable);
        Assert.Equal(0, combustion.PressurePsi);
    }

    [Fact]
    public void PositiveBoostEnablesSubsequentVacuumForTheSameCar()
    {
        var model = new BoostDisplayModel();
        var beforeBoost = model.Calculate(23, false, -15, showVacuum: true);
        Assert.True(beforeBoost.IsAvailable);
        Assert.Equal(0, beforeBoost.PressurePsi);
        Assert.True(model.Calculate(23, false, 7, showVacuum: true).IsAvailable);
        var vacuum = model.Calculate(23, false, -15, showVacuum: true);
        Assert.True(vacuum.IsAvailable);
        Assert.Equal(-15, vacuum.PressurePsi);
        Assert.Equal(7, vacuum.LearnedPeakPsi);

        model.Reset();
        var afterReset = model.Calculate(23, false, -15, showVacuum: true);
        Assert.True(afterReset.IsAvailable);
        Assert.Equal(0, afterReset.PressurePsi);
    }

    [Fact]
    public void ResetsLearnedStateWhenTheCarChanges()
    {
        var model = new BoostDisplayModel();
        model.Calculate(3411, false, 24);
        Assert.True(model.Calculate(3411, false, 24).IsAvailable);

        foreach (var pressure in new[] { 0d, -15, 0 })
        {
            var display = model.Calculate(1024, false, pressure, showVacuum: true);
            Assert.True(display.IsAvailable);
            Assert.Equal(0, display.PressurePsi);
            Assert.Equal(0, display.LearnedPeakPsi);
        }
    }
}

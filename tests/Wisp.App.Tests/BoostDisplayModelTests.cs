using Xunit;

namespace Wisp.App.Tests;

public sealed class BoostDisplayModelTests
{
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

    [Fact]
    public void ShowsTheGaugeOnTheFirstManifoldPressureSample()
    {
        var model = new BoostDisplayModel();

        var display = model.Calculate(3411, false, -11);

        Assert.True(display.IsAvailable);
        Assert.Equal(0, display.PressurePsi);
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
        for (var i = 0; i < 20; i++) Assert.False(model.Calculate(22, false, 0).IsAvailable);
    }

    [Fact]
    public void ElectricVehiclesNeverExposeBoost()
    {
        var model = new BoostDisplayModel();

        Assert.False(model.Calculate(23, true, 15).IsAvailable);
        Assert.False(model.Calculate(23, true, -11).IsAvailable);
    }

    [Fact]
    public void ElectricSamplesClearLearnedBoostState()
    {
        var model = new BoostDisplayModel();
        Assert.True(model.Calculate(23, false, 15).IsAvailable);

        Assert.False(model.Calculate(23, true, 15).IsAvailable);
        Assert.False(model.Calculate(23, true, 0).IsAvailable);
        Assert.False(model.Calculate(23, false, 0).IsAvailable);
    }

    [Fact]
    public void VacuumMakesTheGaugeAvailableBeforePositiveBoost()
    {
        var model = new BoostDisplayModel();
        Assert.True(model.Calculate(23, false, -4).IsAvailable);
        Assert.True(model.Calculate(23, false, 7).IsAvailable);
    }

    [Fact]
    public void ResetsLearnedStateWhenTheCarChanges()
    {
        var model = new BoostDisplayModel();
        model.Calculate(3411, false, 24);
        Assert.True(model.Calculate(3411, false, 24).IsAvailable);

        Assert.False(model.Calculate(1024, false, 0).IsAvailable);
    }
}

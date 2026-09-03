using Wisp.App;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class TireTemperatureDisplayModelTests
{
    [Fact]
    public void AveragesFrontAndRearAxlesWithoutSmoothing()
    {
        var display = new TireTemperatureDisplayModel().Calculate(
            2468,
            new WheelValues(240, 260, 210, 230));

        Assert.True(display.IsAvailable);
        Assert.Equal(250, display.FrontFahrenheit);
        Assert.Equal(220, display.RearFahrenheit);
        Assert.Equal(2d / 3, display.FrontFraction, 8);
        Assert.Equal(17d / 30, display.RearFraction, 8);
    }

    [Fact]
    public void GaugePositionClampsButExactTemperaturesRemainAvailable()
    {
        var display = new TireTemperatureDisplayModel().Calculate(
            2468,
            new WheelValues(30, 40, 360, 380));

        Assert.Equal(35, display.FrontFahrenheit);
        Assert.Equal(370, display.RearFahrenheit);
        Assert.Equal(0, display.FrontFraction);
        Assert.Equal(1, display.RearFraction);
    }

    [Fact]
    public void RawTemperaturesRemainAvailableWhileReadoutsClampToTheGaugeCeiling()
    {
        var display = new TireTemperatureDisplayModel().Calculate(
            2468,
            new WheelValues(388, 412, 360, 380));

        Assert.Equal(400, display.FrontFahrenheit);
        Assert.Equal(370, display.RearFahrenheit);
        Assert.Equal(1, display.FrontFraction);
        Assert.Equal(1, display.RearFraction);
        Assert.Equal(350, display.Front(TireTemperatureUnit.Fahrenheit));
        Assert.Equal(350, display.Rear(TireTemperatureUnit.Fahrenheit));
        Assert.Equal(176.66666666666666, display.Front(TireTemperatureUnit.Celsius), 8);
    }

    [Fact]
    public void SaturatedMarkersHoldAtTheEndpointUntilTemperatureClearsTheBoundary()
    {
        var model = new TireTemperatureDisplayModel();

        Assert.Equal(1, model.Calculate(2468, new WheelValues(360, 360, 200, 200)).FrontFraction);
        Assert.Equal(1, model.Calculate(2468, new WheelValues(348, 348, 200, 200)).FrontFraction);
        Assert.Equal(0.98, model.Calculate(2468, new WheelValues(344, 344, 200, 200)).FrontFraction, 8);
    }

    [Fact]
    public void SaturationStateDoesNotCarryAcrossCars()
    {
        var model = new TireTemperatureDisplayModel();

        Assert.Equal(1, model.Calculate(2468, new WheelValues(360, 360, 200, 200)).FrontFraction);
        Assert.Equal((348d - 50) / 300, model.Calculate(8642, new WheelValues(348, 348, 200, 200)).FrontFraction, 8);
    }

    [Theory]
    [InlineData(32, 0)]
    [InlineData(212, 100)]
    [InlineData(350, 176.66666666666666)]
    public void ConvertsWireFahrenheitToCelsius(double fahrenheit, double expected)
    {
        Assert.Equal(
            expected,
            TireTemperatureDisplay.Convert(fahrenheit, TireTemperatureUnit.Celsius),
            8);
    }

    [Fact]
    public void ZeroFilledOrUnknownPacketsStayUnavailable()
    {
        var model = new TireTemperatureDisplayModel();

        Assert.False(model.Calculate(0, new WheelValues(200, 200, 200, 200)).IsAvailable);
        Assert.False(model.Calculate(2468, default).IsAvailable);
    }
}

using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeElectricPowerGaugeModelTests
{
    [Fact]
    public void MissingNativeStateIsUnavailable()
    {
        var display = new NativeElectricPowerGaugeModel().Update();

        Assert.False(display.Available);
        Assert.Equal(0, display.RegenRatio, 6);
        Assert.Equal(0, display.RegenFill, 6);
        Assert.Equal(0, display.PowerFill, 6);
    }

    [Fact]
    public void MissingNativeStateDoesNotApproximatePowerFromPedalInput()
    {
        var display = new NativeElectricPowerGaugeModel().Update();

        Assert.False(display.Available);
        Assert.Equal(0, display.PowerFill, 6);
        Assert.Equal(0, display.RegenFill, 6);
    }

    [Fact]
    public void CompleteNativeBarStateOverridesTelemetryAndAddsAuthoredRegenMarker()
    {
        var display = new NativeElectricPowerGaugeModel().Update(
            nativeRegenFillAmount: 0.25,
            nativePowerFillAmount: 0.60,
            nativeRegenPowerRatio: 0.40);

        Assert.True(display.Available);
        Assert.Equal(0.40, display.RegenRatio, 6);
        Assert.Equal(0.29, display.RegenFill, 6);
        Assert.Equal(0.60, display.PowerFill, 6);
    }

    [Theory]
    [InlineData(double.NaN, 0.6, 0.4)]
    [InlineData(0.2, double.PositiveInfinity, 0.4)]
    [InlineData(0.2, 0.6, -0.1)]
    [InlineData(0.2, 1.1, 0.4)]
    public void PartialOrInvalidNativeBarStateFailsClosedAtomically(
        double regen,
        double power,
        double ratio)
    {
        var display = new NativeElectricPowerGaugeModel().Update(
            regen,
            power,
            ratio);

        Assert.False(display.Available);
        Assert.Equal(0, display.RegenRatio, 6);
        Assert.Equal(0, display.RegenFill, 6);
        Assert.Equal(0, display.PowerFill, 6);
    }
}

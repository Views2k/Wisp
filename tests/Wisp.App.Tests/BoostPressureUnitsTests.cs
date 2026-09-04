using Xunit;

namespace Wisp.App.Tests;

public sealed class BoostPressureUnitsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(14.503773773, 1)]
    [InlineData(29.007547546, 2)]
    public void ConvertsPsiToBar(double psi, double expectedBar)
    {
        Assert.Equal(expectedBar, BoostPressureUnits.FromPsi(psi, BoostPressureUnit.Bar), 8);
    }

    [Fact]
    public void FormatsEachUnitForItsGaugeReadout()
    {
        Assert.Equal("24", BoostPressureUnits.FormatValue(24, BoostPressureUnit.Psi));
        Assert.Equal("1.7", BoostPressureUnits.FormatValue(24, BoostPressureUnit.Bar));
        Assert.Equal("PSI", BoostPressureUnits.Symbol(BoostPressureUnit.Psi));
        Assert.Equal("BAR", BoostPressureUnits.Symbol(BoostPressureUnit.Bar));
    }

    [Fact]
    public void BarAnalogueScaleUsesFiveBarRange()
    {
        Assert.Equal(5, BoostPressureUnits.AnalogMaximum(BoostPressureUnit.Bar));
        Assert.Equal(72.518868865, BoostPressureUnits.AnalogMaximumPsi(BoostPressureUnit.Bar), 8);
    }
}

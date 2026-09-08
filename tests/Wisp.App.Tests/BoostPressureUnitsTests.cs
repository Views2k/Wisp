using Xunit;

namespace Wisp.App.Tests;

public sealed class BoostPressureUnitsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(14.503773773, 1)]
    [InlineData(29.007547546, 2)]
    [InlineData(-14.503773773, -1)]
    public void ConvertsPsiToBar(double psi, double expectedBar)
    {
        Assert.Equal(expectedBar, BoostPressureUnits.FromPsi(psi, BoostPressureUnit.Bar), 8);
    }

    [Fact]
    public void FormatsEachUnitForItsGaugeReadout()
    {
        Assert.Equal("24", BoostPressureUnits.FormatValue(24, BoostPressureUnit.Psi));
        Assert.Equal("1.7", BoostPressureUnits.FormatValue(24, BoostPressureUnit.Bar));
        Assert.Equal("-20", BoostPressureUnits.FormatValue(-20, BoostPressureUnit.Psi));
        Assert.Equal("-1.4", BoostPressureUnits.FormatValue(-20, BoostPressureUnit.Bar));
        Assert.Equal("PSI", BoostPressureUnits.Symbol(BoostPressureUnit.Psi));
        Assert.Equal("BAR", BoostPressureUnits.Symbol(BoostPressureUnit.Bar));
    }

    [Theory]
    [InlineData(-0.1, BoostPressureUnit.Psi, "0")]
    [InlineData(-0.6, BoostPressureUnit.Psi, "-1")]
    [InlineData(-0.1, BoostPressureUnit.Bar, "0.0")]
    [InlineData(-1, BoostPressureUnit.Bar, "-0.1")]
    public void NearZeroReadoutsDoNotDisplayNegativeZero(double psi, BoostPressureUnit unit, string expected)
    {
        Assert.Equal(expected, BoostPressureUnits.FormatValue(psi, unit));
    }

    [Theory]
    [InlineData(-0.1, "00")]
    [InlineData(0, "00")]
    [InlineData(2.5, "03")]
    [InlineData(-2.5, "-3")]
    [InlineData(-20, "-20")]
    public void AnaloguePsiPaddingPreservesSignedAndRoundedReadouts(double psi, string expected)
    {
        Assert.Equal(expected, BoostPressureUnits.FormatValue(psi, BoostPressureUnit.Psi, padPsi: true));
    }

    [Fact]
    public void BarAnalogueScaleUsesFiveBarRange()
    {
        Assert.Equal(5, BoostPressureUnits.AnalogMaximum(BoostPressureUnit.Bar));
        Assert.Equal(72.518868865, BoostPressureUnits.AnalogMaximumPsi(BoostPressureUnit.Bar), 8);
    }

    [Theory]
    [InlineData(BoostPressureUnit.Psi, -20, -20, 70, 2d / 9)]
    [InlineData(BoostPressureUnit.Bar, -1, -14.503773773, 72.518868865, 1d / 6)]
    public void VacuumScaleHasAZeroReferenceBetweenItsEndpoints(
        BoostPressureUnit unit, double minimum, double minimumPsi, double maximumPsi, double zeroFraction)
    {
        Assert.Equal(minimum, BoostPressureUnits.AnalogMinimum(unit, showVacuum: true));
        Assert.Equal(0, BoostPressureUnits.AnalogMinimum(unit, showVacuum: false));
        Assert.Equal(0, BoostPressureUnits.GaugeFraction(minimumPsi, unit, showVacuum: true), 8);
        Assert.Equal(zeroFraction, BoostPressureUnits.GaugeFraction(0, unit, showVacuum: true), 8);
        Assert.Equal(1, BoostPressureUnits.GaugeFraction(maximumPsi, unit, showVacuum: true), 8);
        Assert.Equal(0, BoostPressureUnits.GaugeFraction(-200, unit, showVacuum: true));
        Assert.Equal(1, BoostPressureUnits.GaugeFraction(200, unit, showVacuum: true));
        Assert.Equal(0, BoostPressureUnits.GaugeFraction(-10, unit, showVacuum: false));
        Assert.Equal(0.5, BoostPressureUnits.GaugeFraction(maximumPsi / 2, unit, showVacuum: false), 8);
    }
}

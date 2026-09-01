using Wisp.App;
using Wisp.Core;
using System.Windows.Media;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeGaugeGeometryTests
{
    [Theory]
    [InlineData(0, 8_000, 120)]
    [InlineData(4_000, 8_000, 240)]
    [InlineData(8_000, 8_000, 360)]
    public void AnalogNeedleUsesMeasured240DegreeSweep(double rpm, double maximumRpm, double expected)
    {
        Assert.Equal(expected, NativeGaugeGeometry.AnalogNeedleAngle(rpm, maximumRpm), 6);
    }

    [Theory]
    [InlineData(0, 240, 150)]
    [InlineData(120, 240, 270)]
    [InlineData(240, 240, 390)]
    [InlineData(300, 240, 390)]
    [InlineData(-10, 240, 150)]
    [InlineData(120, 0, 150)]
    public void ElectricNeedleUsesNative150DegreeStartAndBounded240DegreeSweep(
        double speed,
        double maximumSpeed,
        double expected)
    {
        Assert.Equal(expected, NativeGaugeGeometry.ElectricAnalogNeedleAngle(speed, maximumSpeed), 6);
    }

    [Theory]
    [InlineData(6_500, 7)]
    [InlineData(7_999.995, 8)]
    [InlineData(8_000, 8)]
    [InlineData(9_000, 9)]
    [InlineData(9_500, 10)]
    [InlineData(10_000, 10)]
    [InlineData(10_999.994, 11)]
    [InlineData(11_000, 11)]
    [InlineData(45_000, 30)]
    public void TachScaleUsesFh6RoundedCeilingAndExportedGlyphRange(
        double maximumRpm,
        int expected)
    {
        Assert.Equal(expected, NativeGaugeGeometry.ScaleMaximumThousands(maximumRpm));
    }

    [Theory]
    [InlineData(8_000, 6_500, 0.8125)]
    [InlineData(9_800, 7_500, 0.75)]
    [InlineData(10_000, 9_500, 0.95)]
    public void RedlineUsesExactProviderValueWithinNativeScale(
        double maximumRpm,
        double redlineRpm,
        double expected)
    {
        var angularVelocity = redlineRpm * 2 * Math.PI / 60;
        var exact = Wisp.Core.ExactRedlineResult.Exact(angularVelocity);

        Assert.Equal(expected, NativeGaugeGeometry.RedlineStartNormalized(exact, maximumRpm), 6);
    }

    [Fact]
    public void MissingExactRedlineDrawsNoRedZoneOrShiftState()
    {
        var unavailable = Wisp.Core.ExactRedlineResult.Unavailable(
            Wisp.Core.ExactRedlineStatus.Unavailable);

        Assert.Equal(1, NativeGaugeGeometry.RedlineStartNormalized(unavailable, 8_000));
        Assert.False(NativeGaugeGeometry.IsRedlineValue(8, unavailable));
        Assert.False(NativeGaugeGeometry.IsShiftLightActive(8_000, unavailable));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void MissingNativeTachometerMaximumHasNoFallbackScale(double maximumRpm)
    {
        var exact = Wisp.Core.ExactRedlineResult.Exact(6_500 * 2 * Math.PI / 60);

        Assert.Equal(0, NativeGaugeGeometry.ScaleMaximumThousands(maximumRpm));
        Assert.Equal(0, NativeGaugeGeometry.ScaleMaximumRpm(maximumRpm));
        Assert.Equal(0, NativeGaugeGeometry.NormalizedRpm(4_000, maximumRpm));
        Assert.False(NativeGaugeGeometry.HasExactTachometerState(exact, maximumRpm));
    }

    [Fact]
    public void ExactTachometerStateRequiresRedlineWithinNativeMaximum()
    {
        var valid = Wisp.Core.ExactRedlineResult.Exact(6_500 * 2 * Math.PI / 60);
        var tooHigh = Wisp.Core.ExactRedlineResult.Exact(8_500 * 2 * Math.PI / 60);

        Assert.True(NativeGaugeGeometry.HasExactTachometerState(valid, 8_000));
        Assert.False(NativeGaugeGeometry.HasExactTachometerState(tooHigh, 8_000));
        Assert.False(NativeGaugeGeometry.HasExactTachometerState(
            Wisp.Core.ExactRedlineResult.Unavailable(),
            8_000));
    }

    [Theory]
    [InlineData(7, 6_999.999, false)]
    [InlineData(7, 7_000, true)]
    [InlineData(7, 7_001, true)]
    public void AnalogRpmNumberLitStateChangesAtItsNativeLabelRpm(
        int valueThousands,
        double engineRpm,
        bool expected)
    {
        Assert.Equal(expected, NativeGaugeGeometry.IsAnalogRpmNumberLit(valueThousands, engineRpm));
    }

    [Fact]
    public void RedlineRpmNumberUsesNativeUnlitAndLitAlphaStates()
    {
        var exact = ExactRedlineResult.Exact(6_500 * 2 * Math.PI / 60);
        var frame = new NativeGaugeFrame(
            true,
            0,
            6_999,
            8_000,
            TransmissionGear.First,
            SpeedUnit.MilesPerHour,
            exact);

        Assert.Equal(
            Color.FromArgb(255, 255, 0, 136),
            NativeAnalogGaugeVisual.NumberTintFor(7, frame));
        Assert.Equal(
            Color.FromArgb(205, 255, 0, 136),
            NativeAnalogGaugeVisual.NumberTintFor(7, frame with { EngineRpm = 7_000 }));
        Assert.Equal(
            Color.FromArgb(102, 255, 255, 255),
            NativeAnalogGaugeVisual.NumberTintFor(6, frame));
    }

    [Fact]
    public void AnalogNumberLayerOnlyInvalidatesWhenItsVisibleStateChanges()
    {
        var exact = ExactRedlineResult.Exact(6_500 * 2 * Math.PI / 60);
        var frame = new NativeGaugeFrame(
            true,
            0,
            4_000,
            8_000,
            TransmissionGear.First,
            SpeedUnit.MilesPerHour,
            exact);
        var belowRedlineNumber = NativeAnalogGaugeVisual.NumberLayerStateFor(frame);
        Assert.Equal(
            belowRedlineNumber,
            NativeAnalogGaugeVisual.NumberLayerStateFor(frame with { EngineRpm = 6_999 }));

        var firstLitRedlineNumber = NativeAnalogGaugeVisual.NumberLayerStateFor(
            frame with { EngineRpm = 7_000 });
        Assert.NotEqual(belowRedlineNumber, firstLitRedlineNumber);
        Assert.Equal(
            firstLitRedlineNumber,
            NativeAnalogGaugeVisual.NumberLayerStateFor(frame with { EngineRpm = 7_999 }));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(7, 0, 0, 7)]
    [InlineData(42, 0, 4, 2)]
    [InlineData(250, 2, 5, 0)]
    [InlineData(1200, 9, 9, 9)]
    public void SpeedDigitsUseTheThreeNativeTextureSlots(
        int speed,
        int hundreds,
        int tens,
        int ones)
    {
        Assert.Equal((hundreds, tens, ones), NativeGaugeGeometry.SpeedDigits(speed));
    }

    [Theory]
    [InlineData(TransmissionGear.Reverse, "R")]
    [InlineData(TransmissionGear.Neutral, "N")]
    [InlineData(TransmissionGear.First, "1")]
    [InlineData(TransmissionGear.Second, "2")]
    [InlineData(TransmissionGear.Tenth, "10")]
    public void SemanticGearMapsToNativeAssetToken(TransmissionGear gear, string expected)
    {
        Assert.Equal(expected, NativeGaugeGeometry.GearToken(gear));
    }

    [Theory]
    [InlineData(TransmissionGear.First)]
    [InlineData(TransmissionGear.Fifth)]
    [InlineData(TransmissionGear.Tenth)]
    public void AutomaticDisplayUsesDriveWithoutChangingPhysicalForwardGear(TransmissionGear gear)
    {
        Assert.Equal("Drive", NativeGaugeGeometry.GearToken(gear, GearDisplayMode.Automatic));
        Assert.Equal(((int)gear).ToString(), NativeGaugeGeometry.GearToken(gear, GearDisplayMode.Manual));
    }

    [Theory]
    [InlineData(TransmissionGear.Reverse, "R")]
    [InlineData(TransmissionGear.Neutral, "N")]
    public void AutomaticDisplayPreservesReverseAndNeutral(TransmissionGear gear, string expected)
    {
        Assert.Equal(expected, NativeGaugeGeometry.GearToken(gear, GearDisplayMode.Automatic));
    }

    [Fact]
    public void UnknownGearDoesNotRenderAsNeutral()
    {
        Assert.Null(NativeGaugeGeometry.GearToken(TransmissionGear.Unknown));
    }
}
